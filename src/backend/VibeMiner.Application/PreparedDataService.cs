using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>Free one opened build's file catalog (D085).</summary>
public sealed record PreparedDataFreeRequest(string ProductId, string BuildIdentity);

public interface IPreparedDataCatalog
{
    /// <summary>Every opened build's prepared data, measured now, one build per catalog operation.</summary>
    Task<PreparedDataList> ListPreparedDataAsync(CancellationToken token);

    /// <summary>
    /// Stops the build's file catalog preparation if one runs and deletes its files. Cancellation before the state change
    /// commits changes nothing; once it commits, the files are deleted whatever the token says.
    /// </summary>
    Task<PreparedDataFreed> FreePreparedDataAsync(PreparedDataFreeRequest request, CancellationToken token);
}

/// <summary>
/// Prepared data per opened build (D085): measured sizes, and freeing the one part only that build uses, its file catalog.
/// One free runs at a time; a second waits for it, cancellably.
/// </summary>
public sealed class PreparedDataService(IPreparedDataCatalog catalog)
{
    private readonly SemaphoreSlim freeing = new(1, 1);

    public Task<PreparedDataList> ListAsync(CancellationToken token = default) => catalog.ListPreparedDataAsync(token);

    public async Task<PreparedDataFreed> FreeAsync(PreparedDataFreeRequest request, CancellationToken token = default)
    {
        Validate(request);
        await freeing.WaitAsync(token);
        try { return await catalog.FreePreparedDataAsync(request, token); }
        finally { freeing.Release(); }
    }

    public static void Validate(PreparedDataFreeRequest request) =>
        WorkspaceService.Validate(new(request.ProductId, request.BuildIdentity, "us"));
}
