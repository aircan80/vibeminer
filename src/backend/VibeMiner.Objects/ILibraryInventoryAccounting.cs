namespace VibeMiner.Objects;

/// <summary>Library-wide published-inventory index accounting, borrowing one library lifetime without changing ownership or retention.</summary>
public interface ILibraryInventoryAccounting
{
    ValueTask<LibraryInventoryAccountingMeasurement> MeasureAsync(CancellationToken ct = default);
}

public static class LibraryInventoryAccountingLimits
{
    public const int MaxPublishedInventories = 512;
}

/// <summary>
/// Independent committed-index object counts/lengths plus the union and logical references of published expected inventories.
/// Every nullable field is unmeasured when no inventory is published; published empty inventories produce measured zeros.
/// Logical references deduplicate (owner,scope) across roles/producers and can credit a payload more than once. Outside-union
/// objects are not declared unowned or reclaimable. All fields describe index evidence, never verified physical retention,
/// complete producer discovery, missing payload sizes or filesystem/database overhead.
/// </summary>
public sealed record LibraryInventoryAccountingMeasurement(long PublishedInventories, long UnpublishedInventories,
    long IndexedObjects, long IndexedPayloadBytes, LibraryInventoryCoverage? Union,
    long? OutsideUnionIndexedObjects, long? OutsideUnionIndexedPayloadBytes, long? OwnerReferences,
    long? IndexedOwnerReferences, long? OwnerCreditedIndexedPayloadBytes,
    LibraryInventoryCoverage? SingleOwner, LibraryInventoryCoverage? MultipleOwners,
    LibraryInventoryCoverage? OriginalOnly, LibraryInventoryCoverage? DerivedOnly, LibraryInventoryCoverage? BothRoles,
    LibraryInventoryCoverage? BuildOnly, LibraryInventoryCoverage? GlobalOnly, LibraryInventoryCoverage? BothScopes);
