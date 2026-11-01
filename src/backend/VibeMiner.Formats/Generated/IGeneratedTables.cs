namespace VibeMiner.Formats.Generated;

/// <summary>
/// Decoded DB2 tables in the generated store (#179), filed by <see cref="GeneratedTableSource"/> and the status of the TACT keys the
/// table depends on: a newly known key misses only the tables that use it. Calls may run concurrently.
/// </summary>
public interface IGeneratedTables
{
    /// <summary>The stored table, or null when there is none for this identity and key status. Damaged pieces are deleted and read as null.</summary>
    Task<GeneratedTable?> OpenAsync(GeneratedTableSource source, ITactKeyStatus keys, CancellationToken cancellationToken = default);

    /// <summary>Stores a decoded table (or returns the one already stored) and opens it. Store failures throw <c>GeneratedStoreException</c>.
    /// The content's <see cref="GeneratedTableContent.Owner"/> is disposed when this ends, however it ends.</summary>
    Task<GeneratedTable> StoreAsync(GeneratedTableSource source, ITactKeyStatus keys, GeneratedTableContent content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the stored table, or runs <paramref name="build"/> and stores its result. One build runs per identity at a time; callers
    /// that arrive meanwhile wait for it. A canceled caller stops waiting; the build is canceled only once every waiter has left.
    /// </summary>
    Task<GeneratedTable> GetOrBuildAsync(GeneratedTableSource source, ITactKeyStatus keys, Func<CancellationToken, Task<GeneratedTableContent>> build,
        CancellationToken cancellationToken = default);

    /// <summary>The <see cref="GeneratedTable.Identity"/> a table with these keys used is filed under for this key status, without
    /// touching the store, so a page served before the table is built carries the identity it will have.</summary>
    string IdentityOf(GeneratedTableSource source, ITactKeyStatus keys, IReadOnlyCollection<string> keysUsed);
}
