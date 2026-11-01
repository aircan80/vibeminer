using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Files.Names;
using VibeMiner.Generated;
using VibeMiner.Generated.Testing;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Files.Tests.Names;

/// <summary>
/// The wow-listfile names (D105, #186) over the layer 2 fake and the in-memory generated store: release and part observations,
/// staging, the copy kept in history as parts or deltas, the persisted index and its rebuild, codes, back-off and the switch.
/// Authored listfiles only.
/// </summary>
public sealed class FileNamesTests
{
    private const string Tag = "202610281921";
    private const string NewerTag = "202611011200";
    private const int Part = ObservationRequest.ListfilePartBytes;
    // The generated store of this test's engine; a restart keeps it, a wipe clears it.
    private readonly InMemoryGeneratedStore store = new();
    private static readonly byte[] Small = Encoding.UTF8.GetBytes("1;Interface/Icons/Alpha.blp\n5;World/Maps/Azeroth/Azeroth.wdt\n900;Sound/Music/Theme.mp3\n");
    private static readonly Lazy<byte[]> Large = new(() => Csv(Part * 2 + 1000));

    private sealed class Clock : TimeProvider
    {
        private readonly Lock gate = new();
        private DateTimeOffset now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        // Every read moves on 1 ms, so history entries never tie.
        public override DateTimeOffset GetUtcNow() { lock (gate) return now = now.AddMilliseconds(1); }
        public void Advance(TimeSpan by) { lock (gate) now += by; }
    }

