using System.Globalization;
using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>Demand-only table admission. Large sources and rows stay immutable outside SQLite.</summary>
public sealed class TableDataStore : ITableDataStore, IAsyncDisposable
{
    private readonly DiscoveryCatalog catalog;
    private readonly VerifiedObjectStore objects;
    private readonly SelectedFileAcquirer acquisition;
    private readonly TableRowArtifacts rows;
    internal const string DecoderRevision = Wdc5TableReader.Revision + "+table-admission-v3";
    internal const string ProjectionDecoderRevision = Wdc5TableReader.Revision + "+table-admission-projection-v3";
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
    public TableDataStore(DiscoveryCatalog catalog, VerifiedObjectStore objects, HttpClient client,
        ISharedAcquisition? shared = null, AcquisitionResources? resources = null)
    { this.catalog = catalog; this.objects = objects; acquisition = new(catalog, objects, client, shared, resources);
        rows = resources is null ? new(objects) : new(objects, resources, null); }

    // Each observed candidate-width inconsistency is evidence-only. The parser
    // additionally matches actual table/layout/field/storage metadata before Header.
    // Changing this fixed production profile requires a new admission revision.
    private static Wdc5Limits AdmissionLimits(BundledTableDefinition definition) => new(
        AllowPartialEncryptedSections: true, AllowRawTextEvidence: true,
        DefinitionSha256: definition.Sha256,
        VerifiedRawIntegerRules: definition.Sha256 switch
        {
            "e06461a556669dd560dc8908e417fac9343894f4fd4d446e2d3dc721a908891c" =>
                [new(definition.Sha256, "95D8638F", "1E67DB87", 6, "PrerequisiteProficiency", 3, 32, 8, false)],
            "64f98132c6acae55be7bb27696d11778e5dd6654e8647ea539cad075dfb3c425" =>
                [new(definition.Sha256, "BFDAF9F1", "7275F5F6", 8, "NPCSoundID", 3, 32, 16, true),
                 new(definition.Sha256, "BFDAF9F1", "7275F5F6", 12, "ObjectEffectPackageID", 2, 32, 16, true),
                 new(definition.Sha256, "BFDAF9F1", "7275F5F6", 13, "AnimReplacementSetID", 2, 32, 16, true)],
            ItemEffectDefinitionSha256 =>
                [new(definition.Sha256, "4002A5B1", "4CA77678", 3, "Charges", 3, 32, 16, false),
                 new(definition.Sha256, "4002A5B1", "4CA77678", 6, "SpellCategoryID", 3, 32, 16, true),
                 new(definition.Sha256, "4002A5B1", "4CA77678", 8, "ChrSpecializationID", 3, 32, 16, true)],
            _ => null
        });

