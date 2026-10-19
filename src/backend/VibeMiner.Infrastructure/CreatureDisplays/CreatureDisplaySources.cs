using System.Globalization;
using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>At most two physical target readers, including opens and failed cleanup. Idle readers are
/// scoped to one exact opening; replacement retires old readers before reserving new ones.</summary>
internal sealed class CreatureDisplaySources(DiscoveryCatalog catalog, VerifiedObjectStore objects, CreaturesStore creatures,
    TableDataService tables, AcquisitionResources resources) : ICreatureDisplaySources
{
    private readonly Func<string, long, CancellationToken, Task<FileStream>> openEncodedSource = (sha, length, token) => objects.OpenVerifiedAsync(sha, length, resources, null, token);
    internal CreatureDisplaySources(DiscoveryCatalog catalog, VerifiedObjectStore objects, CreaturesStore creatures,
        TableDataService tables, AcquisitionResources resources, Func<string, long, CancellationToken, Task<FileStream>> openEncodedSource)
        : this(catalog, objects, creatures, tables, resources) { this.openEncodedSource = openEncodedSource; }
    private readonly object gate = new();
    private readonly List<Slot> slots = new(2);
    private readonly CancellationTokenSource shutdown = new();
    private bool closed;
    private sealed class Slot(CreaturesContext context, OpeningSourceReference opening, string table, string artifact)
    {
        internal readonly CreaturesContext Context = context;
        internal readonly OpeningSourceReference Opening = opening;
        internal readonly string Table = table, Artifact = artifact;
        internal readonly TaskCompletionSource<TableRowArtifacts> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly CancellationTokenSource Cancellation = new();
        internal int Borrows;
        internal bool Finished, Retiring, ProducerFinished;
        internal Task? Disposal;
        internal TaskCompletionSource? Notification;
    }
    private sealed class Borrow(CreatureDisplaySources owner, Slot slot, TableRowArtifacts reader) : IAsyncDisposable
    {
        internal readonly TableRowArtifacts Reader = reader;
        internal void Invalidate() { lock (owner.gate) slot.Retiring = true; }
        public ValueTask DisposeAsync() => new(owner.ReleaseAsync(slot));
    }
    internal (int Readers, int Opening, int Borrowed) Pressure
    { get { lock (gate) return (slots.Count, slots.Count(s => !s.Finished), slots.Sum(s => s.Borrows)); } }
    public Task<CreaturesDisplayAuthorization> AuthorizeAsync(CreatureDisplayMetadataRequest request, CancellationToken token) =>
        creatures.AuthorizeDisplayAsync(request.Context, request.ArtifactId, request.CreatureId, token);

    public async Task<CreatureDisplayProbe?> ProbeAsync(CreaturesDisplayAuthorization core, CreatureDisplayTargetRole role, string id, CancellationToken token)
    {
        lock (gate)
        {
            if (closed) throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true);
            if (slots.Any(s => (s.Context != core.Snapshot.Context || s.Opening != core.Opening.Reference || s.Table == CreatureDisplayPolicy.Table(role) && s.Retiring) &&
                (s.Borrows != 0 || !s.Finished || s.Retiring))) throw CreatureDisplayPolicy.Busy();
        }
        var admission = await catalog.ReadCreatureDisplayTargetAsync(core.Opening, CreatureDisplayPolicy.Table(role), token);
        if (admission is null) return null;
        var provider = await TablesCatalog.Provider.Value.WaitAsync(token);
        if (!provider.Definitions.TryGetValue(CreatureDisplayPolicy.Table(role), out var definition)) throw CreatureDisplayPolicy.Unsupported();
        var snapshot = Snapshot(core.Snapshot.Context, admission, role, definition);
        var binding = new CreatureDisplayCatalogBinding(CreatureDisplayPolicy.Table(role), admission);
        int column;
        try { column = ValidateProfile(core, admission, role, definition); }
        catch (DiscoveryException error) when (error.Code == "CREATURE_DISPLAY_STALE")
        {
            // An initially retained historical interpretation is not a delivered dependency.
            // The shared Tables prerequisite can re-admit its verified bytes under current policy.
            // Once delivered, FinishAsync instead fences that exact captured source and reports stale.
            return null;
        }
        catch (DiscoveryException error) when (error.Code == "CREATURE_DISPLAY_SCHEMA_UNSUPPORTED")
        {
            return new(new(role, snapshot, null), binding, null, CreatureDisplayMetadataState.Unsupported,
                CreatureDisplayPolicy.Problem(error, role));
        }
        await ValidateConfigurationAssociationAsync(core, admission, token);
        await using var borrow = await AcquireAsync(core, admission, role, token);
        try
        {
            TableArtifactPage page;
            page = await borrow.Reader.ReadPageAsync(admission.Artifact, 0, 1, uint.Parse(id, CultureInfo.InvariantCulture), token);
            if (page.TotalCount < 0 || page.TotalCount > TableRowArtifacts.MaxRows || page.Rows.Count != (page.TotalCount == 0 ? 0 : 1)) throw CreatureDisplayPolicy.Invalid();
            var count = CreatureDisplayPolicy.N(page.TotalCount);
            if (page.TotalCount != 1) return new(new(role, snapshot, count), binding, null);
            var row = page.Rows[0]; var edge = CreatureDisplayPolicy.Edge(row, column, role, id);
            var evidence = new CreatureDisplayRowEvidence(new(snapshot.TableId, snapshot.ArtifactId, id, column), row.IsCopy,
                row.CopySourceId?.ToString(CultureInfo.InvariantCulture), new(snapshot.TableId, column, edge));
            return new(new(role, snapshot, count, evidence), binding, edge);
        }
        catch { borrow.Invalidate(); throw; }
    }
    public async Task EnsureAsync(CreaturesContext context, CreatureDisplayTargetRole role,
        Func<CreaturesPreparationProgress, Task> progress, CancellationToken token)
    {
        var table = CreatureDisplayPolicy.Table(role);
        var request = new TableDataRequest(context.ProductId, context.BuildIdentity, context.SourceRegion, table, context.ResearchLocale);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                await tables.PrepareAsync(request, stage => progress(new(stage, "stage", TableId: table)), token);
                return;
            }
            catch (DiscoveryException error) when (error.Code == "TABLE_DATA_BUSY")
            {
                await progress(new("Waiting for shared table preparation", "stage", TableId: table));
                await tables.WaitForAvailabilityAsync(token);
            }
        }
        throw new DiscoveryException("TABLE_DATA_BUSY", "The shared table prerequisite could not acquire bounded ownership after eight availability changes.", true);
    }
    public async Task FinishAsync(CreatureDisplayMetadataRequest request, CreaturesDisplayAuthorization core,
        IReadOnlyList<CreatureDisplayProbe> delivered, CancellationToken token)
    {
        var current = await creatures.AuthorizeDisplayAsync(request.Context, request.ArtifactId, request.CreatureId, token);
        CreatureDisplayPolicy.SameCore(core, current, request.Slot);
        _ = CreatureDisplayPolicy.Select(current, request);
        var provider = await TablesCatalog.Provider.Value.WaitAsync(token);
        foreach (var target in delivered)
        {
            if (!provider.Definitions.TryGetValue(target.Binding.TableId, out var definition)) throw CreatureDisplayPolicy.Stale();
            // Unsupported delivered schemas retain their source authority; only current interpretation
            // identity is required, since no semantic edge was admitted for that source.
            ValidateCurrent(core, target.Binding.Admission, target.Target.Role, definition);
            await ValidateConfigurationAssociationAsync(core, target.Binding.Admission, token);
            if (target.Failure != CreatureDisplayMetadataState.Unsupported)
                _ = ValidateProfile(core, target.Binding.Admission, target.Target.Role, definition);
        }
        await catalog.ValidateCreatureDisplayCurrentAsync(core.Opening, core.Artifact, delivered.Select(p => p.Binding).ToArray(), token);
    }
    private async Task ValidateConfigurationAssociationAsync(CreaturesDisplayAuthorization core, StoredTableAdmission target, CancellationToken token)
    {
        var configuration = core.Artifact.Binding.Configurations.Single(c => c.Role == "buildConfig");
        if (configuration.Key != core.Opening.Build.BuildConfig || configuration.ByteLength is < 1 or > 1024 * 1024) throw CreaturesPolicy.Stale();
        var bytes = await objects.ReadAsync(configuration.Sha256, configuration.ByteLength, token);
        using (await resources.EnterCpuAsync(null, token))
        {
            if (Convert.ToHexStringLower(MD5.HashData(bytes)) != configuration.Key) throw CreaturesPolicy.Stale();
            TableDataStore.ValidateSourceAssociation(TactConfigurationParser.Parse(bytes, token), target.Association);
        }
    }
    private static void ValidateCurrent(CreaturesDisplayAuthorization core, StoredTableAdmission admission,
        CreatureDisplayTargetRole role, BundledTableDefinition definition)
    {
        var rows = admission.Artifact;
        if (definition.Sha256 != CreatureDisplayPolicy.Definition(role) || rows.Binding.DefinitionSha256 != definition.Sha256 ||
            rows.Binding.ProviderRevision != BundledTableDefinitions.Revision ||
            rows.Binding.DecoderRevision != (admission.Source.Projection is null ? TableDataStore.DecoderRevision : TableDataStore.ProjectionDecoderRevision) ||
            rows.Binding.SchemaSelectionSha256 != TableRowArtifacts.SchemaSelectionKey(definition.Document, core.Opening.Build.Version) ||
            admission.Association.FileDataId != CreatureDisplayPolicy.File(role)) throw CreatureDisplayPolicy.Stale();
    }
    private static int ValidateProfile(CreaturesDisplayAuthorization core, StoredTableAdmission admission,
        CreatureDisplayTargetRole role, BundledTableDefinition definition)
    {
        ValidateCurrent(core, admission, role, definition);
        return CreatureDisplayPolicy.SelectField(definition.Document, core.Opening.Build.Version, admission.Artifact, role);
    }
    private static TableDataSnapshot Snapshot(CreaturesContext context, StoredTableAdmission admission,
        CreatureDisplayTargetRole role, BundledTableDefinition definition)
    {
        var artifact = admission.Artifact; var c = artifact.Completion;
        return new(context.ProductId, context.BuildIdentity, context.SourceRegion, CreatureDisplayPolicy.Table(role), context.ResearchLocale,
            artifact.ContentKey, admission.PreparedAt, artifact.Header.LayoutHash.ToUpperInvariant(), artifact.Binding.DefinitionSha256,
            artifact.Binding.ProviderRevision, artifact.Binding.DecoderRevision, admission.Source, admission.Association,
            Array.AsReadOnly(artifact.Header.Columns.Select(v => TableDataStore.ProjectColumn(v, definition.Document)).ToArray()),
            new(c.Scope, CreatureDisplayPolicy.N(c.DeclaredRecords), CreatureDisplayPolicy.N(c.PhysicalRows), CreatureDisplayPolicy.N(c.CopyRows),
                CreatureDisplayPolicy.N(c.ExcludedEncryptedSections), CreatureDisplayPolicy.N(c.ExcludedPhysicalRecords + c.ExcludedCopyRecords),
                CreatureDisplayPolicy.N(artifact.Header.DeclaredCopyRecords), CreatureDisplayPolicy.N(c.ExcludedPhysicalRecords), CreatureDisplayPolicy.N(c.ExcludedCopyRecords)),
            CreatureDisplayPolicy.N(c.PhysicalRows + c.CopyRows), CreatureDisplayPolicy.N(c.TextIssueCount));
    }
    private async Task<Borrow> AcquireAsync(CreaturesDisplayAuthorization core, StoredTableAdmission admission,
        CreatureDisplayTargetRole role, CancellationToken token)
    {
        Slot slot; bool start;
        while (true)
        {
            Slot[] evict;
            lock (gate)
            {
                if (closed) throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true);
                var incompatible = slots.Where(s => s.Context != core.Snapshot.Context || s.Opening != core.Opening.Reference ||
                    s.Table == CreatureDisplayPolicy.Table(role) && (s.Artifact != admission.Artifact.ContentKey || s.Retiring)).ToArray();
                if (incompatible.Any(s => s.Borrows != 0 || !s.Finished || s.Retiring)) throw CreatureDisplayPolicy.Busy();
                evict = incompatible;
                if (evict.Length == 0)
                {
                    var existing = slots.FirstOrDefault(s => s.Table == CreatureDisplayPolicy.Table(role) && s.Artifact == admission.Artifact.ContentKey && !s.Retiring);
                    if (existing is not null) { slot = existing; slot.Borrows++; start = false; break; }
                    if (slots.Count >= 2) throw CreatureDisplayPolicy.Busy();
                    slot = new(core.Snapshot.Context, core.Opening.Reference, CreatureDisplayPolicy.Table(role), admission.Artifact.ContentKey) { Borrows = 1 };
                    slots.Add(slot); start = true; break;
                }
                foreach (var old in evict) old.Retiring = true;
            }
            // Never reserve a replacement while an old physical source is still opening/closing.
            foreach (var old in evict) await DisposeSlotAsync(old);
        }
        if (start) _ = OpenAsync(slot, admission);
        try { return new(this, slot, await slot.Ready.Task.WaitAsync(token)); }
        catch { await ReleaseAsync(slot); throw; }
    }
    private async Task OpenAsync(Slot slot, StoredTableAdmission admission)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(slot.Cancellation.Token, shutdown.Token);
        TableRowArtifacts? reader = null; FileStream? encoded = null; Exception? debt = null;
        try
        {
            // Encoded source integrity is checked before the row reader opens; the reserved target
            // credit covers both phases, with at most one physical file held in each phase.
            encoded = await openEncodedSource(admission.Source.Sha256,
                long.Parse(admission.Source.EncodedBytes, CultureInfo.InvariantCulture), lifetime.Token);
            try { await encoded.DisposeAsync(); encoded = null; }
            catch (Exception close) { debt = close; throw; }
            reader = new(objects, resources, null);
            await reader.ReadPageAsync(admission.Artifact, 0, 1, null, lifetime.Token);
            await RetireProducerAsync(slot);
            lock (gate) { slot.Finished = true; slot.Ready.TrySetResult(reader); reader = null; }
        }
        catch (Exception error)
        {
            if (reader is not null) try { await reader.DisposeAsync(); } catch (Exception close) { debt = close; }
            if (encoded is not null) try { await encoded.DisposeAsync(); } catch (Exception close) { debt ??= close; }
            await RetireProducerAsync(slot);
            lock (gate)
            {
                slot.Finished = true;
                if (debt is null) { slots.Remove(slot); slot.Cancellation.Dispose(); }
                else { slot.Retiring = true; slot.Disposal = Task.FromException(Cleanup()); }
                slot.Ready.TrySetException(debt is null ? error : Cleanup());
            }
        }
    }
    private async Task ReleaseAsync(Slot slot)
    {
        bool cancel, dispose;
        lock (gate)
        {
            slot.Borrows--; cancel = slot.Borrows == 0 && !slot.Finished;
            if (cancel) slot.Retiring = true;
            dispose = slot.Borrows == 0 && slot.Finished && (closed || slot.Retiring);
        }
        if (cancel)
        {
            await RequestCancellationAsync(slot);
            try { await slot.Ready.Task; } catch (Exception) { }
            lock (gate) dispose = slot.Ready.Task.IsCompletedSuccessfully;
        }
        if (dispose) await DisposeSlotAsync(slot);
    }
    private Task RequestCancellationAsync(Slot slot)
    {
        TaskCompletionSource notification; bool start;
        lock (gate)
        {
            if (slot.ProducerFinished) return Task.CompletedTask;
            start = slot.Notification is null; notification = slot.Notification ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (start) _ = NotifyAsync();
        return notification.Task;
        async Task NotifyAsync()
        { try { await slot.Cancellation.CancelAsync(); } catch (AggregateException) { } finally { notification.TrySetResult(); } }
    }
    private async Task RetireProducerAsync(Slot slot)
    {
        Task notification;
        lock (gate) { slot.ProducerFinished = true; notification = slot.Notification?.Task ?? Task.CompletedTask; }
        await notification;
    }
    private async Task DisposeSlotAsync(Slot slot)
    {
        TaskCompletionSource? completion = null; Task retirement;
        lock (gate)
        {
            slot.Retiring = true;
            if (slot.Disposal is null) { completion = new(TaskCreationOptions.RunContinuationsAsynchronously); slot.Disposal = completion.Task; }
            retirement = slot.Disposal;
        }
        if (completion is not null)
        {
            try
            {
                var reader = await slot.Ready.Task; await reader.DisposeAsync();
                lock (gate) { slots.Remove(slot); slot.Cancellation.Dispose(); }
                completion.TrySetResult();
            }
            catch (Exception) { completion.TrySetException(Cleanup()); }
        }
        await retirement;
    }
    private static DiscoveryException Cleanup() => new("CREATURE_DISPLAY_CLEANUP_FAILED", "Target source cleanup failed; its reader reservation remains charged.");
    public async ValueTask DisposeAsync()
    {
        Slot[] retiring;
        lock (gate) { closed = true; retiring = slots.ToArray(); foreach (var slot in retiring) slot.Retiring = true; }
        try { await shutdown.CancelAsync(); } catch (AggregateException) { }
        foreach (var slot in retiring) await RequestCancellationAsync(slot);
        Exception? debt = null;
        foreach (var slot in retiring)
        {
            try { await slot.Ready.Task; } catch (Exception error) { debt ??= error; }
            if (slot.Ready.Task.IsCompletedSuccessfully) try { await DisposeSlotAsync(slot); } catch (Exception error) { debt ??= error; }
            else if (slot.Disposal is { } failedDisposal) try { await failedDisposal; } catch (Exception error) { debt ??= error; }
        }
        if (debt is not null && retiring.Any(s => s.Disposal?.IsFaulted == true)) throw Cleanup();
    }
}
