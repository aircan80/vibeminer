using VibeMiner.Formats;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Concepts.Tables;

internal enum RelationLookupState
{
    /// <summary>The index was read. <see cref="RelationLookup.Rows"/> may be empty: absence is proven only when the table is Ready.</summary>
    Ok,
    /// <summary>The table isn't Ready or Partial; <see cref="RelationLookup.Table"/> keeps its layer 5 state.</summary>
    NotReadable,
    /// <summary>RELATION_INDEX_COLUMN: the table has no one plain integer column of that name (none, two, or one keeping raw values).</summary>
    Unsupported,
    /// <summary>RELATION_INDEX_LIMIT: building it would go past the handle's entry budget. Never a silent miss; kept for the handle's life.</summary>
    IndexLimit,
    /// <summary>The table's identity changed while it was indexed or read, and once more after a rebuild.</summary>
    Stale,
}

/// <summary>
/// One key's rows. <see cref="Rows"/> are the first rows (at most the asked number) in layer 5's row order; <see cref="Matches"/>
/// is how many rows have the key. <see cref="Table"/> is the entry the index was built from: Ready means the rows are all of
/// them, Partial that more may be hidden (its key names are context, not a claim about this key). <see cref="Identity"/> is the
/// layer 5 page identity the index was built from (Ok only), so a caller paging the matches can tell a rebuilt index.
/// </summary>
internal sealed record RelationLookup(RelationLookupState State, IReadOnlyList<TableRow> Rows, int Matches, TableEntry? Table, string? Code = null,
    string? Message = null, string? Identity = null)
{
    public bool Complete => State == RelationLookupState.Ok && Table?.State == TableState.Ready;
}

/// <summary>
/// Layer 6's relation indexes over one <see cref="IBuildTables"/> handle (D471): for a (table, integer column) pair, every row's
/// (uint32 value, ordinal) pair in one sorted array (8 bytes a row), built on first need by one unfiltered paged scan and kept for
/// the handle's life. The scan starts at the first caller's priority and runs its later pages at Interactive once an Interactive
/// caller joins. A build is shared by concurrent callers (single flight); a caller that is canceled stops waiting, and a build
/// canceled by its owner is started again by a waiter whose own token is live. Cells outside uint32 or with a text issue are
/// never indexed (no uint32 key can match them). All indexes together hold at most <see cref="MaxEntries"/> rows; a build that
/// doesn't end Ok gives its rows back. Every answer is checked against the build's page identity (the rows read back by ordinal,
/// or one page for an empty match); a changed identity drops the index and rebuilds it once, then is Stale.
/// </summary>
internal sealed class RelationIndexes
{
    public const int DefaultMaxEntries = 4_000_000;
    public const string ColumnCode = "RELATION_INDEX_COLUMN";
    public const string LimitCode = "RELATION_INDEX_LIMIT";
    private const int PageRows = TableQueries.MaxLimit;

    private readonly IBuildTables tables;
    private readonly Lock gate = new();
    private readonly Dictionary<(string Table, string Column), Pending> indexes = [];
    private int used;
    private int builds;