    public async Task<TableDataSnapshot> PrepareAsync(TableDataRequest request, Func<string, Task> progress, CancellationToken token)
    {
        var (context, provider, entry, definition) = await ResolveAsync(request, token);
        var retained = await catalog.ReadTableAdmissionAsync(context, request.TableId, request.ResearchLocale, token);
        if (retained is not null)
        {
            await progress("Verifying retained table rows");
            await ValidateSourceAsync(context, retained.Association, token);
            try { Validate(retained, entry, definition, context.Build.Version); }
            catch (DiscoveryException failure) when (failure.Code == "TABLE_INTERPRETATION_STALE") { retained = null; }
            if (retained is not null)
            {
                await rows.ReadPageAsync(retained.Artifact, 0, 1, null, token);
                return Snapshot(request, retained, definition);
            }
            await progress("Updating retained table interpretation");
        }
        await progress("Resolving selected table dependencies");
        await using var selected = await acquisition.AcquireAsync(new(request.ProductId, request.BuildIdentity, request.SourceRegion),
            uint.Parse(entry.Db2FileDataId!, CultureInfo.InvariantCulture), request.ResearchLocale, progress, token);
        var decoded = selected.Decoded;
        decoded.Position = 0;
        var decodedSha = selected.Receipt.Projection?.IdentitySha256 ?? Convert.ToHexStringLower(await SHA256.HashDataAsync(decoded, token)); decoded.Position = 0;
        var binding = new TableArtifactBinding(selected.Receipt.Sha256, decodedSha, definition.Sha256,
            provider.Info.Revision, request.ResearchLocale,
            AdmissionRevision(entry.TableId, definition.Sha256, provider.Info.Revision, selected.Receipt.Projection is not null),
            TableRowArtifacts.SchemaSelectionKey(definition.Document, context.Build.Version, token));
        var artifact = await catalog.FindTableArtifactAsync(TableRowArtifacts.ContentKey(binding), token);
        if (artifact is null)
        {
            await progress("Decoding and validating table records");
            artifact = await rows.BuildAsync(decoded, definition.Document, context.Build.Version, binding, progress, token,
                AdmissionLimits(definition));
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
        Validate(admission, entry, definition, context.Build.Version);
        await progress("Saving exact table admission");
        await catalog.CommitTableAdmissionAsync(context, request.TableId, request.ResearchLocale,
            selected.Receipt, selected.Association, artifact, preparedAt, token);
        // Durable success wins a subsequent cancellation; publication and SQL are separate resources.
        return Snapshot(request, admission, definition);
    }

    public async Task<TableDataReadiness> ReadAsync(TableDataRequest request, CancellationToken token)
    {
        var (context, _, entry, definition) = await ResolveAsync(request, token);
        var admission = await catalog.ReadTableAdmissionAsync(context, request.TableId, request.ResearchLocale, token);
        if (admission is null) return new(null);
        await ValidateSourceAsync(context, admission.Association, token);
        try { Validate(admission, entry, definition, context.Build.Version); }
        catch (DiscoveryException failure) when (failure.Code == "TABLE_INTERPRETATION_STALE") { return new(null); }
        await rows.ReadPageAsync(admission.Artifact, 0, 1, null, token);
        return new(Snapshot(request, admission, definition));
    }

    public async Task<TableDataPage> ReadRowsAsync(TableRowsRequest request, CancellationToken token)
    {
        var dataRequest = new TableDataRequest(request.ProductId, request.BuildIdentity, request.SourceRegion, request.TableId, request.ResearchLocale);
        var (context, _, entry, definition) = await ResolveAsync(dataRequest, token);
        var admission = await catalog.ReadTableAdmissionAsync(context, request.TableId, request.ResearchLocale, token)
            ?? throw new DiscoveryException("TABLE_NOT_PREPARED", "Load this exact table and research locale before reading its records.");
        await ValidateSourceAsync(context, admission.Association, token);
        Validate(admission, entry, definition, context.Build.Version);
        if (request.ArtifactId != admission.Artifact.ContentKey)
            throw new DiscoveryException("TABLE_ARTIFACT_STALE", "This row page belongs to a different table interpretation. Reopen the table data.");
        var page = await rows.ReadPageAsync(admission.Artifact,
            request.After is null ? 0 : long.Parse(request.After, CultureInfo.InvariantCulture), request.Limit,
            request.RecordId is null ? null : uint.Parse(request.RecordId, CultureInfo.InvariantCulture), token);
        return TableRowProjection.Page(request, page, token);
    }

    private async Task<(CapturedOpeningContext, BundledTableDefinitions, TableCatalogEntry, BundledTableDefinition)>
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
        if (definition.Document.SelectBuild(context.Build.Version, token: token).Blocks.Count == 0)
            throw new DiscoveryException("SCHEMA_BUILD_NOT_LISTED", "The pinned definition does not list this exact build.");
        return (context, provider, entry, definition);
    }
    private async Task ValidateSourceAsync(CapturedOpeningContext context, SelectedFileAssociation association, CancellationToken token)
    {
        var configuration = await catalog.FindConfigurationAsync(context.Build.BuildConfig, token)
            ?? throw new DiscoveryException("LOCAL_CONFIGURATION_MISSING", "This table's exact build configuration proof is missing.");
        var bytes = await objects.ReadAsync(configuration.Sha256, configuration.ByteLength, token);
        if (Convert.ToHexStringLower(MD5.HashData(bytes)) != context.Build.BuildConfig)
            throw new DiscoveryException("CONFIG_INTEGRITY", "Retained configuration bytes do not match this exact build.");
        ValidateSourceAssociation(TactConfigurationParser.Parse(bytes, token), association);
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
    private static void Validate(StoredTableAdmission admission, TableCatalogEntry entry, BundledTableDefinition definition, string buildVersion)
    {
        var artifact = admission.Artifact;
        ValidateInterpretation(artifact, definition.Document, buildVersion);
        if (admission.Association.FileDataId != entry.Db2FileDataId || artifact.Binding.DefinitionSha256 != definition.Sha256 ||
            artifact.Binding.ProviderRevision != BundledTableDefinitions.Revision || artifact.Binding.DecoderRevision !=
                AdmissionRevision(entry.TableId, definition.Sha256, BundledTableDefinitions.Revision, admission.Source.Projection is not null) ||
            admission.Source.Projection is { } projection && artifact.Binding.DecodedSha256 != projection.IdentitySha256 ||
            artifact.Binding.SchemaSelectionSha256 != TableRowArtifacts.SchemaSelectionKey(definition.Document, buildVersion) ||
            !string.Equals(artifact.Header.TableHash, entry.TableId, StringComparison.OrdinalIgnoreCase))
            throw new DiscoveryException("TABLE_INTERPRETATION_STALE", "Retained rows do not match this exact provider definition and reader revision.");
    }
    internal static void ValidateInterpretation(TableRowsArtifact artifact, DbdDocument definition, string buildVersion)
    {
        var selection = definition.SelectBuild(buildVersion, artifact.Header.LayoutHash);
        if (selection.Blocks.Count != 1)
            throw new DiscoveryException(selection.Blocks.Count == 0 ? "DB2_SCHEMA_MISSING" : "DB2_SCHEMA_AMBIGUOUS",
                "The actual layout and exact build do not select one definition block.");
    }
    private static TableDataSnapshot Snapshot(TableDataRequest request, StoredTableAdmission admission, BundledTableDefinition definition)
    {
        var a = admission.Artifact; var c = a.Completion;
        var columns = a.Header.Columns.Select(column => ProjectColumn(column, definition.Document)).ToArray();
        return new(request.ProductId, request.BuildIdentity, request.SourceRegion, request.TableId, request.ResearchLocale,
            a.ContentKey, admission.PreparedAt, a.Header.LayoutHash.ToUpperInvariant(), a.Binding.DefinitionSha256,
            a.Binding.ProviderRevision, a.Binding.DecoderRevision, admission.Source, admission.Association, Array.AsReadOnly(columns),
            new(c.Scope, N(c.DeclaredRecords), N(c.PhysicalRows), N(c.CopyRows), N(c.ExcludedEncryptedSections),
                N(checked(c.ExcludedPhysicalRecords + c.ExcludedCopyRecords)), N(a.Header.DeclaredCopyRecords),
                N(c.ExcludedPhysicalRecords), N(c.ExcludedCopyRecords)), N(checked(c.PhysicalRows + c.CopyRows)), N(c.TextIssueCount));
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
                interpretation.PhysicalSigned, interpretation.CandidateBits, interpretation.CandidateUnsigned, interpretation.Warning) : null);
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    public async ValueTask DisposeAsync() { await rows.DisposeAsync(); acquisition.Dispose(); }
}
