using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using VibeMiner.Cdn;

namespace VibeMiner.Cdn.Testing;

internal sealed partial class FakeCdnHandler(FakeCdn cdn, FetchLabel label) : HttpMessageHandler
{
    [GeneratedRegex("^/[a-z0-9_/-]+?/(config|data)/[0-9a-f]{2}/[0-9a-f]{2}/([0-9a-f]{32})(\\.index)?$")]
    private static partial Regex Path();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var match = Path().Match(request.RequestUri!.AbsolutePath);
        if (!match.Success) return new(HttpStatusCode.BadRequest) { RequestMessage = request };
        var kind = match.Groups[1].Value == "config" ? CdnObjectKind.Config : match.Groups[3].Success ? CdnObjectKind.Index : CdnObjectKind.Data;
        var range = request.Headers.Range;
        if (range is not null && range.Ranges.Count != 1) throw new InvalidOperationException("The fake CDN never accepts a multi-range request.");
        var one = range?.Ranges.Single();
        var answer = await cdn.AnswerAsync(request.RequestUri.Host, kind, CdnKey.Parse(match.Groups[2].Value),
            one?.From, one is null ? null : one.To - one.From + 1, label, cancellationToken);
        HttpContent content = answer.BreakAfter is { } cut ? new BreakingContent(answer.Body, cut) : new ByteArrayContent(answer.Body);
        content.Headers.ContentType = new MediaTypeHeaderValue(answer.Status is HttpStatusCode.OK or HttpStatusCode.PartialContent ? "binary/octet-stream" : "text/html");
        if (answer.Status == HttpStatusCode.PartialContent)
            content.Headers.ContentRange = new ContentRangeHeaderValue(answer.RangeFrom!.Value, answer.RangeFrom.Value + answer.Body.Length - 1, answer.Total!.Value);
        return new(answer.Status) { Content = content, RequestMessage = request, Version = answer.Version };
    }
}

/// <summary>A body that delivers its first bytes, then fails as a dropped connection does.</summary>
internal sealed class BreakingContent(byte[] body, int cut) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new IOException("Connection reset (fake).");
    protected override bool TryComputeLength(out long length) { length = body.Length; return true; }
    protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Breaking(body, cut));
    private sealed class Breaking(byte[] body, int cut) : MemoryStream(body, false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= cut) throw new IOException("Connection reset (fake).");
            return await base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, cut - Position)], cancellationToken);
        }
    }
}

/// <summary>
/// The standard <see cref="ICdnClient"/> test double: answers from a <see cref="FakeCdn"/> with no HTTP stack, following the
/// same rules as <see cref="CdnClient"/> (hosts in order, 200 to a range aborted, exact sizes, checks fail one host).
/// </summary>
public sealed class ScriptedCdnClient(FakeCdn cdn, FetchLabel label = default) : ICdnClient
{
    public Task<CdnReply> FetchAsync(CdnRoute route, CdnRequest request, CancellationToken cancellationToken) =>
        RunAsync(route, request, null, cancellationToken);

    public Task<CdnReply> FetchToAsync(CdnRoute route, CdnRequest request, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sink, CancellationToken cancellationToken) =>
        RunAsync(route, request, sink, cancellationToken);

    private async Task<CdnReply> RunAsync(CdnRoute route, CdnRequest request, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? sink, CancellationToken token)
    {
        var failed = new List<CdnAttempt>();
        foreach (var host in route.Hosts)
        {
            var url = route.UrlFor(host, request.Kind, request.Key);
            var clock = Stopwatch.StartNew();
            CdnAttempt Fail(CdnFailure f, string m, HttpStatusCode? s = null, string? code = null) => new(host, url, f, s, cdn.Time.GetUtcNow(), m, code);
            FakeCdn.Answer answer;
            try { answer = await cdn.AnswerAsync(host, request.Kind, request.Key, request.Offset, request.IsRange ? request.Length : null, label, token); }
            catch (HttpRequestException) { failed.Add(Fail(CdnFailure.Offline, "The CDN host could not be reached.")); continue; }
            var observed = cdn.Time.GetUtcNow();
            if (answer.Status == HttpStatusCode.NotFound) { failed.Add(Fail(CdnFailure.NotFound, "HTTP 404.", answer.Status)); continue; }
            if (request.IsRange && answer.Status == HttpStatusCode.OK) { failed.Add(Fail(CdnFailure.RangeIgnored, "200 to a range; aborted.", answer.Status)); continue; }
            if (answer.Status is not (HttpStatusCode.OK or HttpStatusCode.PartialContent)) { failed.Add(Fail(CdnFailure.HttpStatus, $"HTTP {(int)answer.Status}.", answer.Status)); continue; }
            if (request.IsRange && request.ExpectedTotal is { } total && answer.Total != total)
            { failed.Add(Fail(CdnFailure.SizeConflict, "Range total differs.", answer.Status)); continue; }
            if (!request.IsRange && request.Length is { } expected && answer.Body.Length != expected)
            { failed.Add(Fail(CdnFailure.SizeConflict, "Length differs.", answer.Status)); continue; }
            if (!request.IsRange && request.MaxLength is { } max && answer.Body.Length > max)
            { failed.Add(Fail(CdnFailure.TooLarge, $"The reply is over the {max} byte limit.", answer.Status)); continue; }
            if (sink is not null)
            {
                var end = answer.BreakAfter ?? answer.Body.Length;
                for (var at = 0; at < end; at += 65_536)
                    await sink(answer.Body.AsMemory(at, Math.Min(65_536, end - at)), token);
                if (answer.BreakAfter is not null) { failed.Add(Fail(CdnFailure.Network, "The CDN connection failed.")); throw new CdnException(failed); }
                return new(host, url, answer.Version, answer.Body.Length, answer.Total, null, observed, clock.Elapsed, clock.Elapsed, failed.ToArray());
            }
            if (answer.BreakAfter is not null) { failed.Add(Fail(CdnFailure.Network, "The CDN connection failed.")); continue; }
            if (request.Check is { } check && check(answer.Body) is { } refusal)
            { failed.Add(Fail(CdnFailure.VerificationFailed, "The bytes don't pass their key's check.", answer.Status, refusal)); continue; }
            return new(host, url, answer.Version, answer.Body.Length, answer.Total, answer.Body, observed, clock.Elapsed, clock.Elapsed, failed.ToArray());
        }
        throw new CdnException(failed);
    }
}
