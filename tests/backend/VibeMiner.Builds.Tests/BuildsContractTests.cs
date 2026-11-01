using VibeMiner.Builds.Testing;
using VibeMiner.ObjectAccess.Testing;
using Xunit;

namespace VibeMiner.Builds.Tests;

/// <summary>What a scenario's open must end with, for the side that scripts it.</summary>
public sealed record ExpectedFailure(BuildOpenState State, BuildPart Part, string Code);

public enum Scenario { Openable, NeverFetchedOffline, Missing, Failing }

/// <summary>
/// One behaviour suite for <see cref="IBuilds"/>: the real <see cref="BuildOpener"/> over the in-memory layer 2 and the fake
/// layers 4 and up test against. Each side arranges the same logical build its own way.
/// </summary>
public abstract class BuildsContract
{
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>An opener for which <paramref name="build"/> is in the given scenario.</summary>
    internal abstract IBuilds Arrange(Scenario scenario, SyntheticBuild build, ExpectedFailure? expected = null);
    /// <summary>Makes the next uncached open of <paramref name="build"/> wait for <paramref name="wait"/> partway through.</summary>
    internal abstract void Block(SyntheticBuild build, Func<CancellationToken, Task> wait);
    /// <summary>Opens that actually ran.</summary>
    internal abstract int Opens { get; }

    private static async Task<IBuild> OpenedAsync(IBuilds builds, SyntheticBuild build)
    {
        var result = await builds.OpenAsync(build.Ref, Ct);
        Assert.Equal(BuildOpenState.Open, result.State);
        Assert.Equal((null, null, null, null), (result.Part, result.Code, result.Message, result.ObservedAt));
        Assert.Equal(build.Ref, result.Build);
        return result.Opened!;
    }

    private static void AssertVariants(IReadOnlyList<FileVariant> expected, FileResolution actual) =>
        Assert.Equal(expected.ToArray(), actual.Variants.ToArray());

    [Fact]
    public async Task OpensWithMetadataAndLocales()
    {
        var build = new SyntheticBuild();
        var opened = await OpenedAsync(Arrange(Scenario.Openable, build), build);
        Assert.Equal(build.Ref, opened.Build);
        var m = opened.Metadata;
        Assert.Equal((build.BuildName, build.RootCKey, build.EncodingCKey, build.EncodingEKey, 2, 5),
            (m.BuildName, m.RootCKey, m.EncodingCKey, m.EncodingEKey, m.ArchiveCount, m.FileCount));
        Assert.Equal(["enUS", "deDE"], m.Locales);
        Assert.Null(opened.Cdn);
    }

    [Theory]
    [InlineData(100u, "interface/icons/one.blp")]
    [InlineData(200u, "world/two.m2")]
    [InlineData(300u, "sound/three.ogg")]
    [InlineData(400u, "sound/four.ogg")]
    public async Task ResolvesByFileDataIdAndPath(uint id, string path)
    {
        var build = new SyntheticBuild();
        var opened = await OpenedAsync(Arrange(Scenario.Openable, build), build);
        foreach (var r in new[] { opened.Resolve(id), opened.Resolve(path), opened.Resolve(path.ToUpperInvariant().Replace('/', '\\')) })
        {
            Assert.Equal((FileResolutionState.Found, (uint?)id), (r.State, r.FileDataId));
            AssertVariants(build.Variants(id), r);
        }
    }

    [Fact]
    public async Task ResolvesPlacementsAndLocaleVariants()
    {
        var build = new SyntheticBuild();
        var opened = await OpenedAsync(Arrange(Scenario.Openable, build), build);
        var two = opened.Resolve(100).Variants;
        Assert.Equal([SyntheticBuild.EnUs, SyntheticBuild.DeDe], two.Select(v => v.LocaleFlags));
        Assert.Equal([0ul, 0x8ul], two.Select(v => v.ContentFlags));
        Assert.Equal(build.Archives[0], two[0].Location!.Value.Archive);
        Assert.Null(two[1].Location);
        Assert.Equal(build.Archives[1], opened.Resolve(200).Variants[0].Location!.Value.Archive);
        Assert.All(opened.Resolve(300).Variants.Concat(opened.Resolve(400).Variants), v => { Assert.Null(v.Location); Assert.NotNull(v.EKey); Assert.NotNull(v.EncodedSize); });
    }

    [Fact]
    public async Task UnknownFileAndNotInEncoding()
    {
        var build = new SyntheticBuild();
        var opened = await OpenedAsync(Arrange(Scenario.Openable, build), build);
        foreach (var r in new[] { opened.Resolve(999), opened.Resolve("no/such/file.blp") })
            Assert.Equal((FileResolutionState.UnknownFile, (uint?)null, 0), (r.State, r.FileDataId, r.Variants.Count));
        var missing = opened.Resolve("sound/five.ogg");
        Assert.Equal((FileResolutionState.NotInEncoding, (uint?)500), (missing.State, missing.FileDataId));
        AssertVariants(build.Variants(500), missing);
        Assert.All(missing.Variants, v => Assert.Equal((null, null, null, null), (v.EKey, v.DecodedSize, v.EncodedSize, v.Location)));
    }

