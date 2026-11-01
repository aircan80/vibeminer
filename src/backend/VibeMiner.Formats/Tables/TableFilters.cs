using System.Globalization;
using VibeMiner.Formats.Definitions;

namespace VibeMiner.Formats.Tables;

/// <summary>
/// The outcome of <see cref="TableQueries.CompileFilters"/>: a row test when every filter fits its column, else the BadFilter
/// <see cref="Code"/> (TABLE_FILTER_*) and <see cref="Message"/>.
/// </summary>
public sealed record TableFilterResult(Func<Db2Row, bool>? Matches, string? Code, string? Message)
{
    public bool IsBad => Matches is null;
}

public static partial class TableQueries
{
    /// <summary>The most filters one page takes; more throw.</summary>
    public const int MaxFilters = 8;
    /// <summary>The most rows a filtered page looks at before it returns and continues from NextAfter.</summary>
    public const int DefaultMaxScanRows = 200_000;
    /// <summary>The rows a filtered scan reads at a time.</summary>
    private const int ScanChunkRows = 1024;

    /// <summary>The filter names no column of the table.</summary>
    public const string FilterColumnCode = "TABLE_FILTER_COLUMN";
    /// <summary>The column keeps raw integer evidence (<see cref="Db2Column.Interpretation"/>), so its values aren't keys.</summary>
    public const string FilterRawCode = "TABLE_FILTER_RAW";
    /// <summary>The op doesn't apply to the column's type: Equals on a float, Between on a string.</summary>
    public const string FilterOpCode = "TABLE_FILTER_OP";
    /// <summary>A value doesn't parse for the column's type, or a Between's lower bound is above its upper.</summary>
    public const string FilterValueCode = "TABLE_FILTER_VALUE";

    private const NumberStyles FloatStyle = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent;

