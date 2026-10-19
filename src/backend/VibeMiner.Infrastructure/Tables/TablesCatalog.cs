using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

/// <summary>Local pinned provider metadata. Entry/navigation performs no table acquisition.</summary>
public sealed class TablesCatalog(DiscoveryCatalog catalog) : ITablesCatalog
{
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
        TableDefinitionInfo? info = null; TablesProblem? problem;
        if (!provider.Definitions.TryGetValue(entry.TableId, out var definition))
            problem = new("DEFINITION_NOT_BUNDLED", "This provider entry is listed, but its definition data is not bundled in this application yet.");
        else
        {
            var selection = definition.Document.SelectBuild(context.Build.Version, token: token);
            var layouts = selection.Blocks.SelectMany(block => block.LayoutHashes.Select(hash => (Hash: hash, Block: block)))
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
                Array.AsReadOnly(definition.Document.Columns.Select(Column).ToArray()), candidates.AsReadOnly());
            problem = selection.Blocks.Count == 0
                ? new("SCHEMA_BUILD_NOT_LISTED", "The pinned definition does not list this exact build; no nearby or latest definition was substituted.")
                : selection.Status == "ambiguous" || conflicting || layouts.Length != 1 || selection.Blocks.Any(block => block.LayoutHashes.Count == 0)
                    ? new("SCHEMA_LAYOUT_UNVERIFIED", "The build has multiple or unspecified layout interpretations. Actual source headers are required before selecting a readable schema.")
                    : null;
        }
        return new(request.ProductId, request.BuildIdentity, request.SourceRegion, context.Build.Version, provider.Info, entry, info, problem);
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