    private static SyntheticBuild ListingBuild() => new(files:
    [
        new(100, "a/one.blp", new(SyntheticBuild.EnUs, 0, Placement.Archived), new(SyntheticBuild.DeDe, 0x8, Placement.Loose)),
        new(150, "a/two.blp", new SyntheticVariant(SyntheticBuild.DeDe, 0, Placement.Loose)),
        new(250, "a/three.blp", new SyntheticVariant(uint.MaxValue, 0, Placement.LooseIndexed)),
        new(300, "a/four.blp", new SyntheticVariant(SyntheticBuild.EnUs, 0x8, Placement.NotInEncoding)),
    ]);

    [Theory]
    [InlineData("enUS", 0u, new uint[] { 100, 250, 300 })]
    [InlineData("deDE", 0u, new uint[] { 100, 150, 250 })]
    [InlineData("frFR", 0u, new uint[] { 250 })]
    [InlineData("enUS", 150u, new uint[] { 250, 300 })]
    [InlineData("enUS", 250u, new uint[] { 250, 300 })]
    [InlineData("deDE", 101u, new uint[] { 150, 250 })]
    [InlineData("enUS", 301u, new uint[0])]
    [InlineData("enUS", uint.MaxValue, new uint[0])]
    public async Task ListsFileDataIdsPerLocale(string locale, uint fromId, uint[] expected)
    {
        var build = ListingBuild();
        var opened = await OpenedAsync(Arrange(Scenario.Openable, build), build);
        Assert.Equal(expected, opened.FileDataIds(locale, fromId).ToArray());
    }

    [Fact]
    public async Task FileDataIdEnumerationsAreIndependent()
    {
        var build = ListingBuild();
        var opened = await OpenedAsync(Arrange(Scenario.Openable, build), build);
        Assert.Equal([100u, 250, 300], opened.FileDataIds("enUS").ToArray());
        var ids = opened.FileDataIds("enUS");
        using var a = ids.GetEnumerator();
        using var b = ids.GetEnumerator();
        Assert.True(a.MoveNext()); Assert.True(a.MoveNext());
        Assert.True(b.MoveNext());
        Assert.Equal((250u, 100u), (a.Current, b.Current));
        var parallel = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => opened.FileDataIds("deDE").ToArray())));
        Assert.All(parallel, p => Assert.Equal([100u, 150, 250], p));
    }

    [Theory]
    [InlineData("xxXX")]
    [InlineData("enus")]
    [InlineData("")]
    public async Task UnknownLocaleIsRejectedWhenCalled(string locale)
    {
        var build = new SyntheticBuild();
        var opened = await OpenedAsync(Arrange(Scenario.Openable, build), build);
        Assert.Throws<ArgumentException>(() => opened.FileDataIds(locale));
        Assert.Throws<ArgumentNullException>(() => opened.FileDataIds(null!));
    }

    [Fact]
    public async Task SecondOpenReturnsTheSameInstance()
    {
        var build = new SyntheticBuild();
        var builds = Arrange(Scenario.Openable, build);
        var first = await OpenedAsync(builds, build);
        Assert.Same(first, await OpenedAsync(builds, build));
        Assert.Equal(1, Opens);
    }

    [Fact]
    public async Task ConcurrentOpensShareOne()
    {
        var build = new SyntheticBuild();
        var builds = Arrange(Scenario.Openable, build);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Block(build, ct => gate.Task.WaitAsync(ct));
        var a = builds.OpenAsync(build.Ref, Ct);
        var b = builds.OpenAsync(build.Ref, Ct);
        gate.SetResult();
        var results = await Task.WhenAll(a, b);
        Assert.Same(results[0].Opened, results[1].Opened);
        Assert.NotNull(results[0].Opened);
        Assert.Equal(1, Opens);
    }

    [Fact]
    public async Task NeverFetchedOfflineNeedsNetwork()
    {
        var build = new SyntheticBuild();
        var builds = Arrange(Scenario.NeverFetchedOffline, build);
        var result = await builds.OpenAsync(build.Ref, Ct);
        Assert.Equal((BuildOpenState.NeedsNetwork, BuildPart.BuildConfig, "OBJECT_ACCESS_NEEDS_NETWORK", null),
            (result.State, result.Part, result.Code, result.Opened));
        Assert.Null(result.ObservedAt);
        await builds.OpenAsync(build.Ref, Ct);
        Assert.Equal(2, Opens);
    }

    [Fact]
    public async Task NotFoundIsMissingWithObservedAt()
    {
        var build = new SyntheticBuild();
        var result = await Arrange(Scenario.Missing, build).OpenAsync(build.Ref, Ct);
        Assert.Equal((BuildOpenState.Missing, BuildPart.BuildConfig, "CDN_NOT_FOUND"), (result.State, result.Part, result.Code));
        Assert.NotNull(result.ObservedAt);
        Assert.Null(result.Opened);
    }

    public static TheoryData<RootMode, bool, BuildOpenState, BuildPart, string> Failures => new()
    {
        { RootMode.Encrypted, false, BuildOpenState.Encrypted, BuildPart.Root, "BLTE_ENCRYPTED" },
        { RootMode.Normal, true, BuildOpenState.Corrupt, BuildPart.Encoding, "EN_INTEGRITY" },
        { RootMode.Unsupported, false, BuildOpenState.Unsupported, BuildPart.Root, "ROOT_UNSUPPORTED" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task FormatFailuresKeepTheirState(RootMode root, bool corruptEncoding, BuildOpenState state, BuildPart part, string code)
    {
        var build = new SyntheticBuild(root: root, corruptEncoding: corruptEncoding);
        var builds = Arrange(Scenario.Failing, build, new(state, part, code));
        var result = await builds.OpenAsync(build.Ref, Ct);
        Assert.Equal((state, part, code, null), (result.State, result.Part, result.Code, result.Opened));
        Assert.False(string.IsNullOrEmpty(result.Message));
        await builds.OpenAsync(build.Ref, Ct);
        Assert.Equal(2, Opens);
    }

    [Fact]
    public async Task CancelledOpenThrowsAndTheNextStartsFresh()
    {
        var build = new SyntheticBuild();
        var builds = Arrange(Scenario.Openable, build);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = true;
        Block(build, async ct => { if (!blocking) return; reached.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); });
        using var cts = new CancellationTokenSource();
        var open = builds.OpenAsync(build.Ref, cts.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => open);
        blocking = false;
        var next = await OpenedAsync(builds, build);
        Assert.NotNull(next);
        Assert.Equal(2, Opens);
    }

    [Fact]
    public void BuildRefNormalisesAndValidates()
    {
        var upper = new BuildRef("wow", new string('A', 32), new string('b', 32));
        Assert.Equal(new string('a', 32), upper.BuildConfig);
        Assert.Equal(new BuildRef("wow", new string('a', 32), new string('b', 32)), upper);
        Assert.Throws<ArgumentException>(() => new BuildRef("nope", new string('a', 32), new string('b', 32)));
        Assert.Throws<ArgumentException>(() => new BuildRef("wow", new string('a', 31), new string('b', 32)));
        Assert.Throws<ArgumentException>(() => new BuildRef("wow", new string('a', 32), new string('g', 32)));
        Assert.Throws<ArgumentException>(() => new BuildRef("wow", new string('a', 32), new string('b', 32), "u s"));
    }
}

