using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Builds;

namespace VibeMiner.Concepts.Items;

/// <summary>
/// The list rules every <see cref="IBuildItems"/> shares (policy <see cref="QueryPolicy"/>): request validation, the name match
/// and the cursor. Bad arguments throw <see cref="ArgumentException"/> (or <see cref="ArgumentNullException"/>,
/// <see cref="ArgumentOutOfRangeException"/>).
/// </summary>
public static class ItemQueries
{
    public const string QueryPolicy = "ordinal-ignorecase-contains-v1";
    /// <summary>The order entries come in: the scanned table's layer 5 row order (D451).</summary>
    public const string Order = "table-row-order-v1";
    public const int MaxLimit = 50;
    public const int MaxQueryScalars = 128;
    public const int MaxQueryUtf8Bytes = 512;
    /// <summary>A query longer than this in UTF-16 units is refused before it is decoded.</summary>
    public const int MaxQueryLength = 256;
    public const int MaxCursorLength = 2048;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 4 };

    /// <summary>
    /// Checks the limit (1 to <see cref="MaxLimit"/>), the query (<see cref="IsValidQuery"/>), that an exact ID comes with an
    /// empty query, and the cursor's framing (<see cref="IsWellFormedCursor"/>). Whether a cursor belongs to the request is the
    /// page's InvalidCursor, not an argument error.
    /// </summary>
    public static void Validate(ItemQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1, nameof(query));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, MaxLimit, nameof(query));
        if (!IsValidQuery(query.Query)) throw new ArgumentException("The name query is not a bounded literal: at most 128 Unicode scalars, 512 UTF-8 bytes, no NUL.", nameof(query));
        if (query.ExactId is not null && query.Query.Length != 0) throw new ArgumentException("An exact item ID can't be combined with a name query.", nameof(query));
        if (query.After is { } after && !IsWellFormedCursor(after)) throw new ArgumentException("The cursor is malformed.", nameof(query));
    }

    /// <summary>
    /// A literal query: at most <see cref="MaxQueryLength"/> UTF-16 units, well-formed UTF-16, no NUL, at most
    /// <see cref="MaxQueryScalars"/> scalars and <see cref="MaxQueryUtf8Bytes"/> UTF-8 bytes. Null is false.
    /// </summary>
    public static bool IsValidQuery(string? value)
    {
        if (value is null || value.Length > MaxQueryLength) return false;
        var remaining = value.AsSpan(); var scalars = 0; var bytes = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done ||
                rune.Value == 0 || ++scalars > MaxQueryScalars || (bytes += rune.Utf8SequenceLength) > MaxQueryUtf8Bytes) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }

    /// <summary>An item ID as text: decimal uint32, 1 to 10 ASCII digits, no sign, no leading zeros, no whitespace.</summary>
    public static bool TryParseItemId(string? value, out uint id)
    {
        id = 0;
        return value is { Length: >= 1 and <= 10 } && (value.Length == 1 || value[0] != '0') && value.All(char.IsAsciiDigit) &&
            uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    /// <summary>Cursor framing: 1 to <see cref="MaxCursorLength"/> base64url characters, unpadded, that round-trip exactly.</summary>
    public static bool IsWellFormedCursor(string? value)
    {
        if (value is not { Length: >= 1 and <= MaxCursorLength } || value.Any(ch =>
            ch is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))) return false;
        try { return Base64Url(FromBase64Url(value)) == value; }
        catch (FormatException) { return false; }
    }

    /// <summary>Whether a name passes a query: an empty query passes every name; a non-empty one only a Valid name containing it, ordinal ignore-case.</summary>
    public static bool Matches(ItemName name, string query)
    {
        ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(query);
        return query.Length == 0 || name.State == ItemNameState.Valid && name.Value!.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The request a cursor is bound to: the policy, order, build, locale, query, exact ID and limit (not the cursor itself).</summary>
    public static string Binding(BuildRef build, string locale, ItemQuery query)
    {
        ArgumentNullException.ThrowIfNull(build); ArgumentNullException.ThrowIfNull(query);
        var text = string.Join('\n', QueryPolicy, Order, build.Product, build.BuildConfig, build.CdnConfig, build.Region ?? "", locale,
            query.Query, query.ExactId?.ToString(CultureInfo.InvariantCulture) ?? "", query.Limit.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    /// <summary>
    /// An opaque cursor: the request binding, the last row ordinal consumed, the scanned table's layer 5 page identity and the
    /// other table's content key, so a changed table reads as Stale.
    /// </summary>
    public static string EncodeCursor(ItemCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return Base64Url(JsonSerializer.SerializeToUtf8Bytes(cursor, Json));
    }

    /// <summary>The cursor, or null when it doesn't decode to one made for <paramref name="binding"/>.</summary>
    public static ItemCursor? DecodeCursor(string value, string binding)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            var cursor = JsonSerializer.Deserialize<ItemCursor>(FromBase64Url(value), Json);
            if (cursor is null || cursor.B != binding || cursor.A < 0 || EncodeCursor(cursor) != value) return null;
            return cursor;
        }
        catch (Exception failure) when (failure is JsonException or FormatException) { return null; }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }
}

/// <summary>A decoded cursor: binding, last ordinal consumed, scanned table identity, other table's content key. Short names keep it small.</summary>
public sealed record ItemCursor(string B, long A, string? I, string? O);
