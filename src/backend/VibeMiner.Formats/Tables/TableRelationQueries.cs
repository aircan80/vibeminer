using System.Runtime.CompilerServices;
using VibeMiner.Formats.Definitions;

namespace VibeMiner.Formats.Tables;

/// <summary>
/// A table as follow and referenced-by read it, after a check. <see cref="Columns"/> are the header columns once opened;
/// <see cref="Schema"/> the resolved definition block. <see cref="Read"/> is set only when <see cref="Entry"/> is readable
/// (Ready or Partial), over its <see cref="RowCount"/> ordered rows.
/// </summary>
public sealed record RelationTable(TableEntry Entry, IReadOnlyList<Db2Column>? Columns, TableSchema? Schema, long RowCount, TableRowReader? Read);

/// <summary>
/// The reads follow and referenced-by need from one build and locale's tables; each <see cref="IBuildTables"/> plugs its own
/// in, at the caller's priority. Names are looked up ASCII case-insensitive, as <see cref="TableRef"/> does.
/// </summary>
public interface ITableRelationSource
{
    /// <summary>Checks the table and gives what it read; a table that isn't known answers its entry (NotInBuild, NoDefinition).</summary>
    Task<RelationTable> OpenAsync(TableRef table, CancellationToken cancellationToken);

    /// <summary>The row by ID, exactly as <see cref="IBuildTables.GetAsync"/> answers it.</summary>
    Task<TableRowResult> GetAsync(TableRef table, uint id, CancellationToken cancellationToken);

    /// <summary>Every listed table, shallow, as <see cref="IBuildTables.ListAsync"/> gives it with <see cref="TableListFilter.All"/>.</summary>
    IAsyncEnumerable<TableEntry> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// A listed table's schema from the definitions alone: never opens, fetches or reads the table. Unresolved when only the
    /// file could choose the block (a layout match not checked yet) or the definition is unusable.
    /// </summary>
    Task<RelationSchema> SchemaAsync(TableEntry table, CancellationToken cancellationToken);
}

public enum RelationSchemaState
{
    /// <summary><see cref="RelationSchema.Schema"/> is set.</summary>
    Resolved,
    /// <summary>No definition covers the table (NoDefinition): it can't declare a reference.</summary>
    NoDefinition,
    /// <summary>A definition may cover it, but not without opening the table, or it can't be used (DefinitionUnusable).</summary>
    Unresolved,
}

/// <summary>What <see cref="ITableRelationSource.SchemaAsync"/> knows; <see cref="Schema"/> is set only when Resolved.</summary>
public sealed record RelationSchema(RelationSchemaState State, TableSchema? Schema);

public static partial class TableQueries
{
    /// <summary>The most candidate sources one referenced-by searches; the rest are NotChecked with <see cref="RelationSourceLimitCode"/>.</summary>
    public const int DefaultMaxReferenceSources = 256;
    /// <summary>The most rows one referenced-by scans across all its sources; the rest are NotChecked with <see cref="RelationBudgetCode"/>.</summary>
    public const int DefaultMaxReferenceScanRows = 1_000_000;

    /// <summary>The source table has no header column of that flattened name.</summary>
    public const string RelationColumnCode = "TABLE_RELATION_COLUMN";
    /// <summary>A column on either side keeps raw integer evidence (<see cref="Db2Column.Interpretation"/>), so its values aren't keys.</summary>
    public const string RelationRawCode = "TABLE_RELATION_RAW";
    /// <summary>The cell isn't a canonical decimal integer (invalid text, or not a number).</summary>
    public const string RelationValueCode = "TABLE_RELATION_VALUE";
    /// <summary>The referenced table's schema has no integer column of the declared name.</summary>
    public const string RelationTargetColumnCode = "TABLE_RELATION_TARGET_COLUMN";
    /// <summary>The ID is in a keyed section's encrypted-ID list.</summary>
    public const string RelationProtectedCode = "TABLE_RELATION_PROTECTED";
    /// <summary>The table has more rows than one scan may look at.</summary>
    public const string RelationUnindexedCode = "TABLE_RELATION_UNINDEXED";
    /// <summary>More candidate sources than one referenced-by searches.</summary>
    public const string RelationSourceLimitCode = "TABLE_RELATION_SOURCE_LIMIT";
    /// <summary>Scanning the source would take one referenced-by over its total row budget.</summary>
    public const string RelationBudgetCode = "TABLE_RELATION_BUDGET";

