using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Builds;
using VibeMiner.Concepts.Items;

namespace VibeMiner.Concepts.Creatures;

/// <summary>
/// The list rules every <see cref="IBuildCreatures"/> shares (policy <see cref="QueryPolicy"/>): the same request limits, literal
/// match and cursor framing as items (<see cref="ItemQueries"/>), on the primary name only. Bad arguments throw
/// <see cref="ArgumentException"/> (or <see cref="ArgumentNullException"/>, <see cref="ArgumentOutOfRangeException"/>).
/// </summary>
public static class CreatureQueries
{
    public const string QueryPolicy = "primary-name-ordinal-ignorecase-contains-v1";
    /// <summary>The order entries come in: the Creature table's layer 5 row order (D451, D462).</summary>
    public const string Order = "table-row-order-v1";
    public const int MaxLimit = ItemQueries.MaxLimit;

    /// <summary>Checks the limit (1 to 50), the query (<see cref="ItemQueries.IsValidQuery"/>), that an exact ID comes with an empty query, and the cursor's framing.</summary>
    public static void Validate(CreatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1, nameof(query));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, MaxLimit, nameof(query));
        if (!ItemQueries.IsValidQuery(query.Query)) throw new ArgumentException("The name query is not a bounded literal: at most 128 Unicode scalars, 512 UTF-8 bytes, no NUL.", nameof(query));
        if (query.ExactId is not null && query.Query.Length != 0) throw new ArgumentException("An exact creature ID can't be combined with a name query.", nameof(query));
        if (query.After is { } after && !ItemQueries.IsWellFormedCursor(after)) throw new ArgumentException("The cursor is malformed.", nameof(query));
    }

    /// <summary>An empty query passes every name; a non-empty one only a Valid name containing it, ordinal ignore-case.</summary>
    public static bool Matches(CreatureName name, string query)
    {
        ArgumentNullException.ThrowIfNull(name); ArgumentNullException.ThrowIfNull(query);
        return query.Length == 0 || name.State == CreatureNameState.Valid && name.Value!.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The request a cursor is bound to: the policy, order, build, locale, query, exact ID and limit.</summary>
    public static string Binding(BuildRef build, string locale, CreatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(build); ArgumentNullException.ThrowIfNull(query);
        var text = string.Join('\n', QueryPolicy, Order, build.Product, build.BuildConfig, build.CdnConfig, build.Region ?? "", locale,
            query.Query, query.ExactId?.ToString(CultureInfo.InvariantCulture) ?? "", query.Limit.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    /// <summary>An opaque cursor: the binding, the last row ordinal consumed and the Creature table's layer 5 page identity.</summary>
    public static string EncodeCursor(string binding, long after, string? identity) => ItemQueries.EncodeCursor(new ItemCursor(binding, after, identity, null));

    /// <summary>The cursor's last ordinal and identity, or null when it wasn't made for <paramref name="binding"/>.</summary>
    public static (long After, string? Identity)? DecodeCursor(string value, string binding) =>
        ItemQueries.DecodeCursor(value, binding) is { O: null } cursor ? (cursor.A, cursor.I) : null;
}
