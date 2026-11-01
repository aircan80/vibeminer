using System.Text.RegularExpressions;
using VibeMiner.ObjectAccess;

namespace VibeMiner.Builds;

/// <summary>
/// What one version-service summary says: every product it lists (sorted, each once), the rows it couldn't read and
/// <c>## seqn</c>. <see cref="Readable"/> is false when the whole reply couldn't be read; then <see cref="Products"/> is empty.
/// </summary>
internal sealed record SummaryRead(IReadOnlyList<string> Products, IReadOnlyList<CatalogProblem> Problems, string? Sequence, bool Readable)
{
    /// <summary>Listed products with no empty-flag (versions) row; empty when the reply has no Flags column.</summary>
    public IReadOnlyList<string> WithoutVersions { get; init; } = [];
}

/// <summary>
/// Reads a summary reply (<c>Product!STRING:0|Seqn!DEC:4|Flags!STRING:0</c>; one row per product and document: an empty Flags
/// is versions, otherwise <c>cdn</c>, <c>bgdl</c> ...). A product is listed when any row names it; with a Flags column, one that no
/// empty-flag row names has no versions document. Pure: bytes in, products and problems out. Never throws for bad bytes.
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
        var versioned = new HashSet<string>(StringComparer.Ordinal);
        var flags = table.Columns.Contains("Flags", StringComparer.Ordinal);
        var problems = new List<CatalogProblem>();
        var bad = 0;
        foreach (var row in table.Rows)
        {
            var product = row.Fields["Product"];
            if (ProductPattern().IsMatch(product))
            {
                products.Add(product);
                if (flags && row.Fields["Flags"].Length == 0) versioned.Add(product);
            }
            else if (++bad <= Catalog.MaxListProblems) problems.Add(new("SUMMARY_ROW_INVALID", "A summary row's product isn't 1-64 of [a-z0-9_-].", Row: row.Ordinal));
        }
        if (bad > Catalog.MaxListProblems) problems.Add(new("CATALOG_PROBLEMS_TRUNCATED", $"{bad - Catalog.MaxListProblems} more summary rows can't be read."));
        return new([.. products], problems, table.Sequence, true) { WithoutVersions = flags ? [.. products.Where(p => !versioned.Contains(p))] : [] };
    }
}

/// <summary>
/// Turns a summary observation into a <see cref="SummaryRefresh"/>; <see cref="Catalog"/>, the fake and <c>vm browse</c> (for the
/// newest stored summary) share it. A reply that arrived but couldn't be recorded is still read: the result's state says it isn't in
/// the history log.
/// </summary>
public static class Summaries
{
    public static SummaryRefresh Read(ObservationResult observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.Observation is not { } observation) return new(observed, [], [], [], null);
        var read = SummaryReply.Read(observation.Bytes);
        var fresh = read.Products.Where(p => Products.IsWow(p) && !Products.IsKnown(p)).ToList();
        return new(observed, fresh, read.Products, read.Problems, read.Sequence) { WithoutVersions = read.WithoutVersions };
    }
}
