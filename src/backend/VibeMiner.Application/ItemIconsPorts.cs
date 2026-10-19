using VibeMiner.Domain;

namespace VibeMiner.Application;

public sealed record ItemIconBatchRequest(ItemsContext Context, string ArtifactId, IReadOnlyList<string> ItemIds);
public sealed record ItemIconReadRequest(ItemsContext Context, string ArtifactId, string ItemId, string ImageArtifactId);

/// <summary>A held, verified app-owned PNG; disposing retires its bounded read admission.</summary>
public interface IItemIconReadLease : IAsyncDisposable
{
    Stream Content { get; }
    string ArtifactId { get; }
    string Sha256 { get; }
    long ByteLength { get; }
    int Width { get; }
    int Height { get; }
}
public interface IItemIconsStore
{
    Task<ItemIconBatch> PrepareBatchAsync(ItemIconBatchRequest request,
        Func<ItemIconProgress, Task> progress, CancellationToken token);
    Task<IItemIconReadLease> OpenAsync(ItemIconReadRequest request, CancellationToken token);
}
