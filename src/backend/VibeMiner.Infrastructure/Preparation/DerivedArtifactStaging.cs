using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>Private staging owned only by the Items and two classification builders (at most three files).</summary>
internal sealed class DerivedArtifactStaging(IDisposable reservation, Action<string> delete, string failureCode)
{
    private readonly string?[] paths = new string?[3];
    private readonly FileStream?[] streams = new FileStream?[3];
    private readonly Task?[] closes = new Task?[3];
    private int count;
    private bool closeFailed, retired;
    private Task<bool>? retirement;
    internal DiscoveryException Failure() => new(failureCode,
        "Derived artifact staging retirement could not be confirmed. Restart the engine.");

    internal async Task<FileStream> CreateAsync(Func<CancellationToken, Task<FileStream>> create, CancellationToken token)
    {
        // Validate capacity before calling a factory, so no unregistered returned file can exceed the fixed owner.
        if (count == paths.Length || retired || closes.Any(task => task is not null)) throw Failure();
        var stream = await create(token);
        var slot = count++; streams[slot] = stream;
        try { paths[slot] = stream.Name; }
        catch { closeFailed = true; throw Failure(); }
        return stream;
    }

    internal async Task CloseAsync()
    {
        // Start each of the at most three real closes before observing any failure or pending close.
        for (var i = 0; i < count; i++)
        {
            if (closes[i] is not null) continue;
            try { closes[i] = streams[i]!.DisposeAsync().AsTask(); }
            catch (Exception failure) { closes[i] = Task.FromException(failure); }
        }
        try { await Task.WhenAll(closes.Take(count).Select(task => task!)); }
        catch { closeFailed = true; }
        if (closeFailed) throw Failure();
    }

    internal Task<bool> RetireAsync() => retirement ??= RetireCoreAsync();

    private async Task<bool> RetireCoreAsync()
    {
        if (retired) return true;
        try { await CloseAsync(); }
        catch { closeFailed = true; }
        var failed = closeFailed;
        for (var i = 0; i < count; i++)
        {
            // File.Delete also confirms an already absent path; every accepted path receives its one attempt.
            if (paths[i] is not { } path) { failed = true; continue; }
            try { delete(path); }
            catch { failed = true; }
        }
        if (failed) return false; // Caller retains this exact owner, handles/tasks/paths and reservation as debt.
        try { reservation.Dispose(); }
        catch { return false; }
        retired = true; return true;
    }
}
