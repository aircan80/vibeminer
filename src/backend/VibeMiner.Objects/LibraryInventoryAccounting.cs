using Microsoft.Data.Sqlite;

namespace VibeMiner.Objects;

internal sealed class LibraryInventoryAccounting(ObjectStore store) : ILibraryInventoryAccounting
{
    public ValueTask<LibraryInventoryAccountingMeasurement> MeasureAsync(CancellationToken ct = default) => store.MeasureInventoryAccountingAsync(ct);
}

public sealed partial class ObjectStore
{
    public ILibraryInventoryAccounting ForInventoryAccounting()
    {
        ThrowIfClosed();
        if (Kind != ObjectStoreKind.Library) throw new InvalidOperationException("Inventory accounting requires a mixed library.");
        return new LibraryInventoryAccounting(this);
    }

    private static InventoryUnionResolution ResolveAllInventoryGenerations(SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        var generations = new List<InventoryUnionGeneration>(LibraryInventoryAccountingLimits.MaxPublishedInventories);
        long published = 0, unpublished = 0;
        using var identities = c.CreateCommand(); identities.Transaction = tx;
        identities.CommandText = """
            SELECT CASE WHEN typeof(i.active)='integer' OR i.active IS NULL THEN i.active ELSE 0 END,g.id,
                CASE WHEN typeof(g.state)='integer' THEN g.state ELSE NULL END,
                CASE WHEN typeof(g.key_count)='integer' THEN g.key_count ELSE NULL END,i.owner,CASE WHEN typeof(i.scope)='integer' THEN i.scope ELSE NULL END,
                i.producer,CASE WHEN typeof(i.role)='integer' THEN i.role ELSE NULL END,
                typeof(i.owner)='text' AND length(CAST(i.owner AS BLOB)) BETWEEN 1 AND 512 AND instr(i.owner,char(0))=0,
                typeof(i.producer)='text' AND length(CAST(i.producer AS BLOB)) BETWEEN 1 AND 128 AND instr(i.producer,char(0))=0
            FROM library_inventory_identity i LEFT JOIN library_inventory_generation g ON g.id=i.active
                AND g.owner=i.owner AND g.scope=i.scope AND g.producer=i.producer AND g.role=i.role
            """;
        using var rows = identities.ExecuteReader();
        while (rows.Read())
        {
            ct.ThrowIfCancellationRequested();
            // Check storage classes/UTF-8 lengths in SQL before marshalling potentially damaged identity strings.
            if (rows.GetValue(8) is not long ownerValid || ownerValid != 1 || rows.GetValue(9) is not long producerValid || producerValid != 1 ||
                rows.GetValue(5) is not long scope || scope is < 0 or > 1 || rows.GetValue(7) is not long role || role is < 0 or > 1)
                throw new InvalidOperationException("An inventory identity is malformed.");
            var claim = new LibraryClaim(rows.GetString(4), (LibraryOwnerScope)scope, rows.GetString(6), (LibraryObjectRole)role);
            var generation = ReadActiveInventoryGeneration(rows);
            if (generation is null) { unpublished = checked(unpublished + 1); continue; }
            published = checked(published + 1);
            if (published > LibraryInventoryAccountingLimits.MaxPublishedInventories)
                throw new ObjectStoreException(ObjectStoreCodes.Limit,
                    $"Library inventory accounting supports at most {LibraryInventoryAccountingLimits.MaxPublishedInventories} active published inventories.");
            generations.Add(generation.Value with { Owner = new InventoryOwnerReference(claim.Owner, claim.Scope), Role = claim.Role });
        }
        return new(generations, published, unpublished);
    }

