using System.Globalization;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

/// <summary>Budgets the actual public row/page shapes, including nullable fields and string IDs.</summary>
internal static class TableRowProjection
{
    internal const int MaxRowBytes = 256 * 1024;
    internal const int MaxPageBytes = 512 * 1024;
    // Artifact JSON omits nulls. The wire adds at most copySourceId:null (20),
    // textIssues:null (18), and quotes around the numeric ID (2). Non-null copy IDs add only 2.
    // Reserve 64 bytes before taking the exact slow path, including margin above that 40-byte maximum.
    private const int MaximumWireRowGrowth = 64;
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };

    internal static void RequireRowBudget(Db2Row row, int artifactJsonBytes)
    {
        if (artifactJsonBytes > MaxRowBytes - MaximumWireRowGrowth) _ = Row(row);
    }

    internal static TableDataRow Row(Db2Row row)
    {
        var projected = new TableDataRow(N(row.Id), row.IsCopy, row.CopySourceId is { } id ? N(id) : null,
            row.Values, row.TextIssues is null ? null : Array.AsReadOnly(row.TextIssues.Select(issue =>
                new TableTextIssue(issue.ColumnIndex, issue.Code, issue.RawBytesHex)).ToArray()));
        if (JsonSerializer.SerializeToUtf8Bytes(projected, WireJson).Length > MaxRowBytes)
            throw new DiscoveryException("TABLE_ROW_LIMIT", "One table record exceeds the bounded readable row response size.");
        return projected;
    }

    internal static TableDataPage Page(TableRowsRequest request, TableArtifactPage source, CancellationToken token)
    {
        var after = request.After is null ? 0 : long.Parse(request.After, CultureInfo.InvariantCulture);
        var rows = new List<TableDataRow>(source.Rows.Count);
        foreach (var row in source.Rows) { token.ThrowIfCancellationRequested(); rows.Add(Row(row)); }
        if (after < 0 || after > source.TotalCount || rows.Count > source.TotalCount - after || rows.Count == 0 && after < source.TotalCount)
            throw new DiscoveryException("TABLE_PAGE_INVALID", "The table row page cannot advance within its exact filtered result.");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var end = checked(after + rows.Count);
            var page = new TableDataPage(request.ProductId, request.BuildIdentity, request.SourceRegion,
                request.TableId, request.ResearchLocale, request.ArtifactId, Array.AsReadOnly(rows.ToArray()), N(source.TotalCount),
                end < source.TotalCount ? N(end) : null);
            if (JsonSerializer.SerializeToUtf8Bytes(page, WireJson).Length <= MaxPageBytes) return page;
            // Trimming changes the next ordinal rather than skipping a record that did not fit.
            // A row individually fits, so a valid bounded context will always admit at least one.
            if (rows.Count <= 1)
                throw new DiscoveryException("TABLE_PAGE_LIMIT", "The table page context and one record exceed the bounded response size.");
            rows.RemoveAt(rows.Count - 1);
        }
    }
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
