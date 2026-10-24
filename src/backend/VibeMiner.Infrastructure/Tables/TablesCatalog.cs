using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

/// <summary>Local pinned provider metadata. Entry/navigation performs no table acquisition.</summary>
public sealed class TablesCatalog(DiscoveryCatalog catalog, TableDefinitionSources? sources = null) : ITablesCatalog
{
    private readonly TableDefinitionSources definitions = sources ?? new();
    internal static readonly Lazy<Task<BundledTableDefinitions>> Provider = new(() => Task.Run(BundledTableDefinitions.LoadAsync));

    public async Task<TableCatalogPage> ReadCatalogAsync(TableCatalogRequest request, CancellationToken token)
    {
        TablesService.Validate(request);
        await Context(new(request.ProductId, request.BuildIdentity, request.SourceRegion), token);
        var provider = await Provider.Value.WaitAsync(token);
        var result = new List<TableCatalogEntry>();
        foreach (var entry in provider.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (request.Query.Length == 0 || entry.Name?.Contains(request.Query, StringComparison.OrdinalIgnoreCase) == true ||
                entry.TableId.Contains(request.Query, StringComparison.OrdinalIgnoreCase) || entry.Db2FileDataId?.Contains(request.Query, StringComparison.Ordinal) == true ||
                provider.Definitions.TryGetValue(entry.TableId, out var definition) &&
                    definition.Document.Columns.Any(column => column.Name.Contains(request.Query, StringComparison.OrdinalIgnoreCase))) result.Add(entry);
        }
        var after = request.After is null ? 0 : int.Parse(request.After, CultureInfo.InvariantCulture);
        if (after > result.Count) throw new DiscoveryException("INVALID_CURSOR", "The table catalogue cursor exceeds this exact filtered provider catalogue.");
        var page = result.Skip(after).Take(request.Limit).ToArray(); var next = after + page.Length;
        return new(request.ProductId, request.BuildIdentity, request.SourceRegion, provider.Info, request.Query,
            Array.AsReadOnly(page), N(result.Count), next < result.Count ? N(next) : null);
    }