/// <summary>The real opener over <see cref="InMemoryObjectSource"/> (D137).</summary>
public sealed class RealBuildsContractTests : BuildsContract
{
    private readonly TestObjectSource source = new(new InMemoryObjectSource());
    private SyntheticBuild? arranged;

    internal override IBuilds Arrange(Scenario scenario, SyntheticBuild build, ExpectedFailure? expected = null)
    {
        arranged = build;
        switch (scenario)
        {
            case Scenario.Openable or Scenario.Failing: build.LoadInto(source.Inner); break;
            case Scenario.NeverFetchedOffline: source.Offline = true; break;
            case Scenario.Missing: source.Inner.NotFound(build.BuildConfigKey); break;
        }
        return new BuildOpener(source);
    }

    internal override void Block(SyntheticBuild build, Func<CancellationToken, Task> wait) =>
        source.Inner.BeforeAnswer = (key, ct) => key == build.RootKey ? wait(ct) : Task.CompletedTask;

    internal override int Opens => source.Count("GetKeysAsync:Config");
}

public sealed class InMemoryBuildsContractTests : BuildsContract
{
    private readonly InMemoryBuilds builds = new();

    internal override IBuilds Arrange(Scenario scenario, SyntheticBuild build, ExpectedFailure? expected = null)
    {
        switch (scenario)
        {
            case Scenario.Openable: builds.Add(build.Ref, build.ToFake()); break;
            case Scenario.Missing: builds.Fail(build.Ref, BuildOpenState.Missing, BuildPart.BuildConfig, "CDN_NOT_FOUND", DateTimeOffset.UnixEpoch); break;
            case Scenario.Failing: builds.Fail(build.Ref, expected!.State, expected.Part, expected.Code); break;
        }
        return builds;
    }

    internal override void Block(SyntheticBuild build, Func<CancellationToken, Task> wait) =>
        builds.BeforeOpen = (_, ct) => wait(ct);

    internal override int Opens => builds.Opens;
}
