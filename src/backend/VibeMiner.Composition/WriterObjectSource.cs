using VibeMiner.Cdn;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Composition;

/// <summary>
/// The writer's layer 2: the real <see cref="ObjectSource"/> over the store, then the official CDN and version service, and the
/// wow-listfile's GitHub releases (D105). Layer 4 asks for the listfile only while the names switch is on; the listfile client has
/// its own handler and connection budget. Owns the HTTP clients, not the store: dispose this before the store.
/// </summary>
public sealed class WriterObjectSource : IDisposable
{
    // Layer 1's handler: no redirects or decompression, bounded connections. The clients set their own timeouts.
    private readonly HttpClient http = new(CdnClient.CreateHandler(4)) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly WowListfileConnections listfileConnections = new();
    private readonly HttpClient listfileHttp;

    public WriterObjectSource(ObjectStore store)
    {
        // The listfile client sets its own budgets; HttpClient's timeout would cut a part short.
        listfileHttp = new(WowListfileClient.CreateHandler(listfileConnections)) { Timeout = Timeout.InfiniteTimeSpan };
        Objects = new ObjectSource(store, store, store, null, new CdnClient(http),
            [new VersionServiceObservations(new VersionClient(http)),
             new WowListfileObservations(new WowListfileClient(listfileHttp, connections: listfileConnections))], ObjectVerification.Default);
    }

    /// <summary>History and objects: the store, then the network, recording what it serves.</summary>
    public IObjectSource Objects { get; }

    public void Dispose()
    {
        http.Dispose();
        listfileHttp.Dispose();
    }
}
