using System.Net;
using VibeMiner.Infrastructure;
using VibeMiner.Domain;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class AcquisitionTests
{
    [Fact]
    public async Task OneOfficialPairPreservesAllAdvertisedRegionsAndMissingRoutes()
    {
        var urls = new List<string>();
        using var handler = new Handler((request, _) =>
        {
            lock (urls) urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                request.RequestUri!.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal)
                    ? Fixtures.Versions(Fixtures.Row(), Fixtures.Row("eu"), Fixtures.Row("sg"), Fixtures.Row("us")) : Fixtures.Cdns("us", "eu", "cn")) });
        });
        using var source = new OfficialDiscoverySource(new HttpClient(handler));
        var attempt = await source.FetchAsync("wow", CancellationToken.None);
        Assert.Equal(2, handler.Calls);
        Assert.Contains("https://us.version.battle.net/v2/products/wow/versions", urls);
        Assert.Equal(new[] { "eu", "sg", "us" }, attempt.CheckedRegions);
        Assert.Equal(4, attempt.Observations.Count);
        Assert.Single(attempt.Observations.Select(row => row.Identity.Id).Distinct());
        Assert.Contains(attempt.Errors, error => error.Region == "sg" && error.Code == "ROUTE_MISSING" && error.Severity == DiscoveryProblem.NoteSeverity);
        Assert.Equal(new[] { 0, 1, 2, 3 }, attempt.Observations.Select(row => row.VersionsOrdinal).Order());
        // A region advertised without a route is a regional note; the check is complete (D076).
        Assert.Equal("online", attempt.Connection);
    }
    [Fact]
    public async Task AmbiguousRoutesPreserveAdvertisementWithoutRouting()
    {
        var attempt = await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row()), Fixtures.Cdns("us", "us"));
        Assert.Single(attempt.Observations);
        Assert.Null(attempt.Observations[0].CdnsOrdinal);
        Assert.Contains(attempt.Errors, error => error.Code == "ROUTE_AMBIGUOUS");
        Assert.Contains("us", attempt.SuccessfulRegions);
    }
    [Fact]
    public async Task FailedCdnsDoesNotLoseVersionsAdvertisement()
    {
        using var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(
            request.RequestUri!.AbsolutePath.EndsWith("/versions", StringComparison.Ordinal) ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
            { Content = new StringContent(Fixtures.Versions(Fixtures.Row())) }));
        using var source = new OfficialDiscoverySource(new HttpClient(handler));
        var result = await source.FetchAsync("wow", CancellationToken.None);
        Assert.Single(result.Observations); Assert.Contains(result.Errors, error => error.Code == "HTTP_STATUS");
        Assert.Null(result.Observations[0].CdnsOrdinal);
    }
    [Fact]
    public async Task TransportFailuresHaveSafeExplicitOutcome()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("secret/path/credentials"));
        using var source = new OfficialDiscoverySource(new HttpClient(handler));
        var result = await source.FetchAsync("wow", CancellationToken.None);
        Assert.Equal("offline", result.Connection); Assert.Empty(result.Observations);
        Assert.All(result.Errors, error => Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal));
    }
    [Fact]
    public async Task ChunkedBodyLimitIsEnforcedWhileStreaming()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StreamContent(new MemoryStream(new byte[1024 * 1024 + 1])) }));
        using var source = new OfficialDiscoverySource(new HttpClient(handler));
        var result = await source.FetchAsync("wow", CancellationToken.None);
        Assert.Empty(result.Documents); Assert.All(result.Errors, error => Assert.Equal("METADATA_LIMIT", error.Code));
    }
    [Fact]
    public async Task CancellationWaitsForActualTransportCleanup()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceledInside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { canceledInside.TrySetResult(); await releaseCleanup.Task; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var source = new OfficialDiscoverySource(new HttpClient(handler));
        using var cancel = new CancellationTokenSource();
        var task = source.FetchAsync("wow", cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        await canceledInside.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(task.IsCompleted);
        releaseCleanup.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
    [Fact]
    public async Task HttpStatusFailureDoesNotClaimTheMachineIsOffline()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        using var source = new OfficialDiscoverySource(new HttpClient(handler));
        var result = await source.FetchAsync("wow", CancellationToken.None);
        Assert.Equal("partial", result.Connection); Assert.Empty(result.Observations);
        Assert.All(result.Errors, error => Assert.Equal("HTTP_STATUS", error.Code));
    }
    [Fact]
    public async Task DeclaredContentLengthMismatchIsRefused()
    {
        using var handler = new Handler((_, _) =>
        {
            var content = new StringContent(Fixtures.Versions(Fixtures.Row()));
            content.Headers.ContentLength = content.Headers.ContentLength + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var source = new OfficialDiscoverySource(new HttpClient(handler));
        var result = await source.FetchAsync("wow", CancellationToken.None);
        Assert.Empty(result.Documents); Assert.All(result.Errors, error => Assert.Equal("CONTENT_LENGTH", error.Code));
    }
    [Fact]
    public async Task RegionalIdentityFailurePreservesOtherScope()
    {
        var attempt = await Fixtures.Attempt(Fixtures.Versions(Fixtures.Row(), Fixtures.Row("eu", config: "broken")));
        Assert.Single(attempt.Observations); Assert.Equal("us", attempt.Observations[0].Region);
        Assert.DoesNotContain("eu", attempt.SuccessfulRegions);
        Assert.Contains(attempt.Errors, error => error.Region == "eu" && error.Code == "INVALID_IDENTITY");
    }
}
