using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Files.Names;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using Xunit;

namespace VibeMiner.Files.Tests.Names;

/// <summary>
/// The wow-listfile names (D105) over the layer 2 fake: release and part observations, the stored copy read back from
/// history, codes, back-off and the switch. Authored listfiles only.
/// </summary>
public sealed class FileNamesTests
{
    private const string Tag = "202610281921";
    private const string NewerTag = "202611011200";
    private const int Part = ObservationRequest.ListfilePartBytes;
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
        var names = new FileNames(source, new Switch(false), clock);
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
            var names = new FileNames(source, off, clock);
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
        var names = new FileNames(source, new Switch(), clock);
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
        Assert.Equal("World/Maps/Azeroth/Azeroth.wdt", index!.NameOf(5));
        Assert.Equal(new uint[] { 900u }, index.IdsOf("sound\\music\\theme.MP3"));
    }

    [Fact]
    public async Task AMultiPartAssetIsJoinedAndVerified()
    {
        var (source, clock) = Fake(asset: Large.Value);
        var names = new FileNames(source, new Switch(), clock);
        var status = await names.UpdateAsync(false, default);
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Equal([Tag + "/p0", Tag + "/p1", Tag + "/p2"], Parts(source));
        var index = await names.CurrentAsync(default);
        Assert.Equal(status.Count, index!.Count);
        // A line that straddles a part boundary reads whole.
        var last = index.From(0).Last();
        Assert.Equal($"World/Maps/Zone{last.Id}/File{last.Id}.blp", last.Name);
    }

    [Fact]
    public async Task ASecondUpdateWithinAWeekFetchesNothing()
    {
        var (source, clock) = Fake();
        var names = new FileNames(source, new Switch(), clock);
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
        var names = new FileNames(source, new Switch(), clock);
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
        var names = new FileNames(source, new Switch(), clock);
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
        Assert.Equal("Interface/Icons/Beta.blp", current!.NameOf(2));
        Assert.Null(current.NameOf(5));
        // The old index is untouched for whoever still holds it.
        Assert.Equal("Interface/Icons/Alpha.blp", old!.NameOf(1));
    }

    [Fact]
    public async Task AMissingPartFailsAndTheNextAttemptFetchesOnlyWhatIsMissing()
    {
        var (source, clock) = Fake(asset: Large.Value);
        var p1 = Large.Value.AsSpan(Part, Part).ToArray();
        source.FailObservation(Tag, "p1", ObservationKind.ListfilePart, ObjectState.NotFound, "CDN_NOT_FOUND");
        var names = new FileNames(source, new Switch(), clock);
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
        var names = new FileNames(source, new Switch(), clock);
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
        var names = new FileNames(source, new Switch(), clock);
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
        var names = new FileNames(source, new Switch(), clock);
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
        var names = new FileNames(source, new Switch(), clock);
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
        var names = new FileNames(source, new Switch(), clock);
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
        var names = new FileNames(source, new Switch(), clock);
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
        var names = new FileNames(source, new Switch(), clock);
        await names.UpdateAsync(false, default);
        var newer = Encoding.UTF8.GetBytes("2;Interface/Icons/Beta.blp\n");
        source.AddListfile(NewerTag, Release(NewerTag, newer, digest: "sha256:" + new string('1', 64)), newer);
        var status = await names.UpdateAsync(true, default);
        Assert.Equal(FileNamesState.Ready, status.State);
        Assert.Equal(Tag, status.Tag);
        Assert.Equal("LISTFILE_INVALID", status.Code);
        Assert.Equal("Interface/Icons/Alpha.blp", (await names.CurrentAsync(default))!.NameOf(1));
        // The newest release is one this engine refused, so unforced updates wait the week like a current copy.
        clock.Advance(TimeSpan.FromMinutes(16));
        var observed = source.Observed.Count;
        Assert.Equal("LISTFILE_INVALID", (await names.UpdateAsync(false, default)).Code);
        Assert.Equal(observed, source.Observed.Count);

        var restarted = new FileNames(source, new Switch(), clock);
        var loaded = await restarted.StatusAsync(default);
        Assert.Equal(FileNamesState.Ready, loaded.State);
        Assert.Equal(Tag, loaded.Tag);
        Assert.Null(loaded.Code);
    }

    [Fact]
    public async Task AStoredCopyLoadsFromHistoryAlone()
    {
        var (source, clock) = Fake(asset: Large.Value);
        var first = await new FileNames(source, new Switch(), clock).UpdateAsync(false, default);
        var observed = source.Observed.Count;
        var names = new FileNames(source, new Switch(), clock);
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
        await new FileNames(source, new Switch(), clock).UpdateAsync(false, default);
        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        // Every caller reads the switch on entry; the load's first store read waits until all of them are in the call.
        var on = new Switch { Read = () => { if (Interlocked.Increment(ref entered) == callers) inside.SetResult(); return Task.FromResult(true); } };
        source.BeforeAnswer = (_, token) => inside.Task.WaitAsync(token);
        var names = new FileNames(source, on, clock);
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
        var names = new FileNames(source, on, clock);
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
        var names = new FileNames(source, on, clock);
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
        var names = new FileNames(source, on, clock);
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
        var names = new FileNames(source, new Switch(), clock);
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
        var names = new FileNames(source, new Switch(), clock);
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
        source.BeforeAnswer = async (_, token) => { verifying.TrySetResult(); await go.Task.WaitAsync(token); };
        var names = new FileNames(source, new Switch(), clock);
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
        var names = new FileNames(source, new Switch(), clock);
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
        var names = new FileNames(source, new Switch(), clock);
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
        await new FileNames(source, new Switch(), clock).UpdateAsync(false, default);
        // Newer releases whose parts were never fetched.
        for (var i = 0; i < broken; i++)
        {
            var tag = "t" + i;
            source.AddReply(ObservationRequest.ListfileProduct, ObservationRequest.ListfileReleaseRegion, ObservationKind.ListfileRelease, Release(tag, Small));
            await source.ObserveAsync(new(ObservationKind.ListfileRelease, ObservationRequest.ListfileProduct, ObservationRequest.ListfileReleaseRegion), default);
        }
        var status = await new FileNames(source, new Switch(), clock).StatusAsync(default);
        Assert.Equal(state, status.State);
        if (state == FileNamesState.Ready) Assert.Equal(Tag, status.Tag);
    }

    [Fact]
    public async Task ALoadThatLayer2FailsHasNoNamesAndIsReadAgainAfterAMinute()
    {
        var (fake, clock) = Fake();
        await new FileNames(fake, new Switch(), clock).UpdateAsync(false, default);
        var source = new Wrapped(fake) { ThrowOnGet = true };
        var names = new FileNames(source, new Switch(), clock);
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
        await new FileNames(fake, new Switch(), clock).UpdateAsync(false, default);
        var source = new Wrapped(fake) { ThrowOnGet = true };
        var toggle = new Switch();
        var names = new FileNames(source, toggle, clock);
        Assert.Null(await names.CurrentAsync(default));
        source.ThrowOnGet = false;
        toggle.On = false;
        Assert.Null(await names.CurrentAsync(default));
        toggle.On = true;
        Assert.NotNull(await names.CurrentAsync(default));
        Assert.Equal(2, names.Loads);

        var other = new FileNames(source, new Switch(), clock);
        source.ThrowOnGet = true;
        Assert.Null(await other.CurrentAsync(default));
        source.ThrowOnGet = false;
        Assert.Equal(FileNamesState.Ready, (await other.UpdateAsync(false, default)).State);
        Assert.Equal(2, other.Loads);
    }

    [Fact]
    public async Task ALayer2ExceptionInAnUpdateIsAFailedFetchWithBackOff()
    {
        var (fake, clock) = Fake();
        var source = new Wrapped(fake);
        var names = new FileNames(source, new Switch(), clock);
        Assert.Equal(FileNamesState.NotReady, (await names.StatusAsync(default)).State);
        // History reads fine for the load; the store throws while the fetched part is verified.
        source.ThrowOnGet = true;
        var status = await names.UpdateAsync(false, default);
        Assert.Equal(FileNamesState.Failed, status.State);
        Assert.Equal("LISTFILE_FETCH_FAILED", status.Code);
        source.ThrowOnGet = false;
        var observed = fake.Observed.Count;
        Assert.Equal("LISTFILE_FETCH_FAILED", (await names.UpdateAsync(false, default)).Code);
        Assert.Equal(observed, fake.Observed.Count);
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(FileNamesState.Ready, (await names.UpdateAsync(false, default)).State);
        Assert.Equal(FetchPriority.Background, fake.Asked[^1].Priority);
        // A fresh instance's load for a caller reads interactively.
        Assert.NotNull(await new FileNames(fake, new Switch(), clock).CurrentAsync(default));
        Assert.Equal(FetchPriority.Interactive, fake.Asked[^1].Priority);
    }

    /// <summary>The fake, with layer 2 exceptions and unrecorded release replies on demand.</summary>
    private sealed class Wrapped(InMemoryObjectSource inner) : IObjectSource
    {
        public bool ThrowOnGet { get; set; }
        public bool ThrowOnHistory { get; set; }
        public bool RecordReleases { get; set; } = true;

        public Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken) =>
            ThrowOnGet ? throw new IOException("store unreadable") : inner.GetKeyAsync(request, cdn, priority, cancellationToken);
        public Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority,
            IProgress<ObjectProgress>? progress, CancellationToken cancellationToken) => inner.GetKeysAsync(requests, cdn, priority, progress, cancellationToken);
        public Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority,
            CancellationToken cancellationToken) => inner.GetRangeAsync(request, offset, destination, cdn, priority, cancellationToken);
        public async Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken)
        {
            var result = await inner.ObserveAsync(request, cancellationToken);
            return request.Kind == ObservationKind.ListfileRelease && !RecordReleases ? result with { Recorded = false } : result;
        }
        public Task<long> CountPresentAsync(IReadOnlyList<VibeMiner.Objects.ObjectKey> keys, CancellationToken cancellationToken) =>
            inner.CountPresentAsync(keys, cancellationToken);
        public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<VibeMiner.Objects.ObjectKey> keys, CancellationToken cancellationToken) =>
            inner.HasKeysAsync(keys, cancellationToken);
        public Task<IReadOnlyList<VibeMiner.Objects.HistoryEntry>> ReadHistoryAsync(VibeMiner.Objects.HistoryQuery query, CancellationToken cancellationToken) =>
            ThrowOnHistory ? throw new IOException("history unreadable") : inner.ReadHistoryAsync(query, cancellationToken);
    }
}
