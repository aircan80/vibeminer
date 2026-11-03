namespace VibeMiner.Objects.Testing;

/// <summary>Independent index-only attribution model. It never stores, reads or verifies payload bytes.</summary>
public sealed class InMemoryIndexedOwnership : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly HashSet<ObjectKey> indexed = [];
    private readonly Dictionary<LibraryClaim, HashSet<ObjectKey>> claims = [];
    private bool closed;
    private int active;
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Func<CancellationToken, Task>? BeforeClaim { get; set; }
    public Action? AfterClaim { get; set; }
    public void SetIndexedObject(ObjectKey key) { Validate(key); lock (gate) { Open(); indexed.Add(key); } }
    public Owner ForOwner(string owner, LibraryOwnerScope scope, string producer, LibraryObjectRole role)
    { var claim = new LibraryClaim(owner, scope, producer, role); lock (gate) { Open(); return new(this, claim); } }
    private void Open() { if (closed) throw new ObjectStoreException(ObjectStoreCodes.Closed, "The managed attribution store is closed."); }
    private static void Validate(ObjectKey key)
    {
        if (key.Namespace != 0 || !ObjectFormats.Known(ObjectStoreKind.Library, key.Type)) throw new ArgumentException("Attribution requires a known namespace-zero library key.");
    }
    public ValueTask DisposeAsync()
    { lock (gate) { closed = true; if (active == 0) drained.TrySetResult(); return new(drained.Task); } }
    public sealed class Owner
    {
        private readonly InMemoryIndexedOwnership store;
        private readonly LibraryClaim claim;
        internal Owner(InMemoryIndexedOwnership store, LibraryClaim claim) { this.store = store; this.claim = claim; }
        public ValueTask<bool> OwnsAsync(ObjectKey key, CancellationToken ct = default)
        { lock (store.gate) { store.Open(); ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(store.indexed.Contains(key) && store.claims.TryGetValue(claim, out var keys) && keys.Contains(key)); } }
        public async Task<IReadOnlyList<bool>> ClaimIndexedAsync(IReadOnlyList<ObjectKey> keys, CancellationToken ct = default)
        {
            lock (store.gate) store.Open(); ct.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(keys);
            if (keys.Count > 1024) throw new ObjectStoreException(ObjectStoreCodes.Limit, "An attribution batch is at most 1024 keys.");
            var copy = keys.ToArray(); foreach (var key in copy) Validate(key);
            lock (store.gate) { store.Open(); store.active++; }
            try
            {
                if (store.BeforeClaim is { } before) await before(ct); ct.ThrowIfCancellationRequested();
                bool[] result;
                lock (store.gate)
                {
                    result = new bool[copy.Length];
                    if (!store.claims.TryGetValue(claim, out var owned)) store.claims.Add(claim, owned = []);
                    for (var i = 0; i < copy.Length; i++) if (store.indexed.Contains(copy[i])) { owned.Add(copy[i]); result[i] = true; }
                }
                store.AfterClaim?.Invoke(); return result;
            }
            finally { lock (store.gate) { store.active--; if (store.closed && store.active == 0) store.drained.TrySetResult(); } }
        }
    }
}
