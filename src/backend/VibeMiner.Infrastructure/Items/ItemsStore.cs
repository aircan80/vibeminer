using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

internal sealed record AuthorizedItemSummary(string ItemId, bool Admitted, IReadOnlyList<ItemSourceValue> BaseValues);
internal sealed record ItemsSummaryAuthorization(ItemsSnapshot Snapshot, IReadOnlyList<AuthorizedItemSummary> Items);
internal sealed record ItemIconField(string ItemId, bool Admitted, string? IconFileDataId);
internal sealed record ItemsIconAuthorization(ItemsSnapshot Snapshot, IReadOnlyList<ItemIconField> Items);

/// <summary>Exact Items prerequisites reuse the application's one shared table producer.</summary>
public sealed class ItemsStore(DiscoveryCatalog catalog, VerifiedObjectStore objects, TableDataService tables,
    AcquisitionResources resources) : IItemsStore, IAsyncDisposable
{
    private readonly ItemsProjection projection = new(objects, resources);
    private readonly SemaphoreSlim sessionsGate = new(1, 1);
    private readonly Dictionary<ItemsContext, ItemsProjection.Session> sessions = [];
    private readonly Dictionary<string, TactConfiguration> configurations = [];
    private readonly SemaphoreSlim configurationsGate = new(1, 1);
    // Internal fault-injection seam only. Retirement is non-reentrant and runs
    // under sessionsGate; the production delegate only closes owned resources.
    private readonly Func<ItemsProjection.Session, ValueTask> retireSession = static session => session.DisposeAsync();
    private readonly HashSet<ItemsProjection.Session> cleanupDebts = [];
    // One serialized comparison at a time: a fixed512KiB Store reservation,
    // released on disposal, instead of per-proof large-object-heap churn.
    private readonly EffectsDescriptorBuffer firstEffectsDescriptor = new(), secondEffectsDescriptor = new();
    private bool cleanupFailed;
    private bool closed;

    internal ItemsStore(DiscoveryCatalog catalog, VerifiedObjectStore objects, TableDataService tables,
        AcquisitionResources resources, Func<ItemsProjection.Session, ValueTask> retireSession)
        : this(catalog, objects, tables, resources) => this.retireSession = retireSession;

    public async Task<ItemsReadiness> ReadAsync(ItemsContext context, CancellationToken token) => (await Probe(context, token)).Readiness;
    private async Task<(ItemsReadiness Readiness, TableDataSnapshot? Item, TableDataSnapshot? Sparse)> Probe(ItemsContext context, CancellationToken token)
    {
        ItemsService.Validate(context);
        await sessionsGate.WaitAsync(token);
        try
        {
            RequireUsable();
            if (sessions.TryGetValue(context, out var session))
            {
                var pinned = await catalog.ResolveOpeningAsync(Workspace(context), token);
                var current = await CurrentSources(context, pinned, session.Artifact, token);
                if (current.Current) return (Ready(session.Snapshot), session.Snapshot.ItemSource, session.Snapshot.SparseSource);
                await RetireLocked(session); sessions.Remove(context);
            }
        }
        finally { sessionsGate.Release(); }
        var opening = await catalog.ResolveOpeningAsync(Workspace(context), token);
        var saved = await catalog.ReadItemsAdmissionAsync(opening, context.ResearchLocale, token);
        var sources = await CurrentSources(context, opening, saved?.Artifact, token);
        if (saved is not null && sources.Current)
        {
            var snapshot = (await Session(saved, token)).Snapshot; return (Ready(snapshot), snapshot.ItemSource, snapshot.SparseSource);
        }
        // Without a feature-held source lease, use the shared table service's actual local
        // readiness probe. Its snapshots also avoid probing/hashing ready prerequisites twice.
        var item = (await tables.ReadAsync(Table(context, ItemsProjection.ItemTable), token)).Snapshot;
        var sparse = (await tables.ReadAsync(Table(context, ItemsProjection.SparseTable), token)).Snapshot;
        return (new(null, new[] { (ItemsProjection.ItemTable, item), (ItemsProjection.SparseTable, sparse) }
            .Select(pair => new ItemsDependencyReadiness(pair.Item1, pair.Item2 is null ? "notPrepared" : "ready", pair.Item2?.ArtifactId)).ToArray()), item, sparse);
    }
    public async Task<ItemsSnapshot> PrepareAsync(ItemsContext context, Func<ItemsPreparationProgress, Task> progress, CancellationToken token)
    {
        ItemsService.Validate(context); RequireUsable();
        var probe = await Probe(context, token); if (probe.Readiness.Snapshot is not null) return probe.Readiness.Snapshot;
        var item = probe.Item ?? await Prerequisite(context, ItemsProjection.ItemTable, progress, token);
        var sparse = probe.Sparse ?? await Prerequisite(context, ItemsProjection.SparseTable, progress, token);
        var opening = await catalog.ResolveOpeningAsync(Workspace(context), token);
        var itemAdmission = await catalog.ReadTableAdmissionAsync(opening, ItemsProjection.ItemTable, context.ResearchLocale, token)
            ?? throw new DiscoveryException("ITEMS_PREREQUISITE_STALE", "The Item admission changed before feature preparation.");
        var sparseAdmission = await catalog.ReadTableAdmissionAsync(opening, ItemsProjection.SparseTable, context.ResearchLocale, token)
            ?? throw new DiscoveryException("ITEMS_PREREQUISITE_STALE", "The Sparse admission changed before feature preparation.");
        var binding = new ItemsProjectionBinding(context, item, sparse, itemAdmission.Artifact, sparseAdmission.Artifact);
        ItemsProjection.ValidateBinding(binding);
        var artifact = await catalog.FindItemsArtifactAsync(ItemsProjection.ContentKey(binding), token);
        if (artifact is null)
        {
            artifact = await projection.BuildAsync(binding, progress, token);
            await catalog.RecordItemsArtifactAsync(artifact, token);
        }
        var at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        // Observer callbacks never run while a session/cache borrow is held.
        await progress(new("Saving exact Items admission", "rows", long.Parse(artifact.Counts.ItemRows, CultureInfo.InvariantCulture), long.Parse(artifact.Counts.ItemRows, CultureInfo.InvariantCulture)));
        // Serialize every session open with retirement. At most two cached plus
        // one provisional session exist when a close first becomes uncertain.
        await sessionsGate.WaitAsync(token);
        try
        {
            RequireUsable();
            var opened = await projection.OpenAsync(artifact, at, token);
            try
            {
                await catalog.CommitItemsAdmissionAsync(opening, context.ResearchLocale, artifact, at, token);
                // Durable success wins cancellation; ownership transfers locally.
                await RetainLocked(opened); return opened.Snapshot;
            }
            catch { await RetireLocked(opened); throw; }
        }
        finally { sessionsGate.Release(); }
    }
    private async Task<TableDataSnapshot> Prerequisite(ItemsContext context, string table,
        Func<ItemsPreparationProgress, Task> progress, CancellationToken token)
    {
        var request = Table(context, table);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            RequireUsable();
            var ready = await tables.ReadAsync(request, token); if (ready.Snapshot is not null) return ready.Snapshot;
            await progress(new("Preparing Items table prerequisite", "stage", TableId: table));
            try { return await tables.PrepareAsync(request, stage => progress(new(stage, "stage", TableId: table)), token); }
            catch (DiscoveryException failure) when (failure.Code == "TABLE_DATA_BUSY")
            {
                await progress(new("Waiting for shared table preparation", "stage", TableId: table));
                await tables.WaitForAvailabilityAsync(token);
            }
        }
        throw new DiscoveryException("ITEMS_PREREQUISITE_BUSY", "Shared table preparation remained busy after bounded ownership retries.", true);
    }
    public async Task<ItemsPage> ReadPageAsync(ItemsPageRequest request, CancellationToken token)
    {
        ItemsService.Validate(request); await sessionsGate.WaitAsync(token);
        try
        {
            var session = await RequireSession(request.Context, request.ArtifactId, token);
            return await session.ReadPageAsync(request, token);
        }
        finally { sessionsGate.Release(); }
    }
    public async Task<ItemInspectResult> InspectAsync(ItemInspectRequest request, CancellationToken token)
    {
        ItemsService.Validate(request); await sessionsGate.WaitAsync(token);
        try
        {
            var session = await RequireSession(request.Context, request.ArtifactId, token);
            return await session.InspectAsync(request, token);
        }
        finally { sessionsGate.Release(); }
    }
    internal async Task<ItemsSummaryAuthorization> AuthorizeSummariesAsync(ItemsContext context, string artifactId,
        IReadOnlyList<string> itemIds, CancellationToken token)
    {
        var ids = ValidateAuthorizationIds(context, artifactId, itemIds);
        await sessionsGate.WaitAsync(token);
        try
        {
            var session = await RequireSession(context, artifactId, token);
            return await session.AuthorizeSummariesAsync(context, artifactId, ids, token);
        }
        finally { sessionsGate.Release(); }
    }
    internal async Task<ItemEffectsItemsProof> AuthorizeItemEffectsAsync(ItemEffectsSelection selection, CancellationToken token)
    {
        if (selection is null) throw new DiscoveryException("INVALID_REQUEST", "Choose one exact selected Item.");
        var context = selection.Context; var artifactId = selection.ItemsArtifactId; var itemId = selection.ItemId;
        ItemsService.Validate(new ItemInspectRequest(context, artifactId, itemId));
        token.ThrowIfCancellationRequested(); RequireUsable();
        // Preserve this original opening across every wait. The final effects fence
        // reauthorizes it; a later discovery refresh must never be substituted here.
        var opening = await catalog.ResolveOpeningAsync(Workspace(context), token);
        var authority = await catalog.ReadItemEffectsItemsAuthorityAsync(opening, context.ResearchLocale, artifactId, token);
        var configurationCount = authority.Configurations.Count;
        if (configurationCount != 2)
            throw new DiscoveryException("ITEMS_SOURCE_INVALID", "The selected Items configuration proof is incomplete.");
        var configurations = new ConfigurationReceipt[configurationCount];
        for (var i = 0; i < configurationCount; i++)
        {
            var receipt = authority.Configurations[i];
            if (receipt is null) throw new DiscoveryException("ITEMS_SOURCE_INVALID", "The selected Items configuration proof is incomplete.");
            configurations[i] = receipt with { };
        }
        var capturedConfigurations = Array.AsReadOnly(configurations);
        authority = authority with { Configurations = capturedConfigurations };
        await ValidateEffectsSourcesAsync(opening, authority, token);
        await sessionsGate.WaitAsync(token);
        try
        {
            var session = await RequireEffectsSessionLocked(context, authority.Items, token);
            var authorized = await session.AuthorizeSummariesAsync(context, artifactId, [itemId], token);
            if (authorized.Items.Count != 1 || authorized.Items[0].ItemId != itemId)
                throw new DiscoveryException("ITEMS_PROJECTION_INVALID", "The selected Item summary has conflicting membership.");
            var item = authorized.Items[0]; var count = item.BaseValues.Count;
            if (count > 8 || !item.Admitted && count != 0)
                throw new DiscoveryException("ITEMS_PROJECTION_INVALID", "The selected Item summary exceeds its compact evidence bound.");
            var values = new ItemSourceValue[count];
            for (var i = 0; i < count; i++)
            {
                var value = item.BaseValues[i];
                values[i] = new(value.TableId, value.ColumnIndex, value.Value);
            }
            var artifact = authority.Items.Artifact;
            // The immutable artifact keeps first provenance. Per-operation source
            // admission observations belong to the proof, never the cached session.
            var snapshot = new ItemsSnapshot(context, artifact.ContentKey, authority.Items.PreparedAt,
                artifact.FeatureRevision, ItemsProjection.QueryPolicy,
                artifact.Binding.ItemSource with { Source = authority.Item.Source, Association = authority.Item.Association, PreparedAt = authority.Item.PreparedAt },
                artifact.Binding.SparseSource with { Source = authority.Sparse.Source, Association = authority.Sparse.Association, PreparedAt = authority.Sparse.PreparedAt },
                artifact.Counts, artifact.Groups);
            return new(opening, artifact, snapshot, new(itemId, item.Admitted, Array.AsReadOnly(values)), capturedConfigurations);
        }
        finally { sessionsGate.Release(); }
    }
    private async Task ValidateEffectsSourcesAsync(CapturedOpeningContext opening, ItemEffectsItemsAuthority authority,
        CancellationToken token)
    {
        var artifact = authority.Items.Artifact;
        if (artifact.FeatureRevision != ItemsProjection.Revision)
            throw new DiscoveryException("ITEMS_ARTIFACT_STALE", "The selected Items feature requires its current interpretation.");
        var provider = await TablesCatalog.Provider.Value.WaitAsync(token);
        foreach (var (table, admission) in new[] { (ItemsProjection.ItemTable, authority.Item), (ItemsProjection.SparseTable, authority.Sparse) })
        {
            if (!provider.Definitions.TryGetValue(table, out var definition) || definition.Document.SelectBuild(opening.Build.Version, token: token).Blocks.Count == 0)
                throw new DiscoveryException("SCHEMA_BUILD_NOT_LISTED", "The pinned Items definition does not list this exact build.");
            var a = admission.Artifact;
            if (a.Binding.DefinitionSha256 != definition.Sha256 || a.Binding.ProviderRevision != BundledTableDefinitions.Revision ||
                a.Binding.DecoderRevision != (admission.Source.Projection is null ? TableDataStore.DecoderRevision : TableDataStore.ProjectionDecoderRevision) ||
                a.Binding.SchemaSelectionSha256 != TableRowArtifacts.SchemaSelectionKey(definition.Document, opening.Build.Version, token))
                throw new DiscoveryException("ITEMS_ARTIFACT_STALE", "The selected Item or Sparse interpretation is no longer current.");
            TableDataStore.ValidateInterpretation(a, definition.Document, opening.Build.Version);
        }
        // Receipts were bounded/authenticated together with source authority by the
        // catalog. Read their retained bytes directly, with no later eager SQL read
        // or key-only configuration cache replacing the captured original receipt.
        var buildValidated = false;
        for (var i = 0; i < 2; i++)
        {
            var receipt = authority.Configurations[i];
            var maximum = receipt.Role == "buildConfig" ? ItemEffectsConfigurationBudget.MaxBuildBytes : TactConfigurationParser.MaxBytes;
            if (receipt.ByteLength < 1 || receipt.ByteLength > maximum)
                throw new DiscoveryException("ITEM_EFFECTS_LIMIT", "The effects resource limit was exceeded.");
            var bytes = await objects.ReadAsync(receipt.Sha256, receipt.ByteLength, token);
            using (await resources.EnterCpuAsync(null, token))
            {
                if (Convert.ToHexStringLower(MD5.HashData(bytes)) != receipt.Key)
                    throw new DiscoveryException("CONFIG_INTEGRITY", "The selected Items configuration differs from its captured opening.");
                if (receipt.Role == "buildConfig")
                {
                    ValidateBuild(bytes, authority, token);
                    buildValidated = true;
                }
            }
        }
        if (!buildValidated) throw new DiscoveryException("ITEMS_SOURCE_INVALID", "The selected Items build configuration proof is missing.");

        // A non-async call bounds the parsed graph to this phase; it cannot survive the next raw CDN read.
        static void ValidateBuild(ReadOnlySpan<byte> bytes, ItemEffectsItemsAuthority authority, CancellationToken token)
        {
            var build = ItemEffectsConfigurationBudget.ParseBuild(bytes, token);
            TableDataStore.ValidateSourceAssociation(build, authority.Item.Association);
            TableDataStore.ValidateSourceAssociation(build, authority.Sparse.Association);
        }
    }
    // Caller owns sessionsGate; only the already bounded canonical authority enters
    // this path. Ordinary Items queries keep their existing current-source behavior.
    private async Task<ItemsProjection.Session> RequireEffectsSessionLocked(ItemsContext context,
        StoredItemsAdmission saved, CancellationToken token)
    {
        RequireUsable();
        if (sessions.TryGetValue(context, out var cached))
        {
            if (cached.Artifact.ContentKey == saved.Artifact.ContentKey && cached.Artifact.Sha256 == saved.Artifact.Sha256 &&
                cached.Artifact.ByteLength == saved.Artifact.ByteLength && cached.Artifact.FeatureRevision == saved.Artifact.FeatureRevision)
            {
                using var cpu = await resources.EnterCpuAsync(null, token);
                if (SameEffectsDescriptor(cached.Artifact, saved.Artifact)) return cached;
            }
            await RetireLocked(cached); sessions.Remove(context);
        }
        var opened = await projection.OpenAsync(saved.Artifact, saved.PreparedAt, token);
        try { await RetainLocked(opened); return opened; }
        catch { await RetireLocked(opened); throw; }
    }
    private static readonly JsonSerializerOptions EffectsDescriptorJson = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private bool SameEffectsDescriptor(ItemsProjectionArtifact cached, ItemsProjectionArtifact current)
    {
        // A normal Items cache predates this feature's smaller proof budget. Do
        // not eagerly serialize a larger normal cached descriptor to a string.
        var first = firstEffectsDescriptor; var second = secondEffectsDescriptor;
        first.Reset(); second.Reset();
        try
        {
            using (var writer = new Utf8JsonWriter(first)) { JsonSerializer.Serialize(writer, cached, EffectsDescriptorJson); writer.Flush(); }
            using (var writer = new Utf8JsonWriter(second)) { JsonSerializer.Serialize(writer, current, EffectsDescriptorJson); writer.Flush(); }
            return first.Written.SequenceEqual(second.Written);
        }
        catch (EffectsDescriptorLimitException) { return false; }
    }
    private sealed class EffectsDescriptorLimitException : Exception { }
    private sealed class EffectsDescriptorBuffer : IBufferWriter<byte>
    {
        private byte[] bytes = new byte[256 << 10];
        private int count;
        internal ReadOnlySpan<byte> Written => bytes.AsSpan(0, count);
        internal void Reset() => count = 0;
        internal void Release() { bytes = []; count = 0; }
        public void Advance(int written)
        { if (written < 0 || written > bytes.Length - count) throw new EffectsDescriptorLimitException(); count += written; }
        public Memory<byte> GetMemory(int sizeHint = 0)
        { if (sizeHint < 0 || count == bytes.Length || sizeHint > bytes.Length - count) throw new EffectsDescriptorLimitException(); return bytes.AsMemory(count); }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
    internal async Task<ItemsIconAuthorization> AuthorizeIconsAsync(ItemsContext context, string artifactId,
        IReadOnlyList<string> itemIds, CancellationToken token)
    {
        var ids = ValidateAuthorizationIds(context, artifactId, itemIds);
        await sessionsGate.WaitAsync(token);
        try
        {
            var session = await RequireSession(context, artifactId, token);
            return await session.AuthorizeIconsAsync(context, artifactId, ids, token);
        }
        finally { sessionsGate.Release(); }
    }
    internal static string[] ValidateAuthorizationIds(ItemsContext context, string artifactId, IReadOnlyList<string> itemIds)
    {
        ItemsService.Validate(new ItemsPageRequest(context, artifactId));
        if (itemIds is null || itemIds.Count > 50)
            throw new DiscoveryException("INVALID_REQUEST", "An Item summary authorization batch accepts at most fifty canonical Item IDs.");
        var ids = itemIds.ToArray();
        foreach (var id in ids) ItemsService.Validate(new ItemInspectRequest(context, artifactId, id));
        return ids;
    }
    // Caller holds sessionsGate through its bounded query so eviction cannot retire an active lease.
    private async Task<ItemsProjection.Session> RequireSession(ItemsContext context, string artifactId, CancellationToken token)
    {
        RequireUsable();
        var opening = await catalog.ResolveOpeningAsync(Workspace(context), token);
        if (sessions.TryGetValue(context, out var cached) && cached.Snapshot.ArtifactId == artifactId)
        {
            if ((await CurrentSources(context, opening, cached.Artifact, token)).Current) return cached;
            await RetireLocked(cached); sessions.Remove(context);
            throw new DiscoveryException("ITEMS_ARTIFACT_STALE", "The current Item or Sparse admission changed after this feature was prepared.");
        }
        var saved = await catalog.ReadItemsAdmissionAsync(opening, context.ResearchLocale, token);
        if (saved is null)
        {
            var previous = await catalog.FindItemsArtifactAsync(artifactId, token);
            if (previous?.Binding.Context == context) throw new DiscoveryException("ITEMS_ARTIFACT_STALE", "The requested Items feature no longer has a current source admission.");
            throw new DiscoveryException("ITEMS_NOT_PREPARED", "Prepare this exact build's Items before querying it.");
        }
        if (saved.Artifact.ContentKey != artifactId) throw new DiscoveryException("ITEMS_ARTIFACT_STALE", "The requested Items feature belongs to a different current source admission.");
        if (!(await CurrentSources(context, opening, saved.Artifact, token)).Current)
            throw new DiscoveryException("ITEMS_ARTIFACT_STALE", "The source interpretation or admission is no longer current for this Items feature.");
        var session = await projection.OpenAsync(saved.Artifact, saved.PreparedAt, token);
        try { await RetainLocked(session); return session; }
        catch { await RetireLocked(session); throw; }
    }
    private async Task<ItemsProjection.Session> Session(StoredItemsAdmission saved, CancellationToken token)
    {
        await sessionsGate.WaitAsync(token);
        try
        {
            RequireUsable();
            if (sessions.TryGetValue(saved.Artifact.Binding.Context, out var current) && current.Snapshot.ArtifactId == saved.Artifact.ContentKey) return current;
            var opened = await projection.OpenAsync(saved.Artifact, saved.PreparedAt, token);
            try { await RetainLocked(opened); return opened; }
            catch { await RetireLocked(opened); throw; }
        }
        finally { sessionsGate.Release(); }
    }
    private async Task RetainLocked(ItemsProjection.Session session)
    {
        RequireUsable();
        if (sessions.TryGetValue(session.Snapshot.Context, out var prior))
        { await RetireLocked(prior); sessions.Remove(session.Snapshot.Context); }
        if (sessions.Count >= 2)
        { var oldest = sessions.First(); await RetireLocked(oldest.Value); sessions.Remove(oldest.Key); }
        sessions.Add(session.Snapshot.Context, session);
    }
    private static DiscoveryException CleanupFailure() => new("ITEMS_CLEANUP_FAILED", "Items resource retirement could not be confirmed. Restart the engine.");
    private void RequireUsable()
    {
        if (Volatile.Read(ref cleanupFailed)) throw CleanupFailure();
        ObjectDisposedException.ThrowIf(closed, this);
    }
    private async Task RetireLocked(ItemsProjection.Session session)
    {
        // Session.Dispose marks its state before closing all handles. An earlier
        // failed close cannot be proved retired by calling it again and seeing a
        // no-op. Keep the bounded debt and refuse further session admission.
        if (cleanupDebts.Contains(session)) throw CleanupFailure();
        try { await retireSession(session); }
        catch
        {
            cleanupDebts.Add(session); Volatile.Write(ref cleanupFailed, true);
            throw CleanupFailure();
        }
    }
    private static ItemsReadiness Ready(ItemsSnapshot snapshot) => new(snapshot,
        new[] { snapshot.ItemSource, snapshot.SparseSource }.Select(source => new ItemsDependencyReadiness(source.TableId, "ready", source.ArtifactId)).ToArray());
    private static TableDataRequest Table(ItemsContext context, string table) => new(context.ProductId, context.BuildIdentity, context.SourceRegion, table, context.ResearchLocale);
    private static WorkspaceRequest Workspace(ItemsContext context) => new(context.ProductId, context.BuildIdentity, context.SourceRegion);
    private async Task<(bool Current, ItemsDependencyReadiness[] Dependencies)> CurrentSources(ItemsContext context,
        CapturedOpeningContext opening, ItemsProjectionArtifact? expected, CancellationToken token)
    {
        var provider = await TablesCatalog.Provider.Value.WaitAsync(token);
        if (!opening.Build.ProductId.StartsWith("wow", StringComparison.Ordinal))
            throw new DiscoveryException("TABLE_PRODUCT_UNSUPPORTED", "Items currently requires a supported WoW table source.");
        var admissions = new StoredTableAdmission?[2]; var dependencies = new ItemsDependencyReadiness[2];
        var tableIds = new[] { ItemsProjection.ItemTable, ItemsProjection.SparseTable };
        for (var i = 0; i < tableIds.Length; i++)
        {
            var table = tableIds[i]; var admission = await catalog.ReadTableAdmissionAsync(opening, table, context.ResearchLocale, token);
            if (!provider.Definitions.TryGetValue(table, out var supported) || supported.Document.SelectBuild(opening.Build.Version, token: token).Blocks.Count == 0)
                throw new DiscoveryException("SCHEMA_BUILD_NOT_LISTED", "The pinned Items definition does not list this exact build.");
            if (admission is not null)
            {
                if (!provider.Definitions.TryGetValue(table, out var definition)) admission = null;
                else
                {
                    var a = admission.Artifact;
                    if (a.Binding.DefinitionSha256 != definition.Sha256 || a.Binding.ProviderRevision != BundledTableDefinitions.Revision ||
                        a.Binding.DecoderRevision != (admission.Source.Projection is null ? TableDataStore.DecoderRevision : TableDataStore.ProjectionDecoderRevision) ||
                        a.Binding.SchemaSelectionSha256 != TableRowArtifacts.SchemaSelectionKey(definition.Document, opening.Build.Version, token)) admission = null;
                    else TableDataStore.ValidateInterpretation(a, definition.Document, opening.Build.Version);
                }
            }
            admissions[i] = admission; dependencies[i] = new(table, admission is null ? "notPrepared" : "ready", admission?.Artifact.ContentKey);
        }
        if (admissions[0] is not { } item || admissions[1] is not { } sparse) return (false, dependencies);
        var configuration = await Configuration(opening.Build.BuildConfig, token);
        TableDataStore.ValidateSourceAssociation(configuration, item.Association); TableDataStore.ValidateSourceAssociation(configuration, sparse.Association);
        if (expected is null || expected.FeatureRevision != ItemsProjection.Revision) return (false, dependencies);
        if (item.Artifact.ContentKey != expected.Binding.ItemSource.ArtifactId || sparse.Artifact.ContentKey != expected.Binding.SparseSource.ArtifactId) return (false, dependencies);
        var refreshed = expected.Binding with
        {
            ItemRows = item.Artifact, SparseRows = sparse.Artifact,
            ItemSource = expected.Binding.ItemSource with { Source = item.Source, Association = item.Association, PreparedAt = item.PreparedAt },
            SparseSource = expected.Binding.SparseSource with { Source = sparse.Source, Association = sparse.Association, PreparedAt = sparse.PreparedAt }
        };
        return (ItemsProjection.ContentKey(refreshed) == expected.ContentKey, dependencies);
    }
    private async Task<TactConfiguration> Configuration(string key, CancellationToken token)
    {
        await configurationsGate.WaitAsync(token);
        try
        {
            if (configurations.TryGetValue(key, out var saved)) return saved;
            var receipt = await catalog.FindConfigurationAsync(key, token)
                ?? throw new DiscoveryException("LOCAL_CONFIGURATION_MISSING", "The exact Items build configuration proof is missing.");
            TactConfiguration parsed;
            using (await resources.EnterCpuAsync(null, token))
            {
                var bytes = await objects.ReadAsync(receipt.Sha256, receipt.ByteLength, token);
                if (Convert.ToHexStringLower(MD5.HashData(bytes)) != key) throw new DiscoveryException("CONFIG_INTEGRITY", "The Items source configuration differs from this exact build.");
                parsed = TactConfigurationParser.Parse(bytes, token);
            }
            if (configurations.Count >= 2) configurations.Remove(configurations.Keys.First());
            configurations.Add(key, parsed); return parsed;
        }
        finally { configurationsGate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await sessionsGate.WaitAsync();
        try
        {
            if (closed && !cleanupFailed) return;
            closed = true;
            foreach (var pair in sessions.ToArray())
            {
                if (cleanupDebts.Contains(pair.Value)) continue;
                try { await RetireLocked(pair.Value); sessions.Remove(pair.Key); }
                catch { /* Other independent sessions still need their one close attempt. */ }
            }
            if (cleanupFailed) throw CleanupFailure();
        }
        finally { firstEffectsDescriptor.Release(); secondEffectsDescriptor.Release(); sessionsGate.Release(); }
    }
}
