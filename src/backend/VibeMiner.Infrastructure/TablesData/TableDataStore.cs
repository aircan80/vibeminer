using System.Globalization;
using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>Demand-only table admission. Large sources and rows stay immutable outside SQLite.</summary>
public sealed partial class TableDataStore : ITableDataStore, ITableReferenceTextStore, ITableFollowStore, IAsyncDisposable
{
    private readonly DiscoveryCatalog catalog;
    private readonly VerifiedObjectStore objects;
    private readonly SelectedFileAcquirer acquisition;
    private readonly TableRowArtifacts rows;
    private readonly AcquisitionResources? resources;
    internal const string DecoderRevision = Wdc5TableReader.Revision + "+table-admission-v3";
    internal const string ProjectionDecoderRevision = Wdc5TableReader.Revision + "+table-admission-projection-v3";
    // v4: the bundled ItemEffect scope, and since D101 any fetched definition read with reviewed raw rules.
    internal const string ItemEffectDecoderRevision = Wdc5TableReader.Revision + "+table-admission-v4";
    internal const string ItemEffectProjectionDecoderRevision = Wdc5TableReader.Revision + "+table-admission-projection-v4";
    private const string ItemEffectDefinitionSha256 = "7276994228541b94e821a3b7834497c7aa0c8e8f3d1cea83ac17a40f13c4148c";

    // The definition envelope is known before decoding. Actual layout/storage must
    // still match each exact parser rule; other definitions retain their v3 keys.
    internal static bool IsItemEffectAdmission(string tableId, string definitionSha256, string providerRevision) =>
        string.Equals(tableId, "4002a5b1", StringComparison.OrdinalIgnoreCase) &&
        definitionSha256 == ItemEffectDefinitionSha256 && providerRevision == BundledTableDefinitions.Revision;
    internal static string AdmissionRevision(string tableId, string definitionSha256, string providerRevision, bool projected) =>
        IsItemEffectAdmission(tableId, definitionSha256, providerRevision)
            ? projected ? ItemEffectProjectionDecoderRevision : ItemEffectDecoderRevision
            : projected ? ProjectionDecoderRevision : DecoderRevision;
    private readonly TableDefinitionSources definitions;
    public TableDataStore(DiscoveryCatalog catalog, VerifiedObjectStore objects, HttpClient client,
        ISharedAcquisition? shared = null, AcquisitionResources? resources = null, TableDefinitionSources? definitions = null)
    { this.catalog = catalog; this.objects = objects; this.resources = resources; acquisition = new(catalog, objects, client, shared, resources);
        rows = resources is null ? new(objects) : new(objects, resources, null); this.definitions = definitions ?? new(); }

    // Each observed candidate-width inconsistency is evidence-only. The parser
    // additionally matches actual table/layout/field/storage metadata before Header.
    // Changing this fixed production profile requires a new admission revision.
    // D079 kept these rules: they win over low-bit narrowing, so these exact retail layouts keep their whole-column raw
    // values and the Item effect evidence built on them. Every other verified pallet/common width mismatch narrows instead.
    // Keyed by the reviewed bundled definition.
    private static readonly IReadOnlyDictionary<string, Wdc5RawIntegerRule[]> ReviewedRawRules = new Dictionary<string, Wdc5RawIntegerRule[]>(StringComparer.Ordinal)
    {
        ["e06461a556669dd560dc8908e417fac9343894f4fd4d446e2d3dc721a908891c"] =
            [new("e06461a556669dd560dc8908e417fac9343894f4fd4d446e2d3dc721a908891c", "95D8638F", "1E67DB87", 6, "PrerequisiteProficiency", 3, 32, 8, false)],
        ["64f98132c6acae55be7bb27696d11778e5dd6654e8647ea539cad075dfb3c425"] =
            [new("64f98132c6acae55be7bb27696d11778e5dd6654e8647ea539cad075dfb3c425", "BFDAF9F1", "7275F5F6", 8, "NPCSoundID", 3, 32, 16, true),
             new("64f98132c6acae55be7bb27696d11778e5dd6654e8647ea539cad075dfb3c425", "BFDAF9F1", "7275F5F6", 12, "ObjectEffectPackageID", 2, 32, 16, true),
             new("64f98132c6acae55be7bb27696d11778e5dd6654e8647ea539cad075dfb3c425", "BFDAF9F1", "7275F5F6", 13, "AnimReplacementSetID", 2, 32, 16, true)],
        [ItemEffectDefinitionSha256] =
            [new(ItemEffectDefinitionSha256, "4002A5B1", "4CA77678", 3, "Charges", 3, 32, 16, false),
             new(ItemEffectDefinitionSha256, "4002A5B1", "4CA77678", 6, "SpellCategoryID", 3, 32, 16, true),
             new(ItemEffectDefinitionSha256, "4002A5B1", "4CA77678", 8, "ChrSpecializationID", 3, 32, 16, true)],
    };
    private static Wdc5Limits AdmissionLimits(TableDefinition definition, BundledTableDefinition bundled, string build, string? layout) => new(
        AllowPartialEncryptedSections: true, AllowRawTextEvidence: true,
        DefinitionSha256: definition.Sha256,
        VerifiedRawIntegerRules: RawRules(definition, bundled, build, layout));

