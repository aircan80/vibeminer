using VibeMiner.Cdn;

namespace VibeMiner.Builds;

public sealed record EncodingRow(CdnKey EKey, long EncodedSize);
public sealed record EncodingRowPage(IReadOnlyList<EncodingRow> Rows, bool HasMore);
public interface IEncodingRowSource
{
    BuildRef Build { get; }
    CdnKey EncodingCKey { get; }
    long EncodedCount { get; }
    IEncodingRowLease OpenEncodingRows();
}
public interface IEncodingRowLease : IAsyncDisposable
{
    BuildRef Build { get; }
    CdnKey EncodingCKey { get; }
    long EncodedCount { get; }
    ValueTask<EncodingRowPage> ReadPageAsync(CdnKey? after, int limit = 1024, CancellationToken ct = default);
}

/// <summary>A frozen request identity and exact opened metadata source, independent of route-based canonical ownership.</summary>
public sealed record EncodingInventoryContext
{
    public BuildIdentity Identity { get; }
    public BuildRef Build { get; }
    public CdnKey EncodingCKey { get; }
    public long TotalEKeys { get; }
    public EncodingInventoryContext(BuildIdentity identity, BuildRef build, CdnKey encodingCKey, long totalEKeys)
    {
        try { Identity = BuildRegistry.Identity(identity); }
        catch (BuildRegistryException e) { throw new EncodingInventoryException(EncodingInventoryCodes.Validation, e.Message, e); }
        if (build is null || build.Product != Identity.ProductId || build.BuildConfig != Identity.Fields["BuildConfig"] || totalEKeys is < 0 or > 50_000_000)
            throw new EncodingInventoryException(EncodingInventoryCodes.Validation, "The opened selection or ENCODING row count is inconsistent.");
        Build = build; EncodingCKey = encodingCKey; TotalEKeys = totalEKeys;
    }
}
public interface IEncodingInventoryStore
{
    Task<IEncodingInventoryWrite> BeginAsync(EncodingInventoryContext context, CancellationToken ct = default);
}
public interface IEncodingInventoryWrite : IAsyncDisposable
{
    Task AppendAsync(IReadOnlyList<CdnKey> keys, CancellationToken ct = default);
    Task<long> PublishAsync(CancellationToken ct = default);
    Task AbandonAsync(CancellationToken ct = default);
}
public interface IEncodingInventoryPublisher : IAsyncDisposable
{
    EncodingInventoryPublication Start(BuildIdentity identity, IBuild opened, CancellationToken ct = default);
}
public enum EncodingInventoryState { Queued, Running, Published, Canceled, Superseded, Failed }
public sealed record EncodingInventoryProgress(EncodingInventoryState State, long Processed, long Total);
public sealed record EncodingInventoryResult(EncodingInventoryContext Context, EncodingInventoryState State, long Processed,
    long? Generation, string? Code = null, string? Message = null);
public sealed class EncodingInventoryPublication
{
    private readonly TaskCompletionSource<EncodingInventoryResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EncodingInventoryProgress progress;
    internal EncodingInventoryPublication(EncodingInventoryContext context)
    { Context = context; progress = new(EncodingInventoryState.Queued, 0, context.TotalEKeys); }
    public EncodingInventoryContext Context { get; }
    public Task<EncodingInventoryResult> Completion => completion.Task;
    public EncodingInventoryProgress Progress => Volatile.Read(ref progress);
    internal void Update(EncodingInventoryState state, long processed) => Volatile.Write(ref progress, new(state, processed, Context.TotalEKeys));
    internal void Complete(EncodingInventoryResult result) { Update(result.State, result.Processed); completion.TrySetResult(result); }
}
public static class EncodingInventoryCodes
{
    public const string Validation = "ENCODING_INVENTORY_VALIDATION", Unsupported = "ENCODING_INVENTORY_UNSUPPORTED";
    public const string Unavailable = "ENCODING_INVENTORY_UNAVAILABLE", Corrupt = "ENCODING_INVENTORY_CORRUPT";
    public const string Busy = "ENCODING_INVENTORY_BUSY", Closed = "ENCODING_INVENTORY_CLOSED";
    public const string Storage = "ENCODING_INVENTORY_STORAGE", Superseded = "ENCODING_INVENTORY_SUPERSEDED";
}
public sealed class EncodingInventoryException(string code, string message, Exception? inner = null) : Exception(message, inner)
{ public string Code { get; } = code; }
public sealed record EncodingInventoryPublisherOptions
{
    public int MaxConcurrent { get; init; } = 2;
    public int MaxAdmitted { get; init; } = 16;
    internal Action<EncodingInventoryPoint>? Fault { get; init; }
}
internal enum EncodingInventoryPoint { BeforeReadPage, AfterAppend, BeforePublish, AfterPublish }
