using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>Optional exact-source labels; core Items never waits for this store.</summary>
public sealed partial class ItemClassificationsStore(DiscoveryCatalog catalog, VerifiedObjectStore objects, ItemsStore items,
    TableDataService tables, AcquisitionResources resources) : IItemClassificationsStore, IAsyncDisposable
{
    public bool SupportsSubclassLabels => true;
    private readonly ClassificationIndex indexes = new(objects, resources);
    private readonly CompositeClassificationIndex composites = new(objects, resources);
    private sealed record SessionKey(ItemsContext Context, ItemClassificationKind Kind);
    // One operation per store. No operation hold survives a shared prerequisite wait.
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly Dictionary<SessionKey, Held> sessions = [];
    private bool closed;
    private int active;
    private readonly object lifecycle = new();
    private readonly CancellationTokenSource shutdown = new();
    private TaskCompletionSource? retired;
    private int heldTargets;
    internal int MaximumHeldTargets { get; private set; }
    private sealed class Held(ClassificationIndexSession index, TableRowArtifacts rows, FileStream source, Action retire) : IAsyncDisposable
    {
        internal ClassificationIndexSession Index { get; } = index;
        private bool closed;
        public async ValueTask DisposeAsync()
        {
            if (closed) return; closed = true;
            try { await Index.DisposeAsync(); }
            finally { try { await rows.DisposeAsync(); } finally { try { await source.DisposeAsync(); } finally { retire(); } } }
        }
    }
    public async Task<ItemClassificationBatch> PrepareBatchAsync(ItemClassificationBatchRequest request,
        Func<ItemClassificationProgress, Task> progress, CancellationToken token)
    {
        ItemClassificationsService.Validate(request);
        request = request with { ItemIds = Array.AsReadOnly(request.ItemIds.ToArray()) };
        // The service owns sequencing. Direct callers receive bounded busy rather than an unbounded queue.
        token.ThrowIfCancellationRequested();
        lock (lifecycle)
        {
            if (closed) throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true);
            if (active != 0) throw new DiscoveryException("ITEM_CLASSIFICATIONS_BUSY", "Classification preparation is active or stopping.", true);
            active = 1; retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        token = lifetime.Token;
        await operations.WaitAsync(CancellationToken.None);
        try
        {
            token.ThrowIfCancellationRequested();
            if (request.PolicyRevision == ClassificationPolicy.CompositeRevision)
                return await PrepareV2(request, progress, token);
            var authorization = await items.AuthorizeSummariesAsync(request.Context, request.ArtifactId, request.ItemIds, token);
            var opening = await catalog.ResolveOpeningAsync(Workspace(request.Context), token);
            var classColumn = await ItemColumn(authorization.Snapshot, opening.Build.Version, "ClassID", 32, false, token);
            var subclassColumn = request.IncludeSubclass ? await ItemColumn(authorization.Snapshot, opening.Build.Version, "SubclassID", 8, true, token) : -1;
            Held? held = null; ItemClassificationSource source;
            try
            {
                // Release the operation gate before shared prepare/wait. Service worker remains the single producer.
                operations.Release();
                try
                {
                    if (authorization.Items.Any(i => i.Admitted) && classColumn >= 0)
                        await EnsurePrerequisite(request.Context, progress, token);
                }
                finally { await operations.WaitAsync(CancellationToken.None); }
                token.ThrowIfCancellationRequested();
                authorization = await items.AuthorizeSummariesAsync(request.Context, request.ArtifactId, request.ItemIds, token);
                if (authorization.Items.Any(i => i.Admitted) && classColumn >= 0)
                {
                    held = await Target(request.Context, opening, authorization.Snapshot, progress, token);
                    source = new(ItemClassificationKind.Class, held.Index.Artifact.Binding.Source.Coverage.Scope == "complete"
                        ? ItemClassificationState.Ready : ItemClassificationState.IncompleteTarget,
                        await PublicSnapshot(held.Index.Artifact.Binding, token), held.Index.Artifact.ContentKey);
                }
                else source = new(ItemClassificationKind.Class, ItemClassificationState.Unavailable, null, null,
                    new("ITEM_CLASSIFICATION_KEYS_UNAVAILABLE", "No admitted verified Item class keys are available."));
            }
            catch (DiscoveryException error) when (!Fatal(error))
            {
                source = new(ItemClassificationKind.Class, FailureState(error), null, null, new(error.Code, error.Message, error.Retryable));
            }
            var sources = new List<ItemClassificationSource> { source };
            var subclassProblem = new ItemClassificationProblem("ITEM_SUBCLASS_UNSUPPORTED", "Subclass labels require a separately qualified composite-key target profile.");
            if (request.IncludeSubclass) sources.Add(new(ItemClassificationKind.Subclass, ItemClassificationState.Unsupported, null, null, subclassProblem));
            var outcomes = new List<ItemClassificationOutcome>(authorization.Items.Count);
            foreach (var item in authorization.Items)
            {
                token.ThrowIfCancellationRequested();
                var classKeys = Keys(item, classColumn);
                ItemClassificationValue value;
                if (!item.Admitted) value = new(ItemClassificationState.MissingItem, classKeys);
                else if (classKeys.Count != 1 || !int.TryParse(classKeys[0].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var key) ||
                    key.ToString(CultureInfo.InvariantCulture) != classKeys[0].Value) value = new(ItemClassificationState.FieldUnavailable, classKeys);
                else if (key is < -128 or > 127) value = new(ItemClassificationState.KeyOutOfRange, classKeys);
                else if (held is not null) value = await held.Index.ResolveAsync(key, classKeys, token);
                else value = new(source.State, classKeys, Problem: source.Problem);
                ItemClassificationValue? subclass = request.IncludeSubclass
                    ? new(item.Admitted ? ItemClassificationState.Unsupported : ItemClassificationState.MissingItem,
                        classKeys.Concat(Keys(item, subclassColumn)).ToArray(), Problem: item.Admitted ? subclassProblem : null) : null;
                outcomes.Add(new(item.ItemId, item.Admitted, value, subclass));
            }
            // Both independent authority fences run after all asynchronous prerequisite/index/query work.
            await items.AuthorizeSummariesAsync(request.Context, request.ArtifactId, [], token);
            if (held is not null)
            {
                await ValidateCurrentProfile(held.Index.Artifact.Binding, token);
                await catalog.ReauthorizeItemClassificationTargetAsync(opening, held.Index.Artifact, token);
            }
            var result = new ItemClassificationBatch(request.Context, request.ArtifactId, ClassificationPolicy.Revision,
                sources.AsReadOnly(), outcomes.AsReadOnly());
            RequireResponseBudget(result);
            return result;
        }
        finally { operations.Release(); lock (lifecycle) { active = 0; retired!.TrySetResult(); } }
    }
    private async Task EnsurePrerequisite(ItemsContext context, Func<ItemClassificationProgress, Task> progress, CancellationToken token, ItemClassificationKind kind = ItemClassificationKind.Class)
    {
        var table = ClassificationPolicy.Table(kind);
        var opening = await catalog.ResolveOpeningAsync(Workspace(context), token);
        var admission = await catalog.ReadTableAdmissionAsync(opening, table, context.ResearchLocale, token);
        if (admission is not null)
        {
            try { await ValidateCurrentProfile(Binding(context, opening, admission, await catalog.ReadItemClassificationConfigurationsAsync(opening, token), kind), token); return; }
            catch (DiscoveryException error) when (error.Code is "ITEM_CLASSIFICATION_STALE" or "ITEM_CLASSIFICATION_PROFILE_UNSUPPORTED") { }
        }
        var request = new TableDataRequest(context.ProductId, context.BuildIdentity, context.SourceRegion, table, context.ResearchLocale);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await progress(new("Preparing " + kind.ToString().ToLowerInvariant() + " table prerequisite", "stage", TableId: table));
            try { await tables.PrepareAsync(request, stage => progress(new(stage, "stage", TableId: table)), token); return; }
            catch (DiscoveryException error) when (error.Code == "TABLE_DATA_BUSY")
            {
                await progress(new("Waiting for shared table preparation", "stage", TableId: table));
                await tables.WaitForAvailabilityAsync(token);
            }
        }
        throw new DiscoveryException("ITEM_CLASSIFICATION_PREREQUISITE_BUSY", "Shared table preparation remained busy after bounded retries.", true);
    }
    private async Task<Held> Target(ItemsContext context, CapturedOpeningContext opening, ItemsSnapshot snapshot,
        Func<ItemClassificationProgress, Task> progress, CancellationToken token, ItemClassificationKind kind = ItemClassificationKind.Class,
        HashSet<SessionKey>? pins = null)
    {
        var key = new SessionKey(context, kind);
        if (sessions.TryGetValue(key, out var cached))
        {
            try
            {
                await ValidateCurrentProfile(cached.Index.Artifact.Binding, token);
                await catalog.ReauthorizeItemClassificationTargetAsync(opening, cached.Index.Artifact, token);
                return cached;
            }
            catch (DiscoveryException error) when (!Fatal(error) || error.Code == "ITEM_CLASSIFICATION_STALE") { sessions.Remove(key); await cached.DisposeAsync(); }
        }
        var admission = await catalog.ReadTableAdmissionAsync(opening, ClassificationPolicy.Table(kind), context.ResearchLocale, token)
            ?? throw ClassificationPolicy.Stale();
        var configurations = await catalog.ReadItemClassificationConfigurationsAsync(opening, token);
        var binding = Binding(context, opening, admission, configurations, kind);
        await ValidateCurrentProfile(binding, token);
        var artifact = await catalog.FindItemClassificationArtifactAsync(ClassificationPolicy.ContentKey(binding), token);
        if (artifact is not null)
        {
            // First registered receipts remain authoritative even after observation-only revalidation.
            await catalog.ReauthorizeItemClassificationTargetAsync(opening, artifact, token);
        }
        // Evict before opening to bound all phases, including failed replacement.
        if (sessions.Count == 2)
        {
            var candidates = sessions.Where(pair => pins?.Contains(pair.Key) != true).ToArray();
            if (candidates.Length == 0) throw new DiscoveryException("ITEM_CLASSIFICATIONS_BUSY", "Both target sessions are currently pinned.", true);
            var first = candidates[0]; sessions.Remove(first.Key); await first.Value.DisposeAsync();
        }
        if (heldTargets >= 2) throw new DiscoveryException("ITEM_CLASSIFICATIONS_BUSY", "Target sessions are still retiring.", true);
        heldTargets++; MaximumHeldTargets = Math.Max(MaximumHeldTargets, heldTargets); var retained = false;
        FileStream? source = null; TableRowArtifacts? rows = null; ClassificationIndexSession? index = null;
        try
        {
            foreach (var receipt in configurations)
            {
                var bytes = await objects.ReadAsync(receipt.Sha256, receipt.ByteLength, token);
                using (await resources.EnterCpuAsync(null, token))
                {
                    if (Convert.ToHexStringLower(MD5.HashData(bytes)) != receipt.Key) throw ClassificationPolicy.Invalid();
                    if (receipt.Role == "buildConfig") TableDataStore.ValidateSourceAssociation(TactConfigurationParser.Parse(bytes, token), binding.Source.Association);
                }
            }
            source = await objects.OpenVerifiedAsync(binding.Source.Source.Sha256,
                long.Parse(binding.Source.Source.EncodedBytes, CultureInfo.InvariantCulture), resources, null, token);
            rows = new(objects, resources, null);
            if (artifact is not null) await rows.ReadPageAsync(binding.Rows, 0, 1, null, token);
            if (artifact is null)
            {
                artifact = kind == ItemClassificationKind.Class ? await indexes.BuildAsync(binding, progress, token, rows)
                    : await composites.BuildAsync(binding, progress, token, rows);
                await catalog.RecordItemClassificationArtifactAsync(artifact, token);
                artifact = await catalog.FindItemClassificationArtifactAsync(artifact.ContentKey, token) ?? throw ClassificationPolicy.Invalid();
            }
            index = kind == ItemClassificationKind.Class
                ? await indexes.OpenAsync(artifact, token) : await composites.OpenAsync(artifact, token);
            var currentItems = await items.AuthorizeSummariesAsync(context, snapshot.ArtifactId, [], token);
            await catalog.CommitItemClassificationAdmissionAsync(opening, currentItems.Snapshot, artifact, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), token);
            var held = new Held(index, rows, source, () => heldTargets--); retained = true; index = null; rows = null; source = null;
            sessions.Add(key, held); return held;
        }
        finally
        {
            try { if (index is not null) await index.DisposeAsync(); }
            finally { try { if (rows is not null) await rows.DisposeAsync(); }
                finally { try { if (source is not null) await source.DisposeAsync(); } finally { if (!retained) heldTargets--; } } }
        }
    }
    private static ClassificationIndexBinding Binding(ItemsContext context, CapturedOpeningContext opening, StoredTableAdmission admission,
        IReadOnlyList<ConfigurationReceipt> configurations, ItemClassificationKind kind = ItemClassificationKind.Class)
    {
        var a = admission.Artifact; var c = a.Completion;
        var snapshot = new TableDataSnapshot(context.ProductId, context.BuildIdentity, context.SourceRegion, ClassificationPolicy.Table(kind),
            context.ResearchLocale, a.ContentKey, admission.PreparedAt, a.Header.LayoutHash.ToUpperInvariant(), a.Binding.DefinitionSha256,
            a.Binding.ProviderRevision, a.Binding.DecoderRevision, admission.Source, admission.Association,
            a.Header.Columns.Select(v => new TableDataColumn(v.Name, v.Kind, v.IsId, null,
                v.Interpretation is { } i ? new(i.Policy, i.PhysicalBits, i.PhysicalSigned, i.CandidateBits, i.CandidateUnsigned, i.Warning) : null)).ToArray(),
            new(c.Scope, N(c.DeclaredRecords), N(c.PhysicalRows), N(c.CopyRows), N(c.ExcludedEncryptedSections), N(c.ExcludedPhysicalRecords + c.ExcludedCopyRecords),
                N(a.Header.DeclaredCopyRecords), N(c.ExcludedPhysicalRecords), N(c.ExcludedCopyRecords)), N(c.PhysicalRows + c.CopyRows), N(c.TextIssueCount));
        var textName = kind == ItemClassificationKind.Class ? "ClassName_lang" : "DisplayName_lang";
        var names = kind == ItemClassificationKind.Class ? new[] { "ClassID", textName } : new[] { "ClassID", "SubClassID", textName };
        foreach (var name in names)
        {
            var index = Column(snapshot, name);
            if (index < 0 || snapshot.Columns[index].Interpretation is not null ||
                snapshot.Columns[index].Family != (name == textName ? "string" : "integer"))
                throw new DiscoveryException("ITEM_CLASSIFICATION_PROFILE_UNSUPPORTED", "The admitted target does not expose the supported verified key and text fields.");
        }
        return new(context, opening.Build.Version, opening.Reference, configurations, kind, snapshot, a,
            names.Where(name => name != textName).Select(name => new ClassificationField(name, Column(snapshot, name), "integer", 8, false, true)).ToArray(),
            new(textName, Column(snapshot, textName), "string", null, null, true));
    }
    private static async Task<TableDataSnapshot> PublicSnapshot(ClassificationIndexBinding binding, CancellationToken token)
    {
        var provider = await TablesCatalog.Provider.Value.WaitAsync(token);
        if (!provider.Definitions.TryGetValue(binding.Source.TableId, out var definition) ||
            definition.Sha256 != binding.Source.DefinitionSha256 || definition.Sha256 != binding.Rows.Binding.DefinitionSha256)
            throw ClassificationPolicy.Stale();
        // Hydrate only public presentation metadata. The stored binding/key/bytes and
        // all original receipt timestamps remain unchanged, including legacy Class v1.
        return binding.Source with
        {
            Columns = Array.AsReadOnly(binding.Rows.Header.Columns.Select(column => TableDataStore.ProjectColumn(column, definition.Document)).ToArray())
        };
    }

    private static async Task ValidateCurrentProfile(ClassificationIndexBinding binding, CancellationToken token)
    {
        ClassificationPolicy.ValidateBinding(binding);
        var provider = await TablesCatalog.Provider.Value.WaitAsync(token);
        if (!provider.Definitions.TryGetValue(binding.Source.TableId, out var definition) ||
            binding.Rows.Binding.DefinitionSha256 != definition.Sha256 || binding.Source.ProviderRevision != BundledTableDefinitions.Revision ||
            binding.Source.DecoderRevision != (binding.Source.Source.Projection is null ? TableDataStore.DecoderRevision : TableDataStore.ProjectionDecoderRevision) ||
            binding.Rows.Binding.SchemaSelectionSha256 != TableRowArtifacts.SchemaSelectionKey(definition.Document, binding.BuildVersion, token))
            throw ClassificationPolicy.Stale();
        foreach (var field in binding.KeyFields.Append(binding.TextField))
        {
            var selected = SelectedField(definition.Document, binding.BuildVersion, binding.Source.LayoutHash, field.Name);
            if (selected is null || !selected.Column.VerifiedName || selected.ArrayLength != 1 ||
                (field.Family == "integer" ? selected.Column.Family != "int" || selected.IntegerBits != field.IntegerBits || selected.Unsigned != field.Unsigned
                    : selected.Column.Family is not ("string" or "locstring")))
                throw new DiscoveryException("ITEM_CLASSIFICATION_PROFILE_UNSUPPORTED", "The exact selected definition does not qualify this label key or text profile.");
        }
    }
    private static async Task<int> ItemColumn(ItemsSnapshot snapshot, string version, string name, int bits, bool unsigned, CancellationToken token)
    {
        var provider = await TablesCatalog.Provider.Value.WaitAsync(token);
        if (!provider.Definitions.TryGetValue(ItemsProjection.ItemTable, out var definition)) return -1;
        var field = SelectedField(definition.Document, version, snapshot.ItemSource.LayoutHash, name);
        var column = Column(snapshot.ItemSource, name);
        return field is { Column.VerifiedName: true, ArrayLength: 1 } && field.Column.Family == "int" && field.IntegerBits == bits &&
            field.Unsigned == unsigned && column >= 0 && snapshot.ItemSource.Columns[column].Family == "integer" &&
            snapshot.ItemSource.Columns[column].Interpretation is null ? column : -1;
    }
    private static DbdField? SelectedField(DbdDocument definition, string version, string layout, string name)
    {
        var selection = definition.SelectBuild(version, layout);
        return selection.Blocks.Count == 1 ? selection.Blocks[0].Fields.SingleOrDefault(f => f.Column.Name == name) : null;
    }
    private static int Column(TableDataSnapshot snapshot, string name)
    { var matches = snapshot.Columns.Select((c, i) => (c, i)).Where(v => v.c.Name == name).ToArray(); return matches.Length == 1 ? matches[0].i : -1; }
    private static IReadOnlyList<ItemSourceValue> Keys(AuthorizedItemSummary item, int column) =>
        Array.AsReadOnly(item.BaseValues.Where(v => column >= 0 && v.TableId == ItemsProjection.ItemTable && v.ColumnIndex == column).ToArray());
    private static bool Fatal(DiscoveryException error) => error.Code.StartsWith("ITEMS_", StringComparison.Ordinal) || error.Code is "ENGINE_STOPPING" or "ITEM_CLASSIFICATION_STALE";
    private static ItemClassificationState FailureState(DiscoveryException error) =>
        error.Code.Contains("ENCRYPT", StringComparison.Ordinal) || error.Code.Contains("PROTECTED", StringComparison.Ordinal) ? ItemClassificationState.Protected :
        error.Code.Contains("CORRUPT", StringComparison.Ordinal) || error.Code.Contains("INTEGRITY", StringComparison.Ordinal) || error.Code.Contains("INVALID", StringComparison.Ordinal) ? ItemClassificationState.Corrupt :
        error.Code.Contains("UNSUPPORTED", StringComparison.Ordinal) || error.Code.StartsWith("DB2_SCHEMA", StringComparison.Ordinal) || error.Code == "DEFINITION_NOT_BUNDLED" ? ItemClassificationState.Unsupported : ItemClassificationState.Unavailable;
    internal static void RequireResponseBudget(ItemClassificationBatch result)
    {
        using var count = new ClassificationResponseCounter((512 << 10) - 1024);
        using var writer = new System.Text.Json.Utf8JsonWriter(count);
        JsonSerializer.Serialize(writer, new { protocol = "discovery-v1", id = new string('\u0800', 256), type = "success", result }, ClassificationPolicy.Json);
        writer.Flush();
    }
    private static WorkspaceRequest Workspace(ItemsContext context) => new(context.ProductId, context.BuildIdentity, context.SourceRegion);
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    public async ValueTask DisposeAsync()
    {
        Task retirement;
        lock (lifecycle) { closed = true; retirement = retired?.Task ?? Task.CompletedTask; }
        try { await shutdown.CancelAsync(); }
        catch (AggregateException) { /* Cleanup remains mandatory even after a faulty consumer callback. */ }
        await retirement;
        await operations.WaitAsync();
        try { foreach (var held in sessions.Values) await held.DisposeAsync(); sessions.Clear(); }
        finally { operations.Release(); }
    }
}
