using VibeMiner.Cdn;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Composition;

/// <summary>Explicit mixed-library composition. Owns clients and shared acquisition, never the supplied library or any profile.</summary>
public sealed class LibraryAcquisitionService : IAsyncDisposable
{
    private readonly HttpClient background = new(CdnClient.CreateHandler(4)) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient interactive = new(CdnClient.CreateHandler(4)) { Timeout = Timeout.InfiniteTimeSpan };
    private Task? closing;
    private readonly object gate = new();
    public LibraryAcquisitionService(ObjectStore library, ObjectSourceOptions? options = null, LibraryObjectAccessOptions? limits = null)
    {
        try { Objects = new LibraryObjectAccessService(library, new CdnClient(background), options, limits, new CdnClient(interactive)); }
        catch { background.Dispose(); interactive.Dispose(); throw; }
    }
    public ILibraryObjectAccess Objects { get; }
    public ValueTask DisposeAsync() { lock (gate) return new(closing ??= CloseAsync()); }
    private async Task CloseAsync()
    {
        try { await Objects.DisposeAsync().ConfigureAwait(false); }
        finally { background.Dispose(); interactive.Dispose(); }
    }
}