    public RelationIndexes(IBuildTables tables, int maxEntries = DefaultMaxEntries)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntries, 1);
        this.tables = tables; MaxEntries = maxEntries;
    }

    public int MaxEntries { get; }

    /// <summary>The rows the kept indexes (and builds in flight) hold.</summary>
    public int Entries => Volatile.Read(ref used);

    /// <summary>The full scans started, for tests.</summary>
    public int Builds => Volatile.Read(ref builds);

    /// <summary>Pairs are <c>(ulong)value &lt;&lt; 32 | ordinal</c>, ascending, so one value's rows are adjacent and in table order.</summary>
    private sealed record Index(RelationLookupState State, TableEntry? Table, string? Identity, ulong[]? Pairs, int Entries,
        string? Code = null, string? Message = null);

    // A build in flight or done. Priority only ever rises to Interactive; the build reads it before each page.
    private sealed class Pending(Task<Index> task, TablePriority start)
    {
        private int priority = (int)start;
        public Task<Index> Task { get; } = task;
        public TablePriority Priority => (TablePriority)Volatile.Read(ref priority);
        public void Join(TablePriority caller)
        {
            if (caller == TablePriority.Interactive) Volatile.Write(ref priority, (int)TablePriority.Interactive);
        }
    }

    /// <summary>
    /// The rows of <paramref name="table"/> whose <paramref name="column"/> is <paramref name="key"/>, at most <paramref name="maxRows"/>
    /// of them after the first <paramref name="skip"/> matches.
    /// </summary>
    public async Task<RelationLookup> LookupAsync(string table, string column, uint key, int maxRows, TablePriority priority, CancellationToken cancellationToken,
        int skip = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(table); ArgumentException.ThrowIfNullOrWhiteSpace(column);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        var reference = TableRef.ForName(table);
        TableEntry? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var index = await IndexAsync((table, column), priority, cancellationToken).ConfigureAwait(false);
            if (index.State != RelationLookupState.Ok) return new(index.State, [], 0, index.Table, index.Code, index.Message);
            last = index.Table;
            var (first, count) = Range(index.Pairs!, key);
            var from = first + Math.Min(skip, count);
            var take = Math.Min(count - (from - first), maxRows);
            var rows = new List<TableRow>(take);
            var stale = false;
            if (take == 0)
            {
                // No row to read back (no match, or all skipped), so one page proves the index is still the table's.
                var page = await tables.PageAsync(new TablePageRequest(reference, -1, 1, null, index.Identity), priority, cancellationToken).ConfigureAwait(false);
                if (page.State == TablePageState.NotReadable) return new(RelationLookupState.NotReadable, [], 0, page.Table);
                stale = page.State != TablePageState.Ok;
            }
            for (var i = from; i < from + take && !stale; i++)
            {
                var ordinal = (long)(uint)index.Pairs![i];
                var page = await tables.PageAsync(new TablePageRequest(reference, ordinal - 1L, 1, null, index.Identity), priority, cancellationToken).ConfigureAwait(false);
                if (page.State == TablePageState.NotReadable) return new(RelationLookupState.NotReadable, [], 0, page.Table);
                if (page.State != TablePageState.Ok || page.Rows.Count != 1 || page.Rows[0].Ordinal != ordinal) stale = true;
                else rows.Add(page.Rows[0]);
            }
            if (!stale) return new(RelationLookupState.Ok, rows, count, index.Table, Identity: index.Identity);
            Drop((table, column), index);
        }
        return new(RelationLookupState.Stale, [], 0, last, null, $"{table} changed while it was read; it was indexed again and changed again.");
    }

    // The first pair of the value and how many there are, by binary search.
    private static (int First, int Count) Range(ulong[] pairs, uint value)
    {
        static int LowerBound(ulong[] pairs, ulong target)
        {
            int low = 0, high = pairs.Length;
            while (low < high)
            {
                var mid = low + (high - low) / 2;
                if (pairs[mid] < target) low = mid + 1; else high = mid;
            }
            return low;
        }
        var start = LowerBound(pairs, (ulong)value << 32);
        var end = value == uint.MaxValue ? pairs.Length : LowerBound(pairs, (ulong)(value + 1) << 32);
        return (start, end - start);
    }

    private void Drop((string, string) key, Index index)
    {
        lock (gate)
        {
            if (indexes.TryGetValue(key, out var pending) && pending.Task.IsCompletedSuccessfully && ReferenceEquals(pending.Task.Result, index))
            {
                indexes.Remove(key);
                Interlocked.Add(ref used, -index.Entries);
            }
        }
    }

    private void Forget((string, string) key, Pending pending)
    {
        lock (gate)
            if (indexes.TryGetValue(key, out var kept) && ReferenceEquals(kept, pending)) indexes.Remove(key);
    }

    private async Task<Index> IndexAsync((string Table, string Column) key, TablePriority priority, CancellationToken cancellationToken)
    {
        // Each pass waits on a build or runs one. Only a build canceled by its owner sends a live waiter round again, and the
        // next pass runs a real new build, so the passes are bounded by the callers that cancel.
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskCompletionSource<Index>? owned = null;
            Pending pending;
            lock (gate)
            {
                if (indexes.TryGetValue(key, out var found)) { pending = found; pending.Join(priority); }
                else
                {
                    owned = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    pending = new(owned.Task, priority);
                    indexes[key] = pending;
                }
            }
            if (owned is not null)
            {
                Index built;
                try { built = await BuildAsync(key.Table, key.Column, pending, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { Forget(key, pending); owned.SetCanceled(CancellationToken.None); throw; }
                catch (Exception problem) { Forget(key, pending); owned.SetException(problem); throw; }
                // Only a built index or a spent budget is kept; a table that may still change state is asked again next time.
                if (built.State is not (RelationLookupState.Ok or RelationLookupState.IndexLimit)) Forget(key, pending);
                owned.SetResult(built);
                return built;
            }
            try
            {
                return await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && pending.Task.IsCanceled) { }
        }
    }

    private async Task<Index> BuildAsync(string table, string column, Pending pending, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref builds);
        var reference = TableRef.ForName(table);
        var schema = await tables.SchemaAsync(reference, pending.Priority, cancellationToken).ConfigureAwait(false);
        if (!TableQueries.IsReadable(schema.Table.State)) return new(RelationLookupState.NotReadable, schema.Table, null, null, 0);
        var columns = schema.Columns ?? [];
        var at = -1;
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Name != column || columns[i].Kind != "integer" || columns[i].Interpretation is not null) continue;
            if (at >= 0) { at = -1; break; }
            at = i;
        }
        if (at < 0) return new(RelationLookupState.Unsupported, schema.Table, null, null, 0, ColumnCode, $"{table} has no one plain integer {column} column.");
        // One rebuild when the identity changes between pages.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var pairs = new List<ulong>();
            var reserved = 0;
            var kept = false;
            try
            {
                long after = -1;
                string? identity = null;
                TableEntry entry = schema.Table;
                var stale = false;
                while (true)
                {
                    var page = await tables.PageAsync(new TablePageRequest(reference, after, PageRows, null, identity), pending.Priority, cancellationToken)
                        .ConfigureAwait(false);
                    entry = page.Table;
                    if (page.State == TablePageState.NotReadable) return new(RelationLookupState.NotReadable, entry, null, null, 0);
                    if (page.State != TablePageState.Ok) { stale = true; break; }
                    identity = page.Identity;
                    var valid = 0;
                    foreach (var row in page.Rows)
                        if (KeyOf(row.Row, at) is not null) valid++;
                    reserved += valid;
                    if (Interlocked.Add(ref used, valid) > MaxEntries)
                        return new(RelationLookupState.IndexLimit, entry, null, null, 0, LimitCode,
                            $"Indexing {table}.{column} would hold more than {MaxEntries} rows on this handle.");
                    foreach (var row in page.Rows)
                    {
                        if (KeyOf(row.Row, at) is not { } value) continue;
                        if (row.Ordinal is < 0 or > uint.MaxValue) throw new System.Diagnostics.UnreachableException("A row ordinal is past uint32.");
                        pairs.Add((ulong)value << 32 | (uint)row.Ordinal);
                    }
                    if (page.NextAfter is not { } next || page.Rows.Count == 0) break;
                    after = next;
                }
                if (!stale)
                {
                    var sorted = pairs.ToArray();
                    Array.Sort(sorted);
                    kept = true;
                    return new(RelationLookupState.Ok, entry, identity, sorted, reserved);
                }
                if (attempt == 1) return new(RelationLookupState.Stale, entry, null, null, 0, null, $"{table} changed while it was indexed, twice.");
            }
            finally
            {
                // Every exit but a kept index gives its rows back: limit, not readable, stale, cancellation and failures alike.
                if (!kept) Interlocked.Add(ref used, -reserved);
            }
        }
        throw new System.Diagnostics.UnreachableException();
    }

    private static uint? KeyOf(Db2Row row, int at)
    {
        if (at >= row.Values.Count || row.TextIssues?.Any(issue => issue.ColumnIndex == at) == true) return null;
        return TableQueries.TryParseInteger(row.Values[at], out var value) && value >= 0 && value <= uint.MaxValue ? (uint)value : null;
    }
}
