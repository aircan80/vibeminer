using System.Runtime.CompilerServices;
using VibeMiner.Generated;
using VibeMiner.Generated.RecordFiles;

namespace VibeMiner.Formats.Generated;

/// <summary>
/// A stored table, read back from its record file. Rows keep the reader's order (physical rows, then copies). A read that finds
/// damage deletes the table's node and manifest and the damaged pieces, then throws <see cref="GeneratedStoreException"/> (Corrupt):
/// the next open misses and the caller rebuilds. Thread-safe.
/// </summary>
public sealed class GeneratedTable
{
    /// <summary>The most rows one <see cref="ReadPageAsync"/> returns.</summary>
    public const int MaxPageRows = RecordFile.MaxPageRecords;

    private readonly RecordFile file;
    private readonly Func<CancellationToken, Task> discard;

    internal GeneratedTable(GeneratedTableSource source, GeneratedKey nodeKey, TableMetadata metadata, RecordFile file, Func<CancellationToken, Task> discard)
    {
        Source = source; NodeKey = nodeKey; Scoped = metadata.Identity.EncodingIn is not null; Header = metadata.Header; Sections = metadata.Sections; Completion = metadata.Completion; KeysUsed = metadata.KeysUsed; HoleKeys = metadata.HoleKeys;
        this.file = file; this.discard = discard;
    }

    public GeneratedTableSource Source { get; }
    public Db2TableHeader Header { get; }
    public IReadOnlyList<Db2Section> Sections { get; }
    public Db2Completion Completion { get; }
    /// <summary>The key names the table depends on, sorted.</summary>
    public IReadOnlyList<string> KeysUsed { get; }
    public long RowCount => file.Count;
    /// <summary>The keys of the layer 4 holes the decode met, sorted; each is among <see cref="KeysUsed"/>.</summary>
    public IReadOnlyList<string> HoleKeys { get; }
    /// <summary>The decode read through encrypted chunks, so the table is filed under its source's encoding scope (D350).</summary>
    internal bool Scoped { get; }
    /// <summary>The node the table is filed under: N(KeysUsed) for the key status it was stored or opened with.</summary>
    internal GeneratedKey NodeKey { get; }

    /// <summary>Up to <paramref name="count"/> rows from row <paramref name="firstRow"/>; fewer at the end.</summary>
    public async Task<IReadOnlyList<Db2Row>> ReadPageAsync(long firstRow, int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(firstRow);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(firstRow, RowCount);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxPageRows);
        try
        {
            var records = await file.ReadAsync(firstRow, count, cancellationToken).ConfigureAwait(false);
            var rows = new Db2Row[records.Count];
            for (var i = 0; i < rows.Length; i++) rows[i] = RowCodec.Decode(records[i].Id, records[i].Bytes.Span);
            return rows;
        }
        catch (GeneratedStoreException e) when (e.Code == GeneratedStoreCodes.Corrupt) { await DiscardAsync(cancellationToken).ConfigureAwait(false); throw; }
    }

    /// <summary>The row with this id, or null when the table has none.</summary>
    public async Task<Db2Row?> FindAsync(uint id, CancellationToken cancellationToken = default)
    {
        try
        {
            var record = await file.FindAsync(id, cancellationToken).ConfigureAwait(false);
            return record is { } r ? RowCodec.Decode(r.Id, r.Bytes.Span) : null;
        }
        catch (GeneratedStoreException e) when (e.Code == GeneratedStoreCodes.Corrupt) { await DiscardAsync(cancellationToken).ConfigureAwait(false); throw; }
    }

    /// <summary>The row with this id and its 0-based position in the table's order, or null when the table has none.</summary>
    public async Task<(long Ordinal, Db2Row Row)?> FindWithOrdinalAsync(uint id, CancellationToken cancellationToken = default)
    {
        try
        {
            var record = await file.FindAsync(id, cancellationToken).ConfigureAwait(false);
            return record is { } r ? (r.Ordinal, RowCodec.Decode(r.Id, r.Bytes.Span)) : null;
        }
        catch (GeneratedStoreException e) when (e.Code == GeneratedStoreCodes.Corrupt) { await DiscardAsync(cancellationToken).ConfigureAwait(false); throw; }
    }

    /// <summary>The key the table is filed under, as 32 lower hex: it changes with the source identity and the key status.</summary>
    public string Identity => NodeKey.Hex;

    /// <summary>Every row in order, one block at a time.</summary>
    public async IAsyncEnumerable<Db2Row> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var records = file.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                Db2Row row;
                try
                {
                    if (!await records.MoveNextAsync().ConfigureAwait(false)) yield break;
                    row = RowCodec.Decode(records.Current.Id, records.Current.Bytes.Span);
                }
                catch (GeneratedStoreException e) when (e.Code == GeneratedStoreCodes.Corrupt) { await DiscardAsync(cancellationToken).ConfigureAwait(false); throw; }
                yield return row;
            }
        }
        finally { await records.DisposeAsync().ConfigureAwait(false); }
    }

    private async Task DiscardAsync(CancellationToken cancellationToken)
    {
        // Best effort: the damage is reported either way.
        try { await discard(cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (e is GeneratedStoreException or OperationCanceledException) { }
    }
}
