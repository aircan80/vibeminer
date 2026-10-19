using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using VibeMiner.Domain;

namespace VibeMiner.Protocols.Tact;

public sealed record MetadataColumn(string Name, string Type, string Width, string Descriptor);
public sealed record MetadataRow(int Ordinal, IReadOnlyDictionary<string, string> Fields);
public sealed record MetadataDocument(IReadOnlyList<MetadataColumn> Columns, IReadOnlyList<MetadataRow> Rows,
    string? Sequence);

/// <summary>Original, strict, bounded reader for Blizzard's text metadata table grammar.</summary>
public static class MetadataParser
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxColumns = 64;
    public const int MaxRows = 10_000;
    public const int MaxLineChars = 16_384;
    private static readonly Regex Descriptor = new("^([A-Za-z][A-Za-z0-9_]*)!([A-Za-z][A-Za-z0-9_]*):([0-9]+)$", RegexOptions.CultureInvariant);
    private static readonly Regex SequenceComment = new("^## seqn = ([0-9]+)$", RegexOptions.CultureInvariant);
    public static MetadataDocument Parse(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default)
    {
        if (bytes.Length > MaxBytes) throw Invalid("METADATA_LIMIT", "Metadata exceeds the document limit.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw Invalid("INVALID_METADATA", "Metadata is not valid UTF-8."); }
        if (text.Contains('\0')) throw Invalid("INVALID_METADATA", "Metadata contains NUL.");
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n'))
                throw Invalid("INVALID_METADATA", "Metadata contains an unsupported line ending.");
        List<MetadataColumn>? columns = null;
        var rows = new List<MetadataRow>();
        string? sequence = null;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length > MaxLineChars) throw Invalid("METADATA_LIMIT", "A metadata line exceeds the limit.");
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                var match = SequenceComment.Match(line);
                if (match.Success)
                {
                    var next = match.Groups[1].Value;
                    if (sequence is not null && sequence != next)
                        throw Invalid("INVALID_METADATA", "Metadata has contradictory sequence comments.");
                    sequence = next;
                }
                continue;
            }
            var cells = line.Split('|');
            if (columns is null)
            {
                if (cells.Length > MaxColumns) throw Invalid("METADATA_LIMIT", "Metadata has too many columns.");
                columns = new();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var cell in cells)
                {
                    var match = Descriptor.Match(cell);
                    if (!match.Success || !names.Add(match.Groups[1].Value))
                        throw Invalid("INVALID_METADATA", "Metadata has invalid or duplicate column descriptors.");
                    columns.Add(new(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, cell));
                }
                continue;
            }
            if (cells.Length != columns.Count) throw Invalid("INVALID_METADATA", "Metadata row and header lengths differ.");
            if (rows.Count == MaxRows) throw Invalid("METADATA_LIMIT", "Metadata has too many rows.");
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < cells.Length; i++) fields.Add(columns[i].Name, cells[i]);
            rows.Add(new(rows.Count, new ReadOnlyDictionary<string, string>(fields)));
        }
        if (columns is null) throw Invalid("INVALID_METADATA", "Metadata has no header.");
        return new(columns.AsReadOnly(), rows.AsReadOnly(), sequence);
    }
    private static DiscoveryException Invalid(string code, string message) => new(code, message);
}
