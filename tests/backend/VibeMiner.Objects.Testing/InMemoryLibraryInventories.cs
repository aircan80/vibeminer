using VibeMiner.Objects;

namespace VibeMiner.Objects.Testing;

/// <summary>In-memory expected inventories for upper-layer tests. Shared contracts run against this and the durable library.</summary>
public sealed class InMemoryLibraryInventories : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<LibraryClaim, Identity> identities = [];
    private readonly Dictionary<long, Generation> generations = [];
    private readonly Dictionary<ObjectKey, long> indexedObjects = [];
    private long next;
    private bool closed;

    private sealed class Identity
    {
        public readonly string Binding = Guid.NewGuid().ToString("N");
        public long Latest;
        public long? Active;
    }

    private sealed class Generation(Identity identity)
    {
        public readonly Identity Identity = identity;
        public readonly SortedSet<ObjectKey> Keys = [];
        public int State;
    }

    public ILibraryInventory ForOwner(string owner, LibraryOwnerScope scope, string producer, LibraryObjectRole role)
    {
        var claim = new LibraryClaim(owner, scope, producer, role);
        lock (gate)
        {
            EnsureOpen();
            if (!identities.TryGetValue(claim, out var identity)) identities.Add(claim, identity = new());
            return new View(this, identity);
        }
    }

    public ILibraryInventoryUnion ForInventoryOwner(string owner, LibraryOwnerScope scope, LibraryObjectRole role)
    {
        _ = new LibraryClaim(owner, scope, "union", role);
        lock (gate) { EnsureOpen(); return new UnionView(this, owner, scope, role); }
    }

    private sealed class UnionView(InMemoryLibraryInventories store, string owner, LibraryOwnerScope scope, LibraryObjectRole role) : ILibraryInventoryUnion
    {
        public ValueTask<LibraryInventoryUnionMeasurement> MeasureAsync(CancellationToken ct = default)
        {
            lock (store.gate)
            {
                store.EnsureOpen(); ct.ThrowIfCancellationRequested();
                try
                {
                    long published = 0, unpublished = 0, expected = 0, present = 0, bytes = 0;
                    var activeSets = new List<SortedSet<ObjectKey>>(LibraryInventoryUnionLimits.MaxPublishedProducers);
                    foreach (var (claim, identity) in store.identities)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (claim.Owner != owner || claim.Scope != scope || claim.Role != role || identity.Latest == 0) continue;
                        if (identity.Active is not { } active) { unpublished = checked(unpublished + 1); continue; }
                        published = checked(published + 1);
                        if (published > LibraryInventoryUnionLimits.MaxPublishedProducers)
                            throw new ObjectStoreException(ObjectStoreCodes.Limit, "Too many published producers for an inventory union.");
                        activeSets.Add(store.generations[active].Keys);
                    }
                    if (published == 0) return ValueTask.FromResult(new LibraryInventoryUnionMeasurement(0, unpublished, null, null, null, null));
                    var cursors = activeSets.Select(set => ((IEnumerable<ObjectKey>)set).GetEnumerator()).ToArray();
                    try
                    {
                        var heads = new PriorityQueue<int, ObjectKey>();
                        for (var i = 0; i < cursors.Length; i++) if (cursors[i].MoveNext()) heads.Enqueue(i, cursors[i].Current);
                        ObjectKey? previous = null;
                        while (heads.TryDequeue(out var cursor, out var key))
                        {
                            ct.ThrowIfCancellationRequested();
                            if (previous is null || previous.Value != key)
                            {
                                expected = checked(expected + 1);
                                if (store.indexedObjects.TryGetValue(key, out var length))
                                {
                                    present = checked(present + 1); bytes = checked(bytes + length);
                                }
                                previous = key;
                            }
                            if (cursors[cursor].MoveNext()) heads.Enqueue(cursor, cursors[cursor].Current);
                        }
                    }
                    finally { foreach (var cursor in cursors) cursor.Dispose(); }
                    ct.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(new LibraryInventoryUnionMeasurement(published, unpublished, expected, present, checked(expected - present), bytes));
                }
                catch (OverflowException e) { throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "Indexed union measurement overflowed.", e); }
            }
        }
    }

    /// <summary>Tests only: records committed index presence/length without acquiring or storing payload bytes.</summary>
    public void SetIndexedObject(ObjectKey key, long length)
    {
        lock (gate)
        {
            EnsureOpen(); ValidateKey(key);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
            indexedObjects[key] = length;
        }
    }

    /// <summary>Tests only: removes committed index presence without changing any expected inventory.</summary>
    public void RemoveIndexedObject(ObjectKey key)
    {
        lock (gate) { EnsureOpen(); ValidateKey(key); indexedObjects.Remove(key); }
    }

    public ValueTask DisposeAsync()
    {
        // Every accepted fake operation completes under this same gate; acquiring it drains them.
        lock (gate) closed = true;
        return ValueTask.CompletedTask;
    }

    private void EnsureOpen()
    {
        if (closed) throw new ObjectStoreException(ObjectStoreCodes.Closed, "The in-memory library is closed.");
    }

    private static void ValidateKey(ObjectKey key)
    {
        if (key.Namespace != 0 || !ObjectFormats.Known(ObjectStoreKind.Library, key.Type))
            throw new ArgumentException("An inventory key must be a supported library key in namespace zero.", nameof(key));
    }

    private sealed class View(InMemoryLibraryInventories store, Identity identity) : ILibraryInventory
    {
        private Generation Match(LibraryInventoryGeneration token, bool staging = true)
        {
            ArgumentNullException.ThrowIfNull(token);
            if (token.Binding != identity.Binding || !store.generations.TryGetValue(token.Value, out var generation) ||
                generation.Identity != identity || (staging && (generation.State != 0 || identity.Latest != token.Value)))
                throw new ObjectStoreException(ObjectStoreCodes.InventoryStale, "The inventory generation is stale or belongs to another identity.");
            return generation;
        }

        private void Poison(LibraryInventoryGeneration token)
        {
            if (token is not null && token.Binding == identity.Binding && store.generations.TryGetValue(token.Value, out var generation) &&
                generation.Identity == identity && generation.State == 0) generation.State = 2;
        }

        public Task<LibraryInventoryGeneration> BeginAsync(CancellationToken ct = default)
        {
            lock (store.gate)
            {
                store.EnsureOpen(); ct.ThrowIfCancellationRequested();
                var id = checked(++store.next);
                identity.Latest = id;
                store.generations.Add(id, new(identity));
                return Task.FromResult(new LibraryInventoryGeneration(id) { Binding = identity.Binding });
            }
        }

        public Task AppendAsync(LibraryInventoryGeneration generation, IReadOnlyList<ObjectKey> keys, CancellationToken ct = default)
        {
            lock (store.gate)
            {
                store.EnsureOpen();
                try
                {
                    var staged = Match(generation);
                    ct.ThrowIfCancellationRequested();
                    ArgumentNullException.ThrowIfNull(keys);
                    if (keys.Count > 1024) throw new ObjectStoreException(ObjectStoreCodes.Limit, "An inventory batch is at most 1024 keys.");
                    foreach (var key in keys) ValidateKey(key);
                    ct.ThrowIfCancellationRequested();
                    foreach (var key in keys) staged.Keys.Add(key);
                    return Task.CompletedTask;
                }
                catch { Poison(generation); throw; }
            }
        }

        public Task PublishAsync(LibraryInventoryGeneration generation, CancellationToken ct = default)
        {
            lock (store.gate)
            {
                store.EnsureOpen();
                try
                {
                    var staged = Match(generation);
                    ct.ThrowIfCancellationRequested();
                    staged.State = 1;
                    identity.Active = generation.Value;
                    return Task.CompletedTask;
                }
                catch { Poison(generation); throw; }
            }
        }

        public Task AbandonAsync(LibraryInventoryGeneration generation, CancellationToken ct = default)
        {
            lock (store.gate)
            {
                store.EnsureOpen(); ct.ThrowIfCancellationRequested();
                var staged = Match(generation, staging: false);
                if (staged.State == 1) throw new ObjectStoreException(ObjectStoreCodes.InventoryStale, "A published inventory cannot be abandoned.");
                staged.State = 2;
                return Task.CompletedTask;
            }
        }

        public ValueTask<LibraryInventoryLookup> LookupAsync(ObjectKey key, CancellationToken ct = default)
        {
            lock (store.gate)
            {
                store.EnsureOpen(); ct.ThrowIfCancellationRequested(); ValidateKey(key);
                return ValueTask.FromResult(new LibraryInventoryLookup(identity.Active,
                    identity.Active is { } id && store.generations[id].Keys.Contains(key)));
            }
        }

        public ValueTask<LibraryInventoryMeasurement> MeasureAsync(CancellationToken ct = default)
        {
            lock (store.gate)
            {
                store.EnsureOpen(); ct.ThrowIfCancellationRequested();
                if (identity.Active is not { } active) return ValueTask.FromResult(new LibraryInventoryMeasurement(null, null, null, null, null));
                try
                {
                    long expected = 0, present = 0, bytes = 0;
                    foreach (var key in store.generations[active].Keys)
                    {
                        ct.ThrowIfCancellationRequested();
                        expected = checked(expected + 1);
                        if (!store.indexedObjects.TryGetValue(key, out var length)) continue;
                        present = checked(present + 1);
                        bytes = checked(bytes + length);
                    }
                    ct.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(new LibraryInventoryMeasurement(active, expected, present, checked(expected - present), bytes));
                }
                catch (OverflowException e)
                {
                    throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "Indexed inventory measurement overflowed.", e);
                }
            }
        }

        public ValueTask<LibraryInventoryPage> ReadPageAsync(int limit = 1024, LibraryInventoryCursor? after = null, CancellationToken ct = default)
        {
            lock (store.gate)
            {
                store.EnsureOpen(); ct.ThrowIfCancellationRequested();
                if (limit is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(limit));
                if (after is not null && (after.Binding != identity.Binding || after.Generation != identity.Active))
                    throw new ObjectStoreException(ObjectStoreCodes.InventoryCursorStale, "The inventory cursor is stale or belongs to another identity.");
                if (after is not null) ValidateKey(after.After);
                if (identity.Active is not { } active) return ValueTask.FromResult(new LibraryInventoryPage(null, [], null));
                var keys = store.generations[active].Keys;
                if (after is not null && keys.Count > 0 && after.After.CompareTo(keys.Max) >= 0)
                    return ValueTask.FromResult(new LibraryInventoryPage(active, [], null));
                var range = after is null || keys.Count == 0 ? keys : keys.GetViewBetween(after.After, keys.Max);
                var page = range.Where(key => after is null || key.CompareTo(after.After) > 0).Take(limit + 1).ToArray();
                var more = page.Length > limit;
                if (more) Array.Resize(ref page, limit);
                return ValueTask.FromResult(new LibraryInventoryPage(active, page,
                    more ? new LibraryInventoryCursor(active, page[^1]) { Binding = identity.Binding } : null));
            }
        }
    }
}