    /// <summary>
    /// Follows a foreign key, the rules every <see cref="IBuildTables"/> shares. In order:
    /// <list type="number">
    /// <item>The source row is looked up by ID. When the source's resolved schema declares no foreign key on the column's base name
    /// (or the column is no flattened header column, <see cref="RelationColumnCode"/>, or keeps raw evidence, <see cref="RelationRawCode"/>):
    /// NotAReference. With no schema at all, a row that isn't Found is SourceRow, else NotAReference.</item>
    /// <item>The source row not Found: SourceRow, carrying it.</item>
    /// <item>The target is the declared table, by name (ASCII case-insensitive), and column. A target with no schema (not in the
    /// build, no definition), or whose schema has no integer column of that name (<see cref="RelationTargetColumnCode"/>): Unknown
    /// with the target entry.</item>
    /// <item>The cell is parsed exactly (<see cref="TryParseInteger"/>); 0 is a real value. A cell that isn't a canonical integer
    /// (invalid text, an absent external parent (D291, never 0), or no cell): Unknown with <see cref="RelationValueCode"/>. Outside the target column's declared width and
    /// sign (an ID target: 0 to uint32 max): KeyOutOfRange, never wrapped.</item>
    /// <item>An ID target is looked up by ID: Found, Absent (target Ready), Unknown (partial or unreadable target), and Protected
    /// as Unknown with <see cref="RelationProtectedCode"/> and <see cref="TableFollowResult.ProtectedKey"/>.</item>
    /// <item>Any other target column is scanned: an unreadable target is Unknown; a target column keeping raw evidence is
    /// NotAReference with <see cref="RelationRawCode"/>; more rows than <paramref name="maxScanRows"/> is Unindexed
    /// (<see cref="RelationUnindexedCode"/>). Matches (any element of an array column) are paged by ordinal after
    /// <see cref="TableFollowRequest.After"/>, at most Limit; NextAfter is the last ordinal returned while more match, else null.
    /// No match at all is Absent in a Ready target, Unknown in a Partial one.</item>
    /// </list>
    /// Found from a Partial target (<see cref="TableFollowResult.Target"/> state Partial) is a lower bound for a scanned column:
    /// unreadable rows may match too. A later page with no rows left is still Found.
    /// </summary>
    public static async Task<TableFollowResult> FollowAsync(ITableRelationSource source, TableFollowRequest request, int maxScanRows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        Validate(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxScanRows, 1);
        cancellationToken.ThrowIfCancellationRequested();

        var row = await source.GetAsync(request.Table, request.Id, cancellationToken).ConfigureAwait(false);
        var from = await source.OpenAsync(request.Table, cancellationToken).ConfigureAwait(false);
        var found = row.Outcome == TableRowOutcome.Found;
        TableFollowResult Result(TableFollowOutcome outcome, ForeignKey? reference = null, TableEntry? target = null, string? code = null,
            string? message = null, string? key = null) => new(outcome, row, reference, target, [], null, code, message, key);

        if (from.Schema is null)
            return found ? Result(TableFollowOutcome.NotAReference, message: "The table has no resolved schema.") : Result(TableFollowOutcome.SourceRow);
        if (DeclaredReference(from.Schema, request.Column) is not { } reference)
            return Result(TableFollowOutcome.NotAReference, message: $"Column {request.Column} declares no foreign key.");
        var index = -1;
        if (from.Columns is { } columns)
            for (var i = 0; i < columns.Count; i++) if (columns[i].Name == request.Column) { index = i; break; }
        if (from.Columns is not null && index < 0)
            return Result(TableFollowOutcome.NotAReference, reference, code: RelationColumnCode, message: $"The table has no column {request.Column}.");
        if (index >= 0 && from.Columns![index].Interpretation is not null)
            return Result(TableFollowOutcome.NotAReference, reference, code: RelationRawCode, message: $"Column {request.Column} keeps raw values.");
        if (!found) return Result(TableFollowOutcome.SourceRow, reference);

        var to = await source.OpenAsync(TableRef.ForName(reference.Table), cancellationToken).ConfigureAwait(false);
        if (to.Schema is null)
            return Result(TableFollowOutcome.Unknown, reference, to.Entry, message: $"{reference.Table} has no resolved schema in this build.");
        var targetColumn = to.Schema.Columns.FirstOrDefault(c => c.Name == reference.Column && c.Kind == ColumnKind.Int);
        if (targetColumn is null)
            return Result(TableFollowOutcome.Unknown, reference, to.Entry, RelationTargetColumnCode, $"{reference.Table} has no integer column {reference.Column}.");
        var isId = IsIdColumn(to, reference.Column);

        var cell = row.Row!.Row;
        if (index < 0 || index >= cell.Values.Count || HasTextIssue(cell, index) || !TryParseInteger(cell.Values[index], out var value))
            return Result(TableFollowOutcome.Unknown, reference, to.Entry, RelationValueCode,
                cell.TextIssues?.Any(i => i.ColumnIndex == index && i.Code == Db2Reader.RelationAbsent) == true
                    ? $"Column {request.Column} has no parent for this row." : $"Column {request.Column} holds no integer.");
        var header = to.Columns?.FirstOrDefault(c => c.Name == reference.Column);
        var (min, max) = header is not null ? Domain(to.Schema, header) : isId ? ((Int128)0, (Int128)uint.MaxValue) : Domain(targetColumn);
        if (value < min || value > max)
            return Result(TableFollowOutcome.KeyOutOfRange, reference, to.Entry, message: $"{value} doesn't fit {reference.Table}.{reference.Column}.");

        if (isId)
        {
            var hit = await source.GetAsync(TableRef.ForName(reference.Table), (uint)value, cancellationToken).ConfigureAwait(false);
            return hit.Outcome switch
            {
                TableRowOutcome.Found => new(TableFollowOutcome.Found, row, reference, hit.Table,
                    hit.Row!.Ordinal > request.After ? [hit.Row] : [], null),
                TableRowOutcome.Absent => Result(TableFollowOutcome.Absent, reference, hit.Table),
                TableRowOutcome.Protected => Result(TableFollowOutcome.Unknown, reference, hit.Table, RelationProtectedCode,
                    $"ID {value} is in a section keyed with {hit.ProtectedKey}.", hit.ProtectedKey),
                _ => Result(TableFollowOutcome.Unknown, reference, hit.Table),
            };
        }

        if (!IsReadable(to.Entry.State) || to.Read is null || to.Columns is null) return Result(TableFollowOutcome.Unknown, reference, to.Entry);
        var indexes = Resolve(to.Columns, reference.Column);
        if (indexes.Length == 0)
            return Result(TableFollowOutcome.Unknown, reference, to.Entry, RelationTargetColumnCode, $"{reference.Table} has no column {reference.Column}.");
        if (indexes.Any(i => to.Columns[i].Interpretation is not null))
            return Result(TableFollowOutcome.NotAReference, reference, to.Entry, RelationRawCode, $"{reference.Table}.{reference.Column} keeps raw values.");
        if (to.RowCount > maxScanRows)
            return Result(TableFollowOutcome.Unindexed, reference, to.Entry, RelationUnindexedCode,
                $"{reference.Table} has {to.RowCount} rows; one scan looks at {maxScanRows}.");

        var rows = new List<TableRow>();
        var earlier = false;
        await foreach (var (ordinal, target) in ScanAsync(to, cancellationToken).ConfigureAwait(false))
        {
            if (!AnyEquals(target, indexes, value)) continue;
            if (ordinal <= request.After) { earlier = true; continue; }
            rows.Add(new(ordinal, target));
            if (rows.Count > request.Limit) break;
        }
        if (rows.Count == 0 && !earlier)
            return Result(to.Entry.State == TableState.Ready ? TableFollowOutcome.Absent : TableFollowOutcome.Unknown, reference, to.Entry);
        var more = rows.Count > request.Limit;
        if (more) rows.RemoveAt(rows.Count - 1);
        return new(TableFollowOutcome.Found, row, reference, to.Entry, rows, more ? rows[^1].Ordinal : null);
    }

