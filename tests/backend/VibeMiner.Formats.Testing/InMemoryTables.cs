using System.Globalization;
using System.Runtime.CompilerServices;
using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Formats.Testing;

/// <summary>
/// One table of a fake build. <see cref="State"/> is what a check finds; it is never NotChecked or NotStored.
/// <see cref="ListedState"/> is the shallow state before a check: by default the same for the states a stat and a local
/// definition lookup already know (NotInBuild, NotInLocale, NotInEncoding, NoDefinition, DefinitionUnusable unless its reason is a
/// DB2_SCHEMA_* code, which needs an open), NotStored for
/// NeedsNetwork, Missing and Unavailable, and NotChecked for the rest. <see cref="Generated"/> means the table is already in the
/// generated store, so it lists in its checked state. <see cref="Rows"/> are the readable rows in the generated table's order
/// (physical rows, then copies), only for Ready and Partial; their values line up with <see cref="Columns"/>, the header
/// columns, given for an opened table. <see cref="ProtectedIds"/> maps IDs in keyed sections' encrypted-ID lists to their key
/// name, only for Partial and Encrypted. <see cref="Counts"/> and <see cref="Reason"/> default from the rows and protected IDs.
/// A null <see cref="ContentKey"/> is 32 hex digits made from the FileDataID.
/// </summary>
public sealed record FakeTable(uint FileDataId, string? Name, TableState State, IReadOnlyList<Db2Column>? Columns = null,
    IReadOnlyList<Db2Row>? Rows = null, TableSchema? Schema = null, TableStateReason? Reason = null, TableCounts? Counts = null,
    TableState? ListedState = null, bool Generated = false, string? ContentKey = null, IReadOnlyDictionary<uint, string>? ProtectedIds = null);

/// <summary>
/// The standard <see cref="ITables"/> test double for layer 6 and up: tables are seeded per build and locale, nothing is read or
/// fetched. A build or locale not added answers NeedsNetwork, as the real side does offline. A check of an opened state
/// (Ready, Partial, Encrypted) is remembered across handles, as the generated store would; other checks only answer. Paging
/// and filters use <see cref="TableQueries"/>, scanning at most <see cref="MaxScanRows"/> rows per filtered page; follow and
/// referenced-by use <see cref="TableQueries.FollowAsync"/> and <see cref="TableQueries.ReferencedByAsync"/> with the same scan
/// bound, at most <see cref="MaxReferenceSources"/> sources and <see cref="MaxReferenceScanRows"/> rows. Calls are counted per method (only the public ones).
/// </summary>
public sealed class InMemoryTables : ITables
{
    private readonly Lock gate = new();
    private readonly Dictionary<(BuildRef Build, string Locale), SortedDictionary<uint, FakeTable>> builds = [];
    private readonly Dictionary<BuildRef, (FilesOpenState State, string Code)> failures = [];
    private readonly HashSet<(BuildRef Build, string Locale, uint Id)> checkedTables = [];
    private readonly Dictionary<string, int> calls = [];
    private readonly int maxScanRows = TableQueries.DefaultMaxScanRows;
    private readonly int maxReferenceSources = TableQueries.DefaultMaxReferenceSources;
    private readonly long maxReferenceScanRows = TableQueries.DefaultMaxReferenceScanRows;

