using VibeMiner.Builds;
using VibeMiner.Files;

namespace VibeMiner.Formats.Tables;

/// <summary>
/// Layer 5: a build's client tables (DB2 files read with their WoWDBDefs definitions) per build and locale. Implementations:
/// the service over layer 4, the definitions and the generated store, and the in-memory fake in <c>VibeMiner.Formats.Testing</c>;
/// one contract suite runs against both. File, definition and reader problems never throw: they come back as
/// <see cref="TableState"/>s. Calls throw only for bad arguments (<see cref="ArgumentException"/>), cancellation, and a disposed
/// handle (<see cref="ObjectDisposedException"/>), and when a priority lane's queue is full (<see cref="TablesBusyException"/>, D263).
/// </summary>
public interface ITables
{
    /// <summary>
    /// Opens the build's files for one locale (a <see cref="FileLocales"/> name; an unregistered one throws
    /// <see cref="ArgumentException"/>). Opening reads no table.
    /// </summary>
    Task<TablesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken);
}

/// <summary>
/// The outcome of <see cref="ITables.ForAsync"/>. <see cref="Tables"/> is set only when <see cref="State"/> is Open; a failure
/// keeps layer 4's state, code and time.
/// </summary>
public sealed record TablesOpenResult(BuildRef Build, string Locale, FilesOpenState State, IBuildTables? Tables, string? Code, string? Message,
    DateTimeOffset? ObservedAt);

/// <summary>Who waits for a call. Interactive work never waits behind queued background work (<see cref="TableWorkScheduler"/>).</summary>
public enum TablePriority { Interactive, Background }

/// <summary>
/// One build and locale's tables. Calls may run concurrently. Dispose the handle when done: every later call throws
/// <see cref="ObjectDisposedException"/>; a list already being read runs on to its end. A table is keyed by its FileDataID;
/// its definition name is shown next to it. Rows are opened through the generated store, so a second open of the same table
/// decodes nothing. Until a table is in the store, an Interactive unfiltered page, row by ID or schema may be answered from a
/// direct read of the file while the build runs in the background (D330); the answer, identity included, is the store's.
/// </summary>
public interface IBuildTables : IAsyncDisposable
{
    BuildRef Build { get; }
    string Locale { get; }

    /// <summary>
    /// The tables that match <paramref name="filter"/>, ascending by FileDataID, in their shallow state: from a stat and a local
    /// definition lookup only, never the bytes. A table already in the generated store lists in its checked state. The list is
    /// the definition manifest's tables for this build plus the DB2 files found that the manifest doesn't name (NoDefinition,
    /// Name null); it never claims to cover every DB2 in the build.
    /// </summary>
    IAsyncEnumerable<TableEntry> ListAsync(TableListFilter filter, TablePriority priority, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the table: may fetch its bytes and builds its generated rows. The entry is the checked state, with counts once opened.
    /// A FileDataID or name that isn't a known table answers an entry too (NotInBuild, or NoDefinition with FileDataID 0).
    /// </summary>
    Task<TableEntry> CheckAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken);

    /// <summary>Checks the table, then gives its header columns (once opened) and its resolved schema (when a definition resolves).</summary>
    Task<TableSchemaResult> SchemaAsync(TableRef table, TablePriority priority, CancellationToken cancellationToken);

    /// <summary>One page of rows, after checking the table. See <see cref="TablePageRequest"/> and <see cref="TablePage"/>.</summary>
    Task<TablePage> PageAsync(TablePageRequest request, TablePriority priority, CancellationToken cancellationToken);

    /// <summary>The row with this ID, after checking the table. See <see cref="TableRowOutcome"/>.</summary>
    Task<TableRowResult> GetAsync(TableRef table, uint id, TablePriority priority, CancellationToken cancellationToken);

    /// <summary>The rows a row's foreign key column points at. See <see cref="TableFollowRequest"/>.</summary>
    Task<TableFollowResult> FollowAsync(TableFollowRequest request, TablePriority priority, CancellationToken cancellationToken);

    /// <summary>The rows of other tables whose declared foreign keys point at this row. See <see cref="TableReferencesRequest"/>.</summary>
    Task<TableReferencesResult> ReferencedByAsync(TableReferencesRequest request, TablePriority priority, CancellationToken cancellationToken);
}
