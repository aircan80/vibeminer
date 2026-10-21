using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class BuildRetentionDriver
{
    public BuildRetentionDriver(DiscoveryCatalog catalog, VerifiedObjectStore objects, ISharedAcquisition acquisition,
        EncodedSourcePlans sourcePlans, HttpClient client, AcquisitionResources resources, TimeProvider? clock = null)
        : this((IRetentionDriverCatalog)catalog,
            new PhysicalSessions(catalog, objects, acquisition, sourcePlans, client, resources, clock ?? TimeProvider.System), clock) { }

    internal sealed class PhysicalSessions(DiscoveryCatalog catalog, VerifiedObjectStore objects,
        ISharedAcquisition acquisition, EncodedSourcePlans plans, HttpClient client, AcquisitionResources resources,
        TimeProvider clock) : IRetentionSessionFactory
    {
        public async Task<IRetentionSession> OpenAsync(RetentionDriverJob job, Func<AcquisitionProgress, Task> progress, CancellationToken token)
        {
            RetentionService.Validate(job.Plan.Target);
            if (job.Plan.Ledger.Revision != DownloadLedger.Revision || job.Plan.Target.LedgerId != job.Plan.Ledger.Sha256 && job.Plan.Scope != DiscoveryCatalog.DownloadRootScope ||
                !DiscoveryCatalog.KnownDownloadScope(job.Plan.Scope) || job.Plan.PolicyRevision != "retention-encoded-v1" ||
                job.Plan.Configurations.Count != 2 || job.Plan.Configurations.Count(item => item.Role == "buildConfig") != 1 ||
                job.Plan.Configurations.Count(item => item.Role == "cdnConfig") != 1 || job.Plan.DownloadSource.Role != "download" ||
                job.Plan.SourceReference.Region != job.Plan.Target.SourceRegion)
                throw new DiscoveryException("RETENTION_PLAN_INTEGRITY", "The retention plan differs from its exact admitted source, scope or policy.");
            var artifact = new DownloadLedgerArtifact(job.Plan.Ledger.Sha256, job.Plan.Ledger.ByteLength,
                job.Plan.Ledger.ObjectCount, job.Plan.Ledger.TotalEncodedBytes);
            var context = new BackgroundContext(progress);
            await progress(new("Verifying the retained download ledger", "objects", 0, artifact.ObjectCount));
            var ledger = await new DownloadLedger(objects).OpenReadSessionAsync(artifact, token, resources, context);
            try
            {
                var registration = catalog.RegisterRetentionLedgerSession(job.Binding, async (ordinal, key, length, cancellation) =>
                {
                    var member = await ledger.FindAsync(key, length, cancellation);
                    return member is not null && member.Id - 1 == ordinal;
                });
                return new PhysicalSession(catalog, objects, acquisition, plans, client, resources, clock,
                    job, artifact, ledger, registration, context, progress);
            }
            catch { await ledger.DisposeAsync(); throw; }
        }
    }

    private sealed class PhysicalSession(DiscoveryCatalog catalog, VerifiedObjectStore objects,
        ISharedAcquisition acquisition, EncodedSourcePlans plans, HttpClient client, AcquisitionResources resources,
        TimeProvider clock, RetentionDriverJob job, DownloadLedgerArtifact artifact, DownloadLedgerReadSession ledger,
        IAsyncDisposable ledgerRegistration, BackgroundContext context, Func<AcquisitionProgress, Task> progress) : IRetentionSession
    {
        private readonly object gate = new();
        private Task? preparing;
        private EncodedSourcePlanLease? sourceLease;
        private BulkArchiveLocator.BulkArchiveLocatorSession? map;
        private IAsyncDisposable? mapRegistration;
        private bool disposed;
        public Task<IReadOnlyList<DownloadLedgerRow>> ReadAsync(long nextOrdinal, int limit, CancellationToken token)
            => ledger.ReadPageAsync(nextOrdinal, limit, token);

        public async Task<IReadOnlyList<RetentionMemberObservation>> ObserveAsync(IReadOnlyList<DownloadLedgerRow> members, CancellationToken token)
        {
            if (members.Count > MaximumFrontier) throw new ArgumentOutOfRangeException(nameof(members));
            var requests = members.Where(row => row.EncodedBytes is >= 9 and <= VerifiedObjectStore.MaxObjectBytes)
                .Select(row => (row.EncodingKey, row.EncodedBytes)).ToArray();
            var receipts = await catalog.ReadEncodedReceiptsAsync(requests, token);
            var corrupt = await catalog.ReadKnownCorruptAsync(requests, token);
            var result = new RetentionMemberObservation[members.Count];
            // Bounded sequential local stat avoids another unconstrained filesystem scheduler.
            for (var i = 0; i < members.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var member = members[i];
                if (member.EncodedBytes is < 9 or > VerifiedObjectStore.MaxObjectBytes)
                {
                    result[i] = new(member.Id - 1, member.EncodingKey, member.EncodedBytes, RetentionPresence.Unknown, false,
                        new("RETENTION_OBJECT_UNSUPPORTED", "This declared encoded object is outside the supported per-object retention length of 9 through 536870912 bytes.", false));
                    continue;
                }
                if (!receipts.TryGetValue(member.EncodingKey, out var receipt))
                { result[i] = new(member.Id - 1, member.EncodingKey, member.EncodedBytes, RetentionPresence.Unknown, false); continue; }
                if (corrupt.Contains(member.EncodingKey))
                { result[i] = new(member.Id - 1, member.EncodingKey, member.EncodedBytes, RetentionPresence.Corrupt, true); continue; }
                var stat = await objects.ObservePresenceAsync(receipt.Sha256, receipt.EncodedBytes, token);
                var presence = stat.Kind switch
                {
                    ObjectPresenceKind.Present => RetentionPresence.Present,
                    ObjectPresenceKind.Absent => RetentionPresence.Missing,
                    ObjectPresenceKind.WrongLength => RetentionPresence.Corrupt,
                    _ => RetentionPresence.Unknown
                };
                if (presence == RetentionPresence.Corrupt)
                    await catalog.RecordEncodedInvalidityAsync(member.EncodingKey, member.EncodedBytes, receipt.Sha256, "LOCAL_OBJECT_CORRUPT", token);
                result[i] = new(member.Id - 1, member.EncodingKey, member.EncodedBytes, presence, true,
                    presence == RetentionPresence.Unknown
                        ? new("RETENTION_PRESENCE_UNKNOWN", "Current local presence could not be assessed without ambiguity.", true) : null);
            }
            return result;
        }

        public async Task<IAcquisitionSubscription> AttachAsync(DownloadLedgerRow member,
            Func<AcquisitionProgress, Task> memberProgress, CancellationToken token)
        {
            Task preparation;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                preparation = preparing ??= PrepareAsync(token);
            }
            // Every bulk caller uses this job token. Await the preparation's own retirement,
            // rather than canceling only an observer while map/staging cleanup still runs.
            await preparation;
            token.ThrowIfCancellationRequested();
            return acquisition.Attach(new(member.EncodingKey, member.EncodedBytes, VerifierRevision), sourceLease!.Plan.Source,
                AcquisitionPriority.Background, memberProgress);
        }

        private async Task PrepareAsync(CancellationToken token)
        {
            var cdn = job.Plan.Configurations.Single(item => item.Role == "cdnConfig");
            var source = new EncodedSourceContext(job.Plan.Target.ProductId, job.Plan.Target.BuildIdentity,
                job.Plan.Target.SourceRegion, cdn.Sha256, job.Plan.SourceReference);
            await progress(new("Preparing the pinned encoded source plan", "sources"));
            sourceLease = await plans.AcquireAsync(source, token, context);
            var plan = sourceLease.Plan;
            var locator = new BulkArchiveLocator(objects, new OfficialSourceTransport(client, resources, context), resources, context, clock: clock);
            var descriptor = job.Map;
            if (descriptor is not null)
            {
                try { map = await locator.OpenAsync(descriptor, plan.Sha256, plan.ArchiveKeys, artifact, token); }
                catch (DiscoveryException failure) when (failure.Code is "LOCAL_OBJECT_MISSING" or "LOCAL_OBJECT_CORRUPT")
                { descriptor = null; }
            }
            if (map is null)
            {
                DiscoveryException? last = null;
                foreach (var route in plan.Routes)
                {
                    try
                    {
                        descriptor = await locator.BuildAsync(route, plan.Sha256, plan.ArchiveKeys, artifact,
                            stage => progress(new(stage, "archive metadata")), token,
                            openLedger: _ => Task.FromResult(ledger.OpenOrderedCursor()),
                            checkpoints: new(catalog.FindRetentionNativeSourceAsync, catalog.RecordRetentionNativeSourceAsync));
                        break;
                    }
                    catch (DiscoveryException failure) when (failure.Retryable) { last = failure; }
                }
                if (descriptor is null) throw last ?? new DiscoveryException("RETENTION_LOCATOR_UNAVAILABLE", "No admitted official route could prepare the archive locator.", true);
            }
            // Reaffirm the current generation for both new and retained maps before payload work.
            if (!await catalog.RegisterRetentionMapAsync(job.Binding, descriptor!, token))
                throw new DiscoveryException("RETENTION_STALE", "The retention intent changed during locator preparation.", true);
            map ??= await locator.OpenAsync(descriptor!, plan.Sha256, plan.ArchiveKeys, artifact, token);
            var heldMap = map;
            mapRegistration = plans.RegisterMap(sourceLease, async (key, length, cancellation) =>
            {
                var member = await ledger.FindAsync(key, length, cancellation);
                return member is null ? new(false, []) : new EncodedMapLookup(true, await heldMap.FindAsync(key, length, cancellation));
            });
        }

        public async ValueTask DisposeAsync()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            // Registration withdrawal drains callbacks before either shared stream closes.
            try { if (mapRegistration is not null) await mapRegistration.DisposeAsync(); }
            finally
            {
                try { if (map is not null) await map.DisposeAsync(); }
                finally
                {
                    try { if (sourceLease is not null) await sourceLease.DisposeAsync(); }
                    finally { try { await ledgerRegistration.DisposeAsync(); } finally { await ledger.DisposeAsync(); } }
                }
            }
        }
    }

    private sealed class BackgroundContext(Func<AcquisitionProgress, Task> report) : IAcquisitionWorkContext
    {
        public AcquisitionWorkSnapshot Snapshot => new(1, AcquisitionPriority.Background, []);
        public Task WaitForChangeAsync(long observedRevision, CancellationToken token = default) => Task.Delay(Timeout.Infinite, token);
        public Task ReportAsync(AcquisitionProgress progress) => report(progress);
    }
}