    /// <summary>The most rows a filtered page looks at; at least 1.</summary>
    public int MaxScanRows
    {
        get => maxScanRows;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            maxScanRows = value;
        }
    }

    /// <summary>The most sources one referenced-by searches; at least 0.</summary>
    public int MaxReferenceSources
    {
        get => maxReferenceSources;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            maxReferenceSources = value;
        }
    }

    /// <summary>The most rows one referenced-by scans across its sources; at least 0.</summary>
    public long MaxReferenceScanRows
    {
        get => maxReferenceScanRows;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            maxReferenceScanRows = value;
        }
    }

    /// <summary>Adds (or replaces) a build and locale with exactly these tables.</summary>
    public InMemoryTables Add(BuildRef build, string locale, IEnumerable<FakeTable> tables)
    {
        ArgumentNullException.ThrowIfNull(build); ArgumentNullException.ThrowIfNull(tables);
        FileLocales.BitOf(locale);
        var byId = new SortedDictionary<uint, FakeTable>();
        foreach (var table in tables)
        {
            Validate(table);
            if (!byId.TryAdd(table.FileDataId, table)) throw new ArgumentException($"Table {table.FileDataId} is listed twice.", nameof(tables));
        }
        var names = byId.Values.Where(t => t.Name is not null).Select(t => t.Name!).ToList();
        for (var i = 0; i < names.Count; i++)
            for (var j = i + 1; j < names.Count; j++)
                if (TableQueries.NamesEqual(names[i], names[j])) throw new ArgumentException($"The name {names[i]} is given twice.", nameof(tables));
        lock (gate)
        {
            builds[(build, locale)] = byId;
            failures.Remove(build);
            foreach (var table in byId.Values) if (table.Generated) checkedTables.Add((build, locale, table.FileDataId));
        }
        return this;
    }

    /// <summary>Makes opening <paramref name="build"/> fail with a layer 4 state and code.</summary>
    public InMemoryTables AddFailing(BuildRef build, FilesOpenState state, string code)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (state == FilesOpenState.Open) throw new ArgumentException("A failure isn't Open.", nameof(state));
        lock (gate) failures[build] = (state, code);
        return this;
    }

    /// <summary>How many times a method of <see cref="ITables"/> or <see cref="IBuildTables"/> was called, by its name (<c>nameof</c>).</summary>
    public int Calls(string method)
    {
        lock (gate) return calls.GetValueOrDefault(method);
    }

    private void Count(string method)
    {
        lock (gate) calls[method] = calls.GetValueOrDefault(method) + 1;
    }

    public Task<TablesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
    {
        Count(nameof(ForAsync));
        ArgumentNullException.ThrowIfNull(build);
        FileLocales.BitOf(locale);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (failures.TryGetValue(build, out var failure))
                return Task.FromResult(new TablesOpenResult(build, locale, failure.State, null, failure.Code, "Scripted open failure.", null));
            if (builds.ContainsKey((build, locale)))
                return Task.FromResult(new TablesOpenResult(build, locale, FilesOpenState.Open, new Handle(this, build, locale), null, null, null));
            if (builds.Keys.Any(key => key.Build == build))
                return Task.FromResult(new TablesOpenResult(build, locale, FilesOpenState.LocaleNotInBuild, null, "FILES_LOCALE_NOT_IN_BUILD", null, null));
        }
        return Task.FromResult(new TablesOpenResult(build, locale, FilesOpenState.NeedsNetwork, null, "BUILD_NEEDS_NETWORK", "The build isn't stored.", null));
    }

    private static void Validate(FakeTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.FileDataId == 0) throw new ArgumentException("FileDataID 0 isn't a table.", nameof(table));
        if (!Enum.IsDefined(table.State) || table.State is TableState.NotChecked or TableState.NotStored)
            throw new ArgumentException("A checked state is never NotChecked or NotStored.", nameof(table));
        if (table.ListedState is { } listed && listed != table.State && listed is not (TableState.NotChecked or TableState.NotStored))
            throw new ArgumentException("A listed state is NotChecked, NotStored or the checked state.", nameof(table));
        if (table.Rows is { Count: > 0 } && !TableQueries.IsReadable(table.State))
            throw new ArgumentException("Only a Ready or Partial table has rows.", nameof(table));
        if (table.ProtectedIds is { Count: > 0 } && table.State is not (TableState.Partial or TableState.Encrypted))
            throw new ArgumentException("Only a Partial or Encrypted table has protected IDs.", nameof(table));
        var ids = new HashSet<uint>();
        foreach (var row in table.Rows ?? [])
        {
            if (!ids.Add(row.Id)) throw new ArgumentException($"Row {row.Id} is given twice.", nameof(table));
            if (table.Columns is { } columns && row.Values.Count != columns.Count)
                throw new ArgumentException($"Row {row.Id} doesn't line up with the columns.", nameof(table));
        }
        if (table.ProtectedIds is { } protectedIds && protectedIds.Keys.Any(ids.Contains))
            throw new ArgumentException("A protected ID is also a readable row.", nameof(table));
    }

    private static bool Opened(TableState state) => state is TableState.Ready or TableState.Partial or TableState.Encrypted;

    private static TableState Listed(FakeTable table) => table.ListedState ?? table.State switch
    {
        // A layout mismatch (DB2_SCHEMA_*) is found only by opening the file.
        TableState.DefinitionUnusable when table.Reason?.Code?.StartsWith("DB2_SCHEMA_", StringComparison.Ordinal) == true => TableState.NotChecked,
        TableState.NotInBuild or TableState.NotInLocale or TableState.NotInEncoding or TableState.NoDefinition or TableState.DefinitionUnusable => table.State,
        TableState.NeedsNetwork or TableState.Missing or TableState.Unavailable => TableState.NotStored,
        _ => TableState.NotChecked,
    };

    private static TableEntry Entry(FakeTable table, bool isChecked)
    {
        var state = isChecked ? table.State : Listed(table);
        var keys = (table.ProtectedIds?.Values ?? []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var reason = table.Reason ?? (keys.Length > 0 ? new TableStateReason(null, null, null, null, null, keys) : null);
        var opened = isChecked && Opened(table.State);
        var rows = table.Rows?.Count ?? 0;
        var excluded = table.ProtectedIds?.Count ?? 0;
        var counts = !opened ? null : table.Counts ?? new TableCounts(rows + excluded, rows, keys.Length, excluded, "WDC5", "00000000");
        var inBuild = table.State is not (TableState.NotInBuild or TableState.NotInLocale);
        return new(table.FileDataId, table.Name, state, isChecked, state == table.State ? reason : null, counts,
            inBuild ? table.ContentKey ?? table.FileDataId.ToString("x32", CultureInfo.InvariantCulture) : null);
    }

    private sealed class Handle(InMemoryTables owner, BuildRef build, string locale) : IBuildTables, ITableRelationSource
    {
        private int disposed;

        public BuildRef Build => build;
        public string Locale => locale;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref disposed, 1);
            return ValueTask.CompletedTask;
        }

        private void Enter(string method, TablePriority priority, CancellationToken cancellationToken)
        {
            owner.Count(method);
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
            cancellationToken.ThrowIfCancellationRequested();
        }

        private SortedDictionary<uint, FakeTable> Tables()
        {
            lock (owner.gate) return owner.builds[(build, locale)];
        }

        private bool IsChecked(uint id)
        {
            lock (owner.gate) return owner.checkedTables.Contains((build, locale, id));
        }

        public async IAsyncEnumerable<TableEntry> ListAsync(TableListFilter filter, TablePriority priority,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Enter(nameof(ListAsync), priority, cancellationToken);
            TableQueries.Validate(filter);
            foreach (var table in Tables().Values.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = Entry(table, IsChecked(table.FileDataId));
                if (TableQueries.Matches(filter, entry)) yield return entry;
                await Task.Yield();
            }
        }

        // Finds and checks a table: the checked entry, and the seed when the table is known.
        private (TableEntry Entry, FakeTable? Table) Check(TableRef table)
        {
            TableQueries.Validate(table);
            var tables = Tables();
            FakeTable? found = null;
            if (table.FileDataId is { } id) tables.TryGetValue(id, out found);
            else found = tables.Values.FirstOrDefault(t => t.Name is { } name && TableQueries.NamesEqual(name, table.Name!));
            if (found is null)
                return table.FileDataId is { } missing
                    ? (new TableEntry(missing, null, TableState.NotInBuild, true, null, null, null), null)
                    : (new TableEntry(0, table.Name, TableState.NoDefinition, true, null, null, null), null);
            if (Opened(found.State)) lock (owner.gate) owner.checkedTables.Add((build, locale, found.FileDataId));
            return (Entry(found, true), found);
        }

        public Task<TableEntry> CheckAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken)
        {
            Enter(nameof(CheckAsync), priority, cancellationToken);
            return Task.FromResult(Check(table).Entry);
        }

        public Task<TableSchemaResult> SchemaAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken)
        {
            Enter(nameof(SchemaAsync), priority, cancellationToken);
            var (entry, seed) = Check(table);
            if (seed is null) return Task.FromResult(new TableSchemaResult(entry, null, null));
            var schema = seed.State is TableState.NoDefinition or TableState.DefinitionUnusable ? null : seed.Schema;
            return Task.FromResult(new TableSchemaResult(entry, Opened(seed.State) ? seed.Columns : null, schema));
        }

        public async Task<TablePage> PageAsync(TablePageRequest request, TablePriority priority, CancellationToken cancellationToken)
        {
            Enter(nameof(PageAsync), priority, cancellationToken);
            TableQueries.Validate(request);
            var (entry, seed) = Check(request.Table);
            if (seed is null || !TableQueries.IsReadable(entry.State)) return TableQueries.NotReadable(entry);
            var identity = $"fake:{build.BuildConfig}:{locale}:{entry.FileDataId}:{entry.ContentKey}";
            var rows = seed.Rows ?? [];
            return await TableQueries.PageAsync(entry, identity, request, rows.Count,
                (first, count, _) => Task.FromResult<IReadOnlyList<Db2Row>>(rows.Skip((int)first).Take(count).ToArray()),
                seed.Columns ?? [], seed.Schema, owner.MaxScanRows, cancellationToken).ConfigureAwait(false);
        }

        public Task<TableRowResult> GetAsync(TableRef table, uint id, TablePriority priority, CancellationToken cancellationToken)
        {
            Enter(nameof(GetAsync), priority, cancellationToken);
            return Task.FromResult(Get(table, id));
        }

        private TableRowResult Get(TableRef table, uint id)
        {
            var (entry, seed) = Check(table);
            if (seed is null || !Opened(seed.State)) return new TableRowResult(entry, TableRowOutcome.Unknown, null, null);
            if (seed.ProtectedIds?.GetValueOrDefault(id) is { } key) return new TableRowResult(entry, TableRowOutcome.Protected, null, key);
            var rows = seed.Rows ?? [];
            for (var i = 0; i < rows.Count; i++)
                if (rows[i].Id == id) return new TableRowResult(entry, TableRowOutcome.Found, new TableRow(i, rows[i]), null);
            var outcome = seed.State == TableState.Ready ? TableRowOutcome.Absent : TableRowOutcome.Unknown;
            return new TableRowResult(entry, outcome, null, null);
        }

        public Task<TableFollowResult> FollowAsync(TableFollowRequest request, TablePriority priority, CancellationToken cancellationToken)
        {
            Enter(nameof(FollowAsync), priority, cancellationToken);
            return TableQueries.FollowAsync(this, request, owner.MaxScanRows, cancellationToken);
        }

        public Task<TableReferencesResult> ReferencedByAsync(TableReferencesRequest request, TablePriority priority, CancellationToken cancellationToken)
        {
            Enter(nameof(ReferencedByAsync), priority, cancellationToken);
            return TableQueries.ReferencedByAsync(this, request, owner.MaxScanRows, owner.MaxReferenceSources, owner.MaxReferenceScanRows, cancellationToken);
        }

        Task<RelationTable> ITableRelationSource.OpenAsync(TableRef table, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (entry, seed) = Check(table);
            if (seed is null) return Task.FromResult(new RelationTable(entry, null, null, 0, null));
            var schema = seed.State is TableState.NoDefinition or TableState.DefinitionUnusable ? null : seed.Schema;
            var rows = seed.Rows ?? [];
            TableRowReader? read = TableQueries.IsReadable(entry.State)
                ? (first, count, _) => Task.FromResult<IReadOnlyList<Db2Row>>(rows.Skip((int)first).Take(count).ToArray())
                : null;
            return Task.FromResult(new RelationTable(entry, Opened(seed.State) ? seed.Columns : null, schema, read is null ? 0 : rows.Count, read));
        }

        Task<TableRowResult> ITableRelationSource.GetAsync(TableRef table, uint id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Get(table, id));
        }

        async IAsyncEnumerable<TableEntry> ITableRelationSource.ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var table in Tables().Values.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return Entry(table, IsChecked(table.FileDataId));
                await Task.Yield();
            }
        }

        // From the seed only, never a check: a layout-matched schema needs the file, so it resolves only once the table was checked.
        Task<RelationSchema> ITableRelationSource.SchemaAsync(TableEntry table, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var seed = Tables().GetValueOrDefault(table.FileDataId);
            RelationSchema result = seed switch
            {
                null or { State: TableState.NoDefinition } => new(RelationSchemaState.NoDefinition, null),
                { State: TableState.DefinitionUnusable } or { Schema: null } => new(RelationSchemaState.Unresolved, null),
                { Schema.Match: SchemaMatch.Layout } when !IsChecked(seed.FileDataId) => new(RelationSchemaState.Unresolved, null),
                _ => new(RelationSchemaState.Resolved, seed.Schema),
            };
            return Task.FromResult(result);
        }
    }
}