    public async Task<TableDefinitionSnapshot> ReadDefinitionAsync(TableDefinitionRequest request, CancellationToken token)
    {
        TablesService.Validate(request);
        var context = await Context(new(request.ProductId, request.BuildIdentity, request.SourceRegion), token);
        var provider = await Provider.Value.WaitAsync(token);
        var entry = provider.Entries.SingleOrDefault(entry => entry.TableId == request.TableId)
            ?? throw new DiscoveryException("TABLE_NOT_IN_PROVIDER", "This exact table identity is not listed by the pinned definition provider.");
        TableDefinitionInfo? info = null; TablesProblem? problem; string? fetch = null;
        if (!provider.Definitions.TryGetValue(entry.TableId, out var bundled))
            problem = new("DEFINITION_NOT_BUNDLED", "This provider entry is listed, but its definition data is not bundled in this application yet.");
        else
        {
            // Local only (D081): the fetched definition the admitted rows were read with, or a cached one for a build line
            // the bundled definition doesn't cover. Fetching itself happens when the table's data is prepared.
            var fetchEnabled = await definitions.FetchEnabledAsync(token);
            fetch = fetchEnabled ? "on" : "off";
            var definition = await ChooseAsync(context, entry, bundled, fetchEnabled, token);
            var selection = definition.Document.SelectBuild(context.Build.Version, token: token);
            var listed = selection.Blocks.Count > 0;
            // An unlisted build previews the layouts a file could open by on its version line (D080).
            var layouts = (listed ? selection.Blocks.SelectMany(block => block.LayoutHashes.Select(hash => (Hash: hash, Block: block)))
                    : definition.Document.LayoutCandidates(context.Build.Version, token).Select(value => (Hash: value.LayoutHash, value.Block)))
                .GroupBy(value => value.Hash, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();
            if (layouts.Length > 16) throw new DiscoveryException("DEFINITION_LIMIT", "The exact build has too many layout candidates for a bounded definition preview.");
            var conflicting = false;
            var candidates = new List<TableLayoutCandidate>();
            foreach (var layout in layouts)
            {
                token.ThrowIfCancellationRequested();
                var first = layout.First().Block.Fields;
                var conflict = layout.Any(value => !value.Block.Fields.SequenceEqual(first));
                conflicting |= conflict;
                candidates.Add(new(layout.Key, conflict ? Array.Empty<TableDefinitionField>() : Array.AsReadOnly(first.Select(Field).ToArray())));
            }
            info = new(definition.Sha256, definition.SourceUrl,
                Array.AsReadOnly(definition.Document.Columns.Select(Column).ToArray()), candidates.AsReadOnly(), definition.Fetched?.Info);
            var noun = definition.Fetched is null ? "The pinned definition" : "The " + definition.Described;
            problem = !listed && layouts.Length == 0
                ? new("SCHEMA_BUILD_NOT_LISTED", definition.Fetched is null && fetchEnabled
                    ? "The pinned definition does not list this exact build or any layout on its version line. Opening its data first fetches the newest WoWDBDefs definition for this table."
                    : !fetchEnabled
                    ? "The pinned definition does not list this exact build or any layout on its version line, so this table can’t open. Fetching newer table definitions from WoWDBDefs is off in Settings; turn it on to try the newest definition for this table."
                    : $"{noun} does not list this exact build or any layout on its version line; no nearby or latest definition was substituted.")
                : !listed
                ? new("SCHEMA_BUILD_NOT_LISTED", $"{noun} does not list this exact build. Data can open only if the file's layout hash matches one of its candidate layouts; that match is by layout, and the build stays unverified." +
                    (definition.Fetched is null && fetchEnabled ? " Opening its data first checks WoWDBDefs for a definition that lists this build." : ""))
                : selection.Status == "ambiguous" || conflicting || layouts.Length != 1 || selection.Blocks.Any(block => block.LayoutHashes.Count == 0)
                    ? new("SCHEMA_LAYOUT_UNVERIFIED", "The build has multiple or unspecified layout interpretations. Actual source headers are required before selecting a readable schema.")
                    : null;
        }
        return new(request.ProductId, request.BuildIdentity, request.SourceRegion, context.Build.Version, provider.Info, entry, info, problem, fetch);
    }

    private async Task<TableDefinition> ChooseAsync(CapturedOpeningContext context, TableCatalogEntry entry, BundledTableDefinition bundled,
        bool fetchEnabled, CancellationToken token)
    {
        if (!fetchEnabled) return TableDefinition.Bundled(bundled);
        StoredTableAdmission? admission = null;
        // Tables data has one research locale in this version. A damaged admission is reported by Data, not here.
        try { admission = await catalog.ReadTableAdmissionAsync(context, entry.TableId, "enUS", token); }
        catch (DiscoveryException) { admission = null; }
        // The definition Data reads the file's layout with now; rows read with another are refreshed by Data.
        return admission is not null
            ? await definitions.ForRowsAsync(entry, bundled, context.Build.Version, admission.Artifact, fetchEnabled, token)
            : await definitions.BeforeFileAsync(entry, bundled, context.Build.Version, fetchEnabled, token);
    }

    private async Task<CapturedOpeningContext> Context(WorkspaceRequest request, CancellationToken token)
    {
        var context = await catalog.ResolveOpeningAsync(request, token);
        if (!await catalog.HasAdmissionAsync(context, token))
            throw new DiscoveryException("WORKSPACE_NOT_OPEN", "Open this exact build before browsing its definition catalogue.");
        return context;
    }
    private static TableDefinitionField Column(DbdColumn column) => new(column.Name, column.Family, null, null, 1,
        false, false, false, column.VerifiedName, column.Comment,
        column.Reference is { } reference ? new(reference.Table, reference.Column) : null);
    private static TableDefinitionField Field(DbdField field) => new(field.Column.Name, field.Column.Family, field.IntegerBits,
        field.Unsigned, field.ArrayLength, field.IsId, field.NonInline, field.RelationStorage, field.Column.VerifiedName, field.Comment,
        field.Column.Reference is { } reference ? new(reference.Table, reference.Column) : null);
    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