    internal ValueTask<LibraryInventoryAccountingMeasurement> MeasureInventoryAccountingAsync(CancellationToken ct)
    {
        ThrowIfClosed();
        return readers.Use(0, (c, _, unused) => InventoryRead(c, ct, tx =>
        {
            var resolved = ResolveAllInventoryGenerations(c, tx, ct);
            At(StorePoint.InventoryAccountingSnapshot);
            ct.ThrowIfCancellationRequested();
            var indexed = MeasureAccountingIndex(c, tx, ct);
            if (resolved.Published == 0)
                return new LibraryInventoryAccountingMeasurement(0, resolved.Unpublished, indexed.Objects, indexed.Bytes,
                    null, null, null, null, null, null, null, null, null, null, null, null, null, null);

            var union = new InventoryCoverageCounter(); var single = new InventoryCoverageCounter(); var multiple = new InventoryCoverageCounter();
            var original = new InventoryCoverageCounter(); var derived = new InventoryCoverageCounter(); var bothRoles = new InventoryCoverageCounter();
            var build = new InventoryCoverageCounter(); var global = new InventoryCoverageCounter(); var bothScopes = new InventoryCoverageCounter();
            var owners = new HashSet<InventoryOwnerReference>(LibraryInventoryAccountingLimits.MaxPublishedInventories);
            var roles = 0; var scopes = 0;
            long references = 0, indexedReferences = 0, creditedBytes = 0;
            MergeInventoryStreams(c, tx, resolved.Generations, null, StorePoint.InventoryAccountingRow, (sides, length) =>
            {
                if (owners.Count == 0) throw new InvalidOperationException("An expected key has no published inventory owner.");
                union.Add(length);
                (owners.Count == 1 ? single : multiple).Add(length);
                (roles == 1 ? original : roles == 2 ? derived : bothRoles).Add(length);
                (scopes == 1 ? build : scopes == 2 ? global : bothScopes).Add(length);
                references = checked(references + owners.Count);
                if (length is { } bytes)
                {
                    indexedReferences = checked(indexedReferences + owners.Count);
                    creditedBytes = checked(creditedBytes + checked(bytes * owners.Count));
                }
                owners.Clear(); roles = 0; scopes = 0;
            }, ct, generation =>
            {
                if (generation.Owner is not { } owner) throw new InvalidOperationException("A published inventory stream has no owner provenance.");
                owners.Add(owner);
                if (owners.Count > LibraryInventoryAccountingLimits.MaxPublishedInventories)
                    throw new ObjectStoreException(ObjectStoreCodes.Limit, "Too many current-key inventory owners.");
                roles |= 1 << (int)generation.Role;
                scopes |= 1 << (int)owner.Scope;
            });
            var outsideObjects = checked(indexed.Objects - union.Present);
            var outsideBytes = checked(indexed.Bytes - union.Bytes);
            if (outsideObjects < 0 || outsideBytes < 0) throw new InvalidOperationException("Inventory and committed-index coverage disagree.");
            return new LibraryInventoryAccountingMeasurement(resolved.Published, resolved.Unpublished, indexed.Objects, indexed.Bytes,
                union.Coverage(), outsideObjects, outsideBytes, references, indexedReferences, creditedBytes,
                single.Coverage(), multiple.Coverage(), original.Coverage(), derived.Coverage(), bothRoles.Coverage(),
                build.Coverage(), global.Coverage(), bothScopes.Coverage());
        }), ct);
    }

    private (long Objects, long Bytes) MeasureAccountingIndex(SqliteConnection c, SqliteTransaction tx, CancellationToken ct)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT k,length,typeof(k)='blob' FROM objects ORDER BY k";
        using var rows = cmd.ExecuteReader();
        long count = 0, bytes = 0; ObjectKey previous = default;
        while (rows.Read())
        {
            At(StorePoint.InventoryAccountingIndexRow);
            ct.ThrowIfCancellationRequested();
            if (rows.GetValue(2) is not long blob || blob != 1 || rows.GetBytes(0, 0, null, 0, 0) != ObjectKey.EncodedBytes)
                throw new InvalidOperationException("A committed index key is malformed.");
            var key = ObjectKey.FromIndexKey(rows.GetFieldValue<byte[]>(0));
            LibraryInventory.ValidateKey(key);
            if (count > 0 && key.CompareTo(previous) <= 0) throw new InvalidOperationException("Committed index keys are duplicate or unordered.");
            if (rows.GetValue(1) is not long length || length <= 0) throw new InvalidOperationException("A committed index payload length is unreadable.");
            count = checked(count + 1); bytes = checked(bytes + length); previous = key;
        }
        ct.ThrowIfCancellationRequested();
        return (count, bytes);
    }
}
