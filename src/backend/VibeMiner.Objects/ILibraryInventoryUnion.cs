namespace VibeMiner.Objects;

/// <summary>
/// Immutable owner/scope/role view of the union of all its active producer inventories, independent of a current producer.
/// This borrows the library lifetime and measures index evidence only; it never acquires, verifies or claims payloads.
/// </summary>
public interface ILibraryInventoryUnion
{
    /// <summary>
    /// Measures the published union in one read snapshot. Unpublished registered producers remain explicit, while unregistered
    /// producers are unknowable. More than MaxPublishedProducers active producers is refused with ObjectStoreCodes.Limit.
    /// </summary>
    ValueTask<LibraryInventoryUnionMeasurement> MeasureAsync(CancellationToken ct = default);
}

public static class LibraryInventoryUnionLimits
{
    /// <summary>Bounds simultaneous indexed streams and merge heads. No active producer is silently omitted.</summary>
    public const int MaxPublishedProducers = 256;
}

/// <summary>
/// Published-union index evidence, never complete-owner or verified-retention evidence. Union measurements are null without
/// any published producer; a published empty union gives four zero measurements. Keys and indexed payload bytes count once
/// across producers. Missing lengths, physical ownership and pack/database overhead are not measured.
/// </summary>
public sealed record LibraryInventoryUnionMeasurement(long PublishedProducers, long UnpublishedProducers,
    long? ExpectedKeys, long? IndexedPresentKeys, long? MissingKeys, long? IndexedPayloadBytes);
