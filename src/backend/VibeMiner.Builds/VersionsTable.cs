using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeMiner.Builds;

/// <summary>One data row: its 0-based ordinal among data rows and its cells by column name.</summary>
internal sealed record TableRow(int Ordinal, IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// The version service's table grammar (<c>Name!TYPE:width|...</c>, <c>## seqn = N</c>), strict and bounded. Layer 3 keeps its
/// own reader so it needs no engine project, like layer 1's <c>VersionTable</c>; the rules match <c>MetadataParser</c>.
/// </summary>
internal sealed record VersionsTable(IReadOnlyList<string> Columns, IReadOnlyList<TableRow> Rows, string? Sequence)
{
    public const int MaxBytes = 1024 * 1024, MaxColumns = 64, MaxRows = 10_000, MaxLineChars = 16_384;
    private static readonly Regex Descriptor = new("^([A-Za-z][A-Za-z0-9_]*)!([A-Za-z][A-Za-z0-9_]*):([0-9]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex SequenceComment = new("^## seqn = ([0-9]+)$", RegexOptions.CultureInvariant);

    /// <summary>Parses one reply; throws <see cref="FormatException"/> with the reason when it breaks the grammar.</summary>
    public static VersionsTable Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes) throw new FormatException("The table is over 1 MiB.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new FormatException("The table isn't UTF-8."); }
        if (text.Contains('\0')) throw new FormatException("The table contains NUL.");
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')) throw new FormatException("A lone CR.");
        string[]? columns = null;
        var rows = new List<TableRow>();
        string? sequence = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (line.Length > MaxLineChars) throw new FormatException("A line is too long.");
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                var match = SequenceComment.Match(line);
                if (match.Success)
                {
                    var value = match.Groups[1].Value;
                    if (sequence is not null && sequence != value) throw new FormatException("Two different sequence numbers.");
                    sequence = value;
                }
                continue;
            }
            var cells = line.Split('|');
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
            rows.Add(new(rows.Count, new ReadOnlyDictionary<string, string>(fields)));
        }
        if (columns is null) throw new FormatException("The table has no header.");
        return new(columns, rows, sequence);
    }
}
