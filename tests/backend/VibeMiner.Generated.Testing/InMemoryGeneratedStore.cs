namespace VibeMiner.Generated.Testing;

/// <summary>
/// The standard <see cref="IGeneratedStore"/> test double for layers 4-6: objects in memory, scripted damage, and counts of what was
/// asked. The shared contract tests run it beside the real store.
/// </summary>
public sealed class InMemoryGeneratedStore : IGeneratedStore
{
    private readonly object gate = new();
    private readonly Dictionary<GeneratedKey, byte[]> objects = [];
    private readonly HashSet<GeneratedKey> corrupt = [];
    private int reads, rangeReads, puts, stored;

    /// <summary>Whole reads made.</summary>
    public int Reads => Volatile.Read(ref reads);
    /// <summary>Range reads made.</summary>
    public int RangeReads => Volatile.Read(ref rangeReads);
    /// <summary>Put calls made.</summary>
    public int Puts => Volatile.Read(ref puts);
    /// <summary>Objects a Put stored (AlreadyPresent ones not counted).</summary>
    public int Stored => Volatile.Read(ref stored);

    public IReadOnlyCollection<GeneratedKey> Keys { get { lock (gate) return objects.Keys.ToArray(); } }
    public int Count { get { lock (gate) return objects.Count; } }

    /// <summary>
    /// The next reads of <paramref name="key"/> answer Corrupt until it is deleted, as damaged payload bytes in a pack do: a range past
    /// the object's end still throws first, since the object's length is still known.
    /// </summary>
    public InMemoryGeneratedStore Damage(GeneratedKey key) { lock (gate) corrupt.Add(key); return this; }

    /// <summary>Drops everything, as deleting the store's folder would.</summary>
    public void Clear() { lock (gate) { objects.Clear(); corrupt.Clear(); } }

    public ValueTask<bool> HasAsync(GeneratedKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return ValueTask.FromResult(objects.ContainsKey(key));
    }

    public ValueTask<GeneratedRead> ReadAsync(GeneratedKey key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref reads);
        lock (gate)
        {
            if (!objects.TryGetValue(key, out var bytes)) return ValueTask.FromResult(new GeneratedRead(GeneratedReadState.Absent));
            if (corrupt.Contains(key)) return ValueTask.FromResult(new GeneratedRead(GeneratedReadState.Corrupt, Problem: "Scripted damage."));
            return ValueTask.FromResult(new GeneratedRead(GeneratedReadState.Present, bytes.Length, bytes.ToArray()));
        }
    }

    public ValueTask<GeneratedRangeRead> ReadRangeAsync(GeneratedKey key, long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (destination.Length > GeneratedLimits.MaxObjectBytes) throw new ArgumentOutOfRangeException(nameof(destination), "The range is past the object's end.");
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref rangeReads);
        lock (gate)
        {
            if (!objects.TryGetValue(key, out var bytes)) return ValueTask.FromResult(new GeneratedRangeRead(GeneratedReadState.Absent));
            if (offset + destination.Length > bytes.Length) throw new ArgumentOutOfRangeException(nameof(offset), "The range is past the object's end.");
            if (corrupt.Contains(key)) return ValueTask.FromResult(new GeneratedRangeRead(GeneratedReadState.Corrupt, Problem: "Scripted damage."));
            bytes.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.FromResult(new GeneratedRangeRead(GeneratedReadState.Present, bytes.Length));
        }
    }

    public Task<IReadOnlyList<GeneratedPutOutcome>> PutAsync(IReadOnlyList<GeneratedPut> batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        if (batch.Count > GeneratedLimits.MaxBatch) throw new GeneratedStoreException(GeneratedStoreCodes.Limit, "Too many objects in one Put.");
        foreach (var put in batch)
            if ((put ?? throw new ArgumentNullException(nameof(batch))).Bytes.Length is 0 or > GeneratedLimits.MaxObjectBytes) throw new GeneratedStoreException(GeneratedStoreCodes.Limit, "An object is empty or over 64 MiB.");
        Interlocked.Increment(ref puts);
        var outcomes = new GeneratedPutOutcome[batch.Count];
        lock (gate)
            for (var i = 0; i < batch.Count; i++)
            {
                if (objects.ContainsKey(batch[i].Key)) { outcomes[i] = GeneratedPutOutcome.AlreadyPresent; continue; }
                objects[batch[i].Key] = batch[i].Bytes.ToArray();
                stored++;
                outcomes[i] = GeneratedPutOutcome.Stored;
            }
        return Task.FromResult<IReadOnlyList<GeneratedPutOutcome>>(outcomes);
    }

    /// <summary>How many times <see cref="CompactAsync"/> was called.</summary>
    public int Compactions => Volatile.Read(ref compactions);
    private int compactions;

    /// <summary>Memory holds no dead bytes, so a compaction only counts the call.</summary>
    public Task<GeneratedCompactReport> CompactAsync(double minDeadFraction = 0.25, int maxPacks = 1, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref compactions);
        return Task.FromResult(new GeneratedCompactReport(0, 0, 0, 0, 0));
    }

    public Task DeleteAsync(IReadOnlyList<GeneratedKey> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) foreach (var key in keys) { objects.Remove(key); corrupt.Remove(key); }
        return Task.CompletedTask;
    }
}
