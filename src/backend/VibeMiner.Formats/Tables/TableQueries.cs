namespace VibeMiner.Formats.Tables;

/// <summary>Reads up to <paramref name="count"/> rows from row <paramref name="firstRow"/> of an ordered row source; fewer only at its end.</summary>
public delegate Task<IReadOnlyList<Db2Row>> TableRowReader(long firstRow, int count, CancellationToken cancellationToken);

/// <summary>
/// The query rules every <see cref="IBuildTables"/> shares: request validation, the integer rule filters use, and paging over an
/// ordered row source. Bad arguments throw <see cref="ArgumentException"/> (or <see cref="ArgumentNullException"/>,
/// <see cref="ArgumentOutOfRangeException"/>).
/// </summary>
public static partial class TableQueries
{
    /// <summary>The most rows one page or follow returns. Its own cap, not the generated store's page size: a service reads across
    /// store pages when a limit needs it.</summary>
    public const int MaxLimit = 1000;
    public const int DefaultLimit = 50;
    /// <summary>The most row IDs one referenced-by source returns.</summary>
    public const int MaxRowsPerSource = 1000;
    /// <summary>The most digits a filter integer has, besides its sign (uint64 max and int64 min both fit).</summary>
    public const int MaxIntegerDigits = 20;
    /// <summary>The longest filter column name.</summary>
    public const int MaxFilterColumnLength = 128;
    /// <summary>The longest filter value or upper bound.</summary>
    public const int MaxFilterValueLength = 256;

    /// <summary>The states whose rows can be read.</summary>
    public static bool IsReadable(TableState state) => state is TableState.Ready or TableState.Partial;

    /// <summary>Throws when <paramref name="table"/> is <c>default</c>.</summary>
    public static void Validate(TableRef table, string paramName = "table")
    {
        if (table.FileDataId is null && table.Name is null) throw new ArgumentException("The table reference is empty.", paramName);
    }

