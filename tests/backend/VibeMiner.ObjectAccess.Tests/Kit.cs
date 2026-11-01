using VibeMiner.Cdn;
using VibeMiner.Cdn.Testing;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;

namespace VibeMiner.ObjectAccess.Tests;

/// <summary>A synthetic archive: members back to back (with optional gaps), and where the "index" says each one is.</summary>
internal sealed record SyntheticArchive(CdnKey Key, byte[] Bytes, IReadOnlyDictionary<ObjectKey, ArchiveLocation> Locations)
{
    public ObjectRequest Request(PutObject member, int order = 0) => new(member.Key, member.DeclaredLength, [Locations[member.Key]], order);
}

internal sealed class Scenario
{
    public const string L3 = "level3.blizzard.com", Us = "us.cdn.blizzard.com";
    public static readonly CdnRoute Route = new([L3, Us], "tpr/wow");
    public FakeCdn Cdn { get; } = new() { Time = new ManualTime(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)) };
    public MemoryStore Store { get; } = new();
    public MemoryStore? Shared { get; init; }
    public ObjectSourceOptions Options { get; init; } = new();
    public CdnContext Context => new(Route, Archives);
    public List<CdnKey> Archives { get; } = [];

    private ObjectSource? source;
    public ObjectSource Source => source ??= new ObjectSource(Store, Store, Store, Shared, new ScriptedCdnClient(Cdn),
        [new VersionServiceObservations(new VersionClient(new HttpClient(Versions))), new WoWDBDefsObservations(Definitions)], ObjectVerification.Default, Options);
    public VersionHandler Versions { get; } = new();
    public DefinitionClient Definitions { get; } = new();

    public static CdnKey KeyOf(ObjectKey key) => new(key.KeyBytes());

    public PutObject Serve(PutObject o)
    {
        var kind = o.Key.Type switch { ObjectKeyType.Config => CdnObjectKind.Config, ObjectKeyType.Encoded => CdnObjectKind.Data, _ => CdnObjectKind.Index };
        Cdn.Add(kind, KeyOf(o.Key), o.Bytes.ToArray());
        return o;
    }

    public SyntheticArchive Archive(int seed, IReadOnlyList<PutObject> members, int gap = 0)
    {
        var key = new CdnKey(Synthetic.Random(seed + 7_000_000, 16));
        using var bytes = new MemoryStream();
        var locations = new Dictionary<ObjectKey, ArchiveLocation>();
        foreach (var m in members)
        {
            locations[m.Key] = new(key, bytes.Length, m.DeclaredLength);
            bytes.Write(m.Bytes.Span);
            bytes.Write(new byte[gap]);
        }
        Cdn.Add(CdnObjectKind.Data, key, bytes.ToArray());
        Archives.Add(key);
        return new(key, bytes.ToArray(), locations);
    }

    /// <summary>Requests the fake CDN saw for data objects (archives and loose encoded objects).</summary>
    public int DataRequests => Cdn.Requests.Count(r => r.Kind == CdnObjectKind.Data);

    /// <summary>A version service that answers every product with a small synthetic table.</summary>
    internal sealed class VersionHandler : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            var content = new ByteArrayContent("Region!STRING:0|BuildConfig!HEX:16\n## seqn = 1\nus|00000000000000000000000000000001\n"u8.ToArray());
            content.Headers.ContentType = new("text/plain");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content, RequestMessage = request });
        }
    }

    /// <summary>A WoWDBDefs client with scripted replies and failures per table name; it counts the fetches it was asked for.</summary>
    internal sealed class DefinitionClient : IWoWDBDefsClient
    {
        private readonly Dictionary<string, (string Revision, byte[] Bytes)> replies = [];
        private readonly Dictionary<string, CdnFailure> failures = [];
        public DateTimeOffset At { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        public List<string> Fetched { get; } = [];
        public DefinitionClient Add(string tableName, string revision, byte[] bytes) { replies[tableName] = (revision, bytes); return this; }
        public DefinitionClient Fail(string tableName, CdnFailure failure) { failures[tableName] = failure; return this; }

        public Task<WoWDBDefsReply> FetchAsync(string tableName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Fetched) Fetched.Add(tableName);
            if (replies.TryGetValue(tableName, out var r))
                return Task.FromResult(new WoWDBDefsReply(tableName, r.Revision, new Uri(WoWDBDefsClient.DefinitionUrl(r.Revision, tableName)), At, r.Bytes));
            var failure = failures.GetValueOrDefault(tableName, CdnFailure.NotFound);
            var url = WoWDBDefsClient.CommitsUrl(tableName);
            throw new CdnException([new CdnAttempt(url.Host, url, failure, null, At, $"Scripted {failure} for {tableName}.")]);
        }
    }
}
