namespace VibeMiner.Objects;

/// <summary>
/// Durable expected keys for one immutable library owner/scope/producer/role. This borrows the library lifetime.
/// Expected keys do not imply payload presence or physical ownership. All key batches/pages are bounded to 1024.
/// A new begin fences earlier staging tokens. Failed or canceled append/publication abandons that token.
/// Mutations finish their durable transaction once admitted to it, even if cancellation arrives during that commit.
/// </summary>
public interface ILibraryInventory
{
    Task<LibraryInventoryGeneration> BeginAsync(CancellationToken ct = default);
    Task AppendAsync(LibraryInventoryGeneration generation, IReadOnlyList<ObjectKey> keys, CancellationToken ct = default);
    Task PublishAsync(LibraryInventoryGeneration generation, CancellationToken ct = default);
    Task PublishSourceAsync(LibraryInventoryGeneration generation, LibraryInventorySourceDescriptor descriptor, CancellationToken ct = default);
    ValueTask<long?> MatchSourceAsync(LibraryInventorySourceDescriptor descriptor, CancellationToken ct = default);
    Task AbandonAsync(LibraryInventoryGeneration generation, CancellationToken ct = default);
    ValueTask<LibraryInventoryLookup> LookupAsync(ObjectKey key, CancellationToken ct = default);
    /// <summary>
    /// Measures this active generation's committed-index coverage in one snapshot. This does not read or verify payload files,
    /// add membership, or infer missing payload lengths. Every result field is null when no inventory has been published.
    /// </summary>
    ValueTask<LibraryInventoryMeasurement> MeasureAsync(CancellationToken ct = default);
    /// <summary>Keys in ObjectKey order. A cursor is valid only for its original library, identity and still-active generation.</summary>
    ValueTask<LibraryInventoryPage> ReadPageAsync(int limit = 1024, LibraryInventoryCursor? after = null, CancellationToken ct = default);
}

/// <summary>An opaque generation token returned by BeginAsync. Retain its Binding unchanged.</summary>
public sealed record LibraryInventoryGeneration(long Value)
{
    public string Binding { get; init; } = "";
}

/// <summary>A null Generation means unpublished; a published empty set has a nonnull Generation and Contains=false.</summary>
public sealed record LibraryInventoryLookup(long? Generation, bool Contains);

/// <summary>An opaque continuation returned by ReadPageAsync. Retain its Binding unchanged.</summary>
public sealed record LibraryInventoryCursor(long Generation, ObjectKey After)
{
    public string Binding { get; init; } = "";
}

public sealed record LibraryInventoryPage(long? Generation, IReadOnlyList<ObjectKey> Keys, LibraryInventoryCursor? Next);

/// <summary>
/// Index evidence for one active generation. ExpectedKeys equals IndexedPresentKeys plus MissingKeys; IndexedPayloadBytes sums
/// only indexed lengths of present expected keys. Missing payload lengths are unknown. All fields are null for unpublished
/// inventories; a published empty inventory has its generation and four zero measurements. This is not verified retention.
/// </summary>
public sealed record LibraryInventoryMeasurement(long? Generation, long? ExpectedKeys, long? IndexedPresentKeys,
    long? MissingKeys, long? IndexedPayloadBytes);