    public static void Validate(TableListFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.States is { } states && states.Any(state => !Enum.IsDefined(state)))
            throw new ArgumentException("The filter names an unknown table state.", nameof(filter));
    }

    /// <summary>
    /// Checks the table, cursor and limit, at most <see cref="MaxFilters"/> filters, and each filter's shape: a column name of at
    /// most <see cref="MaxFilterColumnLength"/> characters, values of at most <see cref="MaxFilterValueLength"/>, a known op, and an upper bound for
    /// Between only. Whether a filter fits its column is the page's BadFilter, not an argument error.
    /// </summary>
    public static void Validate(TablePageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Table, nameof(request));
        Cursor(request.After, request.Limit);
        if (request.Filters is { Count: > MaxFilters }) throw new ArgumentException($"A page takes at most {MaxFilters} filters.", nameof(request));
        foreach (var filter in request.Filters ?? [])
        {
            if (filter is null) throw new ArgumentException("A filter is null.", nameof(request));
            if (string.IsNullOrEmpty(filter.Column)) throw new ArgumentException("A filter names no column.", nameof(request));
            if (filter.Value is null) throw new ArgumentException("A filter has no value.", nameof(request));
            if (filter.Column.Length > MaxFilterColumnLength) throw new ArgumentException("A filter's column name is too long.", nameof(request));
            if (filter.Value.Length > MaxFilterValueLength || filter.Upper?.Length > MaxFilterValueLength)
                throw new ArgumentException("A filter's value is too long.", nameof(request));
            if (!Enum.IsDefined(filter.Op)) throw new ArgumentException("A filter's op is unknown.", nameof(request));
            if ((filter.Op == TableFilterOp.Between) != (filter.Upper is not null))
                throw new ArgumentException("A Between filter needs an upper bound, and only Between takes one.", nameof(request));
        }
    }

    public static void Validate(TableFollowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Table, nameof(request));
        if (string.IsNullOrEmpty(request.Column)) throw new ArgumentException("The follow names no column.", nameof(request));
        Cursor(request.After, request.Limit);
    }

    public static void Validate(TableReferencesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Table, nameof(request));
        ArgumentOutOfRangeException.ThrowIfLessThan(request.LimitPerSource, 1, nameof(request));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.LimitPerSource, MaxRowsPerSource, nameof(request));
    }

    private static void Cursor(long after, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(after, -1L, "After");
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1, "Limit");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxLimit, "Limit");
    }

    /// <summary>Whether a listed table passes <paramref name="filter"/>: a literal, ASCII case-insensitive name substring and the state set.</summary>
    public static bool Matches(TableListFilter filter, TableEntry entry)
    {
        ArgumentNullException.ThrowIfNull(filter); ArgumentNullException.ThrowIfNull(entry);
        if (filter.States is { } states && !states.Contains(entry.State)) return false;
        return filter.Name is not { } name || entry.Name is { } own && ContainsAscii(own, name);
    }

    /// <summary>Whether two names are equal ignoring ASCII case only.</summary>
    public static bool NamesEqual(string a, string b) => a.Length == b.Length && ContainsAscii(a, b);

    private static bool ContainsAscii(string text, string part)
    {
        for (var start = 0; start + part.Length <= text.Length; start++)
        {
            var i = 0;
            while (i < part.Length && Ascii(text[start + i]) == Ascii(part[i])) i++;
            if (i == part.Length) return true;
        }
        return false;
    }

    // Leaves non-ASCII characters alone so only A-Z fold.
    private static char Ascii(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;

    /// <summary>
    /// Parses a canonical decimal integer: an optional '-', then 1 to <see cref="MaxIntegerDigits"/> ASCII digits, with no '+',
    /// no leading zeros and no "-0". Anything else is false. Every int64 and uint64 value parses exactly.
    /// </summary>
    public static bool TryParseInteger(string? text, out Int128 value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text)) return false;
        var negative = text[0] == '-';
        var digits = text.AsSpan(negative ? 1 : 0);
        if (digits.Length is 0 or > MaxIntegerDigits) return false;
        if (digits[0] == '0' && (digits.Length > 1 || negative)) return false;
        Int128 magnitude = 0;
        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c)) return false;
            magnitude = magnitude * 10 + (c - '0');
        }
        value = negative ? -magnitude : magnitude;
        return true;
    }

    /// <summary>
    /// An unfiltered page over a readable table's <paramref name="rowCount"/> ordered rows, read through <paramref name="read"/>.
    /// Order: Stale when the request names an identity other than <paramref name="identity"/>; InvalidCursor when
    /// <see cref="TablePageRequest.After"/> is at or past the row count; else Ok with the rows after it and NextAfter the last
    /// ordinal returned, null when the page reaches the end. A request with filters throws: the overload with columns pages those.
    /// </summary>
    public static async Task<TablePage> PageAsync(TableEntry table, string identity, TablePageRequest request, long rowCount, TableRowReader read,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table); ArgumentException.ThrowIfNullOrEmpty(identity); ArgumentNullException.ThrowIfNull(read);
        Validate(request);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        if (request.Filters is { Count: > 0 }) throw new ArgumentException("This method pages without filters.", nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Identity is { } asked && asked != identity) return new(table, TablePageState.Stale, [], null, identity, 0);
        if (request.After >= rowCount) return new(table, TablePageState.InvalidCursor, [], null, identity, 0);
        var first = request.After + 1;
        var count = (int)Math.Min(request.Limit, rowCount - first);
        var rows = count == 0 ? [] : await read(first, count, cancellationToken).ConfigureAwait(false);
        if (rows.Count != count) throw new InvalidOperationException("The row source returned a different number of rows than it holds.");
        var page = new TableRow[count];
        for (var i = 0; i < count; i++) page[i] = new(first + i, rows[i]);
        var end = first + count;
        return new(table, TablePageState.Ok, page, end < rowCount ? end - 1 : null, identity, count);
    }

    /// <summary>The page of a table that isn't readable: no rows, no identity.</summary>
    public static TablePage NotReadable(TableEntry table) => new(table, TablePageState.NotReadable, [], null, null, 0);
}
