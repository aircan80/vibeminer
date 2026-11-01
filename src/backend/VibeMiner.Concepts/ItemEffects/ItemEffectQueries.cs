using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Builds;
using VibeMiner.Concepts.Items;

namespace VibeMiner.Concepts.ItemEffects;

/// <summary>
/// The request rules every <see cref="IBuildItemEffects"/> shares: limits, validation and the cursor. Bad arguments throw
/// <see cref="ArgumentException"/> (or <see cref="ArgumentNullException"/>, <see cref="ArgumentOutOfRangeException"/>).
/// </summary>
public static class ItemEffectQueries
{
    public const string Policy = "item-effects-link-row-order-v1";
    /// <summary>The most entries one page returns (the old page limit, ItemEffectsValidation).</summary>
    public const int MaxLimit = 16;
    /// <summary>The longest name returned as text; a longer one is TextTooLarge.</summary>
    public const int MaxNameUnits = 4096;
    /// <summary>A name longer than this, or malformed UTF-16, isn't a name at all: the name source is Corrupt.</summary>
    public const int MaxSourceNameUnits = 65536;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 4 };

    /// <summary>Checks the limit (1 to <see cref="MaxLimit"/>) and the cursor's framing. Whether a cursor belongs to the request is the page's InvalidCursor.</summary>
    public static void Validate(ItemEffectsQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1, nameof(query));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, MaxLimit, nameof(query));
        if (query.After is { } after && !ItemQueries.IsWellFormedCursor(after)) throw new ArgumentException("The cursor is malformed.", nameof(query));
    }

    /// <summary>The request a cursor is bound to: policy, build, locale, item and limit.</summary>
    public static string Binding(BuildRef build, string locale, uint itemId, int limit)
    {
        ArgumentNullException.ThrowIfNull(build);
        var text = string.Join('\n', Policy, build.Product, build.BuildConfig, build.CdnConfig, build.Region ?? "", locale,
            itemId.ToString(CultureInfo.InvariantCulture), limit.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    /// <summary>An opaque cursor: the binding, the count of matches already returned, the relation index identity and the link.</summary>
    public static string EncodeCursor(ItemEffectsCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor, Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The cursor, or null when it doesn't decode to one made for <paramref name="binding"/>.</summary>
    public static ItemEffectsCursor? DecodeCursor(string value, string binding)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
            var cursor = JsonSerializer.Deserialize<ItemEffectsCursor>(bytes, Json);
            if (cursor is null || cursor.B != binding || cursor.A < 0 || EncodeCursor(cursor) != value) return null;
            return cursor;
        }
        catch (Exception failure) when (failure is JsonException or FormatException) { return null; }
    }
}

/// <summary>A decoded cursor: binding, matches already returned, relation index identity, link name. Short names keep it small.</summary>
public sealed record ItemEffectsCursor(string B, long A, string? I, string L);
