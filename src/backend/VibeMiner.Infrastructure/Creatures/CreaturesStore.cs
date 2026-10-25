using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed record CreaturesDisplayAuthorization(CapturedOpeningContext Opening, CreaturesArtifact Artifact,
    CreaturesSnapshot Snapshot, CreatureSummary? Summary);

/// <summary>Creature-only prerequisites, two fully accounted core sessions and independent bounded queries.</summary>
public sealed partial class CreaturesStore(DiscoveryCatalog catalog, VerifiedObjectStore objects, TableDataService tables,
    AcquisitionResources resources, TableDefinitionSources? definitionSources = null) : ICreaturesStore, IAsyncDisposable
{
    private readonly TableDefinitionSources definitions = definitionSources ?? new();
    /// <summary>The definition choice Creatures uses; creature displays choose the same way.</summary>
    internal TableDefinitionSources Definitions => definitions;
    private readonly Func<string, long, CancellationToken, Task<FileStream>> openEncodedSource = (sha, length, token) => objects.OpenVerifiedAsync(sha, length, resources, null, token);
    internal CreaturesStore(DiscoveryCatalog catalog, VerifiedObjectStore objects, TableDataService tables, AcquisitionResources resources,
        Func<string, long, CancellationToken, Task<FileStream>> openEncodedSource, TableDefinitionSources? definitionSources = null)
        : this(catalog, objects, tables, resources, definitionSources)
    { this.openEncodedSource = openEncodedSource; }
    public const string FeatureRevision = CreaturesPolicy.FeatureRevision, QueryPolicy = CreaturesPolicy.QueryPolicy, SemanticProfile = CreaturesPolicy.SemanticProfile;
    private readonly CreaturesProjection projection = new(objects, resources);
    private readonly object sync = new();
    private readonly List<Slot> slots = new(2);
    private readonly CancellationTokenSource shutdown = new();
    private bool closed; private int operations, queries, preparations;
    private TaskCompletionSource? retired;
    internal (int Sessions, int Opening, int Borrowed, int Queries) Pressure
    { get { lock (sync) return (slots.Count, slots.Count(s => !s.Finished), slots.Sum(s => s.References), queries); } }
    private sealed class Slot(CreaturesContext context, string key)
    {
        internal readonly CreaturesContext Context = context; internal readonly string Key = key;
        internal readonly TaskCompletionSource<Held> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly CancellationTokenSource Cancellation = new();
        internal int References; internal bool Retiring, Finished, ProducerFinished; internal Task? Disposal; internal TaskCompletionSource? CancellationCompleted;
    }
    private sealed class Held(FileStream encoded, TableRowArtifacts rows, CreaturesProjection.Session projection) : IAsyncDisposable
    {
        internal readonly TableRowArtifacts Rows = rows;
        internal readonly CreaturesProjection.Session Projection = projection;
        public async ValueTask DisposeAsync()
        {
            Exception? error = null;
            try { await Projection.DisposeAsync(); } catch (Exception failure) { error = failure; }
            try { await Rows.DisposeAsync(); } catch (Exception failure) { error ??= failure; }
            try { await encoded.DisposeAsync(); } catch (Exception failure) { error ??= failure; }
            if (error is not null) throw new DiscoveryException("CREATURES_CLEANUP_FAILED", "Creature session cleanup failed; its reserved capacity remains charged.");
        }
    }
    private sealed class Borrow(CreaturesStore owner, Slot slot, Held held) : IAsyncDisposable
    {
        internal readonly Held Held = held;
        internal void Invalidate() { lock (owner.sync) slot.Retiring = true; }
        public ValueTask DisposeAsync() => new(owner.ReleaseAsync(slot));
    }
    private sealed class Operation(CreaturesStore owner, bool prepare, CancellationTokenSource lifetime) : IDisposable
    {
        internal CancellationToken Token => lifetime.Token;
        public void Dispose()
        {
            lifetime.Dispose(); lock (owner.sync)
            { if (prepare) owner.preparations--; else owner.queries--; if (--owner.operations == 0) owner.retired!.TrySetResult(); }
        }
    }
    private Operation Enter(bool prepare, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); lock (sync)
        {
            if (closed) throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true);
            if (prepare ? preparations != 0 : queries >= 4) throw new DiscoveryException(prepare ? "CREATURES_BUSY" : "CREATURES_QUERY_BUSY", "Creature work is at its bounded capacity or retiring.", true);
            if (prepare) preparations++; else queries++; if (operations++ == 0) retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new(this, prepare, CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token));
        }
    }
    public async Task<CreaturesReadiness> ReadAsync(CreaturesContext context, CancellationToken token)
    {
        CreaturesService.Validate(context); using var operation = Enter(false, token); token = operation.Token;
        var opening = await catalog.ResolveOpeningAsync(Workspace(context), token);
        // The same start as Tables (D101): with fetching off, a bundled definition must open the build.
        await definitions.RequireFeatureBuildAsync(CreaturesPolicy.Table, "Creatures", opening.Build.Version, token);
        var saved = await SavedAsync(opening, token);
        if (saved is not null)
        {
            await ValidateProfileAsync(saved.Artifact.Binding, token);
            await using var borrow = await AcquireAsync(context, saved.Artifact, saved.PreparedAt, opening, false, null, token);
            try
            {
                var result = new CreaturesReadiness(borrow.Held.Projection.Snapshot, CreatureDependencyState.Ready, saved.Artifact.Binding.Source.ArtifactId);
                await CreaturesResponseCounter.RequireAsync(result, token); await ValidateProfileAsync(saved.Artifact.Binding, token);
                await catalog.ReauthorizeCreaturesAsync(opening, saved.Artifact, token); return result;
            }
            catch { borrow.Invalidate(); throw; }
        }
        // Guard persisted source JSON before entering the shared Tables readiness path.
        var source = await catalog.ReadCreatureSourceAsync(opening, token);
        if (source is null) return new(null, CreatureDependencyState.NotPrepared);
        var ready = (await tables.ReadAsync(Table(context), token)).Snapshot;
        if (ready is not null) await catalog.ReauthorizeCreatureDependencyAsync(opening, ready, token);
        return new(null, ready is null ? CreatureDependencyState.NotPrepared : CreatureDependencyState.Ready, ready?.ArtifactId);
    }
    public async Task<CreaturesSnapshot> PrepareAsync(CreaturesContext context, Func<CreaturesPreparationProgress, Task> progress, CancellationToken token)
    {
        CreaturesService.Validate(context); using var operation = Enter(true, token); token = operation.Token;
        var opening = await catalog.ResolveOpeningAsync(Workspace(context), token); var saved = await SavedAsync(opening, token);
        if (saved is not null)
        {
            await ValidateProfileAsync(saved.Artifact.Binding, token);
            await using var ready = await AcquireAsync(context, saved.Artifact, saved.PreparedAt, opening, false, progress, token);
            try
            {
                await CreaturesResponseCounter.RequireAsync(ready.Held.Projection.Snapshot, token); await ValidateProfileAsync(saved.Artifact.Binding, token);
                await catalog.ReauthorizeCreaturesAsync(opening, saved.Artifact, token); return ready.Held.Projection.Snapshot;
            }
            catch { ready.Invalidate(); throw; }
        }
        await catalog.ReadCreatureSourceAsync(opening, token); // Guard before shared prerequisite work.
        var originalConfigurations = await catalog.ReadCreatureConfigurationsAsync(opening, token);
        // Creatures chooses its definition as Tables does (D101): fail before any download when nothing can open the build.
        await definitions.RequireFeatureBuildAsync(CreaturesPolicy.Table, "Creatures", opening.Build.Version, token);
        await PrerequisiteAsync(context, progress, token); // No feature slot, reader or registry lock is held here.
        var currentOpening = await catalog.ResolveOpeningAsync(Workspace(context), token);
        if (currentOpening.Reference != opening.Reference) throw CreaturesPolicy.Stale();
        var admission = await catalog.ReadCreatureSourceAsync(opening, token) ?? throw CreaturesPolicy.Stale();
        var configurations = await catalog.ReadCreatureConfigurationsAsync(opening, token);
        if (!originalConfigurations.Select(c => c with { VerifiedAt = "" }).OrderBy(c => c.Role).SequenceEqual(configurations.Select(c => c with { VerifiedAt = "" }).OrderBy(c => c.Role))) throw CreaturesPolicy.Stale();
        var binding = await BindingAsync(context, opening, admission, configurations, token);
        var artifact = await catalog.FindCreaturesArtifactAsync(CreaturesPolicy.ContentKey(binding), token);
        var at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        if (artifact is null) artifact = new(CreaturesPolicy.ContentKey(binding), new('0', 64), 64, binding, new("0", "0", "0", "0", "0", "0", "0"));
        await using var prepared = await AcquireAsync(context, artifact, at, opening, true, progress, token);
        try
        {
            var snapshot = prepared.Held.Projection.Snapshot;
            await CreaturesResponseCounter.RequireAsync(snapshot, token);
            await ValidateProfileAsync(prepared.Held.Projection.Artifact.Binding, token);
            await catalog.CommitCreaturesAdmissionAsync(opening, prepared.Held.Projection.Artifact, snapshot.PreparedAt, token);
            // One durable admission is the success point; committed success wins cancellation.
            return snapshot;
        }
        catch (DiscoveryException error) when (error.Code is "CREATURES_STALE" or "CREATURES_PROJECTION_INVALID" or "CREATURES_SOURCE_RECEIPT_MISMATCH") { prepared.Invalidate(); throw; }
    }
    public async Task<CreaturesPage> ReadPageAsync(CreaturesPageRequest request, CancellationToken token)
    {
        CreaturesService.Validate(request); using var operation = Enter(false, token); token = operation.Token;
        var (opening, saved) = await RequireAsync(request.Context, request.ArtifactId, token);
        await using var borrow = await AcquireAsync(request.Context, saved.Artifact, saved.PreparedAt, opening, false, null, token);
        try
        {
            var page = await borrow.Held.Projection.ReadPageAsync(request, token);
            await CreaturesResponseCounter.RequireAsync(page, token);
            await ValidateProfileAsync(saved.Artifact.Binding, token); await catalog.ReauthorizeCreaturesAsync(opening, saved.Artifact, token); return page;
        }
        catch (DiscoveryException error) when (error.Code is "CREATURES_STALE" or "CREATURES_PROJECTION_INVALID" or "CREATURES_SOURCE_RECEIPT_MISMATCH") { borrow.Invalidate(); throw; }
    }
    public async Task<CreatureInspectResult> InspectAsync(CreatureInspectRequest request, CancellationToken token)
    {
        CreaturesService.Validate(request); using var operation = Enter(false, token); token = operation.Token;
        var (opening, saved) = await RequireAsync(request.Context, request.ArtifactId, token);
        await using var borrow = await AcquireAsync(request.Context, saved.Artifact, saved.PreparedAt, opening, false, null, token);
        try
        {
            var summary = await borrow.Held.Projection.FindAsync(uint.Parse(request.CreatureId, CultureInfo.InvariantCulture), token); CreatureDetail? detail = null;
            if (summary is not null)
            {
                TableArtifactPage page;
                page = await borrow.Held.Rows.ReadPageAsync(saved.Artifact.Binding.Rows, 0, 1, uint.Parse(request.CreatureId, CultureInfo.InvariantCulture), token);
                if (page.Rows.Count != 1) throw CreaturesPolicy.Invalid(); var row = page.Rows[0];
                var original = new TableDataRow(row.Id.ToString(CultureInfo.InvariantCulture), row.IsCopy, row.CopySourceId?.ToString(CultureInfo.InvariantCulture), row.Values,
                    row.TextIssues?.Select(i => new TableTextIssue(i.ColumnIndex, i.Code, i.RawBytesHex)).ToArray());
                await borrow.Held.Projection.VerifyOriginalAsync(summary, original, token);
                detail = new(summary, original, new(CreaturesPolicy.Table, saved.Artifact.Binding.Source.ArtifactId, request.CreatureId));
            }
            var result = new CreatureInspectResult(request.Context, request.ArtifactId, request.CreatureId, detail is null ? CreatureInspectState.NotFoundInAdmittedScope : CreatureInspectState.Found, saved.Artifact.Binding.Source.Coverage, detail);
            await CreaturesResponseCounter.RequireAsync(result, token);
            await ValidateProfileAsync(saved.Artifact.Binding, token); await catalog.ReauthorizeCreaturesAsync(opening, saved.Artifact, token);
            return result;
        }
        catch (DiscoveryException error) when (error.Code is "CREATURES_STALE" or "CREATURES_PROJECTION_INVALID" or "CREATURES_SOURCE_RECEIPT_MISMATCH") { borrow.Invalidate(); throw; }
    }
    // C2 may authorize a declared slot from this copied compact proof, then release all core
    // ownership before optional prerequisites. This helper performs no target work or original-row replay.
    internal async Task<CreaturesDisplayAuthorization> AuthorizeDisplayAsync(CreaturesContext context, string artifactId, string creatureId, CancellationToken token)
    {
        CreaturesService.Validate(new CreatureInspectRequest(context, artifactId, creatureId));
        using var operation = Enter(false, token); token = operation.Token;
        var (opening, saved) = await RequireAsync(context, artifactId, token);
        await using var borrow = await AcquireAsync(context, saved.Artifact, saved.PreparedAt, opening, false, null, token);
        try
        {
            var summary = await borrow.Held.Projection.FindAsync(uint.Parse(creatureId, CultureInfo.InvariantCulture), token);
            if (summary is not null) summary = summary with { Facts = Array.AsReadOnly(summary.Facts.ToArray()), DisplaySlots = Array.AsReadOnly(summary.DisplaySlots.ToArray()) };
            await ValidateProfileAsync(saved.Artifact.Binding, token);
            await catalog.ReauthorizeCreaturesAsync(opening, saved.Artifact, token);
            return new(opening, saved.Artifact, borrow.Held.Projection.Snapshot, summary);
        }
        catch (DiscoveryException error) when (error.Code is "CREATURES_STALE" or "CREATURES_PROJECTION_INVALID" or "CREATURES_SOURCE_RECEIPT_MISMATCH") { borrow.Invalidate(); throw; }
    }
    private async Task<(CapturedOpeningContext Opening, StoredCreaturesAdmission Saved)> RequireAsync(CreaturesContext context, string key, CancellationToken token)
    {
        var opening = await catalog.ResolveOpeningAsync(Workspace(context), token); var saved = await SavedAsync(opening, token);
        if (saved is null) throw new DiscoveryException("CREATURES_NOT_PREPARED", "Prepare the current Creature source before browsing.", true);
        if (saved.Artifact.ContentKey != key) throw CreaturesPolicy.Stale(); await ValidateProfileAsync(saved.Artifact.Binding, token); return (opening, saved);
    }
    private async Task<StoredCreaturesAdmission?> SavedAsync(CapturedOpeningContext opening, CancellationToken token)
    {
        try
        {
            var saved = await catalog.ReadCreaturesAdmissionAsync(opening, token);
            if (saved is not null) await ValidateProfileAsync(saved.Artifact.Binding, token);
            return saved;
        }
        catch (DiscoveryException error) when (error.Code == "CREATURES_STALE") { return null; }
    }
    private async Task<Borrow> AcquireAsync(CreaturesContext context, CreaturesArtifact artifact, string at, CapturedOpeningContext opening, bool admit,
        Func<CreaturesPreparationProgress, Task>? progress, CancellationToken token)
    {
        Slot slot; bool start;
        while (true)
        {
            Slot? evict = null;
            lock (sync)
            {
                if (closed) throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true);
                var existing = slots.FirstOrDefault(s => s.Context == context && s.Key == artifact.ContentKey && !s.Retiring);
                if (existing is not null) { slot = existing; slot.References++; start = false; break; }
                if (slots.Count < 2) { slot = new(context, artifact.ContentKey) { References = 1 }; slots.Add(slot); start = true; break; }
                evict = slots.FirstOrDefault(s => s.Finished && s.References == 0 && !s.Retiring);
                if (evict is null) throw new DiscoveryException("CREATURES_BUSY", "Both Creature core sessions are opening, borrowed or retiring.", true);
                evict.Retiring = true;
            }
            await DisposeSlotAsync(evict);
        }
        if (start) _ = OpenSlotAsync(slot, artifact, at, opening, admit, progress);
        try { return new(this, slot, await slot.Ready.Task.WaitAsync(token)); }
        catch { await ReleaseAsync(slot); throw; }
    }
    private async Task OpenSlotAsync(Slot slot, CreaturesArtifact artifact, string at, CapturedOpeningContext opening, bool admit, Func<CreaturesPreparationProgress, Task>? progress)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, slot.Cancellation.Token); var token = lifetime.Token;
        FileStream? encoded = null; TableRowArtifacts? reader = null; CreaturesProjection.Session? session = null;
        try
        {
            await ValidateProfileAsync(artifact.Binding, token);
            encoded = await openEncodedSource(artifact.Binding.Source.Source.Sha256, long.Parse(artifact.Binding.Source.Source.EncodedBytes, CultureInfo.InvariantCulture), token);
            reader = new(objects, resources, null);
            if (artifact.Sha256.All(c => c == '0'))
            {
                artifact = await projection.BuildAsync(artifact.Binding, reader, progress ?? (_ => Task.CompletedTask), token);
                await catalog.RecordCreaturesArtifactAsync(artifact, token);
            }
            else await reader.ReadPageAsync(artifact.Binding.Rows, 0, 1, null, token);
            session = await projection.OpenAsync(artifact, at, token);
            if (!admit) await catalog.ReauthorizeCreaturesAsync(opening, artifact, token);
            var held = new Held(encoded, reader, session); encoded = null; reader = null; session = null;
            await RetireSlotProducerAsync(slot);
            lock (sync) { slot.Finished = true; slot.Ready.TrySetResult(held); }
        }
        catch (Exception error)
        {
            Exception? cleanup = null;
            async Task Retire(Func<ValueTask> close) { try { await close(); } catch (Exception failure) { cleanup ??= failure; } }
            if (session is not null) await Retire(session.DisposeAsync); if (reader is not null) await Retire(reader.DisposeAsync); if (encoded is not null) await Retire(encoded.DisposeAsync);
            await RetireSlotProducerAsync(slot);
            lock (sync)
            {
                slot.Finished = true;
                if (cleanup is null) { slots.Remove(slot); slot.Cancellation.Dispose(); }
                else { slot.Retiring = true; slot.Disposal = Task.FromException(new DiscoveryException("CREATURES_CLEANUP_FAILED", "Creature session cleanup failed; its reserved capacity remains charged.")); }
                slot.Ready.TrySetException(cleanup is null ? error : new DiscoveryException("CREATURES_CLEANUP_FAILED", "Creature session cleanup failed; its reserved capacity remains charged."));
            }
        }
    }
    private Task RequestSlotCancellationAsync(Slot slot)
    {
        TaskCompletionSource notification; bool start;
        lock (sync)
        {
            if (slot.ProducerFinished) return Task.CompletedTask;
            start = slot.CancellationCompleted is null;
            notification = slot.CancellationCompleted ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (start) _ = NotifyAsync();
        return notification.Task;
        async Task NotifyAsync()
        { try { await slot.Cancellation.CancelAsync(); } catch (AggregateException) { } finally { notification.TrySetResult(); } }
    }
    private async Task RetireSlotProducerAsync(Slot slot)
    {
        Task cancellation;
        lock (sync) { slot.ProducerFinished = true; cancellation = slot.CancellationCompleted?.Task ?? Task.CompletedTask; }
        await cancellation;
    }
    private async Task ReleaseAsync(Slot slot)
    {
        bool cancel, dispose;
        lock (sync)
        {
            slot.References--; cancel = slot.References == 0 && !slot.Finished; if (cancel) slot.Retiring = true;
            dispose = slot.References == 0 && slot.Finished && (closed || slot.Retiring);
        }
        if (cancel)
        {
            await RequestSlotCancellationAsync(slot);
            try { await slot.Ready.Task; } catch (Exception) { }
            lock (sync) dispose = slot.Ready.Task.IsCompletedSuccessfully;
        }
        if (dispose) await DisposeSlotAsync(slot);
    }
    private async Task DisposeSlotAsync(Slot slot)
    {
        TaskCompletionSource? completion = null; Task disposal;
        lock (sync)
        {
            slot.Retiring = true;
            if (slot.Disposal is null) { completion = new(TaskCreationOptions.RunContinuationsAsynchronously); slot.Disposal = completion.Task; }
            disposal = slot.Disposal;
        }
        if (completion is not null)
        {
            try { if (slot.Ready.Task.IsCompletedSuccessfully) await (await slot.Ready.Task).DisposeAsync(); lock (sync) slots.Remove(slot); slot.Cancellation.Dispose(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }
        await disposal;
    }
    private async Task PrerequisiteAsync(CreaturesContext context, Func<CreaturesPreparationProgress, Task> progress, CancellationToken token)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if ((await tables.ReadAsync(Table(context), token)).Snapshot is not null) return;
            await progress(new("Preparing Creature prerequisite", "stage", TableId: CreaturesPolicy.Table));
            try { await tables.PrepareAsync(Table(context), stage => progress(new(stage, "stage", TableId: CreaturesPolicy.Table)), token); return; }
            catch (DiscoveryException error) when (error.Code == "TABLE_DATA_BUSY")
            { await progress(new("Waiting for shared Tables preparation", "stage", TableId: CreaturesPolicy.Table)); await tables.WaitForAvailabilityAsync(token); }
        }
        throw new DiscoveryException("CREATURES_PREREQUISITE_BUSY", "Shared Tables preparation remained busy after bounded retries.", true);
    }
    private static WorkspaceRequest Workspace(CreaturesContext context) => new(context.ProductId, context.BuildIdentity, context.SourceRegion);
    private static TableDataRequest Table(CreaturesContext context) =>
        new(context.ProductId, context.BuildIdentity, context.SourceRegion, CreaturesPolicy.Table, context.ResearchLocale, FetchedDefinitions: true, Feature: "Creatures");
    public async ValueTask DisposeAsync()
    {
        Task drain; lock (sync) { closed = true; foreach (var slot in slots) slot.Retiring = true; drain = retired?.Task ?? Task.CompletedTask; }
        try { await shutdown.CancelAsync(); } catch (AggregateException) { }
        await drain; Slot[] closing; lock (sync) closing = slots.ToArray(); Exception? failure = null;
        foreach (var slot in closing) { try { await DisposeSlotAsync(slot); } catch (Exception error) { failure ??= error; } }
        if (failure is not null) throw new DiscoveryException("CREATURES_CLEANUP_FAILED", "Creature shutdown could not retire every reserved session.");
    }
}
