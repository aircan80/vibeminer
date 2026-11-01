using System.Text;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Builds.Tests;

/// <summary>The real opener's layer 2 use: what it asks, how it maps answers, reuse and cancellation (D137: in-memory layer 2 only).</summary>
public sealed class BuildOpenerTests
{
    private const string CdnsHeader = "Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0|ConfigPath!STRING:0";
    private static CancellationToken Ct => CancellationToken.None;
    private readonly ManualTime time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryObjectSource inner;
    private readonly TestObjectSource source;

    public BuildOpenerTests() { inner = new InMemoryObjectSource { Time = time }; source = new TestObjectSource(inner); }

    private async Task<IBuild> OpenedAsync(IBuilds builds, BuildRef build)
    {
        var result = await builds.OpenAsync(build, Ct);
        Assert.True(result.State == BuildOpenState.Open, $"{result.State} {result.Part} {result.Code}");
        return result.Opened!;
    }

    [Fact]
    public async Task StoredMetadataOpensWithTheNetworkOff()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        source.Offline = true;
        var opened = await OpenedAsync(new BuildOpener(source), build.Ref);
        Assert.Equal(FileResolutionState.Found, opened.Resolve(200).State);
        Assert.Null(opened.Cdn);
    }

    [Fact]
    public async Task NeverFetchedOfflineMakesOneObjectCall()
    {
        var build = new SyntheticBuild();
        source.Offline = true;
        var result = await new BuildOpener(source).OpenAsync(build.Ref, Ct);
        Assert.Equal(BuildOpenState.NeedsNetwork, result.State);
        Assert.Equal(1, source.ObjectCalls);
        Assert.Equal(["ReadHistoryAsync", "GetKeysAsync:Config"], source.Calls);
    }

    [Fact]
    public async Task SecondOpenMakesNoCalls()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        var builds = new BuildOpener(source);
        var first = await OpenedAsync(builds, build.Ref);
        source.Reset();
        Assert.Same(first, await OpenedAsync(builds, build.Ref));
        Assert.Empty(source.Calls);
    }

    [Fact]
    public async Task OpenReadsEveryPartAtInteractivePriority()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        await OpenedAsync(new BuildOpener(source), build.Ref);
        Assert.Equal(["ReadHistoryAsync", "GetKeysAsync:Config", "GetKeyAsync:Config", "GetKeyAsync:Config", "GetKeyAsync:Encoded", "GetKeyAsync:Encoded",
            "GetKeysAsync:ArchiveIndex"], source.Calls.Take(7));
        Assert.Equal(3, source.Count("GetKeyAsync:ArchiveIndex") + source.Count("GetKeyAsync:FileIndex"));
        Assert.All(inner.Asked, a => Assert.Equal(FetchPriority.Interactive, a.Priority));
    }

    [Fact]
    public async Task CancellingMidOpenCachesNothing()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        var builds = new BuildOpener(source);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.BeforeAnswer = async (key, ct) => { if (key == build.RootKey) { reached.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        using var cts = new CancellationTokenSource();
        var open = builds.OpenAsync(build.Ref, cts.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        inner.BeforeAnswer = null;
        source.Reset();
        await OpenedAsync(builds, build.Ref);
        Assert.Equal(1, source.Count("GetKeysAsync:Config"));
        Assert.Equal(2, source.Count("GetKeyAsync:Encoded"));
    }

    [Fact]
    public async Task OneWaiterCancellingLeavesTheOther()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        var builds = new BuildOpener(source);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.BeforeAnswer = async (key, ct) => { if (key == build.RootKey) { reached.TrySetResult(); await release.Task.WaitAsync(ct); } };
        using var cts = new CancellationTokenSource();
        var cancelled = builds.OpenAsync(build.Ref, cts.Token);
        var kept = builds.OpenAsync(build.Ref, Ct);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        release.SetResult();
        Assert.Equal(BuildOpenState.Open, (await kept).State);
        Assert.Equal(1, source.Count("GetKeysAsync:Config"));
    }

    [Fact]
    public async Task EveryWaiterCancellingCancelsTheSharedOpen()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        var builds = new BuildOpener(source);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.BeforeAnswer = async (key, ct) =>
        {
            if (key != build.RootKey) return;
            reached.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); } finally { stopped.TrySetResult(); }
        };
        using var a = new CancellationTokenSource();
        using var b = new CancellationTokenSource();
        var first = builds.OpenAsync(build.Ref, a.Token);
        var second = builds.OpenAsync(build.Ref, b.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        a.Cancel();
        Assert.False(stopped.Task.IsCompleted);
        b.Cancel();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }

    [Fact]
    public async Task CacheDropsTheLeastRecentlyOpened()
    {
        var one = new SyntheticBuild(buildName: "one").LoadInto(inner);
        var two = new SyntheticBuild(buildName: "two").LoadInto(inner);
        var builds = new BuildOpener(source, new BuildOpenerOptions { MaxOpenBuilds = 1 });
        var first = await OpenedAsync(builds, one.Ref);
        await OpenedAsync(builds, two.Ref);
        source.Reset();
        Assert.NotSame(first, await OpenedAsync(builds, one.Ref));
        Assert.Equal(1, source.Count("GetKeysAsync:Config"));
    }

    [Fact]
    public async Task RegionIsPartOfTheCacheKey()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        var builds = new BuildOpener(source);
        var plain = await OpenedAsync(builds, build.Ref);
        Assert.NotSame(plain, await OpenedAsync(builds, new BuildRef("wow", build.Ref.BuildConfig, build.Ref.CdnConfig, "us")));
        Assert.Same(plain, await OpenedAsync(builds, new BuildRef("wow", build.Ref.BuildConfig.ToUpperInvariant(), build.Ref.CdnConfig)));
    }

    public static TheoryData<ObjectState, string, BuildOpenState> States => new()
    {
        { ObjectState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK", BuildOpenState.NeedsNetwork },
        { ObjectState.Unavailable, "CDN_OFFLINE", BuildOpenState.NeedsNetwork },
        { ObjectState.Unavailable, "OBJECT_ACCESS_NO_CDN_CONTEXT", BuildOpenState.NeedsNetwork },
        { ObjectState.Unavailable, "CDN_TIMEOUT", BuildOpenState.Unavailable },
        { ObjectState.NotFound, "CDN_NOT_FOUND", BuildOpenState.Missing },
        { ObjectState.Refused, "OBJECT_SIZE_MISMATCH", BuildOpenState.Corrupt },
        { ObjectState.Unsupported, "OBJECT_LOCATION_TOO_LARGE", BuildOpenState.Unsupported },
        { ObjectState.StoreFailed, "OBJECT_STORE_DISK_FULL", BuildOpenState.StoreFailed },
    };

    [Theory]
    [MemberData(nameof(States))]
    public async Task LayerTwoStatesMapPerPart(ObjectState state, string code, BuildOpenState expected)
    {
        var build = new SyntheticBuild().LoadInto(inner);
        var observed = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var parts = new (ObjectKey Key, BuildPart Part)[]
        {
            (build.BuildConfigKey, BuildPart.BuildConfig), (build.CdnConfigKey, BuildPart.CdnConfig), (build.EncodingKey, BuildPart.Encoding),
            (build.RootKey, BuildPart.Root), (build.IndexKeys[1], BuildPart.ArchiveIndex), (build.IndexKeys[^1], BuildPart.FileIndex),
        };
        foreach (var (key, part) in parts)
        {
            source.Override = k => k == key ? new ObjectResult(k, state, Code: code, Message: "scripted", ObservedAt: observed) : null;
            var result = await new BuildOpener(source).OpenAsync(build.Ref, Ct);
            Assert.Equal((expected, (BuildPart?)part, code, "scripted", null), (result.State, result.Part, result.Code, result.Message, result.Opened));
            Assert.Equal(observed, result.ObservedAt);
        }
    }

    [Fact]
    public async Task PresentWithoutBytesIsReadByRange()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        source.WithoutBytes.UnionWith([build.EncodingKey, build.RootKey]);
        var opened = await OpenedAsync(new BuildOpener(source, new BuildOpenerOptions { ReadChunkBytes = 64 }), build.Ref);
        Assert.Equal(FileResolutionState.Found, opened.Resolve(100).State);
        var expected = (build.Objects[build.EncodingKey].Length + 63) / 64 + (build.Objects[build.RootKey].Length + 63) / 64;
        Assert.Equal(expected, source.Count("GetRangeAsync:Encoded"));
    }

    [Fact]
    public async Task OverTheByteLimitIsUnsupported()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        var result = await new BuildOpener(source, new BuildOpenerOptions { MaxDecodedBytes = 1024 }).OpenAsync(build.Ref, Ct);
        Assert.Equal((BuildOpenState.Unsupported, (BuildPart?)BuildPart.Encoding, "BUILD_METADATA_TOO_LARGE"), (result.State, result.Part, result.Code));
    }

    [Fact]
    public async Task RootMissingFromEncodingIsCorrupt()
    {
        var build = new SyntheticBuild(rootInEncoding: false).LoadInto(inner);
        var result = await new BuildOpener(source).OpenAsync(build.Ref, Ct);
        Assert.Equal((BuildOpenState.Corrupt, (BuildPart?)BuildPart.Root, "ROOT_NOT_IN_ENCODING"), (result.State, result.Part, result.Code));
    }

    [Fact]
    public async Task ABadIndexFailsTheOpen()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        var index = build.IndexKeys[0];
        var bytes = build.Objects[index].ToArray();
        bytes[0] ^= 0xff;
        inner.Add(index, bytes);
        var result = await new BuildOpener(source).OpenAsync(build.Ref, Ct);
        Assert.Equal((BuildOpenState.Corrupt, (BuildPart?)BuildPart.ArchiveIndex), (result.State, result.Part));
        Assert.StartsWith("ARCHIVE_INDEX_", result.Code);
        source.Reset();
        inner.Add(index, build.Objects[index]);
        await OpenedAsync(new BuildOpener(source), build.Ref);
    }

    private Task ObserveCdns(string rows, string seqn = "1") => ObserveRaw(Encoding.UTF8.GetBytes($"{CdnsHeader}\n## seqn = {seqn}\n{rows}"));

    private async Task ObserveRaw(byte[] reply)
    {
        inner.AddReply("wow", "us", ObservationKind.Cdns, reply);
        Assert.Equal(ObjectState.Present, (await inner.ObserveAsync(new(ObservationKind.Cdns, "wow", "us"), Ct)).State);
        time.Advance(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task RouteComesFromTheNewestCdnsEntry()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        await ObserveCdns("us|tpr/old|us.cdn.blizzard.com|x|tpr/configs/data\n");
        await ObserveCdns("eu|tpr/wow|eu.cdn.blizzard.com|x|tpr/configs/data\nus|tpr/wow|level3.blizzard.com us.cdn.blizzard.com|x|tpr/configs/data\n");
        var opened = await OpenedAsync(new BuildOpener(source), build.Ref);
        Assert.Equal("tpr/wow", opened.Cdn!.Route.Path);
        Assert.Equal(["level3.blizzard.com", "us.cdn.blizzard.com"], opened.Cdn.Route.Hosts);
        Assert.Equal(build.Archives, opened.Cdn.Archives);
        var calls = source.Calls;
        var contexts = source.Contexts;
        var configs = contexts[calls.ToList().IndexOf("GetKeysAsync:Config")];
        Assert.Equal((opened.Cdn.Route, null), (configs!.Route, configs.Archives));
        Assert.Same(opened.Cdn.Archives, contexts[calls.ToList().IndexOf("GetKeysAsync:ArchiveIndex")]!.Archives);

        var eu = await OpenedAsync(new BuildOpener(source), new BuildRef("wow", build.Ref.BuildConfig, build.Ref.CdnConfig, "eu"));
        Assert.Equal(["eu.cdn.blizzard.com"], eu.Cdn!.Route.Hosts);
        var none = await OpenedAsync(new BuildOpener(source), new BuildRef("wow", build.Ref.BuildConfig, build.Ref.CdnConfig, "kr"));
        Assert.Null(none.Cdn);
    }

    [Fact]
    public async Task NoUsRowTakesAnyAndABadRowFallsBackToAnOlderReply()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        await ObserveCdns("eu|tpr/wow|eu.cdn.blizzard.com|x|tpr/configs/data\n");
        Assert.Equal(["eu.cdn.blizzard.com"], (await OpenedAsync(new BuildOpener(source), build.Ref)).Cdn!.Route.Hosts);
        await ObserveCdns("us|tpr/wow|cdn.example.invalid|x|tpr/configs/data\n");
        Assert.Equal(["eu.cdn.blizzard.com"], (await OpenedAsync(new BuildOpener(source), build.Ref)).Cdn!.Route.Hosts);
        Assert.Null((await OpenedAsync(new BuildOpener(source), new BuildRef("wow", build.Ref.BuildConfig, build.Ref.CdnConfig, "us"))).Cdn);
    }

    [Fact]
    public async Task AnUnreadableNewestReplyFallsBackToAnOlderOne()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        await ObserveCdns("us|tpr/wow|us.cdn.blizzard.com|x|tpr/configs/data\n");
        await ObserveRaw(Encoding.UTF8.GetBytes("not a table\n"));
        Assert.Equal(["us.cdn.blizzard.com"], (await OpenedAsync(new BuildOpener(source), build.Ref)).Cdn!.Route.Hosts);
    }

    [Fact]
    public async Task HighestSeqnWinsOverLaterObserved()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        await ObserveCdns("us|tpr/wow|level3.blizzard.com|x|tpr/configs/data\n", seqn: "20");
        await ObserveCdns("us|tpr/wow|us.cdn.blizzard.com|x|tpr/configs/data\n", seqn: "9");
        await ObserveRaw(Encoding.UTF8.GetBytes($"{CdnsHeader}\nus|tpr/wow|eu.cdn.blizzard.com|x|tpr/configs/data\n"));
        Assert.Equal(["level3.blizzard.com"], (await OpenedAsync(new BuildOpener(source), build.Ref)).Cdn!.Route.Hosts);
    }

    [Fact]
    public async Task OnlyTheMostRecentRepliesAreRead()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        for (var i = 0; i < BuildOpener.MaxCdnsReplies + 5; i++) await ObserveCdns($"us|tpr/wow|us.cdn.blizzard.com|x|tpr/configs/data\n", seqn: $"{i}");
        await OpenedAsync(new BuildOpener(source), build.Ref);
        Assert.Equal(BuildOpener.MaxCdnsReplies, source.Count("GetKeyAsync:HistoryDocument"));
    }

    private static CatalogBuild Listed(params (string Region, bool Current)[] regions)
    {
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var record = new ObjectKey(ObjectKeyType.HistoryDocument, new byte[16]);
        return new("wow", "id", "1.0.0.1", new string('a', 32), null, new Dictionary<string, string>(), at, at, true,
            [.. regions.Select(r => new BuildRegion(r.Region, new string('b', 32), at, at, r.Current, record))]);
    }

    [Fact]
    public async Task ARegionWithOnlyAnUnusableHostIsSkipped()
    {
        // cn sorts first and lists only a host CdnRoute doesn't admit (the QA case on #175); us is usable.
        await ObserveCdns("cn|tpr/wow|blzdist-wow.necdn.leihuo.netease.com|x|tpr/configs/data\nus|tpr/wow|us.cdn.blizzard.com|x|tpr/configs/data\n");
        var choice = await BuildRegions.ChooseAsync(source, Listed(("cn", true), ("eu", true), ("us", true)), cancellationToken: Ct);
        Assert.Equal(("us", true), (choice.Region!.Region, choice.Routed));
        // Without us listed, the first usable region by name: eu has no cdns row at all, kr does.
        await ObserveCdns("cn|tpr/wow|blzdist-wow.necdn.leihuo.netease.com|x|tpr/configs/data\nkr|tpr/wow|kr.cdn.blizzard.com|x|tpr/configs/data\n");
        choice = await BuildRegions.ChooseAsync(source, Listed(("cn", true), ("eu", true), ("kr", true)), cancellationToken: Ct);
        Assert.Equal(("kr", true), (choice.Region!.Region, choice.Routed));
    }

    [Fact]
    public async Task ThePreferredRegionComesFirstAndCurrentListingsBeforeOldOnes()
    {
        await ObserveCdns("eu|tpr/wow|eu.cdn.blizzard.com|x|tpr/configs/data\nus|tpr/wow|us.cdn.blizzard.com|x|tpr/configs/data\n");
        Assert.Equal("eu", (await BuildRegions.ChooseAsync(source, Listed(("eu", true), ("us", true)), "eu", Ct)).Region!.Region);
        Assert.Equal("us", (await BuildRegions.ChooseAsync(source, Listed(("eu", true), ("us", true)), cancellationToken: Ct)).Region!.Region);
        Assert.Equal("eu", (await BuildRegions.ChooseAsync(source, Listed(("eu", true), ("us", false)), cancellationToken: Ct)).Region!.Region);
    }

    [Fact]
    public async Task NoUsableRegionSaysSoAndStillNamesOne()
    {
        await ObserveCdns("cn|tpr/wow|blzdist-wow.necdn.leihuo.netease.com|x|tpr/configs/data\n");
        var choice = await BuildRegions.ChooseAsync(source, Listed(("cn", true), ("us", true)), cancellationToken: Ct);
        Assert.Equal(("us", false), (choice.Region!.Region, choice.Routed));
        Assert.Equal(new BuildRegionChoice(null, false), await BuildRegions.ChooseAsync(source, Listed(), cancellationToken: Ct));
    }

    [Fact]
    public async Task ANullRegionTakesAUsableRowOverAnUnusableOneInTheSameReply()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        await ObserveCdns("cn|tpr/wow|blzdist-wow.necdn.leihuo.netease.com|x|tpr/configs/data\neu|tpr/wow|eu.cdn.blizzard.com|x|tpr/configs/data\n");
        Assert.Equal(["eu.cdn.blizzard.com"], (await OpenedAsync(new BuildOpener(source), build.Ref)).Cdn!.Route.Hosts);
    }

    [Fact]
    public async Task AThirdOpenWaitsForASlot()
    {
        var builds = Enumerable.Range(0, 3).Select(i => new SyntheticBuild(buildName: $"b{i}").LoadInto(inner)).ToArray();
        // The three builds share ENCODING and ROOT; only their build configs differ.
        var atRoot = 0;
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.BeforeAnswer = async (key, ct) =>
        {
            if (key != builds[0].RootKey) return;
            if (Interlocked.Increment(ref atRoot) == 2) reached.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var opener = new BuildOpener(source);
        var first = opener.OpenAsync(builds[0].Ref, Ct);
        var second = opener.OpenAsync(builds[1].Ref, Ct);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var third = opener.OpenAsync(builds[2].Ref, Ct);
        await Task.Delay(100);
        Assert.False(third.IsCompleted);
        Assert.DoesNotContain(inner.Asked, a => a.Key == builds[2].BuildConfigKey);
        release.SetResult();
        Assert.All(await Task.WhenAll(first, second, third), r => Assert.Equal(BuildOpenState.Open, r.State));
    }

    [Fact]
    public async Task CancellingAWaitingOpenLeavesNothing()
    {
        var builds = Enumerable.Range(0, 3).Select(i => new SyntheticBuild(buildName: $"b{i}").LoadInto(inner)).ToArray();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.BeforeAnswer = async (key, ct) =>
        {
            if (key != builds[0].RootKey) return;
            reached.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        var opener = new BuildOpener(source, new BuildOpenerOptions { MaxConcurrentOpens = 1 });
        var first = opener.OpenAsync(builds[0].Ref, Ct);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource();
        var waiting = opener.OpenAsync(builds[1].Ref, cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.DoesNotContain(inner.Asked, a => a.Key == builds[1].BuildConfigKey);
        release.SetResult();
        Assert.Equal(BuildOpenState.Open, (await first).State);
        Assert.Equal(BuildOpenState.Open, (await opener.OpenAsync(builds[1].Ref, Ct)).State);
        Assert.Equal(BuildOpenState.Open, (await opener.OpenAsync(builds[2].Ref, Ct)).State);
    }

    [Fact]
    public async Task AnUnreadableCdnsHistoryLeavesNoRoute()
    {
        var build = new SyntheticBuild().LoadInto(inner);
        await ObserveCdns("us|tpr/wow|us.cdn.blizzard.com|x|tpr/configs/data\n");
        var document = (await inner.ReadHistoryAsync(new(Kind: HistoryKind.Cdns), Ct))[^1].Document;

        source.HistoryThrows = true;
        Assert.Null((await OpenedAsync(new BuildOpener(source), build.Ref)).Cdn);
        source.HistoryThrows = false;

        source.Throwing.Add(document);
        Assert.Null((await OpenedAsync(new BuildOpener(source), build.Ref)).Cdn);
        source.Throwing.Clear();

        source.Override = k => k == document ? new ObjectResult(k, ObjectState.Refused, Code: "OBJECT_ACCESS_CORRUPT") : null;
        Assert.Null((await OpenedAsync(new BuildOpener(source), build.Ref)).Cdn);
        source.Override = null;

        Assert.NotNull((await OpenedAsync(new BuildOpener(source), build.Ref)).Cdn);
    }

    [Fact]
    public async Task EncodedSizeFallsBackToTheIndex()
    {
        var build = new SyntheticBuild(noEncodedRow: [100, 300, 400]).LoadInto(inner);
        var opened = await OpenedAsync(new BuildOpener(source), build.Ref);
        foreach (var id in new uint[] { 100, 300, 400 })
            Assert.Equal(build.Variants(id).ToArray(), opened.Resolve(id).Variants.ToArray());
        var two = opened.Resolve(100).Variants;
        Assert.Equal(two[0].Location!.Value.Size, two[0].EncodedSize);
        Assert.NotNull(two[1].EncodedSize);
        Assert.NotNull(opened.Resolve(300).Variants[0].EncodedSize);
        Assert.Null(opened.Resolve(400).Variants[0].EncodedSize);
        Assert.NotNull(opened.Resolve(200).Variants[0].EncodedSize);
    }

    [Fact]
    public async Task WithARouteAMissingObjectIsMissing()
    {
        var build = new SyntheticBuild();
        await ObserveCdns("us|tpr/wow|us.cdn.blizzard.com|x|tpr/configs/data\n");
        var result = await new BuildOpener(source).OpenAsync(build.Ref, Ct);
        Assert.Equal((BuildOpenState.Missing, (BuildPart?)BuildPart.BuildConfig, "CDN_NOT_FOUND"), (result.State, result.Part, result.Code));
        Assert.Equal(time.GetUtcNow(), result.ObservedAt);
    }

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan by) => current += by;
    }
}
