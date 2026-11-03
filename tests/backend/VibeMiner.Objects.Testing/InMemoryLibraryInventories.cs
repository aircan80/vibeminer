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

    public ILibraryInventoryPair ForInventoryPair(LibraryInventorySelection left, LibraryInventorySelection right)
    {
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        lock (gate) { EnsureOpen(); return new PairView(this, left, right); }
    }

    private sealed class PairView(InMemoryLibraryInventories store, LibraryInventorySelection left, LibraryInventorySelection right) : ILibraryInventoryPair
    {
        private sealed class Totals
        {
            private long expected, present, bytes;
            public void Add(long? length)
            {
                expected = checked(expected + 1);
                if (length is not { } value) return;
                present = checked(present + 1); bytes = checked(bytes + value);
            }
            public LibraryInventoryCoverage Coverage() => new(expected, present, checked(expected - present), bytes);
            public LibraryInventoryUnionMeasurement Side(long published, long unpublished) => published == 0
                ? new(0, unpublished, null, null, null, null)
                : new(published, unpublished, expected, present, checked(expected - present), bytes);
        }

        private (long Published, long Unpublished) Resolve(LibraryInventorySelection selection, int mask,
            List<(SortedSet<ObjectKey> Keys, int Mask)> streams, CancellationToken ct)
        {
            long published = 0, unpublished = 0;
            foreach (var (claim, identity) in store.identities)
            {
                ct.ThrowIfCancellationRequested();
                if (claim.Owner != selection.Owner || claim.Scope != selection.Scope || claim.Role != selection.Role || identity.Latest == 0) continue;
                if (identity.Active is not { } active) { unpublished = checked(unpublished + 1); continue; }
                published = checked(published + 1);
                if (published > LibraryInventoryUnionLimits.MaxPublishedProducers)
                    throw new ObjectStoreException(ObjectStoreCodes.Limit, "Too many published producers for an inventory pair side.");
                streams.Add((store.generations[active].Keys, mask));
            }
            return (published, unpublished);
        }

        public ValueTask<LibraryInventoryPairMeasurement> MeasureAsync(CancellationToken ct = default)
        {
            lock (store.gate)
            {
                store.EnsureOpen(); ct.ThrowIfCancellationRequested();
                try
                {
                    var streams = new List<(SortedSet<ObjectKey> Keys, int Mask)>(2 * LibraryInventoryUnionLimits.MaxPublishedProducers);
                    var l = Resolve(left, 1, streams, ct); var r = Resolve(right, 2, streams, ct);
                    var leftTotals = new Totals(); var rightTotals = new Totals(); var union = new Totals();
                    var intersection = new Totals(); var leftOnly = new Totals(); var rightOnly = new Totals();
                    var cursors = streams.Select(stream => ((IEnumerable<ObjectKey>)stream.Keys).GetEnumerator()).ToArray();
                    try
                    {
                        var heads = new PriorityQueue<int, ObjectKey>();
                        for (var i = 0; i < cursors.Length; i++) if (cursors[i].MoveNext()) heads.Enqueue(i, cursors[i].Current);
                        while (heads.TryPeek(out _, out var key))
                        {
                            var mask = 0;
                            while (heads.TryPeek(out var cursor, out var current) && current == key)
                            {
                                ct.ThrowIfCancellationRequested(); heads.Dequeue(); mask |= streams[cursor].Mask;
                                if (cursors[cursor].MoveNext()) heads.Enqueue(cursor, cursors[cursor].Current);
                            }
                            long? length = store.indexedObjects.TryGetValue(key, out var indexed) ? indexed : null;
                            if ((mask & 1) != 0) leftTotals.Add(length);
                            if ((mask & 2) != 0) rightTotals.Add(length);
                            union.Add(length);
                            if (mask == 3) intersection.Add(length);
                            else if (mask == 1) leftOnly.Add(length);
                            else rightOnly.Add(length);
                        }
                    }
                    finally { foreach (var cursor in cursors) cursor.Dispose(); }
                    ct.ThrowIfCancellationRequested();
                    var known = l.Published > 0 && r.Published > 0;
                    return ValueTask.FromResult(new LibraryInventoryPairMeasurement(leftTotals.Side(l.Published, l.Unpublished), rightTotals.Side(r.Published, r.Unpublished),
                        known ? union.Coverage() : null, known ? intersection.Coverage() : null, known ? leftOnly.Coverage() : null, known ? rightOnly.Coverage() : null));
                }
                catch (OverflowException e) { throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "Indexed pair measurement overflowed.", e); }
            }
        }
    }

    public ILibraryInventoryAccounting ForInventoryAccounting()
    {
        lock (gate) { EnsureOpen(); return new AccountingView(this); }
    }

    private sealed class AccountingView(InMemoryLibraryInventories store) : ILibraryInventoryAccounting
    {
        private sealed class Bucket
        {
            public long Expected, Present, Bytes;
            public void Add(long? length)
            {
                Expected = checked(Expected + 1);
                if (length is not { } value) return;
                Present = checked(Present + 1); Bytes = checked(Bytes + value);
            }
            public LibraryInventoryCoverage Coverage() => new(Expected, Present, checked(Expected - Present), Bytes);
        }

        public ValueTask<LibraryInventoryAccountingMeasurement> MeasureAsync(CancellationToken ct = default)
        {
            lock (store.gate)
            {
                store.EnsureOpen(); ct.ThrowIfCancellationRequested();
                try
                {
                    long published = 0, unpublished = 0, indexedObjects = 0, indexedBytes = 0;
                    var streams = new List<(SortedSet<ObjectKey> Keys, LibraryClaim Claim)>(LibraryInventoryAccountingLimits.MaxPublishedInventories);
                    foreach (var (claim, identity) in store.identities)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (identity.Latest == 0) continue;
                        if (identity.Active is not { } active) { unpublished = checked(unpublished + 1); continue; }
                        published = checked(published + 1);
                        if (published > LibraryInventoryAccountingLimits.MaxPublishedInventories)
                            throw new ObjectStoreException(ObjectStoreCodes.Limit, "Too many published inventories for library accounting.");
                        streams.Add((store.generations[active].Keys, claim));
                    }
                    foreach (var length in store.indexedObjects.Values)
                    {
                        ct.ThrowIfCancellationRequested(); indexedObjects = checked(indexedObjects + 1); indexedBytes = checked(indexedBytes + length);
                    }
                    if (published == 0) return ValueTask.FromResult(new LibraryInventoryAccountingMeasurement(0, unpublished, indexedObjects, indexedBytes,
                        null, null, null, null, null, null, null, null, null, null, null, null, null, null));
                    var union = new Bucket(); var single = new Bucket(); var multiple = new Bucket();
                    var original = new Bucket(); var derived = new Bucket(); var bothRoles = new Bucket();
                    var build = new Bucket(); var global = new Bucket(); var bothScopes = new Bucket();
                    long ownerReferences = 0, indexedOwnerReferences = 0, creditedBytes = 0;
                    var cursors = streams.Select(stream => ((IEnumerable<ObjectKey>)stream.Keys).GetEnumerator()).ToArray();
                    try
                    {
                        var heads = new PriorityQueue<int, ObjectKey>();
                        for (var i = 0; i < cursors.Length; i++) if (cursors[i].MoveNext()) heads.Enqueue(i, cursors[i].Current);
                        var owners = new HashSet<(string Owner, LibraryOwnerScope Scope)>();
                        while (heads.TryPeek(out _, out var key))
                        {
                            owners.Clear(); var roles = 0; var scopes = 0;
                            while (heads.TryPeek(out var cursor, out var current) && current == key)
                            {
                                ct.ThrowIfCancellationRequested(); heads.Dequeue(); var claim = streams[cursor].Claim;
                                owners.Add((claim.Owner, claim.Scope)); roles |= 1 << (int)claim.Role; scopes |= 1 << (int)claim.Scope;
                                if (cursors[cursor].MoveNext()) heads.Enqueue(cursor, cursors[cursor].Current);
                            }
                            long? length = store.indexedObjects.TryGetValue(key, out var indexed) ? indexed : null;
                            union.Add(length); (owners.Count == 1 ? single : multiple).Add(length);
                            (roles == 1 ? original : roles == 2 ? derived : bothRoles).Add(length);
                            (scopes == 1 ? build : scopes == 2 ? global : bothScopes).Add(length);
                            ownerReferences = checked(ownerReferences + owners.Count);
                            if (length is not { } value) continue;
                            indexedOwnerReferences = checked(indexedOwnerReferences + owners.Count);
                            creditedBytes = checked(creditedBytes + checked(value * owners.Count));
                        }
                    }
                    finally { foreach (var cursor in cursors) cursor.Dispose(); }
                    ct.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(new LibraryInventoryAccountingMeasurement(published, unpublished, indexedObjects, indexedBytes, union.Coverage(),
                        checked(indexedObjects - union.Present), checked(indexedBytes - union.Bytes), ownerReferences, indexedOwnerReferences, creditedBytes,
                        single.Coverage(), multiple.Coverage(), original.Coverage(), derived.Coverage(), bothRoles.Coverage(), build.Coverage(), global.Coverage(), bothScopes.Coverage()));
                }
                catch (OverflowException e) { throw new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "Indexed library accounting overflowed.", e); }
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