    /// <summary>
    /// Compiles <paramref name="filters"/> against a table's header <paramref name="columns"/>; all must hold. A column is a
    /// flattened name (<c>Flags[1]</c>, ordinal) or an array's base name, meaning any element. Integer columns take Equals and
    /// Between as canonical decimals (<see cref="TryParseInteger"/>) compared exactly with the cell's string; a value outside the
    /// column's declared width and sign (<paramref name="schema"/>, else the int64/uint64 span; the ID column is always uint32, as
    /// the reader gives it) matches nothing, never wraps.
    /// Float columns take Between only, compared as float32 with finite bounds; NaN never matches. String columns take Equals only,
    /// ordinal; a cell with a <see cref="Db2TextIssue"/> never matches. No filters match every row.
    /// </summary>
    public static TableFilterResult CompileFilters(IReadOnlyList<Db2Column> columns, TableSchema? schema, IReadOnlyList<TableFilter>? filters)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (filters is not { Count: > 0 }) return new(static _ => true, null, null);
        var tests = new List<(int[] Indexes, Func<string, bool> Cell)>(filters.Count);
        foreach (var filter in filters)
        {
            ArgumentNullException.ThrowIfNull(filter);
            var indexes = Resolve(columns, filter.Column);
            if (indexes.Length == 0) return Bad(FilterColumnCode, $"The table has no column {filter.Column}.");
            if (indexes.Any(i => columns[i].Interpretation is not null))
                return Bad(FilterRawCode, $"Column {filter.Column} keeps raw values, so it can't be filtered on.");
            var kind = columns[indexes[0]].Kind;
            if (indexes.Any(i => columns[i].Kind != kind)) return Bad(FilterOpCode, $"The elements of {filter.Column} differ in type.");
            var (cell, error) = kind switch
            {
                "integer" => IntegerTest(filter, Domain(schema, columns[indexes[0]])),
                "float" => FloatTest(filter),
                "string" => StringTest(filter),
                _ => (null, (FilterOpCode, $"Column {filter.Column} has type {kind}, which filters don't support.")),
            };
            if (cell is null) return Bad(error.Code, error.Message);
            tests.Add((indexes, cell));
        }
        var compiled = tests.ToArray();
        return new(row => Matches(compiled, row), null, null);
    }

    private static TableFilterResult Bad(string code, string message) => new(null, code, message);

    private static bool Matches((int[] Indexes, Func<string, bool> Cell)[] tests, Db2Row row)
    {
        foreach (var (indexes, cell) in tests)
        {
            var any = false;
            foreach (var i in indexes)
                if (i < row.Values.Count && !HasTextIssue(row, i) && cell(row.Values[i])) { any = true; break; }
            if (!any) return false;
        }
        return true;
    }

    private static bool HasTextIssue(Db2Row row, int column)
    {
        if (row.TextIssues is not { Count: > 0 } issues) return false;
        foreach (var issue in issues) if (issue.ColumnIndex == column) return true;
        return false;
    }

    // The exact column, else every element of the array with this base name.
    private static int[] Resolve(IReadOnlyList<Db2Column> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++) if (columns[i].Name == name) return [i];
        var elements = new List<int>();
        for (var i = 0; i < columns.Count; i++) if (BaseName(columns[i].Name) == name && columns[i].Name != name) elements.Add(i);
        return [.. elements];
    }

    // "Flags[1]" -> "Flags"; a name without a trailing [digits] is its own base.
    private static string BaseName(string name)
    {
        if (name.Length < 4 || name[^1] != ']') return name;
        var open = name.LastIndexOf('[');
        if (open < 1 || open == name.Length - 2) return name;
        for (var i = open + 1; i < name.Length - 1; i++) if (!char.IsAsciiDigit(name[i])) return name;
        return name[..open];
    }

    // The values an integer column can hold: its declared width and sign, else anything a DB2 cell can be. The ID is always
    // read as uint32, though WoWDBDefs declares it signed.
    private static (Int128 Min, Int128 Max) Domain(TableSchema? schema, Db2Column header)
    {
        if (header.IsId) return (0, uint.MaxValue);
        var name = BaseName(header.Name);
        var column = schema?.Columns.FirstOrDefault(c => c.Name == name && c.Kind == ColumnKind.Int && c.Bits is not null && c.Unsigned is not null);
        return Domain(column);
    }

    // A schema integer column's declared span, else (no column, or no width) anything a DB2 cell can be.
    private static (Int128 Min, Int128 Max) Domain(SchemaColumn? column) => column is { Bits: { } bits and >= 1 and <= 64, Unsigned: { } unsigned }
        ? unsigned ? (0, (Int128.One << bits) - 1) : (-(Int128.One << (bits - 1)), (Int128.One << (bits - 1)) - 1)
        : (long.MinValue, ulong.MaxValue);

    private static (Func<string, bool>? Cell, (string Code, string Message) Error) IntegerTest(TableFilter filter, (Int128 Min, Int128 Max) domain)
    {
        if (!TryParseInteger(filter.Value, out var low))
            return (null, (FilterValueCode, $"{filter.Value} isn't a canonical decimal integer."));
        var high = low;
        if (filter.Op == TableFilterOp.Between)
        {
            if (!TryParseInteger(filter.Upper, out high))
                return (null, (FilterValueCode, $"{filter.Upper} isn't a canonical decimal integer."));
            if (low > high) return (null, (FilterValueCode, "The lower bound is above the upper bound."));
        }
        // Out of the column's span the value can't be stored, so it matches nothing; never wrapped.
        low = Int128.Max(low, domain.Min);
        high = Int128.Min(high, domain.Max);
        if (low > high) return (static _ => false, default);
        return (cell => TryParseInteger(cell, out var value) && value >= low && value <= high, default);
    }

    private static (Func<string, bool>? Cell, (string Code, string Message) Error) FloatTest(TableFilter filter)
    {
        if (filter.Op != TableFilterOp.Between) return (null, (FilterOpCode, "A float column takes Between only."));
        if (!TryParseFloat(filter.Value, out var low) || !float.IsFinite(low))
            return (null, (FilterValueCode, $"{filter.Value} isn't a finite decimal number."));
        if (!TryParseFloat(filter.Upper, out var high) || !float.IsFinite(high))
            return (null, (FilterValueCode, $"{filter.Upper} isn't a finite decimal number."));
        if (low > high) return (null, (FilterValueCode, "The lower bound is above the upper bound."));
        // NaN compares false both ways, so it never matches.
        return (cell => TryParseFloat(cell, out var value) && value >= low && value <= high, default);
    }

    private static bool TryParseFloat(string? text, out float value) => float.TryParse(text, FloatStyle, CultureInfo.InvariantCulture, out value);

    private static (Func<string, bool>? Cell, (string Code, string Message) Error) StringTest(TableFilter filter)
    {
        if (filter.Op != TableFilterOp.Equals) return (null, (FilterOpCode, "A string column takes Equals only."));
        var wanted = filter.Value;
        return (cell => string.Equals(cell, wanted, StringComparison.Ordinal), default);
    }

    /// <summary>
    /// A page over a readable table's <paramref name="rowCount"/> ordered rows with the request's filters applied (see
    /// <see cref="CompileFilters"/>). Without filters it is <see cref="PageAsync(TableEntry, string, TablePageRequest, long, TableRowReader, CancellationToken)"/>.
    /// Order: Stale, then BadFilter, then InvalidCursor. Else Ok: rows after <see cref="TablePageRequest.After"/> are looked at in
    /// order until Limit match or <paramref name="maxScanRows"/> have been looked at. NextAfter is the ordinal of the last row
    /// looked at, so a page with few or no matches still continues; null when the scan reached the end. Scanned is the rows
    /// looked at.
    /// </summary>
    public static async Task<TablePage> PageAsync(TableEntry table, string identity, TablePageRequest request, long rowCount, TableRowReader read,
        IReadOnlyList<Db2Column> columns, TableSchema? schema, int maxScanRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(table); ArgumentException.ThrowIfNullOrEmpty(identity); ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxScanRows, 1);
        Validate(request);
        if (request.Filters is not { Count: > 0 }) return await PageAsync(table, identity, request, rowCount, read, cancellationToken).ConfigureAwait(false);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Identity is { } asked && asked != identity) return new(table, TablePageState.Stale, [], null, identity, 0);
        var filter = CompileFilters(columns, schema, request.Filters);
        if (filter.Matches is not { } matches) return new(table, TablePageState.BadFilter, [], null, identity, 0, filter.Code, filter.Message);
        if (request.After >= rowCount) return new(table, TablePageState.InvalidCursor, [], null, identity, 0);

        var found = new List<TableRow>();
        var next = request.After + 1;
        var stop = Math.Min(rowCount, next + maxScanRows);
        while (next < stop && found.Count < request.Limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(ScanChunkRows, stop - next);
            var rows = await read(next, count, cancellationToken).ConfigureAwait(false);
            if (rows.Count != count) throw new InvalidOperationException("The row source returned a different number of rows than it holds.");
            for (var i = 0; i < count && found.Count < request.Limit; i++, next++)
                if (matches(rows[i])) found.Add(new(next, rows[i]));
        }
        var scanned = next - (request.After + 1);
        return new(table, TablePageState.Ok, found, next < rowCount ? next - 1 : null, identity, scanned);
    }
}
