namespace VibeMiner.Objects;

/// <summary>One immutable, validated owner/scope/role selection, independent of producer discovery.</summary>
public sealed record LibraryInventorySelection
{
    public string Owner { get; }
    public LibraryOwnerScope Scope { get; }
    public LibraryObjectRole Role { get; }

    public LibraryInventorySelection(string owner, LibraryOwnerScope scope, LibraryObjectRole role)
    {
        _ = new LibraryClaim(owner, scope, "inventory-selection", role);
        Owner = owner; Scope = scope; Role = role;
    }
}

/// <summary>Two immutable selections borrowing one library lifetime; measurements describe only this selected pair.</summary>
public interface ILibraryInventoryPair
{
    ValueTask<LibraryInventoryPairMeasurement> MeasureAsync(CancellationToken ct = default);
}

public static class LibraryInventoryPairLimits
{
    public const int MaxPublishedProducersPerSide = LibraryInventoryUnionLimits.MaxPublishedProducers;
    public const int MaxStreamHeads = 2 * MaxPublishedProducersPerSide;
}

/// <summary>Deduplicated committed-index evidence, without payload verification, physical membership or unknown missing lengths.</summary>
public sealed record LibraryInventoryCoverage(long ExpectedKeys, long IndexedPresentKeys, long MissingKeys, long IndexedPayloadBytes);

/// <summary>
/// Side statistics retain their independent published/unpublished evidence. Pair buckets are null if either side has no published
/// producer. Otherwise, Only and Intersection refer strictly to these two published unions, never all-owner exclusivity or complete
/// producer discovery. Missing or corrupt packs can still have indexed objects: no payload access or verified-retention claim occurs.
/// </summary>
public sealed record LibraryInventoryPairMeasurement(LibraryInventoryUnionMeasurement Left, LibraryInventoryUnionMeasurement Right,
    LibraryInventoryCoverage? Union, LibraryInventoryCoverage? Intersection, LibraryInventoryCoverage? LeftOnly, LibraryInventoryCoverage? RightOnly);