    private sealed class Switch(bool on = true) : IFileNameSwitch
    {
        public bool On { get; set; } = on;
        public Func<Task<bool>>? Read { get; set; }
        public int Reads;
        public Task<bool> NamesEnabledAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Reads);
            return Read is { } read ? read() : Task.FromResult(On);
        }
    }

    private sealed class Throwing : IFileNameSwitch
    {
        public Task<bool> NamesEnabledAsync(CancellationToken cancellationToken) => throw new IOException("settings unreadable");
    }

    private sealed class NullTask : IFileNameSwitch
    {
        public Task<bool> NamesEnabledAsync(CancellationToken cancellationToken) => null!;
    }

    private static byte[] Csv(int atLeast)
    {
        var text = new StringBuilder(atLeast + 64);
        for (var id = 1; text.Length < atLeast; id++) text.Append(id).Append(";World/Maps/Zone").Append(id).Append("/File").Append(id).Append(".blp\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] Release(string tag, byte[] asset, string? digest = null, bool withDigest = true, string? url = null, long? size = null,
        string name = WowListfileObservations.AssetName)
    {
        var assetJson = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["size"] = size ?? asset.Length,
            ["browser_download_url"] = url ?? WowListfileObservations.AssetUrl(tag),
        };
        if (withDigest) assetJson["digest"] = digest ?? "sha256:" + Sha(asset);
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["tag_name"] = tag,
            ["published_at"] = "2026-10-28T19:21:00Z",
            ["assets"] = new object[] { new { name = "other.csv", size = 1 }, assetJson },
        });
    }

    private static (InMemoryObjectSource Source, Clock Clock) Fake(string tag = Tag, byte[]? asset = null, byte[]? release = null)
    {
        var clock = new Clock();
        var source = new InMemoryObjectSource { Time = clock };
        asset ??= Small;
        source.AddListfile(tag, release ?? Release(tag, asset), asset);
        return (source, clock);
    }

    private static int Releases(InMemoryObjectSource source) => source.Observed.Count(o => o.Kind == ObservationKind.ListfileRelease);
    private static string[] Parts(InMemoryObjectSource source, int skip = 0) =>
        source.Observed.Skip(skip).Where(o => o.Kind == ObservationKind.ListfilePart).Select(o => o.Product + "/" + o.Region).ToArray();

    [Fact]
    public async Task OffFetchesNothingAndHasNoNames()
    {
        var (source, clock) = Fake();
        var names = new FileNames(source, store, new Switch(false), clock);
        Assert.Equal(FileNamesState.Off, (await names.UpdateAsync(true, default)).State);
        Assert.Equal(FileNamesState.Off, (await names.StatusAsync(default)).State);
        Assert.Null(await names.CurrentAsync(default));
        Assert.Empty(source.Observed);
        Assert.Empty(source.Asked);
    }

    [Fact]
    public async Task NoSwitchThrowingOrNullTaskReadsAsOff()
    {
        var (source, clock) = Fake();
        foreach (var off in new IFileNameSwitch?[] { null, new Throwing(), new NullTask() })
        {
            var names = new FileNames(source, store, off, clock);
            Assert.Equal(FileNamesState.Off, (await names.UpdateAsync(true, default)).State);
            Assert.Equal(FileNamesState.Off, (await names.StatusAsync(default)).State);
            Assert.Null(await names.CurrentAsync(default));
        }
        Assert.Empty(source.Observed);
    }

    [Fact]
    public async Task FirstUpdateFetchesTheReleaseAndItsPartsAndIsReady()
    {
        var (source, clock) = Fake();
        var names = new FileNames(source, store, new Switch(), clock);
        Assert.Equal(FileNamesState.NotReady, (await names.StatusAsync(default)).State);
        var status = await names.UpdateAsync(false, default);
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Equal(Tag, status.Tag);
        Assert.Equal(3, status.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 19, 21, 0, TimeSpan.Zero), status.PublishedAt);
        Assert.NotNull(status.FetchedAt);
        Assert.True(status.CheckedAt < status.FetchedAt);
        Assert.Null(status.Code);
        Assert.Equal([Tag + "/p0"], Parts(source));
        var index = await names.CurrentAsync(default);
        Assert.Equal("World/Maps/Azeroth/Azeroth.wdt", await index!.NameOfAsync(5, default));
        Assert.Equal(new uint[] { 900u }, await index.IdsOfAsync("sound\\music\\theme.MP3", default));
    }

    [Fact]
    public async Task AMultiPartAssetIsJoinedAndVerified()
    {
        var (source, clock) = Fake(asset: Large.Value);
        var names = new FileNames(source, store, new Switch(), clock);
        var status = await names.UpdateAsync(false, default);
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Equal([Tag + "/p0", Tag + "/p1", Tag + "/p2"], Parts(source));
        var index = await names.CurrentAsync(default);
        Assert.Equal(status.Count, index!.Count);
        // A line that straddles a part boundary reads whole.
        var last = (await Walk(index)).Last();
        Assert.Equal($"World/Maps/Zone{last.Id}/File{last.Id}.blp", last.Name);
    }

    [Fact]
    public async Task ASecondUpdateWithinAWeekFetchesNothing()
    {
        var (source, clock) = Fake();
        var names = new FileNames(source, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        var observed = source.Observed.Count;
        clock.Advance(TimeSpan.FromDays(6));
        Assert.Equal(FileNamesState.Ready, (await names.UpdateAsync(false, default)).State);
        Assert.Equal(observed, source.Observed.Count);
        clock.Advance(TimeSpan.FromDays(2));
        var status = await names.UpdateAsync(false, default);
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Equal(2, Releases(source));
        Assert.Single(Parts(source));
        Assert.True(status.CheckedAt > status.FetchedAt);
    }

    [Fact]
    public async Task ForceAsksForTheReleaseAgainButFetchesNoPartsForTheSameTag()
    {
        var (source, clock) = Fake(asset: Large.Value);
        var names = new FileNames(source, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        var before = (await names.StatusAsync(default)).CheckedAt;
        var status = await names.UpdateAsync(true, default);
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Equal(2, Releases(source));
        Assert.Equal(3, Parts(source).Length);
        Assert.True(status.CheckedAt > before);
    }

    [Fact]
    public async Task ANewerTagFetchesItsPartsAndSwaps()
    {
        var (source, clock) = Fake();
        var names = new FileNames(source, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        var old = await names.CurrentAsync(default);
        var newer = Encoding.UTF8.GetBytes("2;Interface/Icons/Beta.blp\n");
        source.AddListfile(NewerTag, Release(NewerTag, newer), newer);
        clock.Advance(TimeSpan.FromDays(8));
        var observed = source.Observed.Count;
        var status = await names.UpdateAsync(false, default);
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Equal(NewerTag, status.Tag);
        Assert.Equal(1, status.Count);
        Assert.Equal([NewerTag + "/p0"], Parts(source, observed));
        var current = await names.CurrentAsync(default);
        Assert.NotSame(old, current);
        Assert.Equal("Interface/Icons/Beta.blp", await current!.NameOfAsync(2, default));
        Assert.Null(await current.NameOfAsync(5, default));
        // The old index is deleted at the swap: a reader still holding it loses names without failing, and that doesn't mark the
        // new copy damaged. Only the new index is left (staging is gone too).
        Assert.Null(await old!.NameOfAsync(1, default));
        Assert.True(old.IsDamaged);
        Assert.False(current.IsDamaged);
        Assert.Same(current, await names.CurrentAsync(default));
        Assert.Null((await names.StatusAsync(default)).Code);
        Assert.Equal(current.Keys.ToHashSet(), store.Keys.ToHashSet());
    }

    [Fact]
    public async Task AMissingPartFailsAndTheNextAttemptFetchesOnlyWhatIsMissing()
    {
        var (source, clock) = Fake(asset: Large.Value);
        var p1 = Large.Value.AsSpan(Part, Part).ToArray();
        source.FailObservation(Tag, "p1", ObservationKind.ListfilePart, ObjectState.NotFound, "CDN_NOT_FOUND");
        var names = new FileNames(source, store, new Switch(), clock);
        var failed = await names.UpdateAsync(false, default);
        Assert.Equal(FileNamesState.Failed, failed.State);
        Assert.Equal("LISTFILE_NOT_FOUND", failed.Code);
        Assert.NotNull(failed.ObservedAt);
        Assert.Equal([Tag + "/p0", Tag + "/p1"], Parts(source));
        Assert.Null(await names.CurrentAsync(default));
        source.AddReply(Tag, "p1", ObservationKind.ListfilePart, p1);
        var observed = source.Observed.Count;
        var status = await names.UpdateAsync(true, default);
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Null(status.Code);
        Assert.Equal([Tag + "/p1", Tag + "/p2"], Parts(source, observed));
    }

    [Fact]
    public async Task AShortPartIsRefetchedAloneOnceThenRefused()
    {
        var (source, clock) = Fake(asset: Large.Value);
        var good = Large.Value.AsSpan(0, Part).ToArray();
        source.AddReply(Tag, "p0", ObservationKind.ListfilePart, good[..^1]);
        var names = new FileNames(source, store, new Switch(), clock);
        var failed = await names.UpdateAsync(false, default);
        Assert.Equal(FileNamesState.Failed, failed.State);
        Assert.Equal("LISTFILE_INVALID", failed.Code);
        // Every part once, then only the short one again.
        Assert.Equal([Tag + "/p0", Tag + "/p1", Tag + "/p2", Tag + "/p0"], Parts(source));
        // GitHub serves the right bytes again: a forced attempt refetches only that part and is ready.
        source.AddReply(Tag, "p0", ObservationKind.ListfilePart, good);
        var observed = source.Observed.Count;
        var status = await names.UpdateAsync(true, default);
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Equal([Tag + "/p0"], Parts(source, observed));
    }

    [Fact]
    public async Task ADigestMismatchIsRefetchedOnceThenRefused()
    {
        var release = Release(Tag, Small, digest: "sha256:" + new string('0', 64));
        var (source, clock) = Fake(release: release);
        var names = new FileNames(source, store, new Switch(), clock);
        var status = await names.UpdateAsync(false, default);
        Assert.Equal(FileNamesState.Failed, status.State);
        Assert.Equal("LISTFILE_INVALID", status.Code);
        Assert.Contains("SHA-256", status.Message);
        Assert.Equal(2, Parts(source).Length);
        Assert.Null(await names.CurrentAsync(default));
    }

    [Fact]
    public async Task BytesThatMatchTheDigestButDoNotParseAreNotRefetched()
    {
        var bad = Encoding.UTF8.GetBytes("5;a\n3;b\n");
        var (source, clock) = Fake(asset: bad);
        var names = new FileNames(source, store, new Switch(), clock);
        var status = await names.UpdateAsync(false, default);
        Assert.Equal(FileNamesState.Failed, status.State);
        Assert.Equal("LISTFILE_INVALID", status.Code);
        Assert.Single(Parts(source));
    }

    public static TheoryData<string, string> BadReleases => new()
    {
        { "{not json", "LISTFILE_INVALID" },
        { "[]", "LISTFILE_INVALID" },
        { "{\"tag_name\":\"" + Tag + "\",\"assets\":[]}", "LISTFILE_INVALID" },
        { Encoding.UTF8.GetString(Release(Tag, Small, withDigest: false)), "LISTFILE_INVALID" },
        { Encoding.UTF8.GetString(Release(Tag, Small, digest: "md5:" + new string('0', 67))), "LISTFILE_INVALID" },
        { Encoding.UTF8.GetString(Release(Tag, Small, url: "https://example.com/" + WowListfileObservations.AssetName)), "LISTFILE_INVALID" },
        { Encoding.UTF8.GetString(Release(Tag, Small, url: WowListfileObservations.AssetUrl(NewerTag))), "LISTFILE_INVALID" },
        { Encoding.UTF8.GetString(Release("Release.1", Small, url: "https://github.com/wowdev/wow-listfile/releases/download/Release.1/" + WowListfileObservations.AssetName)), "LISTFILE_INVALID" },
        { Encoding.UTF8.GetString(Release(new string('a', 65), Small, url: "https://github.com/wowdev/wow-listfile/releases/download/" + new string('a', 65) + "/" + WowListfileObservations.AssetName)), "LISTFILE_INVALID" },
        { Encoding.UTF8.GetString(Release(Tag, Small, size: 0)), "LISTFILE_INVALID" },
        { Encoding.UTF8.GetString(Release(Tag, Small, size: (512L << 20) + 1)), "LISTFILE_TOO_LARGE" },
        { Encoding.UTF8.GetString(Release(Tag, Small, name: "community-listfile.csv")), "LISTFILE_NOT_FOUND" },
    };

    [Theory]
    [MemberData(nameof(BadReleases))]
    public async Task ABadReleaseIsRefusedWithoutFetchingParts(string json, string code)
    {
        var (source, clock) = Fake(release: Encoding.UTF8.GetBytes(json));
        var names = new FileNames(source, store, new Switch(), clock);
        var status = await names.UpdateAsync(false, default);
        Assert.Equal(FileNamesState.Failed, status.State);
        Assert.Equal(code, status.Code);
        Assert.NotNull(status.ObservedAt);
        // The reply was recorded, so it still counts as a check.
        Assert.NotNull(status.CheckedAt);
        Assert.Empty(Parts(source));
    }

    [Theory]
    [InlineData(ObjectState.Unavailable, "CDN_OFFLINE", null, "LISTFILE_FETCH_OFFLINE")]
    [InlineData(ObjectState.NotFound, "CDN_NOT_FOUND", null, "LISTFILE_NOT_FOUND")]
    [InlineData(ObjectState.Refused, "CDN_TOO_LARGE", null, "LISTFILE_TOO_LARGE")]
    [InlineData(ObjectState.Unavailable, "CDN_TIMEOUT", "GitHub took too long.", "LISTFILE_FETCH_FAILED")]
    [InlineData(ObjectState.StoreFailed, "OBJECT_STORE_DISK_FULL", "The disk is full.", "LISTFILE_FETCH_FAILED")]
    public async Task Layer2FailuresMapToListfileCodes(ObjectState state, string layer2, string? message, string code)
    {
        var (source, clock) = Fake();
        source.FailObservation(ObservationRequest.ListfileProduct, ObservationRequest.ListfileReleaseRegion, ObservationKind.ListfileRelease, state, layer2, message);
        var names = new FileNames(source, store, new Switch(), clock);
        var status = await names.UpdateAsync(false, default);
        Assert.Equal(FileNamesState.Failed, status.State);
        Assert.Equal(code, status.Code);
        if (message is not null) Assert.Contains(message, status.Message);
        Assert.Equal(state is ObjectState.NotFound or ObjectState.Unavailable, status.ObservedAt is not null);
        Assert.Equal(FileNamesState.Failed, (await names.StatusAsync(default)).State);
    }

    [Fact]
    public async Task AFailureWaitsFifteenMinutesUnlessForced()
    {
        var (source, clock) = Fake();
        source.FailObservation(ObservationRequest.ListfileProduct, ObservationRequest.ListfileReleaseRegion, ObservationKind.ListfileRelease,
            ObjectState.Unavailable, "CDN_OFFLINE");
        var names = new FileNames(source, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        Assert.Equal(1, Releases(source));
        clock.Advance(TimeSpan.FromMinutes(14));
        var waited = await names.UpdateAsync(false, default);
        Assert.Equal("LISTFILE_FETCH_OFFLINE", waited.Code);
        Assert.Equal(1, Releases(source));
        await names.UpdateAsync(true, default);
        Assert.Equal(2, Releases(source));
        clock.Advance(TimeSpan.FromMinutes(16));
        source.AddReply(ObservationRequest.ListfileProduct, ObservationRequest.ListfileReleaseRegion, ObservationKind.ListfileRelease, Release(Tag, Small));
        var status = await names.UpdateAsync(false, default);
        Assert.Equal(3, Releases(source));
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Null(status.Code);
    }

    [Fact]
    public async Task AnOlderCopyKeepsServingAfterAFailedUpdateAndAfterARestart()
    {
        var (source, clock) = Fake();
        var names = new FileNames(source, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        var newer = Encoding.UTF8.GetBytes("2;Interface/Icons/Beta.blp\n");
        source.AddListfile(NewerTag, Release(NewerTag, newer, digest: "sha256:" + new string('1', 64)), newer);
        var status = await names.UpdateAsync(true, default);
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Equal(Tag, status.Tag);
        Assert.Equal("LISTFILE_INVALID", status.Code);
        Assert.Equal("Interface/Icons/Alpha.blp", await (await names.CurrentAsync(default))!.NameOfAsync(1, default));
        // The newest release is one this engine refused, so unforced updates wait the week like a current copy.
        clock.Advance(TimeSpan.FromMinutes(16));
        var observed = source.Observed.Count;
        Assert.Equal("LISTFILE_INVALID", (await names.UpdateAsync(false, default)).Code);
        Assert.Equal(observed, source.Observed.Count);

        var restarted = new FileNames(source, store, new Switch(), clock);
        var loaded = await restarted.StatusAsync(default);
        Assert.Equal(FileNamesState.Ready, loaded.State);
        Assert.Equal(Tag, loaded.Tag);
        Assert.Null(loaded.Code);
    }

    [Fact]
    public async Task AStoredCopyLoadsFromHistoryAlone()
    {
        var (source, clock) = Fake(asset: Large.Value);
        var first = await new FileNames(source, store, new Switch(), clock).UpdateAsync(false, default);
        var observed = source.Observed.Count;
        var names = new FileNames(source, store, new Switch(), clock);
        var status = await names.StatusAsync(default);
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Equal(first with { }, status);
        Assert.NotNull(await names.CurrentAsync(default));
        // Within the week an unforced update still fetches nothing.
        Assert.Equal(FileNamesState.Ready, (await names.UpdateAsync(false, default)).State);
        Assert.Equal(observed, source.Observed.Count);
    }

    [Fact]
    public async Task ConcurrentCallersLoadOnce()
    {
        const int callers = 8;
        var (source, clock) = Fake();
        await new FileNames(source, store, new Switch(), clock).UpdateAsync(false, default);
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        // Every caller reads the switch on entry; the load's first store read waits until all of them are in the call.
        var on = new Switch { Read = () => { if (Interlocked.Increment(ref entered) == callers) inside.SetResult(); return Task.FromResult(true); } };
        source.BeforeAnswer = (_, token) => inside.Task.WaitAsync(token);
        var names = new FileNames(source, store, on, clock);
        var tasks = Enumerable.Range(0, callers).Select(_ => Task.Run(() => names.CurrentAsync(default))).ToArray();
        var indexes = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, names.Loads);
        Assert.All(indexes, index => Assert.Same(indexes[0], index));
        Assert.NotNull(indexes[0]);
    }

    [Fact]
    public async Task TurningTheSwitchOffMeansIdsOnlyAtOnce()
    {
        var (source, clock) = Fake();
        var on = new Switch();
        var names = new FileNames(source, store, on, clock);
        await names.UpdateAsync(false, default);
        on.On = false;
        Assert.Null(await names.CurrentAsync(default));
        Assert.Equal(FileNamesState.Off, (await names.StatusAsync(default)).State);
        on.On = true;
        Assert.NotNull(await names.CurrentAsync(default));
    }

    [Fact]
    public async Task TurningTheSwitchOffStopsADownloadBetweenParts()
    {
        var (source, clock) = Fake(asset: Large.Value);
        var on = new Switch();
        var names = new FileNames(source, store, on, clock);
        on.Read = () => Task.FromResult(source.Observed.Count(o => o.Kind == ObservationKind.ListfilePart) < 1);
        Assert.Equal(FileNamesState.Off, (await names.UpdateAsync(false, default)).State);
        Assert.Single(Parts(source));
        on.Read = null;
        var observed = source.Observed.Count;
        Assert.Equal(FileNamesState.Ready, (await names.UpdateAsync(true, default)).State);
        Assert.Equal([Tag + "/p1", Tag + "/p2"], Parts(source, observed));
    }

    [Fact]
    public async Task CancellationPropagatesAndRemembersNoFailure()
    {
        var (source, clock) = Fake(asset: Large.Value);
        using var cancel = new CancellationTokenSource();
        var on = new Switch();
        // The switch is read again before each part; cancel there, so the part fetch sees it.
        on.Read = () => { if (source.Observed.Any(o => o.Kind == ObservationKind.ListfilePart)) cancel.Cancel(); return Task.FromResult(true); };
        var names = new FileNames(source, store, on, clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => names.UpdateAsync(false, cancel.Token));
        on.Read = null;
        var status = await names.StatusAsync(default);
        Assert.Equal(FileNamesState.NotReady, status.State);
        Assert.Null(status.Code);
        Assert.Null(await names.CurrentAsync(default));
        // Not a failure, so no back-off: the next unforced update goes on with the parts not yet fetched.
        var observed = source.Observed.Count;
        Assert.Equal(FileNamesState.Ready, (await names.UpdateAsync(false, default)).State);
        Assert.Equal([Tag + "/p1", Tag + "/p2"], Parts(source, observed));
    }

    [Fact]
    public async Task ABrokenNewerReleaseIsNotDownloadedAgainWhileACopyServes()
    {
        var (source, clock) = Fake();
        var names = new FileNames(source, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        var newer = Encoding.UTF8.GetBytes("2;Interface/Icons/Beta.blp\n");
        source.AddListfile(NewerTag, Release(NewerTag, newer, digest: "sha256:" + new string('1', 64)), newer);
        clock.Advance(TimeSpan.FromDays(8));
        await names.UpdateAsync(false, default);
        // Fetched once and refetched once for the digest.
        Assert.Equal(2, Parts(source).Count(p => p.StartsWith(NewerTag, StringComparison.Ordinal)));
        var observed = source.Observed.Count;
        for (var window = 0; window < 6; window++)
        {
            clock.Advance(TimeSpan.FromMinutes(16));
            var status = await names.UpdateAsync(false, default);
            Assert.Equal(FileNamesState.Ready, status.State);
            Assert.Equal(Tag, status.Tag);
        }
        Assert.Equal(observed, source.Observed.Count);
        // After the week the release is asked about again, but the same refused release fetches no parts.
        clock.Advance(TimeSpan.FromDays(8));
        Assert.Equal("LISTFILE_INVALID", (await names.UpdateAsync(false, default)).Code);
        Assert.Equal(observed + 1, source.Observed.Count);
        Assert.Empty(Parts(source, observed));
        // Forced, its stored parts are checked again and every part is fetched once more for the digest.
        await names.UpdateAsync(true, default);
        Assert.Single(Parts(source, observed));
    }

    [Fact]
    public async Task ABrokenReleaseWithNoCopyIsAskedAboutButNotDownloadedAgain()
    {
        var (source, clock) = Fake(release: Release(Tag, Small, digest: "sha256:" + new string('0', 64)));
        var names = new FileNames(source, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        Assert.Equal(2, Parts(source).Length);
        for (var window = 0; window < 4; window++)
        {
            clock.Advance(TimeSpan.FromMinutes(16));
            var status = await names.UpdateAsync(false, default);
            Assert.Equal(FileNamesState.Failed, status.State);
            Assert.Equal("LISTFILE_INVALID", status.Code);
        }
        Assert.Equal(5, Releases(source));
        Assert.Equal(2, Parts(source).Length);
        // A fixed release (another SHA-256) is downloaded at once.
        source.AddListfile(Tag, Release(Tag, Small), Small);
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(FileNamesState.Ready, (await names.UpdateAsync(false, default)).State);
    }

    [Fact]
    public async Task TwoConcurrentUpdatesFetchOnce()
    {
        var (source, clock) = Fake();
        var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The first update waits at its switch read before the part, once the release was asked for.
        var gate = new Switch { Read = async () => { if (Releases(source) > 0) { verifying.TrySetResult(); await go.Task; } return true; } };
        var names = new FileNames(source, store, gate, clock);
        var first = names.UpdateAsync(false, default);
        await verifying.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var second = names.UpdateAsync(false, default);
        go.SetResult();
        var statuses = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(statuses, status => Assert.Equal(FileNamesState.Ready, status.State));
        Assert.Equal(1, Releases(source));
        Assert.Single(Parts(source));
    }

    [Fact]
    public async Task AnUnrecordedReleaseReplyIsNotACheck()
    {
        var (fake, clock) = Fake();
        var source = new Wrapped(fake) { RecordReleases = false };
        var names = new FileNames(source, store, new Switch(), clock);
        var status = await names.UpdateAsync(false, default);
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Null(status.CheckedAt);
        // Not a check, so the next unforced update asks again.
        await names.UpdateAsync(false, default);
        Assert.Equal(2, Releases(fake));
        Assert.Single(Parts(fake));
    }

    [Fact]
    public async Task AClockMovedBackMakesTheCheckDue()
    {
        var (source, clock) = Fake();
        var names = new FileNames(source, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        clock.Advance(TimeSpan.FromDays(-1));
        Assert.Equal(FileNamesState.Ready, (await names.UpdateAsync(false, default)).State);
        Assert.Equal(2, Releases(source));
        Assert.Single(Parts(source));
    }

    [Theory]
    [InlineData(FileNames.MaxTagsTried - 1, FileNamesState.Ready)]
    [InlineData(FileNames.MaxTagsTried, FileNamesState.NotReady)]
    public async Task ALoadTriesAtMostMaxTagsTried(int broken, FileNamesState state)
    {
        var (source, clock) = Fake();
        await new FileNames(source, store, new Switch(), clock).UpdateAsync(false, default);
        // Newer releases whose parts were never fetched.
        for (var i = 0; i < broken; i++)
        {
            var tag = "t" + i;
            source.AddReply(ObservationRequest.ListfileProduct, ObservationRequest.ListfileReleaseRegion, ObservationKind.ListfileRelease, Release(tag, Small));
            await source.ObserveAsync(new(ObservationKind.ListfileRelease, ObservationRequest.ListfileProduct, ObservationRequest.ListfileReleaseRegion), default);
        }
        var status = await new FileNames(source, store, new Switch(), clock).StatusAsync(default);
        Assert.Equal(state, status.State);
        if (state == FileNamesState.Ready) Assert.Equal(Tag, status.Tag);
    }

    [Fact]
    public async Task ALoadThatLayer2FailsHasNoNamesAndIsReadAgainAfterAMinute()
    {
        var (fake, clock) = Fake();
        await new FileNames(fake, store, new Switch(), clock).UpdateAsync(false, default);
        var source = new Wrapped(fake) { ThrowOnGet = true };
        var names = new FileNames(source, store, new Switch(), clock);
        var status = await names.StatusAsync(default);
        Assert.Equal(FileNamesState.NotReady, status.State);
        Assert.Equal("LISTFILE_UNREADABLE", status.Code);
        Assert.Null(await names.CurrentAsync(default));
        source.ThrowOnGet = false;
        // The failed load is served for a minute without reading history again.
        Assert.Equal(1, names.Loads);
        Assert.Null(await names.CurrentAsync(default));
        Assert.Equal(1, names.Loads);
        clock.Advance(FileNames.UnreadableRetry);
        source.ThrowOnHistory = true;
        Assert.Null(await names.CurrentAsync(default));
        Assert.Equal(2, names.Loads);
        source.ThrowOnHistory = false;
        clock.Advance(FileNames.UnreadableRetry);
        Assert.Equal(FileNamesState.Ready, (await names.StatusAsync(default)).State);
        Assert.NotNull(await names.CurrentAsync(default));
        Assert.Equal(3, names.Loads);
    }

    [Fact]
    public async Task AnUpdateOrASwitchChangeRetriesAFailedLoadAtOnce()
    {
        var (fake, clock) = Fake();
        await new FileNames(fake, store, new Switch(), clock).UpdateAsync(false, default);
        var source = new Wrapped(fake) { ThrowOnGet = true };
        var toggle = new Switch();
        var names = new FileNames(source, store, toggle, clock);
        Assert.Null(await names.CurrentAsync(default));
        source.ThrowOnGet = false;
        toggle.On = false;
        Assert.Null(await names.CurrentAsync(default));
        toggle.On = true;
        Assert.NotNull(await names.CurrentAsync(default));
        Assert.Equal(2, names.Loads);

        var other = new FileNames(source, store, new Switch(), clock);
        source.ThrowOnGet = true;
        Assert.Null(await other.CurrentAsync(default));
        source.ThrowOnGet = false;
        Assert.Equal(FileNamesState.Ready, (await other.UpdateAsync(false, default)).State);
        Assert.Equal(2, other.Loads);
    }

    [Fact]
    public async Task AStoreFailureInAnUpdateIsAFailedFetchWithBackOff()
    {
        var (source, clock) = Fake();
        var names = new FileNames(source, store, new Switch(), clock);
        Assert.Equal(FileNamesState.NotReady, (await names.StatusAsync(default)).State);
        // The verified parts can't be kept in history.
        source.FailRecording();
        var status = await names.UpdateAsync(false, default);
        Assert.Equal(FileNamesState.Failed, status.State);
        Assert.Equal("LISTFILE_FETCH_FAILED", status.Code);
        Assert.Null(await names.CurrentAsync(default));
        source.FailRecording(null);
        var observed = source.Observed.Count;
        Assert.Equal("LISTFILE_FETCH_FAILED", (await names.UpdateAsync(false, default)).Code);
        Assert.Equal(observed, source.Observed.Count);
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(FileNamesState.Ready, (await names.UpdateAsync(false, default)).State);
        // Staged, so no part is fetched again.
        Assert.Single(Parts(source));
    }

    [Fact]
    public async Task AGeneratedStoreFailureInAnUpdateIsAFailedFetch()
    {
        var (source, clock) = Fake();
        var failing = new FailingStore(store) { FailPuts = true };
        var names = new FileNames(source, failing, new Switch(), clock);
        var status = await names.UpdateAsync(false, default);
        Assert.Equal(FileNamesState.Failed, status.State);
        Assert.Equal("LISTFILE_FETCH_FAILED", status.Code);
        failing.FailPuts = false;
        Assert.Equal(FileNamesState.Ready, (await names.UpdateAsync(true, default)).State);
    }

    [Fact]
    public async Task NoGeneratedStoreIsOff()
    {
        var (source, clock) = Fake();
        var names = new FileNames(source, null, new Switch(), clock);
        Assert.Equal(FileNamesState.Off, (await names.UpdateAsync(true, default)).State);
        Assert.Equal(FileNamesState.Off, (await names.StatusAsync(default)).State);
        Assert.Empty(source.Observed);
    }

    // ---------------------------------------------------------------- #186: the copy in history, the index in the generated store

    private static byte[] Version(int release, int names = 200)
    {
        var text = new StringBuilder();
        for (var id = 1; id <= names; id++)
            text.Append(id).Append(";World/Maps/Zone").Append(id).Append(id % 50 == 0 ? $"/v{release}" : "").Append("/File.blp\n");
        text.Append(10_000 + release).Append(";Added/In").Append(release).Append(".blp\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static Dictionary<uint, string> NamesIn(byte[] csv) =>
        Encoding.ASCII.GetString(csv).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(';', 2))
            .ToDictionary(p => uint.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture), p => p[1]);

    private static string TagOf(int release) => "r" + release.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<IReadOnlyList<HistoryEntry>> History(IObjectSource source, HistoryKind kind, string? tag = null) =>
        await source.ReadHistoryAsync(new(Product: tag, Kind: kind, Limit: 100_000), default);

    /// <summary>Publishes <paramref name="csv"/> as release <paramref name="release"/> and runs a forced update.</summary>
    private async Task<FileNamesStatus> PublishAsync(InMemoryObjectSource source, FileNames names, int release, byte[] csv)
    {
        source.AddListfile(TagOf(release), Release(TagOf(release), csv), csv);
        return await names.UpdateAsync(true, default);
    }

    private async Task AssertNamesAsync(FileNames names, byte[] csv)
    {
        var index = await names.CurrentAsync(default);
        Assert.NotNull(index);
        Assert.Equal(NamesIn(csv).OrderBy(n => n.Key).Select(n => (n.Key, n.Value)).ToList(), await Walk(index!));
    }

    [Fact]
    public async Task TheFirstReleaseIsKeptAsPartsAndTheNextAsOneDelta()
    {
        var (source, clock) = Fake(TagOf(1), Version(1));
        var names = new FileNames(source, store, new Switch(), clock);
        Assert.Equal(FileNamesState.Ready, (await names.UpdateAsync(false, default)).State);
        Assert.Single(await History(source, HistoryKind.ListfilePart, TagOf(1)));
        Assert.Empty(await History(source, HistoryKind.ListfileDelta));

        var status = await PublishAsync(source, names, 2, Version(2));
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Equal(TagOf(2), status.Tag);
        var delta = Assert.Single(await History(source, HistoryKind.ListfileDelta));
        Assert.Equal((TagOf(2), ObservationRequest.ListfileDeltaRegion), (delta.Product, delta.Region));
        Assert.Empty(await History(source, HistoryKind.ListfilePart, TagOf(2)));
        // The part was fetched without recording it, then the release was kept as the delta alone.
        Assert.All(source.Observed.Where(o => o.Kind == ObservationKind.ListfilePart), o => Assert.False(o.Record));
        await AssertNamesAsync(names, Version(2));
    }

    [Fact]
    public async Task AChainHoldsSixteenDeltasThenTheNextReleaseIsFullAgain()
    {
        var (source, clock) = Fake(TagOf(1), Version(1));
        var names = new FileNames(source, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        // Releases 2..17 are the 16 deltas on release 1; release 18 starts a new chain.
        for (var release = 2; release <= 18; release++)
            Assert.Equal(FileNamesState.Ready, (await PublishAsync(source, names, release, Version(release))).State);
        Assert.Equal(16, (await History(source, HistoryKind.ListfileDelta)).Count);
        Assert.Empty(await History(source, HistoryKind.ListfilePart, TagOf(17)));
        Assert.Single(await History(source, HistoryKind.ListfilePart, TagOf(18)));
        Assert.Equal(FileNamesState.Ready, (await PublishAsync(source, names, 19, Version(19))).State);
        Assert.Empty(await History(source, HistoryKind.ListfilePart, TagOf(19)));

        // The 16-delta chain rebuilds from history alone.
        store.Clear();
        var hidden = new Wrapped(source);
        foreach (var entry in await History(source, HistoryKind.ListfilePart, TagOf(18))) hidden.Hidden.Add(entry.Document);
        foreach (var entry in await History(source, HistoryKind.ListfileDelta, TagOf(19))) hidden.Hidden.Add(entry.Document);
        var restarted = new FileNames(hidden, store, new Switch(), clock);
        Assert.Equal(TagOf(17), (await restarted.StatusAsync(default)).Tag);
        await AssertNamesAsync(restarted, Version(17));
    }

    [Fact]
    public async Task ADeltaOverSixteenMiBIsKeptAsAFullCopy()
    {
        var (source, clock) = Fake(TagOf(1), Small);
        var names = new FileNames(source, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        // Every name is new, so the delta would hold the whole 16 MiB file and more.
        var status = await PublishAsync(source, names, 2, Large.Value);
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Empty(await History(source, HistoryKind.ListfileDelta));
        Assert.Equal(3, (await History(source, HistoryKind.ListfilePart, TagOf(2))).Count);
    }

    [Fact]
    public async Task ARestartWithTheIndexPresentReadsNoParts()
    {
        var (source, clock) = Fake(asset: Large.Value);
        var first = await new FileNames(source, store, new Switch(), clock).UpdateAsync(false, default);
        var parts = (await History(source, HistoryKind.ListfilePart)).Select(e => e.Document).ToHashSet();
        var asked = source.Asked.Count;
        var names = new FileNames(source, store, new Switch(), clock);
        Assert.Equal(first, await names.StatusAsync(default));
        Assert.Equal("World/Maps/Zone7/File7.blp", await (await names.CurrentAsync(default))!.NameOfAsync(7, default));
        Assert.DoesNotContain(source.Asked.Skip(asked), a => parts.Contains(a.Key));
    }

    [Fact]
    public async Task AWipedGeneratedStoreIsRebuiltFromPartsAndDeltas()
    {
        var (source, clock) = Fake(TagOf(1), Version(1));
        var names = new FileNames(source, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        await PublishAsync(source, names, 2, Version(2));
        var built = await PublishAsync(source, names, 3, Version(3));
        Assert.Equal(2, (await History(source, HistoryKind.ListfileDelta)).Count);

        store.Clear();
        var restarted = new FileNames(source, store, new Switch(), clock);
        var status = await restarted.StatusAsync(default);
        Assert.Equal(built with { CheckedAt = status.CheckedAt }, status);
        await AssertNamesAsync(restarted, Version(3));
        // Rebuilt for good: one more restart opens it without reading history's copy.
        Assert.Equal(GeneratedReadState.Present, (await ListfileStore.OpenAsync(store, TagOf(3), Sha(Version(3)))).State);
    }

    [Fact]
    public async Task ABrokenChainMakesItsReleasesUnusableAndTheNextTagIsTried()
    {
        var (fake, clock) = Fake(TagOf(1), Version(1));
        var names = new FileNames(fake, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        await PublishAsync(fake, names, 2, Version(2));
        await PublishAsync(fake, names, 3, Version(3));
        // Release 2's delta is gone, so release 3 has no base.
        var source = new Wrapped(fake);
        foreach (var entry in await History(fake, HistoryKind.ListfileDelta, TagOf(2))) source.Hidden.Add(entry.Document);
        store.Clear();
        var restarted = new FileNames(source, store, new Switch(), clock);
        var status = await restarted.StatusAsync(default);
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Equal(TagOf(1), status.Tag);
        await AssertNamesAsync(restarted, Version(1));
    }

    [Fact]
    public async Task StagingResumesAfterARestartAndIsDeletedOnceKept()
    {
        var (source, clock) = Fake(asset: Large.Value);
        source.FailObservation(Tag, "p1", ObservationKind.ListfilePart, ObjectState.Unavailable, "CDN_OFFLINE");
        Assert.Equal("LISTFILE_FETCH_OFFLINE", (await new FileNames(source, store, new Switch(), clock).UpdateAsync(false, default)).Code);
        var sha = Sha(Large.Value);
        Assert.True(await store.HasAsync(FileNames.StagedKey(Tag, sha, 0)));
        // Nothing was kept in history before the whole file was verified.
        Assert.Empty(await History(source, HistoryKind.ListfilePart));

        source.AddReply(Tag, "p1", ObservationKind.ListfilePart, Large.Value.AsSpan(Part, Part).ToArray());
        var observed = source.Observed.Count;
        var restarted = new FileNames(source, store, new Switch(), clock);
        Assert.Equal(FileNamesState.Ready, (await restarted.UpdateAsync(true, default)).State);
        Assert.Equal([Tag + "/p1", Tag + "/p2"], Parts(source, observed));
        for (var part = 0; part < 3; part++) Assert.False(await store.HasAsync(FileNames.StagedKey(Tag, sha, part)));
        Assert.Equal(3, (await History(source, HistoryKind.ListfilePart)).Count);
    }

    [Fact]
    public async Task ACorruptIndexIsRebuiltOnLoad()
    {
        var (source, clock) = Fake();
        await new FileNames(source, store, new Switch(), clock).UpdateAsync(false, default);
        store.Damage(ListfileStore.ManifestKeys(Tag, Sha(Small)).Paths);
        var names = new FileNames(source, store, new Switch(), clock);
        Assert.Equal(FileNamesState.Ready, (await names.StatusAsync(default)).State);
        await AssertNamesAsync(names, Small);
    }

    [Fact]
    public async Task AFailedIndexReadAnswersNoNameAndTheNextLoadRepairsIt()
    {
        var (source, clock) = Fake();
        var names = new FileNames(source, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        var index = (await names.CurrentAsync(default))!;
        var manifests = ListfileStore.ManifestKeys(Tag, Sha(Small));
        foreach (var key in store.Keys.Where(k => k != manifests.Names && k != manifests.Paths)) store.Damage(key);
        Assert.Null(await index.NameOfAsync(5, default));
        Assert.Empty(await index.IdsOfAsync("World/Maps/Azeroth/Azeroth.wdt", default));
        Assert.Empty(await Walk(index));
        Assert.True(index.IsDamaged);
        var loads = names.Loads;
        var repaired = await names.CurrentAsync(default);
        Assert.Equal(loads + 1, names.Loads);
        Assert.NotSame(index, repaired);
        Assert.Equal("World/Maps/Azeroth/Azeroth.wdt", await repaired!.NameOfAsync(5, default));
        Assert.Same(repaired, await names.CurrentAsync(default));
        // The page that lacked names can say so: the status carries LISTFILE_UNREADABLE for a minute.
        var status = await names.StatusAsync(default);
        Assert.Equal((FileNamesState.Ready, "LISTFILE_UNREADABLE"), (status.State, status.Code));
        clock.Advance(FileNames.UnreadableRetry);
        Assert.Null((await names.StatusAsync(default)).Code);
    }

    [Fact]
    public async Task ALegacyProfileWithPartsInHistoryStillLoads()
    {
        var (source, clock) = Fake(asset: Large.Value);
        // As the engine before #186 kept a copy: the release and every part recorded as they were fetched.
        await source.ObserveAsync(new(ObservationKind.ListfileRelease, ObservationRequest.ListfileProduct, ObservationRequest.ListfileReleaseRegion), default);
        for (var part = 0; part < 3; part++)
            Assert.True((await source.ObserveAsync(new(ObservationKind.ListfilePart, Tag, ObservationRequest.ListfilePartRegion(part)), default)).Recorded);
        var names = new FileNames(source, store, new Switch(), clock);
        var status = await names.StatusAsync(default);
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Equal(Tag, status.Tag);
        await AssertNamesAsync(names, Large.Value);
        // The next release is a delta on the legacy copy.
        var next = Large.Value.Concat(Encoding.ASCII.GetBytes("99999999;New/Name.blp\n")).ToArray();
        source.AddListfile(NewerTag, Release(NewerTag, next), next);
        Assert.Equal(NewerTag, (await names.UpdateAsync(true, default)).Tag);
        Assert.Single(await History(source, HistoryKind.ListfileDelta, NewerTag));
    }

    [Fact]
    public async Task LoadsReadInteractivelyAndAnUpdateReadsItsBaseInTheBackground()
    {
        var (source, clock) = Fake(TagOf(1), Version(1));
        await new FileNames(source, store, new Switch(), clock).UpdateAsync(false, default);
        store.Clear();
        var names = new FileNames(source, store, new Switch(), clock);
        var asked = source.Asked.Count;
        Assert.NotNull(await names.CurrentAsync(default));
        Assert.All(source.Asked.Skip(asked), a => Assert.Equal(FetchPriority.Interactive, a.Priority));
        asked = source.Asked.Count;
        await PublishAsync(source, names, 2, Version(2));
        Assert.NotEmpty(source.Asked.Skip(asked));
        Assert.All(source.Asked.Skip(asked), a => Assert.Equal(FetchPriority.Background, a.Priority));
    }

    [Fact]
    public async Task ASuccessfulUpdateCompactsTheGeneratedStore()
    {
        var (source, clock) = Fake();
        var names = new FileNames(source, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        Assert.Equal(1, store.Compactions);
        // A check with nothing new keeps nothing and compacts nothing.
        await names.UpdateAsync(true, default);
        Assert.Equal(1, store.Compactions);
    }

    [Fact]
    public async Task ABaseIsFoundUnderAnOlderTagWithTheSameBytes()
    {
        var (source, clock) = Fake(TagOf(1), Version(1));
        var names = new FileNames(source, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        // Release 2 has release 1's bytes, but keeping it fails, so it has no copy.
        source.FailRecording();
        var failed = await PublishAsync(source, names, 2, Version(1));
        Assert.Equal((TagOf(1), "LISTFILE_FETCH_FAILED"), (failed.Tag, failed.Code));
        source.FailRecording(null);
        // Release 3 is a delta against the loaded release 1: its base SHA-256 is release 2's too, and release 2 is newer.
        Assert.Equal(TagOf(3), (await PublishAsync(source, names, 3, Version(3))).Tag);
        Assert.Single(await History(source, HistoryKind.ListfileDelta, TagOf(3)));
        store.Clear();
        var restarted = new FileNames(source, store, new Switch(), clock);
        Assert.Equal(TagOf(3), (await restarted.StatusAsync(default)).Tag);
        await AssertNamesAsync(restarted, Version(3));
    }

    [Fact]
    public async Task ATagPublishedAgainWithOtherBytesIsKeptWhole()
    {
        var (source, clock) = Fake();
        var names = new FileNames(source, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        var other = Encoding.UTF8.GetBytes("2;Interface/Icons/Beta.blp\n7;Sound/Other.ogg\n");
        source.AddListfile(Tag, Release(Tag, other), other);
        var status = await names.UpdateAsync(true, default);
        Assert.Equal((FileNamesState.Ready, Tag, 2), (status.State, status.Tag, status.Count));
        Assert.Empty(await History(source, HistoryKind.ListfileDelta));
        Assert.Equal(2, (await History(source, HistoryKind.ListfilePart, Tag)).Count);
        store.Clear();
        var restarted = new FileNames(source, store, new Switch(), clock);
        Assert.Equal(2, (await restarted.StatusAsync(default)).Count);
        await AssertNamesAsync(restarted, other);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARecordThatIsNotKeptFailsTheUpdateAndTheOlderCopyServes(bool full)
    {
        // Full: a 3-part copy whose second record isn't kept. Delta: the one delta record isn't kept.
        var (first, next) = full ? (Small, Large.Value) : (Version(1), Version(2));
        var (source, clock) = Fake(TagOf(1), first);
        await new FileNames(source, store, new Switch(), clock).UpdateAsync(false, default);
        var wrapped = new Wrapped(source) { RecordLimit = full ? 1 : 0 };
        var names = new FileNames(wrapped, store, new Switch(), clock);
        source.AddListfile(TagOf(2), Release(TagOf(2), next), next);
        var status = await names.UpdateAsync(true, default);
        Assert.Equal((FileNamesState.Ready, TagOf(1), "LISTFILE_FETCH_FAILED"), (status.State, status.Tag, status.Code));
        Assert.Equal(GeneratedReadState.Absent, (await ListfileStore.OpenAsync(store, TagOf(2), Sha(next))).State);
        Assert.True(await store.HasAsync(FileNames.StagedKey(TagOf(2), Sha(next), 0)));
        Assert.Equal(full ? 1 : 0, (await History(source, HistoryKind.ListfilePart, TagOf(2))).Count);
        Assert.Empty(await History(source, HistoryKind.ListfileDelta));
        var restarted = new FileNames(source, store, new Switch(), clock);
        Assert.Equal(TagOf(1), (await restarted.StatusAsync(default)).Tag);
        await AssertNamesAsync(restarted, first);
    }

    [Fact]
    public async Task AChainWhoseBaseBytesAreNotTheDeltasBaseIsUnusable()
    {
        var clock = new Clock();
        var source = new InMemoryObjectSource { Time = clock };
        var a = Version(1);
        // Same length, other bytes: what the stored parts of release 1 hold.
        var b = (byte[])a.Clone();
        b[Array.IndexOf(b, (byte)'Z')] = (byte)'Y';
        var c = Version(2);
        source.AddListfile(TagOf(1), Release(TagOf(1), a), b);
        await source.ObserveAsync(new(ObservationKind.ListfileRelease, ObservationRequest.ListfileProduct, ObservationRequest.ListfileReleaseRegion), default);
        await source.ObserveAsync(new(ObservationKind.ListfilePart, TagOf(1), ObservationRequest.ListfilePartRegion(0)), default);
        var delta = await ListfileDelta.EncodeAsync(new MemoryStream(a), new MemoryStream(c), SHA256.HashData(a), SHA256.HashData(c), c.Length,
            ObservationRequest.ListfileMaxDeltaBytes);
        source.AddReply(ObservationRequest.ListfileProduct, ObservationRequest.ListfileReleaseRegion, ObservationKind.ListfileRelease, Release(TagOf(2), c));
        await source.ObserveAsync(new(ObservationKind.ListfileRelease, ObservationRequest.ListfileProduct, ObservationRequest.ListfileReleaseRegion), default);
        Assert.True((await source.RecordAsync(new Observation(ObservationKind.ListfileDelta, TagOf(2), ObservationRequest.ListfileDeltaRegion,
            new Uri(WowListfileObservations.AssetUrl(TagOf(2))), clock.GetUtcNow(), delta!), default)).Recorded);

        var names = new FileNames(source, store, new Switch(), clock);
        Assert.Equal(FileNamesState.NotReady, (await names.StatusAsync(default)).State);
        Assert.Equal(GeneratedReadState.Absent, (await ListfileStore.OpenAsync(store, TagOf(2), Sha(c))).State);
    }

    [Fact]
    public async Task ANewTagWithTheLoadedBytesIsKeptAsAnEmptyDeltaWithoutDownloading()
    {
        var (source, clock) = Fake(TagOf(1), Version(1));
        var names = new FileNames(source, store, new Switch(), clock);
        await names.UpdateAsync(false, default);
        var observed = source.Observed.Count;
        var status = await PublishAsync(source, names, 2, Version(1));
        Assert.Equal((FileNamesState.Ready, TagOf(2)), (status.State, status.Tag));
        Assert.Empty(Parts(source, observed));
        var delta = Assert.Single(await History(source, HistoryKind.ListfileDelta, TagOf(2)));
        var bytes = (await source.GetKeyAsync(new ObjectRequest(delta.Document), null, FetchPriority.Interactive, default)).Bytes!;
        Assert.Equal(ListfileDelta.FixedHeaderBytes + 4, bytes.Length);
        store.Clear();
        var restarted = new FileNames(source, store, new Switch(), clock);
        Assert.Equal(TagOf(2), (await restarted.StatusAsync(default)).Tag);
        await AssertNamesAsync(restarted, Version(1));
    }

    private static async Task<List<(uint Id, string Name)>> Walk(LoadedNames index)
    {
        var all = new List<(uint, string)>();
        await foreach (var entry in index.FromAsync(0, default)) all.Add(entry);
        return all;
    }

    /// <summary>A generated store whose puts throw on demand.</summary>
    private sealed class FailingStore(IGeneratedStore inner) : IGeneratedStore
    {
        public bool FailPuts { get; set; }
        public ValueTask<bool> HasAsync(GeneratedKey key, CancellationToken cancellationToken = default) => inner.HasAsync(key, cancellationToken);
        public ValueTask<GeneratedRead> ReadAsync(GeneratedKey key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public ValueTask<GeneratedRangeRead> ReadRangeAsync(GeneratedKey key, long offset, Memory<byte> destination, CancellationToken cancellationToken = default) =>
            inner.ReadRangeAsync(key, offset, destination, cancellationToken);
        public Task<IReadOnlyList<GeneratedPutOutcome>> PutAsync(IReadOnlyList<GeneratedPut> batch, CancellationToken cancellationToken = default) =>
            FailPuts ? throw new GeneratedStoreException(GeneratedStoreCodes.DiskFull, "The disk is full.") : inner.PutAsync(batch, cancellationToken);
        public Task DeleteAsync(IReadOnlyList<GeneratedKey> keys, CancellationToken cancellationToken = default) => inner.DeleteAsync(keys, cancellationToken);
        public Task<GeneratedCompactReport> CompactAsync(double minDeadFraction = 0.25, int maxPacks = 1, CancellationToken cancellationToken = default) =>
            inner.CompactAsync(minDeadFraction, maxPacks, cancellationToken);
    }

    /// <summary>The fake, with layer 2 exceptions and unrecorded release replies on demand.</summary>
    private sealed class Wrapped(InMemoryObjectSource inner) : IObjectSource
    {
        public bool ThrowOnGet { get; set; }
        public bool ThrowOnHistory { get; set; }
        public bool RecordReleases { get; set; } = true;
        /// <summary>Keys that read as not stored.</summary>
        public HashSet<ObjectKey> Hidden { get; } = [];
        /// <summary>Records allowed before every later one comes back Present but not Recorded; null records all.</summary>
        public int? RecordLimit { get; set; }
        private int records;

        public Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken) =>
            ThrowOnGet ? throw new IOException("store unreadable")
            : Hidden.Contains(request.Key) ? Task.FromResult(new ObjectResult(request.Key, ObjectState.Unavailable, Code: "OBJECT_ACCESS_NO_CDN_CONTEXT"))
            : inner.GetKeyAsync(request, cdn, priority, cancellationToken);
        public Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority,
            IProgress<ObjectProgress>? progress, CancellationToken cancellationToken) => inner.GetKeysAsync(requests, cdn, priority, progress, cancellationToken);
        public Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority,
            CancellationToken cancellationToken) => inner.GetRangeAsync(request, offset, destination, cdn, priority, cancellationToken);
        public async Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken)
        {
            var result = await inner.ObserveAsync(request, cancellationToken);
            return request.Kind == ObservationKind.ListfileRelease && !RecordReleases ? result with { Recorded = false } : result;
        }
        public Task<ObservationResult> RecordAsync(Observation observation, CancellationToken cancellationToken) =>
            RecordLimit is { } limit && Interlocked.Increment(ref records) > limit
                ? Task.FromResult(new ObservationResult(ObjectState.Present, observation, Recorded: false))
                : inner.RecordAsync(observation, cancellationToken);
        public Task<long> CountPresentAsync(IReadOnlyList<VibeMiner.Objects.ObjectKey> keys, CancellationToken cancellationToken) =>
            inner.CountPresentAsync(keys, cancellationToken);
        public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<VibeMiner.Objects.ObjectKey> keys, CancellationToken cancellationToken) =>
            inner.HasKeysAsync(keys, cancellationToken);
        public Task<IReadOnlyList<VibeMiner.Objects.HistoryEntry>> ReadHistoryAsync(VibeMiner.Objects.HistoryQuery query, CancellationToken cancellationToken) =>
            ThrowOnHistory ? throw new IOException("history unreadable") : inner.ReadHistoryAsync(query, cancellationToken);
    }
}
