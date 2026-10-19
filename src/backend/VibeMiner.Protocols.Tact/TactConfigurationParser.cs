using System.Collections.ObjectModel;
using System.Text;
using VibeMiner.Domain;

namespace VibeMiner.Protocols.Tact;

public sealed record TactConfiguration(IReadOnlyDictionary<string, IReadOnlyList<string>> Fields);

/// <summary>Pure parser for bounded TACT text configuration assignments; acquisition verifies raw MD5.</summary>
public static class TactConfigurationParser
{
    public const int MaxBytes = 1024 * 1024;
    public const int MaxLines = 16_384;
    public const int MaxLineChars = 65_536;
    public const int MaxFields = 4096;
    public const int MaxValuesPerField = 4096;

    public static TactConfiguration Parse(ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > MaxBytes) throw Error("CONFIG_LIMIT", "Configuration exceeds the byte limit.");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw Error("CONFIG_INVALID", "Configuration is not valid UTF-8."); }
        var fields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var lineCount = 0;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } rawLine)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++lineCount > MaxLines || rawLine.Length > MaxLineChars)
                throw Error("CONFIG_LIMIT", "Configuration exceeds a line limit.");
            foreach (var ch in rawLine)
                if ((char.IsControl(ch) && ch != '\t') || ch == '\ufeff')
                    throw Error("CONFIG_INVALID", "Configuration contains an unsupported control character.");
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var separator = line.IndexOf('=');
            if (separator < 0 || line.IndexOf('=', separator + 1) >= 0)
                throw Error("CONFIG_INVALID", "Configuration assignment must contain one equals sign.");
            var key = line[..separator].Trim();
            if (key.Length == 0 || !IsKeyCharacter(key[0], false) || key.Any(ch => !IsKeyCharacter(ch, true)))
                throw Error("CONFIG_INVALID", "Configuration field name is invalid.");
            var values = line[(separator + 1)..].Trim().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (values.Length > MaxValuesPerField || fields.Count >= MaxFields)
                throw Error("CONFIG_LIMIT", "Configuration exceeds a field or value limit.");
            if (!fields.TryAdd(key, Array.AsReadOnly(values)))
                throw Error("CONFIG_INVALID", "Configuration contains a duplicate field.");
        }
        // StringReader accepts lone CR; the supported wire grammar permits only LF and CRLF.
        for (var i = 0; i < text.Length; i++)
        {
            if ((i & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (text[i] == '\r' && (i + 1 == text.Length || text[i + 1] != '\n'))
                throw Error("CONFIG_INVALID", "Configuration contains an unsupported line ending.");
        }
        return new(new ReadOnlyDictionary<string, IReadOnlyList<string>>(fields));
    }

    private static bool IsKeyCharacter(char ch, bool allowHyphen) => ch is >= 'a' and <= 'z' or >= '0' and <= '9' || allowHyphen && ch == '-';
    private static DiscoveryException Error(string code, string message) => new(code, message);
}
