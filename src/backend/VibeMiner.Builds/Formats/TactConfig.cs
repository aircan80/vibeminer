using System.Collections.ObjectModel;
using System.Text;

namespace VibeMiner.Builds.Formats;

/// <summary>
/// A TACT text config (build or CDN config): <c>key = value value ...</c> lines, strict and bounded. A port of the
/// engine's <c>TactConfigurationParser</c> with the same grammar and codes (<c>CONFIG_INVALID</c>, <c>CONFIG_LIMIT</c>).
/// Unknown fields and empty values are kept; values keep their order.
/// </summary>
internal sealed record TactConfig(IReadOnlyDictionary<string, IReadOnlyList<string>> Fields)
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxLines = 16_384;
    public const int MaxLineChars = 65_536;
    public const int MaxFields = 4096;
    public const int MaxValuesPerField = 4096;

    public static TactConfig Parse(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > MaxBytes) throw Limit("The config is over the byte limit.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw Invalid("The config isn't UTF-8."); }
        // StringReader accepts a lone CR; the wire grammar allows only LF and CRLF.
        for (var i = 0; i < text.Length; i++)
        {
            if ((i & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n')) throw Invalid("The config has a lone CR.");
        }
        var fields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var lineCount = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } rawLine)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++lineCount > MaxLines || rawLine.Length > MaxLineChars) throw Limit("The config is over a line limit.");
            foreach (var ch in rawLine)
                if ((char.IsControl(ch) && ch != '\t') || ch == '\ufeff') throw Invalid("The config has a control character.");
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator < 0 || line.IndexOf('=', separator + 1) >= 0) throw Invalid("A config line needs exactly one equals sign.");
            var key = line[..separator].Trim();
            if (key.Length == 0 || !IsKeyCharacter(key[0], false) || key.Any(ch => !IsKeyCharacter(ch, true)))
                throw Invalid("A config field name is invalid.");
            var values = line[(separator + 1)..].Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (values.Length > MaxValuesPerField || fields.Count >= MaxFields) throw Limit("The config is over a field or value limit.");
            if (!fields.TryAdd(key, Array.AsReadOnly(values))) throw Invalid("The config repeats a field.");
        }
        return new(new ReadOnlyDictionary<string, IReadOnlyList<string>>(fields));
    }

    private static bool IsKeyCharacter(char ch, bool allowHyphen) => ch is >= 'a' and <= 'z' or >= '0' and <= '9' || allowHyphen && ch == '-';
    private static BuildFormatException Invalid(string message) => new(BuildFailure.Corrupt, "CONFIG_INVALID", message);
    private static BuildFormatException Limit(string message) => new(BuildFailure.Corrupt, "CONFIG_LIMIT", message);
}
