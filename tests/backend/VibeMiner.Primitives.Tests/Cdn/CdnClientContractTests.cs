using System.Security.Cryptography;
using VibeMiner.Cdn.Testing;
using Xunit;

namespace VibeMiner.Cdn.Tests;

/// <summary>
/// One behaviour suite for every <see cref="ICdnClient"/>: the real client over the fake CDN's HTTP front end, and the scripted
/// client layer 2 tests use. If the double drifts from the real client, this suite fails for one of them.
/// </summary>
public abstract class CdnClientContract
{
    protected const string L3 = "level3.blizzard.com", Us = "us.cdn.blizzard.com";
    protected static readonly CdnRoute Route = new([L3, Us], "tpr/wow");
    protected readonly FakeCdn Cdn = new() { Time = new ManualTime(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)) };
    protected abstract ICdnClient Client { get; }

    private static byte[] Bytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }
    private static CdnKey KeyOf(byte[] b) => new(MD5.HashData(b));
    private static CdnCheck Md5(CdnKey key) => b => new CdnKey(MD5.HashData(b)) == key ? null : "OBJECT_KEY_MISMATCH";

    [Fact]
    public async Task WholeAndRangeFetchesReturnTheExactBytes()
    {
        var body = Bytes(200_000, 1); var key = KeyOf(body);
        Cdn.Add(CdnObjectKind.Data, key, body).Http2(L3);
        var whole = await Client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, key, body.Length, Md5(key)), CancellationToken.None);
        Assert.Equal(body, whole.Bytes);
        Assert.Equal(L3, whole.Host);
        Assert.Equal(new Version(2, 0), whole.HttpVersion);
        var range = await Client.FetchAsync(Route, CdnRequest.Range(CdnObjectKind.Data, key, 1000, 5000, body.Length), CancellationToken.None);
        Assert.Equal(body.AsSpan(1000, 5000).ToArray(), range.Bytes);
        Assert.Equal(body.Length, range.Total);
        Assert.Equal([(L3, (long?)null, (long?)null), (L3, 1000, 5000)], Cdn.Requests.Select(r => (r.Host, r.Offset, r.Length)));
    }

    [Fact]
    public async Task MissingEverywhereIsADatedNotFound()
    {
        var ex = await Assert.ThrowsAsync<CdnException>(() => Client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Config, KeyOf([1])), CancellationToken.None));
        Assert.Equal(CdnFailure.NotFound, ex.Overall);
        Assert.True(ex.Retryable);
        Assert.Equal(Cdn.Time.GetUtcNow(), ex.ObservedAt);
        Assert.Equal(2, ex.Attempts.Count);
    }

    [Fact]
    public async Task AWholeReplyOverItsMaxLengthIsTooLarge()
    {
        var body = Bytes(5000, 3); var key = KeyOf(body);
        Cdn.Add(CdnObjectKind.Config, key, body);
        var ex = await Assert.ThrowsAsync<CdnException>(() => Client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Config, key, maxLength: 4999), CancellationToken.None));
        Assert.Equal(CdnFailure.TooLarge, ex.Overall);
        Assert.Equal(body, (await Client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Config, key, maxLength: 5000), CancellationToken.None)).Bytes);
    }

    [Fact]
    public async Task OfflineEverywhereIsOffline()
    {
        Cdn.SetMode(L3, FakeHostMode.Offline).SetMode(Us, FakeHostMode.Offline);
        var ex = await Assert.ThrowsAsync<CdnException>(() => Client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, KeyOf([1])), CancellationToken.None));
        Assert.Equal(CdnFailure.Offline, ex.Overall);
    }

    [Fact]
    public async Task TheNextHostServesWhatTheFirstLacksIgnoresOrCorrupts()
    {
        var body = Bytes(4096, 2); var key = KeyOf(body);
        Cdn.Add(CdnObjectKind.Index, key, body).MissingOn(L3, CdnObjectKind.Index, key);
        var a = await Client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Index, key, 4096, Md5(key)), CancellationToken.None);
        Assert.Equal((Us, CdnFailure.NotFound), (a.Host, Assert.Single(a.Failed).Failure));

        var other = Bytes(4096, 3); var otherKey = KeyOf(other);
        Cdn.Add(CdnObjectKind.Data, otherKey, other).CorruptOn(L3, CdnObjectKind.Data, otherKey);
        var b = await Client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, otherKey, 4096, Md5(otherKey)), CancellationToken.None);
        Assert.Equal(other, b.Bytes);
        Assert.Equal(("OBJECT_KEY_MISMATCH", CdnFailure.VerificationFailed), (Assert.Single(b.Failed).Code, b.Failed[0].Failure));

        Cdn.SetMode(L3, FakeHostMode.IgnoresRanges);
        var c = await Client.FetchAsync(Route, CdnRequest.Range(CdnObjectKind.Data, otherKey, 10, 10), CancellationToken.None);
        Assert.Equal(other.AsSpan(10, 10).ToArray(), c.Bytes);
        Assert.Equal(CdnFailure.RangeIgnored, Assert.Single(c.Failed).Failure);
    }

    [Fact]
    public async Task ARangeThatFailsItsCheckMovesToTheNextHost()
    {
        var part = Bytes(3000, 5); var partKey = KeyOf(part);
        var archive = Bytes(1000, 6).Concat(part).Concat(Bytes(1000, 7)).ToArray(); var archiveKey = KeyOf(archive);
        Cdn.Add(CdnObjectKind.Data, archiveKey, archive).CorruptOn(L3, CdnObjectKind.Data, archiveKey);
        var reply = await Client.FetchAsync(Route, CdnRequest.Range(CdnObjectKind.Data, archiveKey, 1000, 3000, check: Md5(partKey)), CancellationToken.None);
        Assert.Equal(Us, reply.Host);
        Assert.Equal(part, reply.Bytes);
        Assert.Equal(("OBJECT_KEY_MISMATCH", CdnFailure.VerificationFailed), (Assert.Single(reply.Failed).Code, reply.Failed[0].Failure));
    }

    [Fact]
    public async Task UnavailableHostsAreRetryableHttpStatus()
    {
        Cdn.SetMode(L3, FakeHostMode.Unavailable).SetMode(Us, FakeHostMode.Unavailable);
        var ex = await Assert.ThrowsAsync<CdnException>(() => Client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, KeyOf([1])), CancellationToken.None));
        Assert.Equal(CdnFailure.HttpStatus, ex.Overall);
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task AWrongDeclaredLengthIsASizeConflict()
    {
        var body = Bytes(100, 4); var key = KeyOf(body);
        Cdn.Add(CdnObjectKind.Data, key, body);
        var ex = await Assert.ThrowsAsync<CdnException>(() => Client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, key, 101), CancellationToken.None));
        Assert.Equal(CdnFailure.SizeConflict, ex.Overall);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task StreamedFetchesDeliverEveryByteInOrder()
    {
        var body = Bytes(300_001, 5); var key = KeyOf(body);
        Cdn.Add(CdnObjectKind.Data, key, body).MissingOn(L3, CdnObjectKind.Data, key);
        using var sink = new MemoryStream();
        var reply = await Client.FetchToAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, key, body.Length), (b, t) => sink.WriteAsync(b, t), CancellationToken.None);
        Assert.Equal(body, sink.ToArray());
        Assert.Equal((Us, 300_001L), (reply.Host, reply.Length));
    }

    [Fact]
    public async Task CancellationIsNotACdnFailure()
    {
        var release = new TaskCompletionSource();
        Cdn.BeforeAnswer = (_, token) => release.Task.WaitAsync(token);
        using var cancel = new CancellationTokenSource();
        var pending = Client.FetchAsync(Route, CdnRequest.Whole(CdnObjectKind.Data, KeyOf([1])), cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }
}

public sealed class RealCdnClientContractTests : CdnClientContract
{
    private readonly CdnClient client;
    public RealCdnClientContractTests() => client = new CdnClient(new HttpClient(Cdn.Handler()), new CdnClientOptions { Time = Cdn.Time });
    protected override ICdnClient Client => client;
}

public sealed class ScriptedCdnClientContractTests : CdnClientContract
{
    private readonly ScriptedCdnClient client;
    public ScriptedCdnClientContractTests() => client = new ScriptedCdnClient(Cdn);
    protected override ICdnClient Client => client;
}
