using VibeMiner.Builds;
using VibeMiner.Cdn;
using VibeMiner.Objects;

namespace VibeMiner.Composition;

/// <summary>Explicit mixed-library binding. Borrows the library; creates no profile, acquisition, registry work or reusable source receipt.</summary>
public sealed class LibraryEncodingInventoryStore : IEncodingInventoryStore
{
    private readonly ObjectStore library;
    public LibraryEncodingInventoryStore(ObjectStore library)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (library.Kind != ObjectStoreKind.Library) throw new ArgumentException("ENCODING inventories require a mixed library.", nameof(library));
        this.library = library;
    }
    public async Task<IEncodingInventoryWrite> BeginAsync(EncodingInventoryContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var inventory = library.ForOwner(context.Identity.Id, LibraryOwnerScope.Build, EncodingInventoryPublisher.Producer, LibraryObjectRole.Original).Inventory;
            var generation = await inventory.BeginAsync(ct).ConfigureAwait(false);
            return new Write(inventory, generation);
        }
        catch (ObjectStoreException e) { throw Map(e); }
    }
    private sealed class Write(ILibraryInventory inventory, LibraryInventoryGeneration generation) : IEncodingInventoryWrite
    {
        private bool finished;
        public async Task AppendAsync(IReadOnlyList<CdnKey> keys, CancellationToken ct = default)
        {
            if (finished) throw new EncodingInventoryException(EncodingInventoryCodes.Closed, "The inventory write is closed.");
            if (keys is null || keys.Count > 1024)
            {
                await AbandonAsync(CancellationToken.None).ConfigureAwait(false);
                throw new EncodingInventoryException(EncodingInventoryCodes.Validation, "An inventory append is at most 1024 keys.");
            }
            ct.ThrowIfCancellationRequested();
            var objects = new ObjectKey[keys.Count];
            for (var i = 0; i < objects.Length; i++) objects[i] = new ObjectKey(ObjectKeyType.Encoded, keys[i].ToBytes());
            try { await inventory.AppendAsync(generation, objects, ct).ConfigureAwait(false); }
            catch (ObjectStoreException e) { throw Map(e); }
        }
        public async Task<long> PublishAsync(CancellationToken ct = default)
        {
            if (finished) throw new EncodingInventoryException(EncodingInventoryCodes.Closed, "The inventory write is closed.");
            try { await inventory.PublishAsync(generation, ct).ConfigureAwait(false); finished = true; return generation.Value; }
            catch (ObjectStoreException e) { throw Map(e); }
        }
        public async Task AbandonAsync(CancellationToken ct = default)
        {
            if (finished) return;
            try { await inventory.AbandonAsync(generation, ct).ConfigureAwait(false); finished = true; }
            catch (ObjectStoreException e) { throw Map(e); }
        }
        public async ValueTask DisposeAsync()
        {
            if (finished) return;
            try { await AbandonAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (EncodingInventoryException e) when (e.Code == EncodingInventoryCodes.Superseded) { finished = true; }
        }
    }
    private static EncodingInventoryException Map(ObjectStoreException e) => new(e.Code switch
    {
        ObjectStoreCodes.InventoryStale => EncodingInventoryCodes.Superseded,
        ObjectStoreCodes.InventoryUpgradeRequired => EncodingInventoryCodes.Unsupported,
        ObjectStoreCodes.Limit => EncodingInventoryCodes.Validation,
        _ => EncodingInventoryCodes.Storage,
    }, e.Message, e);
}