    /// <summary>
    /// Lists what points at a row, the rules every <see cref="IBuildTables"/> shares. The row not Found is SourceRow. Candidates are
    /// the listed tables whose schema from the definitions (<see cref="ITableRelationSource.SchemaAsync"/>) declares a foreign key
    /// to this table's name (ASCII case-insensitive): one source per declaring column, an array's base name meaning any element.
    /// Only NoDefinition, NotInBuild and NotInLocale tables are left out (they can't declare one, or hold no rows here); a table
    /// whose schema is Unresolved goes in <see cref="TableReferencesResult.Unresolved"/>. The value looked up is this row's value of the
    /// declared target column (its ID when that column is the ID). Sources come by FileDataID then schema column order; the first
    /// <paramref name="maxSources"/> are searched, the rest are NotChecked with <see cref="RelationSourceLimitCode"/>; a source whose
    /// rows would take the call's scans past <paramref name="maxTotalScanRows"/> is NotChecked with <see cref="RelationBudgetCode"/>.
    /// Per source:
    /// <list type="bullet">
    /// <item>NotChecked when the source isn't readable (its entry says why), its column is missing (<see cref="RelationColumnCode"/>) or keeps
    /// raw evidence (<see cref="RelationRawCode"/>), this table lacks the target column (<see cref="RelationTargetColumnCode"/>) or holds no
    /// integer there (<see cref="RelationValueCode"/>), or the source has more rows than <paramref name="maxScanRows"/>
    /// (<see cref="RelationUnindexedCode"/>).</item>
    /// <item>Else Complete (Ready) or Partial (a lower bound) with the matching row IDs by ordinal, at most
    /// <see cref="TableReferencesRequest.LimitPerSource"/>, Truncated when more match. A value outside the source column's
    /// declared width and sign matches nothing, never wraps.</item>
    /// </list>
    /// NoReferences only when nothing is Unresolved and every source is Complete with no rows (no candidates included); else
    /// Searched. A row of a table with no definition name is Searched with no sources: nothing could be looked for.
    /// </summary>
    public static async Task<TableReferencesResult> ReferencedByAsync(ITableRelationSource source, TableReferencesRequest request, int maxScanRows,
        int maxSources, long maxTotalScanRows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        Validate(request);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxScanRows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSources, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTotalScanRows, 0);
        cancellationToken.ThrowIfCancellationRequested();

