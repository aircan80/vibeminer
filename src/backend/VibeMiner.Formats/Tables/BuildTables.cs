using System.Runtime.CompilerServices;
using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Generated;
using VibeMiner.Generated;
using VibeMiner.Objects;

namespace VibeMiner.Formats.Tables;

/// <summary>
/// One build and locale's tables over a layer 4 handle, which it owns: the handle closes once this one is disposed and no call is
/// still running. Shallow states come from a stat, a local definition lookup and a generated store lookup; a check opens the DB2
/// file only when the generated store doesn't hold its rows yet. The handle asks WoWDBDefs at most once (one fetch budget).
/// A layer 2 failure layer 4 throws while checking stored state (<see cref="ObjectStoreException"/>) is the table's StoreFailed.
/// Reading a DB2 file (a check that builds, a protected-ID lookup) and each chunk of a scan run in a <paramref name="scheduler"/>
/// slot of the call's priority; anything served from the generated store takes none (D263). While a table isn't in the generated
/// store, an Interactive unfiltered page, row by ID or schema reads the DB2 file directly and starts the build in the background
/// (D330): the answer is the one the generated path gives once built, identity included.
/// </summary>
internal sealed class BuildTables(IBuildFiles files, string version, WowLocale locale, TablesService.Manifest manifest, TableDefinitions definitions,
    IGeneratedTables generated, Db2Limits limits, Func<BuildRef, ITactKeyStatus> keys, int maxScanRows, int maxReferenceSources, long maxReferenceScanRows,
    ProtectedIds protectedIds, TableWorkScheduler scheduler, BackgroundBuilds builds) : IBuildTables
{
    /// <summary>The unmanifested DB2s looked for: listfile names under dbfilesclient/ only, so no other file's head is read.</summary>
    private static readonly FileFilter Db2Names = new() { PathPrefix = "dbfilesclient/", Formats = new HashSet<FileFormat> { FileFormat.Db2 } };
    /// <summary>Enough of the file's head for any DB2 header the reader admits (WDC5's is the longest).</summary>
    private const int HeaderBytes = 204;
    private readonly DefinitionFetchBudget budget = new();
    private readonly SemaphoreSlim asking = new(1, 1);
    private int running, disposed, closed;

    public BuildRef Build => files.Build;
    public string Locale => files.Locale;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0 && Volatile.Read(ref running) == 0) await CloseAsync().ConfigureAwait(false);
    }

    private async ValueTask CloseAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) == 0) await files.DisposeAsync().ConfigureAwait(false);
    }

    private void Enter(TablePriority priority, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref running);
        if (Volatile.Read(ref disposed) != 0)
        {
            Interlocked.Decrement(ref running);
            throw new ObjectDisposedException(GetType().FullName);
        }
    }

    private async ValueTask ExitAsync()
    {
        if (Interlocked.Decrement(ref running) == 0 && Volatile.Read(ref disposed) != 0) await CloseAsync().ConfigureAwait(false);
    }

    private static TableStateReason StoreFailure(ObjectStoreException failure) => new(failure.Code, failure.Message, null, null, null, []);

    /// <summary>A stat, or the layer 2 failure that stopped it.</summary>
    private async Task<(FileStat? Stat, TableStateReason? Failure)> StatAsync(uint id, CancellationToken cancellationToken)
    {
        try { return (await files.StatAsync(FileRef.Id(id), cancellationToken).ConfigureAwait(false), null); }
        catch (ObjectStoreException failure) { return (null, StoreFailure(failure)); }
    }

    // ListAsync

    public async IAsyncEnumerable<TableEntry> ListAsync(TableListFilter filter, TablePriority priority,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        TableQueries.Validate(filter);
        Enter(priority, cancellationToken);
        try
        {
            await foreach (var entry in ListCoreAsync(filter, cancellationToken).ConfigureAwait(false)) yield return entry;
        }
        finally { await ExitAsync().ConfigureAwait(false); }
    }

    private async IAsyncEnumerable<TableEntry> ListCoreAsync(TableListFilter filter, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // A name filter matches only named tables, so the DB2 files the manifest doesn't name aren't looked for. A layer 2
        // failure while looking leaves them out: the list never claims to cover every DB2.
        var others = new SortedDictionary<uint, BuildFile>();
        if (filter.Name is null)
        {
            try
            {
                await foreach (var file in files.ListAsync(Db2Names, cancellationToken).ConfigureAwait(false))
                    if (!manifest.ById.ContainsKey(file.FileDataId)) others[file.FileDataId] = file;
            }
            catch (ObjectStoreException) { }
        }
        var ids = new SortedSet<uint>(manifest.ById.Keys);
        ids.UnionWith(others.Keys);
        var status = keys(Build);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            manifest.ById.TryGetValue(id, out var table);
            // The name is known before any lookup: skip the stat and the definition for a name that doesn't match.
            if (filter.Name is { } name && !TableQueries.Matches(new() { Name = name }, Shallow(id, table?.Name))) continue;
            var entry = table is null ? Unmanifested(id, others[id].ContentKey, false)
                : await ShallowAsync(id, table, status, cancellationToken).ConfigureAwait(false);
            if (TableQueries.Matches(filter, entry)) yield return entry;
        }
    }

    private static TableEntry Shallow(uint id, string? name) => new(id, name, TableState.NotChecked, false, null, null, null);

    private static TableEntry Unmanifested(uint id, string? contentKey, bool isChecked) =>
        new(id, null, TableState.NoDefinition, isChecked, new(TableStates.NotInManifest, "The definition manifest doesn't name this DB2 file.", null, null, null, []),
            null, contentKey);

    private async Task<TableEntry> ShallowAsync(uint id, DefinitionTable table, ITactKeyStatus status, CancellationToken cancellationToken)
    {
        var (stat, failure) = await StatAsync(id, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return new(id, table.Name, TableState.StoreFailed, false, failure, null, null);
        var contentKey = stat!.File?.ContentKey;
        if (TableStates.Lookup(stat.State) is { } missing) return new(id, table.Name, missing, false, null, null, contentKey);
        var notChecked = new TableEntry(id, table.Name, stat.File!.Storage == FileStorage.Stored ? TableState.NotChecked : TableState.NotStored, false,
            null, null, contentKey);
        var resolution = await definitions.ResolveAsync(table.TableId, version, null, locale, DefinitionLookup.Local, cancellationToken).ConfigureAwait(false);
        // A local miss with fetching on may still be fetched, and the file's layout may still pick a block: only a check says.
        if (resolution.State == DefinitionState.NoMatch) return notChecked;
        if (TableStates.Definition(resolution.State) is { } unusable)
            return new(id, table.Name, unusable, false, TableStates.DefinitionReason(resolution), null, contentKey);
        var stored = await generated.OpenAsync(Source(contentKey!, resolution.Definition!), status, cancellationToken).ConfigureAwait(false);
        return stored is not null ? Checked(id, table.Name, contentKey, stored) : notChecked;
    }

    private GeneratedTableSource Source(string contentKey, TableDefinition definition) =>
        new(Build, Locale, contentKey, definition.Sha256, limits);

    private static TableEntry Checked(uint id, string? name, string? contentKey, GeneratedTable table)
    {
        var (state, reason, counts) = TableStates.Generated(table);
        return new(id, name, state, true, reason, counts, contentKey);
    }

    // CheckAsync

    /// <summary>A check's outcome: the entry, the stored table when it opened, and the definition it read with.</summary>
    private sealed record Opened(TableEntry Entry, GeneratedTable? Table, DefinitionTable? Definition, DefinitionResolution? Resolution,
        DirectRead? Direct = null);

    public async Task<TableEntry> CheckAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken)
    {
        TableQueries.Validate(table);
        Enter(priority, cancellationToken);
        try { return (await OpenAsync(table, priority, cancellationToken).ConfigureAwait(false)).Entry; }
        finally { await ExitAsync().ConfigureAwait(false); }
    }

    /// <summary>
    /// A check. With <paramref name="direct"/> at Interactive priority, a table not in the generated store yet comes back as a
    /// <see cref="DirectRead"/> (the caller disposes it) while its build runs in the background, unless the file doesn't open
    /// directly or its last background build failed; then, as without it, the call waits for the build.
    /// </summary>
    private async Task<Opened> OpenAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken, bool direct = false)
    {
        uint id;
        DefinitionTable? definition;
        if (table.FileDataId is { } asked) { id = asked; manifest.ById.TryGetValue(id, out definition); }
        else if (manifest.Named(table.Name!) is { } named) (id, definition) = named;
        else return new(new(0, table.Name, TableState.NoDefinition, true,
            new(TableStates.NotInManifest, "The definition manifest has no table of this name.", null, null, null, []), null, null), null, null, null);

        var (stat, failure) = await StatAsync(id, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return new(new(id, definition?.Name, TableState.StoreFailed, true, failure, null, null), null, definition, null);
        var contentKey = stat!.File?.ContentKey;
        if (TableStates.Lookup(stat.State) is { } missing) return new(new(id, definition?.Name, missing, true, null, null, contentKey), null, definition, null);
        if (definition is null)
            // A listed file that isn't a DB2 is no table of this build.
            return new(stat.File!.Format == FileFormat.Db2 ? Unmanifested(id, contentKey, true) : new(id, null, TableState.NotInBuild, true, null, null, contentKey),
                null, null, null);

        // Already decoded with the definition a local lookup picks: no byte is read.
        var status = keys(Build);
        var local = await definitions.ResolveAsync(definition.TableId, version, null, locale, DefinitionLookup.Local, cancellationToken).ConfigureAwait(false);
        if (local.State == DefinitionState.Resolved
            && await generated.OpenAsync(Source(contentKey!, local.Definition!), status, cancellationToken).ConfigureAwait(false) is { } hit)
            return new(Checked(id, definition.Name, contentKey, hit), hit, definition, local);

        // From here the file is read: in a slot. A caller joining a build another one started holds its own lane's slot while it
        // waits, so it never waits behind queued work of the other lane.
        var slot = await scheduler.EnterAsync(priority, cancellationToken).ConfigureAwait(false);
        DirectRead? handed = null;
        try
        {
            var opened = await OpenSlottedAsync(id, definition, contentKey, status, slot, priority, cancellationToken, direct).ConfigureAwait(false);
            handed = opened.Direct;
            return opened;
        }
        finally { if (handed is null) slot.Dispose(); }
    }

    private async Task<Opened> OpenSlottedAsync(uint id, DefinitionTable definition, string? contentKey, ITactKeyStatus status, TableWorkSlot slot,
        TablePriority priority, CancellationToken cancellationToken, bool direct)
    {
        // The definition the reader will select from: by the file's own layout, so a build listed only by layout resolves too.
        var (layout, unread) = await LayoutAsync(id, cancellationToken).ConfigureAwait(false);
        var resolution = await ResolveAsync(definition.TableId, layout, cancellationToken).ConfigureAwait(false);
        // A head that can't be read hides the layout: its own state says why, not a definition the layout might have picked.
        if (resolution.State != DefinitionState.Resolved && unread is not null)
            return new(new(id, definition.Name, unread.Value.State, true, unread.Value.Reason, null, contentKey), null, definition, resolution);
        if (TableStates.Definition(resolution.State) is { } unusable)
            return new(new(id, definition.Name, unusable, true, TableStates.DefinitionReason(resolution), null, contentKey), null, definition, resolution);

        var document = resolution.Definition!.Document;
        var source = Source(contentKey!, resolution.Definition);
        if (direct && priority == TablePriority.Interactive && !builds.HasFailed(source))
        {
            // The build may have ended while this call waited for its slot.
            if (await generated.OpenAsync(source, status, cancellationToken).ConfigureAwait(false) is { } built)
                return new(Checked(id, definition.Name, contentKey, built), built, definition, resolution);
            if (await DirectAsync(id, definition.Name, contentKey!, document, source, status, slot, cancellationToken).ConfigureAwait(false) is { } read)
                return new(read.Entry, null, definition, resolution, read);
        }
        var opens = new List<Db2Open>();
        try
        {
            var stored = await generated.GetOrBuildAsync(source, status, token => DecodeAsync(id, document, opens, token), cancellationToken).ConfigureAwait(false);
            builds.Stored(source);
            return new(Checked(id, definition.Name, contentKey, stored), stored, definition, resolution);
        }
        catch (OpenFailedException failed)
        {
            var open = failed.Open;
            return Failed(TableStates.Open(open.State, open.Code), new(open.Code, open.Message, open.ObservedAt, null, open.State,
                TableStates.KeyNames(open.Holes.Select(hole => hole.Name))));
        }
        catch (Db2FormatException refused)
        {
            return Failed(TableStates.Reader(refused.Code), new(refused.Code, refused.Message, null, null, Db2OpenState.Rejected, HoleNames(opens)));
        }
        catch (Db2SourceException read)
        {
            return Failed(TableStates.Open(read.State, read.Code), new(read.Code, read.Message, read.ObservedAt, null, read.State, HoleNames(opens)));
        }
        catch (GeneratedStoreException store)
        {
            return Failed(TableState.StoreFailed, new(store.Code, store.Message, null, null, null, []));
        }

        Opened Failed(TableState state, TableStateReason reason) =>
            new(new(id, definition.Name, state, true, reason, null, contentKey), null, definition, resolution);
    }

    /// <summary>
    /// The DB2 header's layout hash; null when the head isn't a DB2 (the open then says why). A head that can't be read gives no
    /// layout and the state that stopped it, as the open would map it: the read's own state, Encrypted when a hole covers its
    /// start, StoreFailed for a layer 2 failure.
    /// </summary>
    private async Task<(string? Layout, (TableState State, TableStateReason Reason)? Unread)> LayoutAsync(uint id, CancellationToken cancellationToken)
    {
        FileRead read;
        try { read = await files.ReadRangeAsync(FileRef.Id(id), 0, HeaderBytes, cancellationToken).ConfigureAwait(false); }
        catch (ObjectStoreException failure) { return (null, (TableState.StoreFailed, StoreFailure(failure))); }
        if (read.State is FileReadState.Complete or FileReadState.Partial && read.Segments is [{ Offset: 0 } first, ..])
            return (Db2Reader.LayoutHash(first.Bytes.Span), null);
        var keys = TableStates.KeyNames(read.Holes.Select(hole => hole.KeyName));
        if (read.State == FileReadState.Protected || read.State == FileReadState.Partial && read.Holes is [{ Offset: 0 }, ..])
            return (null, (TableState.Encrypted, new("DB2_SOURCE_PROTECTED", "The file's head lies in a keyed hole.", null, null, Db2OpenState.Protected, keys)));
        // An empty file: no head to read, the open says why.
        if (read.State is FileReadState.Complete or FileReadState.Partial) return (null, null);
        var state = Db2FileSource.StateOf(read.State);
        return (null, (TableStates.Open(state, read.Code), new(read.Code, read.Message, read.ObservedAt, null, state, keys)));
    }

    /// <summary>A resolution that may ask WoWDBDefs, sharing this handle's one budget; asks run one at a time.</summary>
    private async Task<DefinitionResolution> ResolveAsync(string tableId, string? layout, CancellationToken cancellationToken)
    {
        await asking.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await definitions.ResolveAsync(tableId, version, layout, locale, DefinitionLookup.Ask(budget, _ => Task.CompletedTask),
                cancellationToken).ConfigureAwait(false);
        }
        finally { asking.Release(); }
    }

    private static Db2Open[] Snapshot(List<Db2Open> opens)
    {
        lock (opens) return [.. opens];
    }

    private static IReadOnlyList<string> HoleNames(List<Db2Open> opens) => TableStates.KeyNames(Snapshot(opens).SelectMany(open => open.Holes).Select(hole => hole.Name));

    /// <summary>
    /// Opens the DB2 file and hands its rows to the generated store, which reads them once. The open is the content's owner: the
    /// store closes it when it is done, so a decode that runs on after its caller canceled still closes the file.
    /// </summary>
    private async Task<GeneratedTableContent> DecodeAsync(uint id, DbdDocument document, List<Db2Open> opens, CancellationToken token)
    {
        var open = await Db2Reader.OpenAsync(files, FileRef.Id(id), document, version, limits, token).ConfigureAwait(false);
        if (open.State != Db2OpenState.Open || open.Table is not { } table)
        {
            await open.DisposeAsync().ConfigureAwait(false);
            throw new OpenFailedException(open);
        }
        lock (opens) opens.Add(open);
        return new GeneratedTableContent(table.Header, table.Sections, table.Completion, [.. KeysUsed(table, open)], RowsAsync(open, token), open);
    }

    /// <summary>The key names a decode depends on, sorted. Decrypted sections' keys too: their rows depend on the key, so the identity
    /// carries its fingerprint (#179, D310).</summary>
    private static IReadOnlyList<string> KeysUsed(Db2Table table, Db2Open open) =>
        TableStates.KeyNames(table.Sections.Where(section => section.IsEncrypted).Select(section => section.Key).Concat(open.Holes.Select(hole => hole.Name)));

    /// <summary>
    /// Opens the DB2 file for a direct read (D330) in the caller's slot and starts the table's build in the background. The entry and
    /// identity are the ones the stored table will have. Null when the file doesn't open, or has broken copy chains (any full read
    /// then fails): the caller waits for the build, which says why.
    /// </summary>
    private async Task<DirectRead?> DirectAsync(uint id, string? name, string contentKey, DbdDocument document, GeneratedTableSource source,
        ITactKeyStatus status, TableWorkSlot slot, CancellationToken cancellationToken)
    {
        Db2Open open;
        try { open = await Db2Reader.OpenAsync(files, FileRef.Id(id), document, version, limits, cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (e is Db2FormatException or Db2SourceException or ObjectStoreException) { return null; }
        if (open.State != Db2OpenState.Open || open.Table is not { UnresolvedCopies: 0 } table)
        {
            await open.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        try
        {
            var used = KeysUsed(table, open);
            var rows = table.ReadableRowCount;
            var (state, reason, counts) = TableStates.Generated(table.Header, table.Sections, table.Completion, used, rows);
            var identity = generated.IdentityOf(source, status, used);
            BuildInBackground(id, document, source, status);
            return new DirectRead(new(id, name, state, true, reason, counts, contentKey), identity, table, rows, open, slot);
        }
        catch
        {
            await open.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Builds the table in a Background slot, detached from the call that asked, unless <see cref="BackgroundBuilds"/> says one runs
    /// or too many do. It joins any build of the same table already running (one decode per table), and holds the handle's files
    /// open until it ends, also past the handle's dispose. It is never canceled; a failure is remembered so later calls wait for
    /// the build and answer its state. A full Background queue starts nothing: a later direct read tries again.
    /// </summary>
    private void BuildInBackground(uint id, DbdDocument document, GeneratedTableSource source, ITactKeyStatus status)
    {
        if (!builds.TryStart(source)) return;
        // Called inside a running call, so the files are still open.
        Interlocked.Increment(ref running);
        _ = BuildAsync();

        async Task BuildAsync()
        {
            bool stored = false, failure = false;
            try
            {
                await Task.Yield();
                using var slot = await scheduler.EnterAsync(TablePriority.Background, CancellationToken.None).ConfigureAwait(false);
                await generated.GetOrBuildAsync(source, status, token => DecodeAsync(id, document, [], token), CancellationToken.None).ConfigureAwait(false);
                stored = true;
            }
            catch (TablesBusyException) { }
            // Detached: nobody observes it. Any failure (a reader, layer 4 or store one) sends later calls to the generated path.
            catch (Exception) { failure = true; }
            finally
            {
                builds.End(source, stored, failure);
                await ExitAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>A direct read's open DB2 file and the slot it reads in, both released on dispose.</summary>
    private sealed class DirectRead(TableEntry entry, string identity, Db2Table table, long rowCount, Db2Open open, TableWorkSlot slot) : IAsyncDisposable
    {
        public TableEntry Entry => entry;
        public string Identity => identity;
        public Db2Table Table => table;
        public long RowCount => rowCount;

        public async Task<IReadOnlyList<Db2Row>> ReadAsync(long first, int count, CancellationToken token)
        {
            var rows = new List<Db2Row>(count);
            await foreach (var row in table.ReadOrdinalsAsync(first, count, token).ConfigureAwait(false)) rows.Add(row);
            return rows;
        }

        public async ValueTask DisposeAsync()
        {
            try { await open.DisposeAsync().ConfigureAwait(false); }
            finally { slot.Dispose(); }
        }
    }

    private static async IAsyncEnumerable<Db2Row> RowsAsync(Db2Open open, [EnumeratorCancellation] CancellationToken token)
    {
        try
        {
            await foreach (var row in open.Table!.ReadAllAsync(token).ConfigureAwait(false)) yield return row;
        }
        finally { await open.DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>Carries a DB2 open that didn't open out of the generated store's build, so nothing is stored.</summary>
    private sealed class OpenFailedException(Db2Open open) : Exception(open.Message ?? open.Code ?? open.State.ToString())
    {
        public Db2Open Open { get; } = open;
    }

    // SchemaAsync

    public async Task<TableSchemaResult> SchemaAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken)
    {
        TableQueries.Validate(table);
        Enter(priority, cancellationToken);
        try
        {
            var opened = await OpenAsync(table, priority, cancellationToken, direct: true).ConfigureAwait(false);
            if (opened.Direct is { } read)
                await using (read)
                    return new(read.Entry, read.Table.Header.Columns,
                        await SchemaOfAsync(opened.Definition!, read.Table.Header.LayoutHash, cancellationToken).ConfigureAwait(false));
            if (opened.Table is { } stored)
                return new(opened.Entry, stored.Header.Columns, await SchemaOfAsync(opened.Definition!, stored, cancellationToken).ConfigureAwait(false));
            return new(opened.Entry, null, opened.Resolution is { State: DefinitionState.Resolved } resolved ? resolved.Schema : null);
        }
        finally { await ExitAsync().ConfigureAwait(false); }
    }

    /// <summary>The schema of the stored table's own layout, from a local lookup; null when none resolves.</summary>
    private Task<TableSchema?> SchemaOfAsync(DefinitionTable definition, GeneratedTable stored, CancellationToken cancellationToken) =>
        SchemaOfAsync(definition, stored.Header.LayoutHash, cancellationToken);

    private async Task<TableSchema?> SchemaOfAsync(DefinitionTable definition, string layout, CancellationToken cancellationToken)
    {
        var resolution = await definitions.ResolveAsync(definition.TableId, version, layout, locale,
            DefinitionLookup.Local, cancellationToken).ConfigureAwait(false);
        return resolution.State == DefinitionState.Resolved ? resolution.Schema : null;
    }

    // PageAsync and GetAsync

    public async Task<TablePage> PageAsync(TablePageRequest request, TablePriority priority, CancellationToken cancellationToken)
    {
        TableQueries.Validate(request);
        Enter(priority, cancellationToken);
        try
        {
            var opened = await OpenAsync(request.Table, priority, cancellationToken, direct: request.Filters is not { Count: > 0 }).ConfigureAwait(false);
            if (opened.Direct is { } direct)
            {
                await using (direct)
                {
                    if (!TableQueries.IsReadable(direct.Entry.State)) return TableQueries.NotReadable(direct.Entry);
                    try { return await TableQueries.PageAsync(direct.Entry, direct.Identity, request, direct.RowCount, direct.ReadAsync, cancellationToken).ConfigureAwait(false); }
                    // A row that doesn't read fails the build too: the generated path below says how.
                    catch (Exception e) when (e is Db2FormatException or Db2SourceException) { }
                }
                opened = await OpenAsync(request.Table, priority, cancellationToken).ConfigureAwait(false);
            }
            if (opened.Table is not { } stored || !TableQueries.IsReadable(opened.Entry.State)) return TableQueries.NotReadable(opened.Entry);
            try
            {
                TableRowReader read = (first, count, token) => ReadRowsAsync(stored, first, count, token);
                if (request.Filters is not { Count: > 0 })
                    return await TableQueries.PageAsync(opened.Entry, stored.Identity, request, stored.RowCount, read, cancellationToken).ConfigureAwait(false);
                var schema = await SchemaOfAsync(opened.Definition!, stored, cancellationToken).ConfigureAwait(false);
                return await TableQueries.PageAsync(opened.Entry, stored.Identity, request, stored.RowCount, Scheduled(read, priority), stored.Header.Columns, schema,
                    maxScanRows, cancellationToken).ConfigureAwait(false);
            }
            catch (GeneratedStoreException store) { return TableQueries.NotReadable(Damaged(opened.Entry, store)); }
        }
        finally { await ExitAsync().ConfigureAwait(false); }
    }

    /// <summary>A scan's reads, each chunk in a slot of its own: a Background scan yields to Interactive work between chunks.</summary>
    private TableRowReader Scheduled(TableRowReader read, TablePriority priority) => async (first, count, token) =>
    {
        using var slot = await scheduler.EnterAsync(priority, token).ConfigureAwait(false);
        return await read(first, count, token).ConfigureAwait(false);
    };

    /// <summary>A stored table found damaged while reading. It is gone from the store: the next call decodes it again.</summary>
    private static TableEntry Damaged(TableEntry entry, GeneratedStoreException store) => entry with
    {
        State = TableState.StoreFailed, Reason = new(store.Code, store.Message, null, null, null, []), Counts = null,
    };

    private static async Task<IReadOnlyList<Db2Row>> ReadRowsAsync(GeneratedTable table, long first, int count, CancellationToken token)
    {
        if (count <= GeneratedTable.MaxPageRows) return await table.ReadPageAsync(first, count, token).ConfigureAwait(false);
        var rows = new List<Db2Row>(count);
        while (rows.Count < count)
        {
            var page = await table.ReadPageAsync(first + rows.Count, Math.Min(GeneratedTable.MaxPageRows, count - rows.Count), token).ConfigureAwait(false);
            if (page.Count == 0) break;
            rows.AddRange(page);
        }
        return rows;
    }

    public async Task<TableRowResult> GetAsync(TableRef table, uint id, TablePriority priority, CancellationToken cancellationToken)
    {
        TableQueries.Validate(table);
        Enter(priority, cancellationToken);
        try { return await GetCoreAsync(table, id, priority, cancellationToken, direct: true).ConfigureAwait(false); }
        finally { await ExitAsync().ConfigureAwait(false); }
    }

    private async Task<TableRowResult> GetCoreAsync(TableRef table, uint id, TablePriority priority, CancellationToken cancellationToken, bool direct = false)
    {
        var opened = await OpenAsync(table, priority, cancellationToken, direct).ConfigureAwait(false);
        if (opened.Direct is { } read)
        {
            await using (read)
            {
                try
                {
                    // The generated path's order: the row, Absent for a Ready table, else the file's protected IDs.
                    var (ordinal, lookup) = await read.Table.FindWithOrdinalAsync(id, cancellationToken).ConfigureAwait(false);
                    if (lookup is Db2Lookup.Found found) return new(read.Entry, TableRowOutcome.Found, new TableRow(ordinal, found.Row), null);
                    if (read.Entry.State == TableState.Ready) return new(read.Entry, TableRowOutcome.Absent, null, null);
                    return lookup is Db2Lookup.Protected hit ? new(read.Entry, TableRowOutcome.Protected, null, hit.Key) : new(read.Entry, TableRowOutcome.Unknown, null, null);
                }
                catch (Exception e) when (e is Db2FormatException or Db2SourceException) { }
            }
            opened = await OpenAsync(table, priority, cancellationToken).ConfigureAwait(false);
        }
        var entry = opened.Entry;
        if (opened.Table is not { } stored) return new(entry, TableRowOutcome.Unknown, null, null);
        try
        {
            if (await stored.FindWithOrdinalAsync(id, cancellationToken).ConfigureAwait(false) is { } found)
                return new(entry, TableRowOutcome.Found, new TableRow(found.Ordinal, found.Row), null);
        }
        catch (GeneratedStoreException store) { return new(Damaged(entry, store), TableRowOutcome.Unknown, null, null); }
        if (entry.State == TableState.Ready) return new(entry, TableRowOutcome.Absent, null, null);
        // The generated store keeps no encrypted-ID lists: the file's own index says whether the ID is protected, read once
        // per table identity and then kept.
        string? key;
        if (protectedIds.Get(stored.Identity) is { } known) key = known.GetValueOrDefault(id);
        else
        {
            using var slot = await scheduler.EnterAsync(priority, cancellationToken).ConfigureAwait(false);
            key = await ProtectedKeyAsync(stored.Identity, entry.FileDataId, opened.Resolution!.Definition!.Document, id, cancellationToken).ConfigureAwait(false);
        }
        return key is null ? new(entry, TableRowOutcome.Unknown, null, null) : new(entry, TableRowOutcome.Protected, null, key);
    }

    /// <summary>
    /// The key of a protected ID from the DB2 file's own index, null when it isn't protected or the file doesn't open now. The
    /// file's protected IDs are kept under the stored table's identity unless there are too many.
    /// </summary>
    private async Task<string?> ProtectedKeyAsync(string identity, uint fileDataId, DbdDocument document, uint id, CancellationToken cancellationToken)
    {
        await using var open = await Db2Reader.OpenAsync(files, FileRef.Id(fileDataId), document, version, limits, cancellationToken).ConfigureAwait(false);
        if (open.Table is not { } table) return null;
        if (table.ProtectedIds(ProtectedIds.MaxIds) is { } ids)
        {
            protectedIds.Put(identity, ids);
            return ids.GetValueOrDefault(id);
        }
        try { return await table.FindAsync(id, cancellationToken).ConfigureAwait(false) is Db2Lookup.Protected hit ? hit.Key : null; }
        catch (Exception e) when (e is Db2FormatException or Db2SourceException) { return null; }
    }

    // FollowAsync and ReferencedByAsync

    /// <summary>
    /// The shared rules over this handle's reads. A stored table found damaged mid-scan is gone from the store (the next call
    /// decodes it again); the call then answers with what it can: an Unknown follow, or a referenced-by that only lists the
    /// damaged table as unresolved, both with the source row looked up again.
    /// </summary>
    public async Task<TableFollowResult> FollowAsync(TableFollowRequest request, TablePriority priority, CancellationToken cancellationToken)
    {
        TableQueries.Validate(request);
        Enter(priority, cancellationToken);
        var relations = new Relations(this, priority);
        try { return await TableQueries.FollowAsync(relations, request, maxScanRows, cancellationToken).ConfigureAwait(false); }
        catch (DamagedException damaged)
        {
            // Only a scan finds damage, so the column's foreign key was known: keep it.
            var row = await GetCoreAsync(request.Table, request.Id, priority, cancellationToken).ConfigureAwait(false);
            var from = await relations.OpenAsync(request.Table, cancellationToken).ConfigureAwait(false);
            return new(TableFollowOutcome.Unknown, row, TableQueries.DeclaredReference(from.Schema, request.Column), damaged.Entry, [], null,
                damaged.Entry.Reason?.Code, damaged.Entry.Reason?.Message);
        }
        finally { await ExitAsync().ConfigureAwait(false); }
    }

    public async Task<TableReferencesResult> ReferencedByAsync(TableReferencesRequest request, TablePriority priority, CancellationToken cancellationToken)
    {
        TableQueries.Validate(request);
        Enter(priority, cancellationToken);
        try
        {
            return await TableQueries.ReferencedByAsync(new Relations(this, priority), request, maxScanRows, maxReferenceSources, maxReferenceScanRows, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DamagedException damaged)
        {
            var row = await GetCoreAsync(request.Table, request.Id, priority, cancellationToken).ConfigureAwait(false);
            return new(TableReferencesOutcome.Searched, row, [], [damaged.Entry]);
        }
        finally { await ExitAsync().ConfigureAwait(false); }
    }

    /// <summary>Carries a stored table found damaged while a relation scanned it out of the shared rules.</summary>
    private sealed class DamagedException(TableEntry entry) : Exception(entry.Reason?.Message)
    {
        public TableEntry Entry { get; } = entry;
    }

    /// <summary>The shared relation rules' view of this handle at one call's priority.</summary>
    private sealed class Relations(BuildTables owner, TablePriority priority) : ITableRelationSource
    {
        public Task<RelationTable> OpenAsync(TableRef table, CancellationToken cancellationToken) => owner.RelationOpenAsync(table, priority, cancellationToken);
        public Task<TableRowResult> GetAsync(TableRef table, uint id, CancellationToken cancellationToken) => owner.GetCoreAsync(table, id, priority, cancellationToken);
        public IAsyncEnumerable<TableEntry> ListAsync(CancellationToken cancellationToken) => owner.ListCoreAsync(TableListFilter.All, cancellationToken);
        public Task<RelationSchema> SchemaAsync(TableEntry table, CancellationToken cancellationToken) => owner.RelationSchemaAsync(table, cancellationToken);
    }

    // A check, as CheckAsync makes it: it may fetch and build the table. Its scans read each chunk in a slot.
    private async Task<RelationTable> RelationOpenAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(table, priority, cancellationToken).ConfigureAwait(false);
        if (opened.Table is not { } stored)
            return new(opened.Entry, null, opened.Resolution is { State: DefinitionState.Resolved } resolved ? resolved.Schema : null, 0, null);
        var schema = await SchemaOfAsync(opened.Definition!, stored, cancellationToken).ConfigureAwait(false);
        if (!TableQueries.IsReadable(opened.Entry.State)) return new(opened.Entry, stored.Header.Columns, schema, 0, null);
        var entry = opened.Entry;
        TableRowReader read = async (first, count, token) =>
        {
            try { return await ReadRowsAsync(stored, first, count, token).ConfigureAwait(false); }
            catch (GeneratedStoreException store) { throw new DamagedException(Damaged(entry, store)); }
        };
        return new(entry, stored.Header.Columns, schema, stored.RowCount, Scheduled(read, priority));
    }

    // PreloadAsync

    /// <summary>
    /// Preparing checks this many tables at a time: the default Background lane, so the preload holds at most this many places
    /// in the scheduler's Background queue and never fills it.
    /// </summary>
    internal const int PreloadChecks = 2;
    internal static readonly TimeSpan PreloadInterval = TimeSpan.FromMilliseconds(100);
    /// <summary>The first and the longest wait before a check the full Background queue refused (TABLES_BUSY) is asked again.</summary>
    private static readonly TimeSpan BusyFirst = TimeSpan.FromMilliseconds(50), BusyMost = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The preload (D360). Holds the handle's files open to its end like any call. A dispose of the handle while it runs ends
    /// it as Canceled at the next table. A layer 2 failure the ensure throws is Failed with layer 2's code.
    /// </summary>
    public async Task<TablePreloadResult> PreloadAsync(IProgress<TablePreloadProgress>? progress, CancellationToken cancellationToken)
    {
        // Canceled before it starts is a Canceled result, not a throw: the token is looked at inside.
        Enter(TablePriority.Background, CancellationToken.None);
        var tally = new PreloadTally(progress, TimeProvider.System, PreloadInterval);
        try { return await PreloadCoreAsync(tally, cancellationToken).ConfigureAwait(false); }
        finally { await ExitAsync().ConfigureAwait(false); }
    }

    private async Task<TablePreloadResult> PreloadCoreAsync(PreloadTally tally, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Every listed table whose file the build holds in this locale; the targets are the ones a definition may read and
            // the generated store doesn't hold yet. Both are bounded by the build's table count.
            var selected = new List<FileRef>();
            var targets = new List<TableEntry>();
            var already = 0;
            await foreach (var entry in ListCoreAsync(TableListFilter.All, cancellationToken).ConfigureAwait(false))
            {
                if (entry.State is TableState.NotInBuild or TableState.NotInLocale or TableState.NotInEncoding) continue;
                selected.Add(FileRef.Id(entry.FileDataId));
                if (entry.Checked)
                {
                    already++;
                    if (!PreloadTally.IsPrepared(entry.State)) tally.Table(entry, countDone: false);
                }
                else if (entry.State is TableState.NotChecked or TableState.NotStored or TableState.StoreFailed) targets.Add(entry);
            }
            tally.Tables(already + targets.Count, already);

            EnsureResult ensured;
            try
            {
                ensured = await files.EnsureAsync(EnsureSelection.Many(selected), EnsurePriority.Background, tally, cancellationToken).ConfigureAwait(false);
            }
            catch (ObjectStoreException failure) { return await tally.FinishAsync(TablePreloadOutcome.Failed, null, failure.Code, failure.Message).ConfigureAwait(false); }
            var failedFiles = ensured.Files.Count(file => file.State is not (FileEnsureState.Stored or FileEnsureState.AlreadyStored or FileEnsureState.Canceled));
            tally.Downloaded(ensured.Final, failedFiles);
            if (ensured.Outcome == EnsureOutcome.Canceled) return await tally.FinishAsync(TablePreloadOutcome.Canceled).ConfigureAwait(false);
            var stored = new Dictionary<uint, FileEnsured>(ensured.Files.Count);
            foreach (var file in ensured.Files) if (file.FileDataId is { } id) stored[id] = file;
            selected.Clear();

            // Downloads end before preparation starts (D360).
            tally.Preparing();
            await PrepareAsync(targets, stored, tally, cancellationToken).ConfigureAwait(false);
            return await tally.FinishAsync(null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || Volatile.Read(ref disposed) != 0)
        {
            return await tally.FinishAsync(TablePreloadOutcome.Canceled).ConfigureAwait(false);
        }
        catch (Exception unexpected)
        {
            return await tally.FinishAsync(TablePreloadOutcome.Failed, null, "TABLE_PRELOAD_FAILED", unexpected.Message).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Checks the targets <see cref="PreloadChecks"/> at a time in Background slots. A table whose file didn't store isn't opened (it
    /// would fetch outside the preload's Background ensure): it ends in the state its ensure gave, as a check would answer it.
    /// The first worker that throws stops the others.
    /// </summary>
    private async Task PrepareAsync(List<TableEntry> targets, Dictionary<uint, FileEnsured> stored, PreloadTally tally, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var next = -1;
        var workers = new Task[Math.Min(PreloadChecks, targets.Count)];
        for (var i = 0; i < workers.Length; i++) workers[i] = WorkAsync();
        // A worker's failure faults the whole wait (a fault outranks the others' cancellation), so it is what comes out.
        await Task.WhenAll(workers).ConfigureAwait(false);

        async Task WorkAsync()
        {
            await Task.Yield();
            try
            {
                while (Interlocked.Increment(ref next) is var at && at < targets.Count)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    if (Volatile.Read(ref disposed) != 0) throw new OperationCanceledException("The handle was disposed.");
                    var target = targets[at];
                    tally.Table(stored.TryGetValue(target.FileDataId, out var file) && file.State is FileEnsureState.Stored or FileEnsureState.AlreadyStored
                        ? await PreloadCheckAsync(target, stop.Token).ConfigureAwait(false)
                        : NotFetched(target, file));
                }
            }
            catch
            {
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>A Background check; a full Background queue is asked again after a growing wait instead of failing the preload.</summary>
    private async Task<TableEntry> PreloadCheckAsync(TableEntry target, CancellationToken cancellationToken)
    {
        var wait = BusyFirst;
        while (true)
        {
            try { return (await OpenAsync(TableRef.Id(target.FileDataId), TablePriority.Background, cancellationToken).ConfigureAwait(false)).Entry; }
            catch (TablesBusyException) { }
            catch (ObjectStoreException failure) { return target with { State = TableState.StoreFailed, Checked = true, Reason = StoreFailure(failure), Counts = null }; }
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, BusyMost.Ticks));
        }
    }

    /// <summary>The table whose file the ensure didn't store, in the state a check would give it.</summary>
    private static TableEntry NotFetched(TableEntry target, FileEnsured file)
    {
        var state = file.State switch
        {
            FileEnsureState.NotInLocale => TableState.NotInLocale,
            FileEnsureState.NotInEncoding => TableState.NotInEncoding,
            FileEnsureState.Missing => TableState.Missing,
            FileEnsureState.NeedsNetwork => TableState.NeedsNetwork,
            FileEnsureState.Unavailable => TableState.Unavailable,
            FileEnsureState.Refused => TableState.Corrupt,
            FileEnsureState.Unsupported => TableState.Unsupported,
            FileEnsureState.StoreFailed => TableState.StoreFailed,
            _ => TableState.NotInBuild,
        };
        return target with
        {
            State = state, Checked = true, Counts = null,
            Reason = new(file.Code, $"The DB2 file didn't store ({file.State}).", null, null, null, []),
        };
    }

    /// <summary>
    /// From the definitions alone, no byte read: a checked table by its stored layout, else the build's block from a local
    /// lookup. A table that only its layout could resolve (not found locally yet), or whose definition is unusable, is
    /// Unresolved; one the manifest doesn't name, or no definition covers, is NoDefinition.
    /// </summary>
    private async Task<RelationSchema> RelationSchemaAsync(TableEntry table, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!manifest.ById.TryGetValue(table.FileDataId, out var definition) || table.State == TableState.NoDefinition) return new(RelationSchemaState.NoDefinition, null);
        if (table.State == TableState.DefinitionUnusable) return new(RelationSchemaState.Unresolved, null);
        var layout = table.Checked ? table.Counts?.LayoutHash : null;
        var resolution = await definitions.ResolveAsync(definition.TableId, version, layout, locale, DefinitionLookup.Local, cancellationToken).ConfigureAwait(false);
        if (resolution is { State: DefinitionState.Resolved, Schema: { } schema }) return new(RelationSchemaState.Resolved, schema);
        return resolution.State is DefinitionState.NotInProvider ? new(RelationSchemaState.NoDefinition, null) : new(RelationSchemaState.Unresolved, null);
    }
}
