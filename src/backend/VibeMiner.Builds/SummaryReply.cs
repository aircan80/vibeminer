using System.Text.RegularExpressions;
using VibeMiner.ObjectAccess;

namespace VibeMiner.Builds;

/// <summary>
/// What one version-service summary says: every product it lists (sorted, each once), the rows it couldn't read and
/// <c>## seqn</c>. <see cref="Readable"/> is false when the whole reply couldn't be read; then <see cref="Products"/> is empty.
/// </summary>
internal sealed record SummaryRead(IReadOnlyList<string> Products, IReadOnlyList<CatalogProblem> Problems, string? Sequence, bool Readable);

/// <summary>
/// Reads a summary reply (<c>Product!STRING:0|Seqn!DEC:4|Flags!STRING:0</c>; one row per product and document: an empty Flags
/// is versions, otherwise <c>cdn</c>, <c>bgdl</c> ...). Only the Product column is used: a product is listed when any row
/// names it. Pure: bytes in, products and problems out. Never throws for bad bytes.
/// </summary>
internal static partial class SummaryReply
{
    [GeneratedRegex("^[a-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductPattern();

    public static SummaryRead Read(ReadOnlySpan<byte> reply)
    {
        VersionsTable table;
        try { table = VersionsTable.Parse(reply); }
        catch (FormatException e) { return new([], [new("SUMMARY_UNREADABLE", $"The summary can't be read: {e.Message}")], null, false); }
        if (!table.Columns.Contains("Product", StringComparer.Ordinal))
            return new([], [new("SUMMARY_COLUMNS_MISSING", "The summary has no Product column.")], table.Sequence, false);
        var products = new SortedSet<string>(StringComparer.Ordinal);
        var problems = new List<CatalogProblem>();
        var bad = 0;
        foreach (var row in table.Rows)
        {
            var product = row.Fields["Product"];
            if (ProductPattern().IsMatch(product)) products.Add(product);
            else if (++bad <= Catalog.MaxListProblems) problems.Add(new("SUMMARY_ROW_INVALID", "A summary row's product isn't 1-64 of [a-z0-9_-].", Row: row.Ordinal));
        }
        if (bad > Catalog.MaxListProblems) problems.Add(new("CATALOG_PROBLEMS_TRUNCATED", $"{bad - Catalog.MaxListProblems} more summary rows can't be read."));
        return new([.. products], problems, table.Sequence, true);
    }
}

/// <summary>
/// Turns a summary observation into a <see cref="SummaryRefresh"/>; <see cref="Catalog"/> and the fake share it. A reply that
/// arrived but couldn't be recorded is still read: the result's state says it isn't in the history log.
/// </summary>
internal static class Summaries
{
    public static SummaryRefresh Read(ObservationResult observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.Observation is not { } observation) return new(observed, [], [], [], null);
        var read = SummaryReply.Read(observation.Bytes);
        var fresh = read.Products.Where(p => Products.LooksLikeWow(p) && !Products.IsKnown(p)).ToList();
        return new(observed, fresh, read.Products, read.Problems, read.Sequence);
    }
}