        var row = await source.GetAsync(request.Table, request.Id, cancellationToken).ConfigureAwait(false);
        if (row.Outcome != TableRowOutcome.Found) return new(TableReferencesOutcome.SourceRow, row, [], []);
        var self = await source.OpenAsync(request.Table, cancellationToken).ConfigureAwait(false);
        if (self.Entry.Name is not { } name) return new(TableReferencesOutcome.Searched, row, [], []);

        var candidates = new List<(TableEntry Entry, SchemaColumn Column)>();
        var unresolved = new List<TableEntry>();
        await foreach (var entry in source.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            if (entry.State is TableState.NoDefinition or TableState.NotInBuild or TableState.NotInLocale) continue;
            var schema = await source.SchemaAsync(entry, cancellationToken).ConfigureAwait(false);
            if (schema.State == RelationSchemaState.Unresolved) { unresolved.Add(entry); continue; }
            foreach (var column in schema.Schema?.Columns ?? [])
                if (column.ForeignKey is { } fk && NamesEqual(fk.Table, name)) candidates.Add((entry, column));
        }
        // Stable, so a table's sources keep schema column order.
        candidates = [.. candidates.OrderBy(c => c.Entry.FileDataId)];

        var opened = new Dictionary<uint, RelationTable>();
        var values = new Dictionary<string, (Int128 Value, string? Code)>(StringComparer.Ordinal);
        var sources = new List<TableReferenceSource>(candidates.Count);
        long scanned = 0;
        for (var n = 0; n < candidates.Count; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (entry, column) = candidates[n];
            var fk = column.ForeignKey!;
            TableReferenceSource NotChecked(TableEntry table, string? code) => new(table, column.Name, fk, TableReferenceScope.NotChecked, [], false, code);
            if (n >= maxSources) { sources.Add(NotChecked(entry, RelationSourceLimitCode)); continue; }
            if (!opened.TryGetValue(entry.FileDataId, out var table))
                opened[entry.FileDataId] = table = await source.OpenAsync(entry.FileDataId, cancellationToken).ConfigureAwait(false);
            if (!IsReadable(table.Entry.State) || table.Read is null || table.Columns is null) { sources.Add(NotChecked(table.Entry, null)); continue; }
            var indexes = Resolve(table.Columns, column.Name);
            if (indexes.Length == 0) { sources.Add(NotChecked(table.Entry, RelationColumnCode)); continue; }
            if (indexes.Any(i => table.Columns[i].Interpretation is not null)) { sources.Add(NotChecked(table.Entry, RelationRawCode)); continue; }
            if (!values.TryGetValue(fk.Column, out var looked)) values[fk.Column] = looked = ValueOf(self, row.Row!.Row, fk.Column);
            if (looked.Code is { } code) { sources.Add(NotChecked(table.Entry, code)); continue; }
            var scope = table.Entry.State == TableState.Ready ? TableReferenceScope.Complete : TableReferenceScope.Partial;
            var (min, max) = Domain(table.Schema, table.Columns[indexes[0]]);
            if (looked.Value < min || looked.Value > max) { sources.Add(new(table.Entry, column.Name, fk, scope, [], false)); continue; }
            if (table.RowCount > maxScanRows) { sources.Add(NotChecked(table.Entry, RelationUnindexedCode)); continue; }
            if (table.RowCount > maxTotalScanRows - scanned) { sources.Add(NotChecked(table.Entry, RelationBudgetCode)); continue; }
            scanned += table.RowCount;

            var ids = new List<uint>();
            var truncated = false;
            await foreach (var (_, match) in ScanAsync(table, cancellationToken).ConfigureAwait(false))
            {
                if (!AnyEquals(match, indexes, looked.Value)) continue;
                if (ids.Count == request.LimitPerSource) { truncated = true; break; }
                ids.Add(match.Id);
            }
            sources.Add(new(table.Entry, column.Name, fk, scope, ids, truncated));
        }
        var none = unresolved.Count == 0 && sources.All(s => s.Scope == TableReferenceScope.Complete && s.RowIds.Count == 0);
        return new(none ? TableReferencesOutcome.NoReferences : TableReferencesOutcome.Searched, row, sources, unresolved);
    }

    /// <summary>The foreign key <paramref name="schema"/> declares for a flattened column (an array element by its base name), else null.</summary>
    internal static ForeignKey? DeclaredReference(TableSchema? schema, string column)
    {
        var declared = schema?.Columns.FirstOrDefault(c => c.Name == BaseName(column) && c.ForeignKey is not null);
        return declared?.ForeignKey is { } reference && IsElement(declared, column) ? reference : null;
    }

    // This row's value of the column a foreign key targets: its ID when that column is the ID.
    private static (Int128 Value, string? Code) ValueOf(RelationTable table, Db2Row row, string column)
    {
        if (IsIdColumn(table, column)) return (row.Id, null);
        if (table.Columns is null) return (0, RelationTargetColumnCode);
        for (var i = 0; i < table.Columns.Count; i++)
        {
            if (table.Columns[i].Name != column) continue;
            if (table.Columns[i].Interpretation is not null) return (0, RelationRawCode);
            if (table.Columns[i].Kind != "integer") return (0, RelationTargetColumnCode);
            return i < row.Values.Count && !HasTextIssue(row, i) && TryParseInteger(row.Values[i], out var value) ? (value, null) : (0, RelationValueCode);
        }
        return (0, RelationTargetColumnCode);
    }

    // The ID column: the schema marks it, or the header does.
    private static bool IsIdColumn(RelationTable table, string column) =>
        table.Schema?.Columns.Any(c => c.Name == column && c.Id != KeyStorage.None) == true ||
        table.Columns?.Any(c => c.Name == column && c.IsId) == true;

    // A flattened name names this schema column: its own name for a scalar, Name[i] within the array for an array.
    private static bool IsElement(SchemaColumn column, string flattened)
    {
        if (!column.IsArray) return flattened == column.Name;
        if (flattened == column.Name) return false;
        var digits = flattened.AsSpan(column.Name.Length + 1, flattened.Length - column.Name.Length - 2);
        return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var i) &&
            i < column.ArrayLength && digits.Length == i.ToString(System.Globalization.CultureInfo.InvariantCulture).Length;
    }

    private static bool AnyEquals(Db2Row row, int[] indexes, Int128 value)
    {
        foreach (var i in indexes)
            if (i < row.Values.Count && !HasTextIssue(row, i) && TryParseInteger(row.Values[i], out var cell) && cell == value) return true;
        return false;
    }

    // Every row of a readable table in order, read a chunk at a time.
    private static async IAsyncEnumerable<(long Ordinal, Db2Row Row)> ScanAsync(RelationTable table, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (long next = 0; next < table.RowCount;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(ScanChunkRows, table.RowCount - next);
            var rows = await table.Read!(next, count, cancellationToken).ConfigureAwait(false);
            if (rows.Count != count) throw new InvalidOperationException("The row source returned a different number of rows than it holds.");
            foreach (var row in rows) yield return (next++, row);
        }
    }
}