    /// <summary>
    /// The reviewed raw rules a definition reads its file with. The bundled definition keeps all of its own. A fetched
    /// definition (D101) gets the rules for the file's layout only when its block for that layout lists the same fields,
    /// widths and flags as the reviewed bundled block, so the review still describes what is read. The reader then
    /// matches each rule's field and storage against the file as before.
    /// </summary>
    internal static IReadOnlyList<Wdc5RawIntegerRule>? RawRules(TableDefinition definition, BundledTableDefinition bundled, string build, string? layout,
        CancellationToken token = default)
    {
        if (!ReviewedRawRules.TryGetValue(bundled.Sha256, out var reviewed)) return null;
        if (definition.Fetched is null) return definition.Sha256 == bundled.Sha256 ? reviewed : null;
        if (layout is null) return null;
        var rules = reviewed.Where(rule => string.Equals(rule.LayoutHash, layout, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (rules.Length == 0 || !SameReviewedBlock(definition.Document, bundled.Document, build, layout, token)) return null;
        return rules.Select(rule => rule with { ExpectedDefinitionSha256 = definition.Sha256 }).ToArray();
    }
    private static bool SameReviewedBlock(DbdDocument fetched, DbdDocument bundled, string build, string layout, CancellationToken token)
    {
        var selected = fetched.SelectFile(build, layout, token);
        if (selected.Blocks.Count != 1) return false;
        var reviewed = bundled.Blocks.Where(block => block.LayoutHashes.Contains(layout, StringComparer.OrdinalIgnoreCase)).ToArray();
        return reviewed.Length > 0 && reviewed.All(block => SameFields(block.Fields, selected.Blocks[0].Fields));
    }
    // Comments are notes; everything the reader and the review depend on must match.
    private static bool SameFields(IReadOnlyList<DbdField> left, IReadOnlyList<DbdField> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.Column.Name == pair.Second.Column.Name &&
            pair.First.Column.Family == pair.Second.Column.Family && pair.First.Column.VerifiedName == pair.Second.Column.VerifiedName &&
            pair.First.IntegerBits == pair.Second.IntegerBits && pair.First.Unsigned == pair.Second.Unsigned &&
            pair.First.ArrayLength == pair.Second.ArrayLength && pair.First.IsId == pair.Second.IsId &&
            pair.First.NonInline == pair.Second.NonInline && pair.First.RelationStorage == pair.Second.RelationStorage);

    /// <summary>
    /// The admission revision binds the raw rules a decoding used, so one content key never covers two decodings. v4 is
    /// the bundled ItemEffect scope and every fetched definition that gets reviewed raw rules (D101); a fetched definition
    /// without them, like rows decoded before D101, keeps v3. The bundled ItemSubClass and CreatureDisplayInfo rules
    /// predate v4 and keep their v3 keys.
    /// </summary>
    internal static string AdmissionRevision(string tableId, TableDefinition definition, BundledTableDefinition bundled, string build, string? layout,
        bool projected, CancellationToken token = default) =>
        (string.Equals(tableId, "4002a5b1", StringComparison.OrdinalIgnoreCase) || definition.Fetched is not null) &&
        RawRules(definition, bundled, build, layout, token) is { Count: > 0 }
            ? projected ? ItemEffectProjectionDecoderRevision : ItemEffectDecoderRevision
            : projected ? ProjectionDecoderRevision : DecoderRevision;
    /// <summary>A fetched definition may carry v4 only on a reviewed table and layout (D101): another definition than the
    /// reviewed bundled one, named by its own WoWDBDefs commit. Reading checks the rest against the definition.</summary>
    internal static bool MayBeFetchedReviewedAdmission(string tableId, string layout, string definitionSha256, string providerRevision) =>
        ReviewedRawRules.Any(pair => pair.Value.Any(rule => string.Equals(rule.TableHash, tableId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(rule.LayoutHash, layout, StringComparison.OrdinalIgnoreCase)) && definitionSha256 != pair.Key) &&
        providerRevision != BundledTableDefinitions.Revision &&
        providerRevision.Length == 40 && providerRevision.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public async Task<TableDataSnapshot> PrepareAsync(TableDataRequest request, Func<string, Task> progress, CancellationToken token)
    {
        var (context, _, entry, bundled, fetchEnabled) = await ResolveAsync(request, token);
        var build = context.Build.Version;
        // A prepare asks WoWDBDefs at most once, whichever check asks first (D081).
        var budget = new TableDefinitionFetchBudget();
        var retained = await catalog.ReadTableAdmissionAsync(context, request.TableId, request.ResearchLocale, token);
        if (retained is not null)
        {
            await progress("Verifying retained table rows");
            await ValidateSourceAsync(context, retained.Association, token);
            // The same rules as a first load, including asking WoWDBDefs when due, so a table opened by layout upgrades
            // to a definition that lists the build once upstream has one.
            var kept = (await definitions.ChooseAsync(entry, bundled, build, retained.Artifact.Header.LayoutHash, fetchEnabled, progress, token, budget)).Definition
                ?? TableDefinition.Bundled(bundled);
            try { Validate(retained, entry, kept, bundled, build); }
            catch (DiscoveryException failure) when (failure.Code == "TABLE_INTERPRETATION_STALE") { retained = null; }
            if (retained is not null)
            {
                await rows.ReadPageAsync(retained.Artifact, 0, 1, null, token);
                return Snapshot(request, retained, kept, build, bundled);
            }
            await progress("Updating retained table interpretation");
        }
        // D081, exact build first: before anything is downloaded, a definition must list the build or open it by layout.
        // Tables and the features that read tables may ask WoWDBDefs (D101); a failed fetch still leaves a bundled layout match usable.
        // A load with no usable rows (a first open or Retry) is deliberate: it asks WoWDBDefs again even inside the wait
        // after a failed attempt. Re-checks of rows already open keep the wait.
        var before = await definitions.ChooseAsync(entry, bundled, build, null, fetchEnabled, progress, token, budget, deliberate: true);
        if (before.Definition is null) throw TableDefinitionSources.Unusable(entry, build, null, before, fetchEnabled, request.Feature);
        await progress("Resolving selected table dependencies");
        await using var selected = await acquisition.AcquireAsync(new(request.ProductId, request.BuildIdentity, request.SourceRegion),
            uint.Parse(entry.Db2FileDataId!, CultureInfo.InvariantCulture), request.ResearchLocale, progress, token);
        var decoded = selected.Decoded;
        decoded.Position = 0;
        var layout = await LayoutAsync(decoded, token);
        var definition = before.Definition;
        if (layout is not null)
        {
            // The file's own layout decides between exact build and layout match, bundled first. When nothing covers it
            // and fetching is off, the bundled definition goes to the reader, which says why it can't read the file.
            // Deliberate too, for a listed build whose file has an uncovered layout: no earlier check asked, so Retry asks
            // here at once. The budget keeps it to one attempt per prepare whichever check asks.
            var choice = await definitions.ChooseAsync(entry, bundled, build, layout, fetchEnabled, progress, token, budget, deliberate: true);
            // The reason is always said in words, including that fetching is off. Only requests that don't choose
            // definitions (FetchedDefinitions false, kept for tests) leave it to the reader.
            if (choice.Definition is null && request.FetchedDefinitions) throw TableDefinitionSources.Unusable(entry, build, layout, choice, fetchEnabled, request.Feature);
            definition = choice.Definition ?? TableDefinition.Bundled(bundled);
        }
        decoded.Position = 0;
        var decodedSha = selected.Receipt.Projection?.IdentitySha256 ?? Convert.ToHexStringLower(await SHA256.HashDataAsync(decoded, token)); decoded.Position = 0;
        var binding = new TableArtifactBinding(selected.Receipt.Sha256, decodedSha, definition.Sha256,
            definition.Revision, request.ResearchLocale,
            AdmissionRevision(entry.TableId, definition, bundled, build, layout, selected.Receipt.Projection is not null, token),
            TableRowArtifacts.SchemaSelectionKey(definition.Document, build, token));
        var artifact = await catalog.FindTableArtifactAsync(TableRowArtifacts.ContentKey(binding), token);
        if (artifact is null)
        {
            await progress("Decoding and validating table records");
            artifact = await rows.BuildAsync(decoded, definition.Document, context.Build.Version, binding, progress, token,
                AdmissionLimits(definition, bundled, build, layout));
            if (!string.Equals(artifact.Header.TableHash, entry.TableId, StringComparison.OrdinalIgnoreCase))
                throw new DiscoveryException("TABLE_IDENTITY_MISMATCH", "The verified file header does not identify this provider table.");
            await catalog.RecordTableArtifactAsync(artifact, token);
        }
        else
        {
            ValidateInterpretation(artifact, definition.Document, context.Build.Version);
            await rows.ReadPageAsync(artifact, 0, 1, null, token);
        }
        var preparedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var admission = new StoredTableAdmission(artifact, selected.Receipt, selected.Association, preparedAt);
        await ValidateSourceAsync(context, admission.Association, token);
        Validate(admission, entry, definition, bundled, context.Build.Version);
        await progress("Saving exact table admission");
        await catalog.CommitTableAdmissionAsync(context, request.TableId, request.ResearchLocale,
            selected.Receipt, selected.Association, artifact, preparedAt, token);
        // Durable success wins a subsequent cancellation; publication and SQL are separate resources.
        return Snapshot(request, admission, definition, context.Build.Version, bundled);
    }

    public async Task<TableDataReadiness> ReadAsync(TableDataRequest request, CancellationToken token)
    {
        var (context, _, entry, bundled, fetchEnabled) = await ResolveAsync(request, token);
        var admission = await catalog.ReadTableAdmissionAsync(context, request.TableId, request.ResearchLocale, token);
        if (admission is null) return new(null);
        await ValidateSourceAsync(context, admission.Association, token);
        var definition = await definitions.ForRowsAsync(entry, bundled, context.Build.Version, admission.Artifact, fetchEnabled, token);
        try { Validate(admission, entry, definition, bundled, context.Build.Version); }
        catch (DiscoveryException failure) when (failure.Code == "TABLE_INTERPRETATION_STALE") { return new(null); }
        // Rows read without an exact bundled definition say "not prepared" while WoWDBDefs is due to be asked, so
        // Tables prepares again: that reuses the stored file and upgrades when upstream now lists the build (D081).
        if (await definitions.RefreshDueAsync(entry, bundled, context.Build.Version, admission.Artifact.Header.LayoutHash, fetchEnabled, token)) return new(null);
        await rows.ReadPageAsync(admission.Artifact, 0, 1, null, token);
        return new(Snapshot(request, admission, definition, context.Build.Version, bundled));
    }

    public async Task<TableDataPage> ReadRowsAsync(TableRowsRequest request, CancellationToken token)
    {
        var dataRequest = new TableDataRequest(request.ProductId, request.BuildIdentity, request.SourceRegion, request.TableId, request.ResearchLocale, FetchedDefinitions: true);
        var (context, _, entry, bundled, fetchEnabled) = await ResolveAsync(dataRequest, token);
        var admission = await catalog.ReadTableAdmissionAsync(context, request.TableId, request.ResearchLocale, token)
            ?? throw new DiscoveryException("TABLE_NOT_PREPARED", "Load this exact table and research locale before reading its records.");
        await ValidateSourceAsync(context, admission.Association, token);
        Validate(admission, entry, await definitions.ForRowsAsync(entry, bundled, context.Build.Version, admission.Artifact, fetchEnabled, token), bundled, context.Build.Version);
        if (request.ArtifactId != admission.Artifact.ContentKey)
            throw new DiscoveryException("TABLE_ARTIFACT_STALE", "This row page belongs to a different table interpretation. Reopen the table data.");
        var page = await rows.ReadPageAsync(admission.Artifact,
            request.After is null ? 0 : long.Parse(request.After, CultureInfo.InvariantCulture), request.Limit,
            request.RecordId is null ? null : uint.Parse(request.RecordId, CultureInfo.InvariantCulture), token);
        return TableRowProjection.Page(request, page, token);
    }

    private async Task<(CapturedOpeningContext Context, BundledTableDefinitions Provider, TableCatalogEntry Entry, BundledTableDefinition Bundled, bool FetchEnabled)>
        ResolveAsync(TableDataRequest request, CancellationToken token)
    {
        var context = await catalog.ResolveOpeningAsync(new(request.ProductId, request.BuildIdentity, request.SourceRegion), token);
        if (!await catalog.HasAdmissionAsync(context, token))
            throw new DiscoveryException("WORKSPACE_NOT_OPEN", "Open this exact build before loading its table records.");
        if (!context.Build.ProductId.StartsWith("wow", StringComparison.Ordinal))
            throw new DiscoveryException("TABLE_PRODUCT_UNSUPPORTED", "This table reader currently supports WoW products.");
        var provider = await TablesCatalog.Provider.Value.WaitAsync(token);
        var entry = provider.Entries.SingleOrDefault(entry => entry.TableId == request.TableId)
            ?? throw new DiscoveryException("TABLE_NOT_IN_PROVIDER", "This exact table identity is not listed by the pinned definition provider.");
        if (!provider.Definitions.TryGetValue(entry.TableId, out var definition))
            throw new DiscoveryException("DEFINITION_NOT_BUNDLED", "This table's definition is not bundled yet.");
        if (entry.Db2FileDataId is null)
            throw new DiscoveryException("TABLE_FILE_ID_UNKNOWN", "The pinned provider does not identify this table's DB2 FileDataID.");
        // An unlisted build can still open when the acquired file's layout selects a block on its version line (D080),
        // or, with the Settings switch on, through a definition fetched from WoWDBDefs (D081).
        // Tables and every feature prerequisite choose the same way and honour the same switch (D101).
        var fetchEnabled = request.FetchedDefinitions && await definitions.FetchEnabledAsync(token);
        if (!fetchEnabled && !OpensBuild(definition.Document, context.Build.Version, token))
            throw request.FetchedDefinitions
                ? TableDefinitionSources.Unusable(entry, context.Build.Version, null, new(null, null, null), false, request.Feature)
                : new DiscoveryException("SCHEMA_BUILD_NOT_LISTED", "The pinned definition does not list this exact build or any layout on its version line.");
        return (context, provider, entry, definition, fetchEnabled);
    }
    // The file's layout picks bundled or fetched (D081); null when the header can't be read, so the reader reports why.
    private static async Task<string?> LayoutAsync(Stream decoded, CancellationToken token)
    {
        if (decoded.Length < 204) return null;
        var header = new byte[204];
        try { decoded.Position = 0; await decoded.ReadExactlyAsync(header, token); }
        catch (Exception error) when (error is IOException or EndOfStreamException or DiscoveryException) { return null; }
        finally { decoded.Position = 0; }
        return Wdc5TableReader.LayoutHash(header);
    }
    internal static bool OpensBuild(DbdDocument definition, string buildVersion, CancellationToken token) =>
        definition.ListsBuild(buildVersion, token) || definition.LayoutCandidates(buildVersion, token).Count > 0;
    private async Task ValidateSourceAsync(CapturedOpeningContext context, SelectedFileAssociation association, CancellationToken token) =>
        ValidateSourceAssociation(await ReadBuildConfigurationAsync(context, token), association);
    private async Task<TactConfiguration> ReadBuildConfigurationAsync(CapturedOpeningContext context, CancellationToken token)
    {
        var configuration = await catalog.FindConfigurationAsync(context.Build.BuildConfig, token)
            ?? throw new DiscoveryException("LOCAL_CONFIGURATION_MISSING", "This table's exact build configuration proof is missing.");
        var bytes = await objects.ReadAsync(configuration.Sha256, configuration.ByteLength, token);
        if (Convert.ToHexStringLower(MD5.HashData(bytes)) != context.Build.BuildConfig)
            throw new DiscoveryException("CONFIG_INTEGRITY", "Retained configuration bytes do not match this exact build.");
        return TactConfigurationParser.Parse(bytes, token);
    }
    internal static void ValidateSourceAssociation(TactConfiguration configuration, SelectedFileAssociation association)
    {
        var encoding = DownloadDeclarations.ReadEncoding(configuration);
        if (association.EncodingSource.ContentKey != encoding.ContentKey || association.EncodingSource.EncodingKey != encoding.EncodingKey ||
            association.EncodingSource.EncodedBytes != N(encoding.EncodedBytes) || association.EncodingSource.DecodedBytes != N(encoding.DecodedBytes) ||
            !configuration.Fields.TryGetValue("root", out var keys) || keys.Count != 1 ||
            !string.Equals(keys[0], association.RootSource.ContentKey, StringComparison.OrdinalIgnoreCase))
            throw new DiscoveryException("TABLE_SOURCE_CONTEXT_MISMATCH", "Retained table lookup receipts do not belong to this exact build configuration.");
    }
    internal static void Validate(StoredTableAdmission admission, TableCatalogEntry entry, TableDefinition definition, BundledTableDefinition bundled, string buildVersion)
    {
        var artifact = admission.Artifact;
        // Rows read with another definition (bundled or fetched) are stale, never an interpretation error.
        if (artifact.Binding.DefinitionSha256 != definition.Sha256 || artifact.Binding.ProviderRevision != definition.Revision)
            throw new DiscoveryException("TABLE_INTERPRETATION_STALE", "Retained rows do not match this exact provider definition and reader revision.");
        ValidateInterpretation(artifact, definition.Document, buildVersion);
        if (admission.Association.FileDataId != entry.Db2FileDataId || artifact.Binding.DecoderRevision !=
                AdmissionRevision(entry.TableId, definition, bundled, buildVersion, artifact.Header.LayoutHash, admission.Source.Projection is not null) ||
            admission.Source.Projection is { } projection && artifact.Binding.DecodedSha256 != projection.IdentitySha256 ||
            artifact.Binding.SchemaSelectionSha256 != TableRowArtifacts.SchemaSelectionKey(definition.Document, buildVersion) ||
            !string.Equals(artifact.Header.TableHash, entry.TableId, StringComparison.OrdinalIgnoreCase))
            throw new DiscoveryException("TABLE_INTERPRETATION_STALE", "Retained rows do not match this exact provider definition and reader revision.");
    }
    internal static void ValidateInterpretation(TableRowsArtifact artifact, DbdDocument definition, string buildVersion)
    {
        var selection = definition.SelectFile(buildVersion, artifact.Header.LayoutHash);
        if (selection.Blocks.Count != 1)
            throw new DiscoveryException(selection.Blocks.Count == 0 ? "DB2_SCHEMA_MISSING" : "DB2_SCHEMA_AMBIGUOUS",
                "The actual layout and exact build do not select one definition block.");
    }
    internal static TableDataSnapshot Snapshot(TableDataRequest request, StoredTableAdmission admission, TableDefinition definition, string buildVersion, BundledTableDefinition bundled)
    {
        var a = admission.Artifact; var c = a.Completion;
        var columns = a.Header.Columns.Select(column => ProjectColumn(column, definition.Document)).ToArray();
        return WithDefinitionFacts(new(request.ProductId, request.BuildIdentity, request.SourceRegion, request.TableId, request.ResearchLocale,
            a.ContentKey, admission.PreparedAt, a.Header.LayoutHash.ToUpperInvariant(), a.Binding.DefinitionSha256,
            a.Binding.ProviderRevision, a.Binding.DecoderRevision, admission.Source, admission.Association, Array.AsReadOnly(columns),
            new(c.Scope, N(c.DeclaredRecords), N(c.PhysicalRows), N(c.CopyRows), N(c.ExcludedEncryptedSections),
                N(checked(c.ExcludedPhysicalRecords + c.ExcludedCopyRecords)), N(a.Header.DeclaredCopyRecords),
                N(c.ExcludedPhysicalRecords), N(c.ExcludedCopyRecords)), N(checked(c.PhysicalRows + c.CopyRows)), N(c.TextIssueCount)),
            definition, buildVersion, bundled);
    }
    /// <summary>How a snapshot says which definition read its rows: a layout match, and a fetched definition with the rule
    /// that chose it (D081). Features hydrate the snapshots they show with it too (D101).</summary>
    internal static TableDataSnapshot WithDefinitionFacts(TableDataSnapshot snapshot, TableDefinition definition, string buildVersion, BundledTableDefinition bundled)
    {
        var match = definition.Document.SelectFile(buildVersion, snapshot.LayoutHash).Match == "layout" ? TableSchemaMatch.LayoutOnly : null;
        return snapshot with
        {
            SchemaMatch = match,
            FetchedDefinition = definition.Fetched?.Info,
            FetchedBecause = definition.Fetched is null ? null
                : bundled.Document.ListsBuild(buildVersion) ? TableFetchedBecause.LayoutNotBundled
                : match is not null ? TableFetchedBecause.LayoutMatch : TableFetchedBecause.ExactBuild,
            // Whether turning fetching off would still open this file: the bundled definition matches its layout (D080).
            BundledFallback = definition.Fetched is null ? null : TableDefinitionSources.ByLayout(bundled.Document, buildVersion, snapshot.LayoutHash, default),
        };
    }
    // Public table metadata is one projection of the exact pinned definition. Feature
    // indexes may retain older internal descriptors without rewriting their immutable bytes.
    internal static TableDataColumn ProjectColumn(Db2Column column, DbdDocument definition)
    {
        var name = column.Name.Split('[')[0];
        var declared = definition.Columns.SingleOrDefault(value => value.Name == name);
        return new(column.Name, column.Kind, column.IsId,
            column.Interpretation is null && declared?.Reference is { } reference ? new(reference.Table, reference.Column) : null,
            column.Interpretation is { } interpretation ? new(interpretation.Policy, interpretation.PhysicalBits,
                interpretation.PhysicalSigned, interpretation.CandidateBits, interpretation.CandidateUnsigned, interpretation.Warning) : null,
            column.Narrowing is { } narrowing ? new(narrowing.StoredBits, narrowing.UsedBits) : null);
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    public async ValueTask DisposeAsync() { await DisposeReferenceReadersAsync(); await rows.DisposeAsync(); acquisition.Dispose(); }
}
