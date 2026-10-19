using System.Text;

namespace VibeMiner.Domain;

/// <summary>Unavailable text retains exact source bytes; it is never an ordinary empty string.</summary>
public static class TableTextEvidence
{
    public const string InvalidUtf8 = "DB2_TEXT_ENCODING_INVALID";
    public const int MaximumRawCellBytes = 65_535;
    public const long MaximumEmittedRawBytes = 16L << 20;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static long Validate(IReadOnlyList<TableTextIssue>? issues, IReadOnlyList<TableDataColumn> columns,
        IReadOnlyList<string> values, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (issues is null || issues.Count == 0) return 0;
        if (columns.Count != values.Count || issues.Count > columns.Count) throw Invalid();
        var previous = -1; long bytes = 0;
        foreach (var issue in issues)
        {
            token.ThrowIfCancellationRequested();
            if (issue is null || issue.Code != InvalidUtf8 || issue.ColumnIndex <= previous || issue.ColumnIndex >= columns.Count ||
                columns[issue.ColumnIndex].Family != "string" || columns[issue.ColumnIndex].IsId || values[issue.ColumnIndex] != "" ||
                issue.RawBytesHex is null || issue.RawBytesHex.Length is < 2 or > MaximumRawCellBytes * 2 ||
                issue.RawBytesHex.Length % 2 != 0 || issue.RawBytesHex.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw Invalid();
            previous = issue.ColumnIndex;
            var raw = Convert.FromHexString(issue.RawBytesHex);
            if (raw.AsSpan().Contains((byte)0)) throw Invalid();
            var malformed = false;
            try { _ = Utf8.GetCharCount(raw); }
            catch (DecoderFallbackException) { malformed = true; }
            if (!malformed) throw Invalid();
            bytes = checked(bytes + raw.Length);
            if (bytes > MaximumRawCellBytes) throw Invalid();
        }
        return bytes;
    }

    private static DiscoveryException Invalid() => new("TABLE_TEXT_EVIDENCE_INVALID",
        "Unavailable text must retain bounded exact malformed UTF-8 bytes at distinct string columns.");
}
