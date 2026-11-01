using VibeMiner.Cdn.Testing;
using System.Net;
using System.Text;
using Xunit;

namespace VibeMiner.Cdn.Tests;

public sealed class VersionClientTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 3, 0, 0, TimeSpan.Zero);

    // Synthetic replies in the service's grammar; the keys are made up.
    internal const string Cdns =
        "Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0|ConfigPath!STRING:0\n" +
        "## seqn = 3020098\n" +
        "us|tpr/wow|blzddist1-a.akamaihd.net level3.blizzard.com us.cdn.blizzard.com|http://blzddist1-a.akamaihd.net/?maxhosts=4|tpr/configs/data\n" +
        "eu|tpr/wow|eu.cdn.blizzard.com level3.blizzard.com|https://eu.cdn.blizzard.com/?maxhosts=4|tpr/configs/data\n" +
        "kr|tpr/wow|kr.cdn.blizzard.com|x|tpr/configs/data\n" +
        "kr|tpr/wow|kr.cdn.blizzard.com|y|tpr/configs/data\n" +
        "cn|tpr/wow|cdn.example.cn|z|tpr/configs/data\n";

    private static (VersionClient, ScriptedHandler) Make(Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        var handler = new ScriptedHandler((r, _) => Task.FromResult(answer(r)));
        return (new VersionClient(new HttpClient(handler), new CdnClientOptions { Time = new ManualTime(T0) }), handler);
    }

    [Theory]
    [InlineData(VersionDocumentKind.Versions, "versions")]
    [InlineData(VersionDocumentKind.Cdns, "cdns")]
    [InlineData(VersionDocumentKind.Bgdl, "bgdl")]
    public async Task EachDocumentIsFetchedRawWithItsUrlAndTime(VersionDocumentKind kind, string name)
    {
        var body = Encoding.UTF8.GetBytes(Cdns);
        var (client, handler) = Make(r => Replies.Make(r, HttpStatusCode.OK, body, "text/plain"));
        var reply = await client.FetchAsync("wow", "eu", kind, CancellationToken.None);
        Assert.Equal(body, reply.Bytes);
        Assert.Equal(T0, reply.ObservedAt);
        Assert.Equal($"https://eu.version.battle.net/v2/products/wow/{name}", reply.Url.AbsoluteUri);
        Assert.Equal(reply.Url, handler.Requests.Single().RequestUri);
        Assert.Equal(kind, reply.Kind);
    }

    [Fact]
    public async Task TheSummaryIsFetchedFromTheSameHostWithNoProduct()
    {
        var body = "Product!STRING:0|Seqn!DEC:4|Flags!STRING:0\n## seqn = 9\nwow|8|\nwow|7|cdn\n"u8.ToArray();
        var (client, handler) = Make(r => Replies.Make(r, HttpStatusCode.OK, body, "text/plain"));
        var reply = await client.FetchAsync("", "us", VersionDocumentKind.Summary, CancellationToken.None);
        Assert.Equal("https://us.version.battle.net/v2/summary", reply.Url.AbsoluteUri);
        Assert.Equal(reply.Url, handler.Requests.Single().RequestUri);
        Assert.Equal(body, reply.Bytes);
        Assert.Equal(("", VersionDocumentKind.Summary), (reply.Product, reply.Kind));
        Assert.Throws<ArgumentException>(() => VersionClient.UrlFor("wow", "us", VersionDocumentKind.Summary));
        Assert.Throws<ArgumentException>(() => VersionClient.UrlFor("", "us", VersionDocumentKind.Versions));
        Assert.Throws<ArgumentException>(() => VersionClient.UrlFor("", "xx", VersionDocumentKind.Summary));
    }

    [Fact]
    public async Task FailuresAreTyped()
    {
        async Task<CdnFailure> Fail(Func<HttpRequestMessage, HttpResponseMessage> answer)
        {
            var (client, _) = Make(answer);
            return (await Assert.ThrowsAsync<CdnException>(() => client.FetchAsync("wow", "us", VersionDocumentKind.Versions, CancellationToken.None))).Overall;
        }
        Assert.Equal(CdnFailure.NotFound, await Fail(r => Replies.Make(r, HttpStatusCode.NotFound, [], "text/plain")));
        Assert.Equal(CdnFailure.HttpStatus, await Fail(r => Replies.Make(r, HttpStatusCode.ServiceUnavailable, [1], "text/plain")));
        Assert.Equal(CdnFailure.Protocol, await Fail(r => Replies.Make(r, HttpStatusCode.OK, [1], "text/html")));
        Assert.Equal(CdnFailure.Protocol, await Fail(r => { var m = Replies.Make(r, HttpStatusCode.MovedPermanently, [], "text/plain"); m.Headers.Location = new Uri("https://x/"); return m; }));
        Assert.Equal(CdnFailure.TooLarge, await Fail(r => Replies.Make(r, HttpStatusCode.OK, new byte[VersionClient.MaxBytes + 1], "text/plain")));
        Assert.Equal(CdnFailure.SizeConflict, await Fail(r => Replies.Make(r, HttpStatusCode.OK, [], "text/plain")));
        Assert.Equal(CdnFailure.Protocol, await Fail(r => { var m = Replies.Make(r, HttpStatusCode.OK, [1], "text/plain"); m.Content.Headers.ContentEncoding.Add("gzip"); return m; }));

        var offline = new VersionClient(new HttpClient(new ScriptedHandler((_, _) => throw new HttpRequestException(HttpRequestError.NameResolutionError, "dns"))));
        Assert.Equal(CdnFailure.Offline, (await Assert.ThrowsAsync<CdnException>(() => offline.FetchAsync("wow", "us", VersionDocumentKind.Cdns, CancellationToken.None))).Overall);
    }

    [Fact]
    public void ProductsAndRegionsAreChecked()
    {
        Assert.Throws<ArgumentException>(() => VersionClient.UrlFor("../wow", "us", VersionDocumentKind.Versions));
        Assert.Throws<ArgumentException>(() => VersionClient.UrlFor("wow", "xx", VersionDocumentKind.Versions));
        Assert.Throws<ArgumentException>(() => VersionClient.UrlFor("WOW", "us", VersionDocumentKind.Versions));
    }

    [Fact]
    public void ACdnsTableGivesOneOfficialRoutePerRegion()
    {
        var table = VersionTable.Parse(Encoding.UTF8.GetBytes(Cdns));
        Assert.Equal(3020098, table.Sequence);
        var routes = CdnsRoutes.Read(table, out var unusable);
        Assert.Equal(["eu", "us"], routes.Keys);
        Assert.Equal(["level3.blizzard.com", "us.cdn.blizzard.com"], routes["us"].Hosts);
        Assert.Equal("tpr/wow", routes["us"].Path);
        Assert.Equal(["eu.cdn.blizzard.com", "level3.blizzard.com"], routes["eu"].Hosts);
        Assert.Equal("ROUTE_AMBIGUOUS", unusable["kr"]);
        Assert.Equal("ROUTE_INVALID", unusable["cn"]);
    }

    [Theory]
    [InlineData("A!STRING:0|B!STRING:0\nx\n")]          // wrong cell count
    [InlineData("A!STRING:0|A!STRING:0\n")]             // duplicate column
    [InlineData("A-STRING:0\n")]                        // bad descriptor
    [InlineData("## seqn = 1\n## seqn = 2\nA!STRING:0\n")]
    [InlineData("A!STRING:0\rx\n")]
    [InlineData("# only a comment\n")]
    [InlineData("A!STRING:0\nx\0\n")]
    public void BrokenTablesAreRefused(string text) =>
        Assert.Throws<FormatException>(() => VersionTable.Parse(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void NonUtf8IsRefusedAndCrlfIsAccepted()
    {
        Assert.Throws<FormatException>(() => VersionTable.Parse([0x41, 0x21, 0xFF, 0x0A]));
        var t = VersionTable.Parse("A!STRING:0|B!HEX:16\r\nx|y\r\n"u8);
        Assert.Equal("y", t.Rows.Single()["B"]);
        Assert.Null(t.Sequence);
    }
}
