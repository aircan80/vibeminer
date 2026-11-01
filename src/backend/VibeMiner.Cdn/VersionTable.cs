using System.Text;
using System.Text.RegularExpressions;

namespace VibeMiner.Cdn;

/// <summary>
/// The version service's table grammar (<c>Name!TYPE:width|...</c>, <c>## seqn = N</c>), strict and bounded. Layer 1 keeps
/// its own reader so it needs no engine project; the rules match <c>MetadataParser</c> (docs/formats/tact.md).
/// </summary>
public sealed record VersionTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyDictionary<string, string>> Rows, long? Sequence)
{
    private const int MaxColumns = 64, MaxRows = 10_000, MaxLineChars = 16_384;
    private static readonly Regex Descriptor = new("^([A-Za-z][A-Za-z0-9_]*)!([A-Za-z][A-Za-z0-9_]*):([0-9]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex SequenceComment = new("^## seqn = ([0-9]{1,18})$", RegexOptions.CultureInvariant);

    /// <summary>Parses one reply; throws <see cref="FormatException"/> with the reason when it breaks the grammar.</summary>
    public static VersionTable Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > VersionClient.MaxBytes) throw new FormatException("The table is over 1 MiB.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new FormatException("The table isn't UTF-8."); }
        if (text.Contains('\0')) throw new FormatException("The table contains NUL.");
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')) throw new FormatException("A lone CR.");
        string[]? columns = null;
        var rows = new List<IReadOnlyDictionary<string, string>>();
        long? sequence = null;
        foreach (var line in text.Split('\n'))
        {
            var row = line.EndsWith('\r') ? line[..^1] : line;
            if (row.Length > MaxLineChars) throw new FormatException("A line is too long.");
            if (row.Length == 0) continue;
            if (row.StartsWith('#'))
            {
                var match = SequenceComment.Match(row);
                if (match.Success)
                {
                    var value = long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (sequence is not null && sequence != value) throw new FormatException("Two different sequence numbers.");
                    sequence = value;
                }
                continue;
            }
            var cells = row.Split('|');
            if (columns is null)
            {
                if (cells.Length > MaxColumns) throw new FormatException("Too many columns.");
                columns = new string[cells.Length];
                for (var i = 0; i < cells.Length; i++)
                {
                    var match = Descriptor.Match(cells[i]);
                    if (!match.Success) throw new FormatException("A column descriptor is invalid.");
                    columns[i] = match.Groups[1].Value;
                }
                if (columns.Distinct(StringComparer.Ordinal).Count() != columns.Length) throw new FormatException("Duplicate columns.");
                continue;
            }
            if (cells.Length != columns.Length) throw new FormatException("A row has the wrong number of cells.");
            if (rows.Count == MaxRows) throw new FormatException("Too many rows.");
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < cells.Length; i++) fields.Add(columns[i], cells[i]);
            rows.Add(fields);
        }
        if (columns is null) throw new FormatException("The table has no header.");
        return new(columns, rows, sequence);
    }
}

/// <summary>Routes from a <c>cdns</c> reply, by region. Hosts keep their advertised order; non-official hosts are dropped.</summary>
public static class CdnsRoutes
{
    /// <summary>
    /// The usable route per region. A region with no row, more than one row, no path or no official host has no route;
    /// those regions are listed in <paramref name="unusable"/> with the reason.
    /// </summary>
    public static IReadOnlyDictionary<string, CdnRoute> Read(VersionTable table, out IReadOnlyDictionary<string, string> unusable)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!table.Columns.Contains("Name") || !table.Columns.Contains("Path") || !table.Columns.Contains("Hosts"))
            throw new FormatException("A cdns table has Name, Path and Hosts columns.");
        var routes = new SortedDictionary<string, CdnRoute>(StringComparer.Ordinal);
        var bad = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in table.Rows.GroupBy(r => r["Name"], StringComparer.Ordinal))
        {
            var rows = group.ToArray();
            if (rows.Length != 1) { bad[group.Key] = "ROUTE_AMBIGUOUS"; continue; }
            var hosts = rows[0]["Hosts"].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            try { routes[group.Key] = new CdnRoute(hosts, rows[0]["Path"]); }
            catch (ArgumentException) { bad[group.Key] = "ROUTE_INVALID"; }
        }
        unusable = bad;
        return routes;
    }
}
