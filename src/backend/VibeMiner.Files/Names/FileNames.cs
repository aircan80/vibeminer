using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Generated;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Files.Names;

/// <summary>
/// The wow-listfile names (D105) over layer 2 and the generated store (#186). A check is one
/// <see cref="ObservationKind.ListfileRelease"/> observation (the releases/latest JSON), recorded in the history log; layer 1 owns
/// the transport rules. A download fetches the release's 8 MiB parts without recording them and stages each in the generated
/// store, so it resumes across restarts; the staged parts must have their exact lengths, match GitHub's SHA-256 joined and parse.
/// The verified release is then kept in the object store, which holds everything recoverable: as one
/// <see cref="ObservationKind.ListfileDelta"/> against the loaded copy when that copy's chain has fewer than
/// <see cref="ListfileDelta.MaxChainDeltas"/> deltas since its full copy and the delta fits 16 MiB, else as a full copy of
/// <see cref="ObservationKind.ListfilePart"/> entries. Only once every record came back Recorded is the names index
/// (<see cref="ListfileStore"/>) built from the staged bytes, swapped in, and the staging deleted.
/// A load walks release observations newest first, each distinct tag once, at most <see cref="MaxTagsTried"/>: the first release
/// whose index opens is the copy, read without touching its parts; an index that is absent or corrupt is rebuilt from the
/// release's stored copy (its newest full parts, or a delta whose target is the release, followed back by base SHA-256 to a full
/// copy, at most 16 deltas), and a release with no whole copy is skipped. The generated store is wipeable: everything in it comes
/// back from the object store. At a swap the old copy's index keys the new index doesn't share are deleted, with the staging, and
/// the store is compacted (<see cref="SwapInAsync"/>); a reader still holding the old copy just answers without names. Indexes of
/// other releases (built by a load that fell back to an older tag) and staging of a release that is never finished stay until a
/// collector exists (<see cref="ListfileStore.Keys"/> says why segments can't be deleted blindly).
/// A check is due after a week, or at once when the newest release is neither the loaded copy nor a release this engine refused;
/// after a failure, unforced updates wait 15 min, and they never fetch the parts of a refused release (tag and SHA-256) again.
/// Failures and refusals stay in memory (D113). A layer 2 or generated store exception is a failed attempt in an update and no
/// copy in a load, served for <see cref="UnreadableRetry"/> and then read again (an update or a switch change reads again at
/// once). Names never fail a Files page: an index read that fails answers as no name and marks the copy damaged, and the next
/// load rebuilds it (<see cref="LoadedNames"/>). The switch is read on every call and fails closed; without a generated store
/// names are Off, since they have nowhere to live.
/// </summary>
public sealed class FileNames : IFileNames
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(7);
    public static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(15);
    /// <summary>How long a load that layer 2 failed is served before the next call reads history again.</summary>
    public static readonly TimeSpan UnreadableRetry = TimeSpan.FromMinutes(1);
    /// <summary>History entries read per page.</summary>
    internal const int HistoryPage = 1000;
    /// <summary>Pages read per history walk; a longer history reads as no copy rather than an unbounded read.</summary>
    internal const int MaxHistoryPages = 1000;
    /// <summary>Newest release entries kept while reading history; older ones only date checks already counted.</summary>
    internal const int MaxReleaseEntries = 4096;
    /// <summary>Distinct tags a load tries before it gives up, so a history of broken releases can't make a start slow.</summary>
    internal const int MaxTagsTried = 16;
    /// <summary>Delta documents of one tag read while looking for the one that makes a release.</summary>
    internal const int MaxDeltaCandidates = 8;
    /// <summary>Bytes before each staged part's CSV bytes: its fetch time, UTC ticks.</summary>
    private const int StagedHeader = 8;
    /// <summary>Base bytes fed to a delta chain at a time, so one replay step's output stays small.</summary>
    private const int ReplaySlice = 1 << 20;

    private readonly IObjectSource objects;
    private readonly IGeneratedStore? store;
    private readonly IFileNameSwitch? names;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim loading = new(1, 1);
    private readonly SemaphoreSlim updating = new(1, 1);
    // ListfileStore.BuildAsync needs its caller to serialise builds per store: a load's repair and an update can both build, and
    // two file systems (the CLI's reader and its ensure writer) can share one generated store.
    private static readonly ConditionalWeakTable<IGeneratedStore, SemaphoreSlim> Builds = new();
    private SemaphoreSlim Building => Builds.GetValue(store!, static _ => new SemaphoreSlim(1, 1));
    private volatile Snapshot? snapshot;
    private volatile Failure? failure;
    private volatile Refusal? refused;
    // The last load that layer 2 failed and when; cleared by an update or a switch change.
    private volatile Unread? unread;
    // The loaded copy once one of its reads failed; the next call loads again and rebuilds it.
    private volatile LoadedNames? damaged;
    // When a read of the loaded copy last failed: StatusAsync says so for UnreadableRetry, since that call's page lacked names.
    private long readFailedTicks = -1;
    // The switch as last read: -1 unknown, 0 off, 1 on.
    private int lastOn = -1;
    private int loads;

    private sealed record Copy(LoadedNames Names, ListfileRelease Release, DateTimeOffset FetchedAt);
    /// <summary>The loaded copy (or none), the newest release observation's time and the newest readable release (tag, SHA-256).
    /// <see cref="Unreadable"/> is set on a load that failed; such a snapshot is kept for <see cref="UnreadableRetry"/> only.</summary>
    private sealed record Snapshot(Copy? Copy, DateTimeOffset? CheckedAt, (string Tag, string Sha256)? Newest, string? Unreadable = null);
    private sealed record Unread(Snapshot Snapshot, DateTimeOffset At);
    private sealed record Failure(DateTimeOffset At, string Code, string Message, DateTimeOffset? ObservedAt);
    /// <summary>A release whose stored bytes were refused after fetching them; unforced attempts don't fetch its parts again.</summary>
    private sealed record Refusal(string Tag, string Sha256, ListfileFetchException Error);
    /// <summary>
    /// A release's copy in the object store: the newest part entries of a full copy (<see cref="Root"/>), then the deltas from it
    /// to <see cref="Release"/>, oldest first (none for a full copy). <see cref="FetchedAt"/> is when the release's bytes were fetched.
    /// </summary>
    private sealed record StoredCopy(ListfileRelease Release, ListfileRelease Root, HistoryEntry[] RootParts, IReadOnlyList<byte[]> Deltas,
        DateTimeOffset FetchedAt);
    /// <summary>A stored copy that can't be read whole: a part gone or of the wrong length.</summary>
    private sealed class CopyBrokenException(string message) : Exception(message);

    /// <param name="store">Where the names index and the download staging live; null leaves names Off.</param>
    public FileNames(IObjectSource objects, IGeneratedStore? store, IFileNameSwitch? names, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        this.objects = objects; this.store = store; this.names = names; this.time = time ?? TimeProvider.System;
    }

    /// <summary>How many times history was read to load a copy (tests: single-flight).</summary>
    internal int Loads => Volatile.Read(ref loads);

    /// <summary>The loaded names, or null when the switch is off or nothing usable is stored. Never fetches.</summary>
    internal async Task<LoadedNames?> CurrentAsync(CancellationToken cancellationToken)
    {
        if (!await OnAsync(cancellationToken).ConfigureAwait(false)) return null;
        return (await LoadedAsync(cancellationToken).ConfigureAwait(false)).Copy?.Names;
    }

    public async Task<FileNamesStatus> StatusAsync(CancellationToken cancellationToken)
    {
        if (!await OnAsync(cancellationToken).ConfigureAwait(false)) return new(FileNamesState.Off);
        return Status(await LoadedAsync(cancellationToken).ConfigureAwait(false), failure ?? RecentReadFailure());
    }

    public async Task<FileNamesStatus> UpdateAsync(bool force, CancellationToken cancellationToken)
    {
        if (!await OnAsync(cancellationToken).ConfigureAwait(false)) return new(FileNamesState.Off);
        await updating.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // An update reads history again rather than serve a failed load.
            unread = null;
            var current = await LoadedAsync(cancellationToken).ConfigureAwait(false);
            var now = time.GetUtcNow();
            if (!force && Fresh(current, refused, now)) return Status(current, failure);
            if (!force && failure is { } last && last.At <= now && now - last.At < MinimumGap) return Status(current, last);
            if (current.Unreadable is { } unreadable)
            {
                failure = new(now, "LISTFILE_FETCH_FAILED", Failed(unreadable).Message, null);
                return Status(current, failure);
            }
            try
            {
                if (!await AttemptAsync(force, cancellationToken).ConfigureAwait(false)) return new(FileNamesState.Off);
                failure = null;
            }
            catch (ListfileFetchException error)
            {
                failure = new(time.GetUtcNow(), error.Code, error.Message, error.ObservedAt);
            }
            // A layer 2 or generated store exception is a failed attempt, with the same back-off.
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failure = new(time.GetUtcNow(), "LISTFILE_FETCH_FAILED", Failed(Unreadable(error)).Message, null);
            }
            return Status(snapshot!, failure);
        }
        finally { updating.Release(); }
    }

    // A copy is fresh while the newest release observation is younger than a week and that release is the loaded copy or
    // one this engine refused, so a broken newer release waits the week too. A check time in the future (a clock moved
    // back) is due.
    private static bool Fresh(Snapshot current, Refusal? refusal, DateTimeOffset now) =>
        current.Copy is { } copy && current.CheckedAt is { } at && at <= now && now - at < RefreshInterval && current.Newest is { } newest
        && (newest == (copy.Release.Tag, copy.Release.Sha256) || refusal is not null && newest == (refusal.Tag, refusal.Sha256));

    // One check: the newest release, then the parts staging lacks, verified, kept in the object store, indexed and swapped in.
    // False when the switch went off meanwhile (the parts staged so far stay for the next attempt).
    private async Task<bool> AttemptAsync(bool force, CancellationToken token)
    {
        var result = await objects.ObserveAsync(new(ObservationKind.ListfileRelease, ObservationRequest.ListfileProduct, ObservationRequest.ListfileReleaseRegion),
            token).ConfigureAwait(false);
        // A cancel during the fetch or the history write comes back as a result; it is the caller's, not a failed fetch.
        token.ThrowIfCancellationRequested();
        if (result.State != ObjectState.Present || result.Observation is not { } observation) throw Mapped(result);
        var at = observation.ObservedAt;
        // A recorded reply is a check, readable or not, as it is after a restart.
        if (result.Recorded) Swap(s => s with { CheckedAt = s.CheckedAt is { } c && c >= at ? c : at });
        var release = ListfileRelease.Parse(observation.Bytes, at);
        if (result.Recorded) Swap(s => s with { Newest = (release.Tag, release.Sha256) });
        if (snapshot!.Copy is { } copy && copy.Release.Tag == release.Tag && copy.Release.Sha256 == release.Sha256) return true;
        // A release refused before isn't downloaded again unless asked.
        if (!force && refused is { } refusal && refusal.Tag == release.Tag && refusal.Sha256 == release.Sha256) throw refusal.Error;
        // The same bytes under a new tag are kept as a delta without ops against the loaded copy; nothing is downloaded.
        if (await SameBytesAsync(release, token).ConfigureAwait(false)) return true;
        ListfileFetchException Refuse(string detail)
        {
            var error = Invalid(detail, at);
            refused = new(release.Tag, release.Sha256, error);
            return error;
        }
        var fetched = new HashSet<int>();
        var rechecked = new HashSet<int>();
        var refetched = false;
        DateTimeOffset fetchedAt;
        while (true)
        {
            var verified = await VerifyStagedAsync(release, token).ConfigureAwait(false);
            if (verified.Check == Check.Usable) { fetchedAt = verified.FetchedAt!.Value; break; }
            switch (verified.Check)
            {
                case Check.Invalid:
                    throw Refuse($"The file doesn’t parse: {verified.Detail}");
                case Check.Missing:
                    foreach (var part in verified.Missing!)
                    {
                        // A part this attempt already fetched that still can't be read wasn't kept.
                        if (!fetched.Add(part)) throw Failed($"Part {part} of release {release.Tag} couldn’t be kept.");
                        if (!await FetchPartAsync(release, part, token).ConfigureAwait(false)) return false;
                    }
                    break;
                case Check.BadPart:
                    // A part of the wrong length is fetched once more on its own; wrong again, it is GitHub's answer.
                    var bad = verified.Missing![0];
                    if (!rechecked.Add(bad)) throw Refuse(verified.Detail!);
                    fetched.Add(bad);
                    if (!await FetchPartAsync(release, bad, token).ConfigureAwait(false)) return false;
                    break;
                case Check.Wrong:
                    // Whole-file bytes that don't match GitHub's digest: fetch every part once more, then verify again.
                    if (refetched) throw Refuse(verified.Detail!);
                    refetched = true;
                    for (var part = 0; part < release.Parts; part++)
                    {
                        fetched.Add(part);
                        if (!await FetchPartAsync(release, part, token).ConfigureAwait(false)) return false;
                    }
                    break;
            }
        }

        // GitHub's file, verified: keep it in the object store first, so the index is never the only copy.
        await KeepAsync(release, fetchedAt, token).ConfigureAwait(false);
        LoadedNames built;
        try { built = await BuildAsync(release, fetchedAt, StagedAsync(release, token), rebuild: false, token).ConfigureAwait(false); }
        catch (ListfileFormatException error) { throw Failed($"The staged copy of release {release.Tag} changed: {error.Message}"); }
        await SwapInAsync(built, release, fetchedAt, token).ConfigureAwait(false);
        return true;
    }

    // A new copy in use: swapped in, then what the old copy and the download left in the generated store is deleted and the space
    // reclaimed. The old copy's index keys that the new one doesn't use are deleted (there is no collector yet, and these two are the
    // only indexes known to share segments); a reader still holding the old copy just loses names for that call. Clean-up failures
    // only cost space, so they never fail the update.
    private async Task SwapInAsync(LoadedNames built, ListfileRelease release, DateTimeOffset fetchedAt, CancellationToken token)
    {
        Copy? previous = null;
        Swap(s => { previous = s.Copy; return s with { Copy = new(built, release, fetchedAt) }; });
        var old = refused;
        if (old is not null && old.Tag == release.Tag && old.Sha256 == release.Sha256) refused = null;
        try
        {
            // The download is done; staging of this release (and of a refused one) is no longer needed.
            await DeleteStagingAsync(release, token).ConfigureAwait(false);
            if (old is not null && (old.Tag, old.Sha256) != (release.Tag, release.Sha256))
                await DeleteStagingAsync(old.Tag, old.Sha256, ListfileRelease.MaxAssetBytes, token).ConfigureAwait(false);
            if (previous is { } was && !ReferenceEquals(was.Names, built) && (was.Release.Tag, was.Release.Sha256) != (release.Tag, release.Sha256))
            {
                var kept = built.Keys.ToHashSet();
                var unused = was.Names.Keys.Where(key => !kept.Contains(key)).ToArray();
                // Under the build lock, so no build of this store (another file system's repair, say) runs between its puts and its open.
                var building = Building;
                await building.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    for (var at = 0; at < unused.Length; at += GeneratedLimits.MaxBatch)
                        await store!.DeleteAsync(unused[at..Math.Min(unused.Length, at + GeneratedLimits.MaxBatch)], token).ConfigureAwait(false);
                }
                finally { building.Release(); }
            }
            // Bounded: rounds of sealed packs whose dead share is high enough, until a round finds none. The staging just deleted
            // (about 150 MB for a full download) spans packs that sealed during the download; the active one is reclaimed once it seals.
            for (var round = 0; round < CompactRounds; round++)
                if ((await store!.CompactAsync(maxPacks: CompactPacks, cancellationToken: token).ConfigureAwait(false)).PacksCompacted == 0) break;
        }
        catch (GeneratedStoreException) { }
    }

    /// <summary>Sealed packs one compaction round after an update may rewrite.</summary>
    internal const int CompactPacks = 4;
    /// <summary>Compaction rounds after an update.</summary>
    internal const int CompactRounds = 4;

    // A new tag whose bytes (SHA-256 and size) are the loaded copy's: one delta without ops against that copy, made from the stored
    // copy alone, then indexed from it. False when that isn't possible (no such copy, its chain is full, it can't be read or split);
    // the release is then downloaded as usual.
    private async Task<bool> SameBytesAsync(ListfileRelease release, CancellationToken token)
    {
        if (snapshot?.Copy is not { } current || current.Release.Tag == release.Tag || current.Release.Sha256 != release.Sha256
            || current.Release.Size != release.Size) return false;
        var (entries, _, complete) = await ReleaseEntriesAsync(token).ConfigureAwait(false);
        if (!complete) return false;
        var catalog = new ReleaseCatalog(objects, entries, FetchPriority.Background);
        var stored = await ResolveAsync(catalog, current.Release, FetchPriority.Background, 0, new ResolveBudget(), token).ConfigureAwait(false);
        if (stored is null || stored.Deltas.Count >= ListfileDelta.MaxChainDeltas) return false;
        byte[]? delta;
        try
        {
            await using var @base = new ChunkStream(CopyBytesAsync(stored, FetchPriority.Background, token));
            await using var target = new ChunkStream(CopyBytesAsync(stored, FetchPriority.Background, token));
            var sha = Convert.FromHexString(release.Sha256);
            delta = await ListfileDelta.EncodeAsync(@base, target, sha, sha, release.Size, ObservationRequest.ListfileMaxDeltaBytes, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ListfileFormatException or ListfileDeltaException or CopyBrokenException) { return false; }
        if (delta is null) return false;
        var url = new Uri(WowListfileObservations.AssetUrl(release.Tag));
        await RecordAsync(new Observation(ObservationKind.ListfileDelta, release.Tag, ObservationRequest.ListfileDeltaRegion, url, current.FetchedAt, delta),
            release, token).ConfigureAwait(false);
        LoadedNames built;
        try { built = await BuildAsync(release, current.FetchedAt, CopyBytesAsync(stored, FetchPriority.Background, token), rebuild: false, token).ConfigureAwait(false); }
        catch (Exception error) when (error is ListfileFormatException or ListfileDeltaException or CopyBrokenException)
        {
            throw Failed($"The stored copy of release {current.Release.Tag} couldn’t be read again: {error.Message}");
        }
        await SwapInAsync(built, release, current.FetchedAt, token).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> FetchPartAsync(ListfileRelease release, int part, CancellationToken token)
    {
        // Turning the switch off stops a download between parts.
        if (!await OnAsync(token).ConfigureAwait(false)) return false;
        var result = await objects.ObserveAsync(new(ObservationKind.ListfilePart, release.Tag, ObservationRequest.ListfilePartRegion(part), Record: false), token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (result.State != ObjectState.Present || result.Observation is not { } observation) throw Mapped(result);
        var staged = new byte[StagedHeader + observation.Bytes.Length];
        BinaryPrimitives.WriteInt64LittleEndian(staged, observation.ObservedAt.UtcTicks);
        observation.Bytes.CopyTo(staged, StagedHeader);
        var key = StagedKey(release.Tag, release.Sha256, part);
        // A key already stored is left as it is, so a refetched part replaces the old one by delete and put.
        await store!.DeleteAsync([key], token).ConfigureAwait(false);
        await store.PutAsync([new GeneratedPut(key, staged)], token).ConfigureAwait(false);
        return true;
    }

    /// <summary>The generated store key of one staged part: a versioned identity of the release's tag, SHA-256 and the part index.</summary>
    internal static GeneratedKey StagedKey(string tag, string sha256, int part) =>
        GeneratedKey.Hash(Encoding.UTF8.GetBytes($"wowlistfile-staging-v1|{tag}|{sha256}|{part}"));

    private Task DeleteStagingAsync(ListfileRelease release, CancellationToken token) =>
        DeleteStagingAsync(release.Tag, release.Sha256, release.Size, token);

    private async Task DeleteStagingAsync(string tag, string sha256, long size, CancellationToken token)
    {
        var parts = (int)((size + ObservationRequest.ListfilePartBytes - 1) / ObservationRequest.ListfilePartBytes);
        var keys = Enumerable.Range(0, parts).Select(part => StagedKey(tag, sha256, part)).ToArray();
        // The copy is already in use; staging left behind only costs space until the store is wiped.
        try { await store!.DeleteAsync(keys, token).ConfigureAwait(false); }
        catch (GeneratedStoreException) { }
    }

    /// <summary>Missing: parts not staged or unreadable. BadPart: one part of the wrong length (refetched alone). Wrong: the whole
    /// file doesn't match the digest (every part refetched). Invalid: GitHub's own bytes don't parse.</summary>
    private enum Check { Usable, Missing, BadPart, Wrong, Invalid }

    private sealed record Verified(Check Check, DateTimeOffset? FetchedAt = null, IReadOnlyList<int>? Missing = null, string? Detail = null);

    // The release's staged parts, read one at a time, hashed and parsed as they come, so the whole CSV is never in memory.
    private async Task<Verified> VerifyStagedAsync(ListfileRelease release, CancellationToken token)
    {
        var missing = new List<int>();
        for (var part = 0; part < release.Parts; part++)
            if (!await store!.HasAsync(StagedKey(release.Tag, release.Sha256, part), token).ConfigureAwait(false)) missing.Add(part);
        if (missing.Count > 0) return new(Check.Missing, Missing: missing);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var parser = new ListfileParser(static (_, _) => { });
        string? unparsed = null;
        var fetchedAt = DateTimeOffset.MinValue;
        for (var part = 0; part < release.Parts; part++)
        {
            var staged = await ReadStagedAsync(release, part, token).ConfigureAwait(false);
            if (staged is not { } found) return new(Check.Missing, Missing: [part]);
            var bytes = found.Bytes;
            if (bytes.Length != release.PartLength(part))
                return new(Check.BadPart, Missing: [part], Detail: $"Part {part} of release {release.Tag} has {bytes.Length} bytes, not {release.PartLength(part)}.");
            if (found.FetchedAt > fetchedAt) fetchedAt = found.FetchedAt;
            hash.AppendData(bytes.Span);
            // After a refusal, the rest is still hashed: bytes that aren't GitHub's are refetched, GitHub's own bytes aren't.
            if (unparsed is null)
                try { parser.Feed(bytes.Span); }
                catch (ListfileFormatException error) { unparsed = error.Message; }
        }
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != release.Sha256)
            return new(Check.Wrong, Detail: $"The file of release {release.Tag} doesn’t match the SHA-256 GitHub published.");
        if (unparsed is not null) return new(Check.Invalid, Detail: unparsed);
        try { parser.Finish(); }
        catch (ListfileFormatException error) { return new(Check.Invalid, Detail: error.Message); }
        return new(Check.Usable, fetchedAt);
    }

    /// <summary>One staged part's CSV bytes and fetch time, or null when it isn't staged; a damaged one is deleted and reads as null.</summary>
    private async Task<(ReadOnlyMemory<byte> Bytes, DateTimeOffset FetchedAt)?> ReadStagedAsync(ListfileRelease release, int part, CancellationToken token)
    {
        var key = StagedKey(release.Tag, release.Sha256, part);
        var read = await store!.ReadAsync(key, token).ConfigureAwait(false);
        if (read.State == GeneratedReadState.Absent) return null;
        long ticks = read.Bytes is { Length: >= StagedHeader } b ? BinaryPrimitives.ReadInt64LittleEndian(b) : -1;
        if (read.State != GeneratedReadState.Present || ticks is < 0 or > 3155378975999999999)
        {
            await store.DeleteAsync([key], token).ConfigureAwait(false);
            return null;
        }
        return (read.Bytes.AsMemory(StagedHeader), new DateTimeOffset(ticks, TimeSpan.Zero));
    }

    /// <summary>The staged CSV, part by part, for an encode or an index build; a part gone since it was verified fails the attempt.</summary>
    private async IAsyncEnumerable<ReadOnlyMemory<byte>> StagedAsync(ListfileRelease release, [EnumeratorCancellation] CancellationToken token)
    {
        for (var part = 0; part < release.Parts; part++)
        {
            var staged = await ReadStagedAsync(release, part, token).ConfigureAwait(false)
                ?? throw Failed($"Part {part} of release {release.Tag} couldn’t be kept.");
            yield return staged.Bytes;
        }
    }

    // The verified release into the object store: one delta against the loaded copy when that is allowed and small enough,
    // else every part. Usable only once every record came back Recorded.
    private async Task KeepAsync(ListfileRelease release, DateTimeOffset fetchedAt, CancellationToken token)
    {
        var url = new Uri(WowListfileObservations.AssetUrl(release.Tag));
        if (await DeltaAsync(release, token).ConfigureAwait(false) is { } delta)
        {
            await RecordAsync(new Observation(ObservationKind.ListfileDelta, release.Tag, ObservationRequest.ListfileDeltaRegion, url, fetchedAt, delta),
                release, token).ConfigureAwait(false);
            return;
        }
        for (var part = 0; part < release.Parts; part++)
        {
            var staged = await ReadStagedAsync(release, part, token).ConfigureAwait(false)
                ?? throw Failed($"Part {part} of release {release.Tag} couldn’t be kept.");
            await RecordAsync(new Observation(ObservationKind.ListfilePart, release.Tag, ObservationRequest.ListfilePartRegion(part), url, staged.FetchedAt,
                staged.Bytes.ToArray()), release, token).ConfigureAwait(false);
        }
    }

    private async Task RecordAsync(Observation observation, ListfileRelease release, CancellationToken token)
    {
        var result = await objects.RecordAsync(observation, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (result.State == ObjectState.Present && result.Recorded) return;
        throw Failed($"Release {release.Tag} couldn’t be kept{(result.Message is { Length: > 0 } message ? ": " + message : ".")}");
    }

    // The delta from the loaded copy to the staged release, or null for a full copy: no loaded copy, its stored copy can't be
    // read, its chain already has the most deltas, the delta is over 16 MiB, or either CSV doesn't split into units.
    private async Task<byte[]?> DeltaAsync(ListfileRelease release, CancellationToken token)
    {
        // A tag published again with other bytes is kept whole: delta entries are found by tag, so a base under the same tag would
        // shadow the target.
        if (snapshot?.Copy is not { } current || current.Release.Tag == release.Tag) return null;
        var (entries, _, complete) = await ReleaseEntriesAsync(token).ConfigureAwait(false);
        if (!complete) return null;
        var catalog = new ReleaseCatalog(objects, entries, FetchPriority.Background);
        var stored = await ResolveAsync(catalog, current.Release, FetchPriority.Background, 0, new ResolveBudget(), token).ConfigureAwait(false);
        if (stored is null || stored.Deltas.Count >= ListfileDelta.MaxChainDeltas) return null;
        try
        {
            await using var @base = new ChunkStream(CopyBytesAsync(stored, FetchPriority.Background, token));
            await using var target = new ChunkStream(StagedAsync(release, token));
            return await ListfileDelta.EncodeAsync(@base, target, Convert.FromHexString(current.Release.Sha256), Convert.FromHexString(release.Sha256),
                release.Size, ObservationRequest.ListfileMaxDeltaBytes, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is ListfileFormatException or ListfileDeltaException or CopyBrokenException) { return null; }
    }

    private async Task<LoadedNames> BuildAsync(ListfileRelease release, DateTimeOffset fetchedAt, IAsyncEnumerable<ReadOnlyMemory<byte>> csv, bool rebuild,
        CancellationToken token)
    {
        var building = Building;
        await building.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var index = await ListfileStore.BuildAsync(store!, new ListfileStoreRelease(release.Tag, release.Sha256, release.PublishedAt, fetchedAt), csv, token,
                rebuild).ConfigureAwait(false);
            return new LoadedNames(index, MarkDamaged);
        }
        finally { building.Release(); }
    }

    private void MarkDamaged(LoadedNames which)
    {
        // Only the loaded copy is repaired; an older one a caller still holds just answers without names.
        if (!ReferenceEquals(snapshot?.Copy?.Names, which)) return;
        damaged = which;
        Interlocked.Exchange(ref readFailedTicks, time.GetUtcNow().UtcTicks);
    }

    private Failure? RecentReadFailure()
    {
        var ticks = Interlocked.Read(ref readFailedTicks);
        if (ticks < 0) return null;
        var at = new DateTimeOffset(ticks, TimeSpan.Zero);
        var now = time.GetUtcNow();
        return at <= now && now - at < UnreadableRetry
            ? new(at, "LISTFILE_UNREADABLE", "The community file names couldn’t be read for a recent page, so some files there have no name or were left out of a path filter. The copy is read again.", null)
            : null;
    }

    private bool IsDamaged(Snapshot known) => known.Copy is { } copy && ReferenceEquals(copy.Names, damaged);

    // Compare-and-swap, so a load that ran meanwhile and an update's change never overwrite each other.
    private void Swap(Func<Snapshot, Snapshot> change)
    {
        while (true)
        {
            var was = snapshot!;
            if (ReferenceEquals(Interlocked.CompareExchange(ref snapshot, change(was), was), was)) return;
        }
    }

    // The cached snapshot, or a load of it. A load that failed gives a snapshot without a copy, served for UnreadableRetry (so a
    // listing doesn't walk history every batch), then loaded again. A damaged copy is loaded again, which rebuilds its index.
    private async Task<Snapshot> LoadedAsync(CancellationToken token)
    {
        if (snapshot is { } known && !IsDamaged(known)) return known;
        if (RecentUnread() is { } failed) return failed;
        await loading.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var before = snapshot;
            if (before is { } loaded && !IsDamaged(loaded)) return loaded;
            if (RecentUnread() is { } stillFailed) return stillFailed;
            Interlocked.Increment(ref loads);
            var repair = damaged;
            Snapshot fresh;
            try { fresh = await LoadAsync(repair, token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                var unreadable = new Snapshot(null, null, null, Unreadable(error));
                unread = new(unreadable, time.GetUtcNow());
                return unreadable;
            }
            if (repair is not null) Interlocked.CompareExchange(ref damaged, null, repair);
            // An update that swapped a newer copy in while this load ran wins: it is kept, and this load's result dropped.
            var current = Interlocked.CompareExchange(ref snapshot, fresh, before);
            return ReferenceEquals(current, before) ? fresh : current!;
        }
        finally { loading.Release(); }
    }

    private Snapshot? RecentUnread()
    {
        if (unread is not { } last) return null;
        var now = time.GetUtcNow();
        // A time in the future (a clock moved back) is due.
        return last.At <= now && now - last.At < UnreadableRetry ? last.Snapshot : null;
    }

    private static string Unreadable(Exception error) => $"The stored copy couldn’t be read ({error.GetType().Name}).";

    /// <summary>The release observations, newest first (at most <see cref="MaxReleaseEntries"/>), the newest one's time, and false
    /// when history is longer than <see cref="MaxHistoryPages"/> pages.</summary>
    private async Task<(List<HistoryEntry> NewestFirst, DateTimeOffset? CheckedAt, bool Complete)> ReleaseEntriesAsync(CancellationToken token)
    {
        var entries = new LinkedList<HistoryEntry>();
        DateTimeOffset? checkedAt = null;
        HistoryEntry? after = null;
        for (var read = 0; ; read++)
        {
            if (read == MaxHistoryPages) return ([], checkedAt, false);
            var batch = await objects.ReadHistoryAsync(new(Product: ObservationRequest.ListfileProduct, Region: ObservationRequest.ListfileReleaseRegion,
                Kind: HistoryKind.ListfileRelease, After: after, Limit: HistoryPage), token).ConfigureAwait(false);
            foreach (var entry in batch)
            {
                if (entry.Kind != HistoryKind.ListfileRelease || entry.Product != ObservationRequest.ListfileProduct
                    || entry.Region != ObservationRequest.ListfileReleaseRegion) continue;
                if (checkedAt is null || entry.ObservedAt > checkedAt) checkedAt = entry.ObservedAt;
                entries.AddLast(entry);
                if (entries.Count > MaxReleaseEntries) entries.RemoveFirst();
            }
            if (batch.Count < HistoryPage) break;
            after = batch[^1];
        }
        var newestFirst = new List<HistoryEntry>(entries.Count);
        for (var node = entries.Last; node is not null; node = node.Previous) newestFirst.Add(node.Value);
        return (newestFirst, checkedAt, true);
    }

    private async Task<Snapshot> LoadAsync(LoadedNames? repair, CancellationToken token)
    {
        var (entries, checkedAt, complete) = await ReleaseEntriesAsync(token).ConfigureAwait(false);
        if (!complete) return new(null, checkedAt, null);
        var catalog = new ReleaseCatalog(objects, entries, FetchPriority.Interactive);
        (string, string)? newest = null;
        var tags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; await catalog.AtAsync(i, token).ConfigureAwait(false) is { } release; i++)
        {
            newest ??= (release.Tag, release.Sha256);
            if (!tags.Add(release.Tag)) continue;
            if (tags.Count > MaxTagsTried) break;
            var damagedOne = repair is not null && repair.Tag == release.Tag && repair.Sha256 == release.Sha256;
            if (await OpenAsync(catalog, release, damagedOne, token).ConfigureAwait(false) is { } copy) return new(copy, checkedAt, newest);
        }
        return new(null, checkedAt, newest);
    }

    // A release's index as it is, or rebuilt from its stored copy when absent, corrupt or damaged; null when it has no whole copy.
    private async Task<Copy?> OpenAsync(ReleaseCatalog catalog, ListfileRelease release, bool damagedOne, CancellationToken token)
    {
        var rebuild = damagedOne;
        if (!damagedOne)
        {
            var open = await ListfileStore.OpenAsync(store!, release.Tag, release.Sha256, token).ConfigureAwait(false);
            if (open.State == GeneratedReadState.Present) return new(new LoadedNames(open.Index!, MarkDamaged), release, open.Index!.FetchedAt);
            rebuild = open.State == GeneratedReadState.Corrupt;
        }
        if (await ResolveAsync(catalog, release, FetchPriority.Interactive, 0, new ResolveBudget(), token).ConfigureAwait(false) is not { } stored) return null;
        try
        {
            var built = await BuildAsync(release, stored.FetchedAt, CopyBytesAsync(stored, FetchPriority.Interactive, token), rebuild, token).ConfigureAwait(false);
            return new(built, release, stored.FetchedAt);
        }
        catch (Exception error) when (error is ListfileFormatException or ListfileDeltaException or CopyBrokenException) { return null; }
    }

    // Where the object store keeps a release: its newest full parts, else a delta whose target is the release on top of its base's
    // copy, found by the base's SHA-256 among the release observations; at most MaxChainDeltas deltas from a full copy.
    private async Task<StoredCopy?> ResolveAsync(ReleaseCatalog catalog, ListfileRelease release, FetchPriority priority, int hops, ResolveBudget budget,
        CancellationToken token)
    {
        if (await FullPartsAsync(release, token).ConfigureAwait(false) is { } parts)
            return new(release, release, parts, [], parts.Max(p => p.ObservedAt));
        if (hops == ListfileDelta.MaxChainDeltas) return null;
        if (await DeltaToAsync(release, priority, token).ConfigureAwait(false) is not { } delta) return null;
        var baseSha = Convert.ToHexStringLower(delta.Header.BaseSha256);
        // Every release with the base's bytes is a candidate, newest first: a newer tag with those bytes may have no copy of its own.
        for (var i = 0; await catalog.WithShaAsync(baseSha, release.Tag, i, token).ConfigureAwait(false) is { } @base; i++)
        {
            if (!budget.Take()) return null;
            if (await ResolveAsync(catalog, @base, priority, hops + 1, budget, token).ConfigureAwait(false) is { } below)
                return new(release, below.Root, below.RootParts, [.. below.Deltas, delta.Bytes], delta.Entry.ObservedAt);
        }
        return null;
    }

    /// <summary>Releases one resolve may try in all, so many same-SHA candidates at every level stay bounded.</summary>
    internal const int MaxResolveSteps = 64;

    private sealed class ResolveBudget
    {
        private int left = MaxResolveSteps;
        public bool Take() => left-- > 0;
    }

    // The newest part entries p0..pN of the release's tag, all there; or null.
    private async Task<HistoryEntry[]?> FullPartsAsync(ListfileRelease release, CancellationToken token)
    {
        var url = WowListfileObservations.AssetUrl(release.Tag);
        var newest = new HistoryEntry?[release.Parts];
        HistoryEntry? after = null;
        for (var read = 0; ; read++)
        {
            if (read == MaxHistoryPages) return null;
            var batch = await objects.ReadHistoryAsync(new(Product: release.Tag, Kind: HistoryKind.ListfilePart, After: after, Limit: HistoryPage), token)
                .ConfigureAwait(false);
            foreach (var entry in batch)
            {
                // Oldest first, so a later entry of a part replaces an earlier one.
                if (entry.Kind != HistoryKind.ListfilePart || entry.Product != release.Tag || entry.Url != url) continue;
                if (ObservationRequest.ListfilePartIndex(entry.Region) is { } part && part < release.Parts
                    && (newest[part] is not { } kept || entry.ObservedAt >= kept.ObservedAt))
                    newest[part] = entry;
            }
            if (batch.Count < HistoryPage) break;
            after = batch[^1];
        }
        return newest.Any(entry => entry is null) ? null : [.. newest.Select(entry => entry!)];
    }

    // The newest delta entry of the release's tag whose header makes the release (target SHA-256 and size); or null.
    private async Task<(HistoryEntry Entry, byte[] Bytes, ListfileDeltaHeader Header)?> DeltaToAsync(ListfileRelease release, FetchPriority priority,
        CancellationToken token)
    {
        var url = WowListfileObservations.AssetUrl(release.Tag);
        var entries = new List<HistoryEntry>();
        HistoryEntry? after = null;
        for (var read = 0; ; read++)
        {
            if (read == MaxHistoryPages) return null;
            var batch = await objects.ReadHistoryAsync(new(Product: release.Tag, Region: ObservationRequest.ListfileDeltaRegion, Kind: HistoryKind.ListfileDelta,
                After: after, Limit: HistoryPage), token).ConfigureAwait(false);
            entries.AddRange(batch.Where(entry => entry.Kind == HistoryKind.ListfileDelta && entry.Product == release.Tag
                && entry.Region == ObservationRequest.ListfileDeltaRegion && entry.Url == url));
            if (batch.Count < HistoryPage) break;
            after = batch[^1];
        }
        var documents = new HashSet<ObjectKey>();
        var tried = 0;
        foreach (var entry in entries.OrderByDescending(entry => entry.ObservedAt))
        {
            if (!documents.Add(entry.Document)) continue;
            if (++tried > MaxDeltaCandidates) break;
            var result = await objects.GetKeyAsync(new ObjectRequest(entry.Document), cdn: null, priority, token).ConfigureAwait(false);
            if (!result.IsPresent || result.Bytes is not { } bytes) continue;
            ListfileDeltaHeader header;
            try { header = ListfileDelta.ReadHeader(bytes); }
            catch (ListfileDeltaException) { continue; }
            if (header.TargetSize == release.Size && Convert.ToHexStringLower(header.TargetSha256) == release.Sha256) return (entry, bytes, header);
        }
        return null;
    }

    /// <summary>A stored copy's CSV, streamed: the root's parts read one at a time and, for a delta copy, replayed through its chain.</summary>
    private async IAsyncEnumerable<ReadOnlyMemory<byte>> CopyBytesAsync(StoredCopy copy, FetchPriority priority, [EnumeratorCancellation] CancellationToken token)
    {
        if (copy.Deltas.Count == 0)
        {
            for (var part = 0; part < copy.Root.Parts; part++) yield return await PartAsync(copy, part, priority, token).ConfigureAwait(false);
            yield break;
        }
        var output = new ArrayBufferWriter<byte>(ReplaySlice);
        using var chain = new ListfileDeltaChain([.. copy.Deltas.Select(delta => (ReadOnlyMemory<byte>)delta)], output);
        for (var part = 0; part < copy.Root.Parts; part++)
        {
            var bytes = await PartAsync(copy, part, priority, token).ConfigureAwait(false);
            for (var at = 0; at < bytes.Length; at += ReplaySlice)
            {
                token.ThrowIfCancellationRequested();
                chain.Feed(bytes.AsSpan(at, Math.Min(ReplaySlice, bytes.Length - at)));
                if (output.WrittenCount == 0) continue;
                yield return output.WrittenMemory;
                output.ResetWrittenCount();
            }
        }
        chain.Complete();
        if (output.WrittenCount > 0) yield return output.WrittenMemory;
    }

    private async Task<byte[]> PartAsync(StoredCopy copy, int part, FetchPriority priority, CancellationToken token)
    {
        var result = await objects.GetKeyAsync(new ObjectRequest(copy.RootParts[part].Document), cdn: null, priority, token).ConfigureAwait(false);
        if (!result.IsPresent || result.Bytes is not { } bytes) throw new CopyBrokenException($"Part {part} of release {copy.Root.Tag} isn’t stored.");
        if (bytes.LongLength != copy.Root.PartLength(part))
            throw new CopyBrokenException($"Part {part} of release {copy.Root.Tag} has {bytes.LongLength} bytes, not {copy.Root.PartLength(part)}.");
        return bytes;
    }

    /// <summary>
    /// The release observations a load or an update read, newest first, parsed as needed: each distinct document once, unreadable
    /// ones skipped. A walk by position and a search by SHA-256 share what was read. Not thread-safe; one per walk.
    /// </summary>
    private sealed class ReleaseCatalog(IObjectSource objects, IReadOnlyList<HistoryEntry> newestFirst, FetchPriority priority)
    {
        private readonly List<ListfileRelease> read = [];
        private readonly HashSet<ObjectKey> documents = [];
        private int next;

        /// <summary>The <paramref name="index"/>th readable release, newest first, or null past the last.</summary>
        public async Task<ListfileRelease?> AtAsync(int index, CancellationToken token)
        {
            while (read.Count <= index)
                if (!await ReadNextAsync(token).ConfigureAwait(false)) return null;
            return read[index];
        }

        /// <summary>
        /// The <paramref name="nth"/> release, newest first, with this SHA-256 under another tag than <paramref name="notTag"/>, each
        /// tag once; null past the last.
        /// </summary>
        public async Task<ListfileRelease?> WithShaAsync(string sha256, string notTag, int nth, CancellationToken token)
        {
            var tags = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; await AtAsync(i, token).ConfigureAwait(false) is { } release; i++)
                if (release.Sha256 == sha256 && release.Tag != notTag && tags.Add(release.Tag) && tags.Count == nth + 1) return release;
            return null;
        }

        private async Task<bool> ReadNextAsync(CancellationToken token)
        {
            while (next < newestFirst.Count)
            {
                var entry = newestFirst[next++];
                if (!documents.Add(entry.Document)) continue;
                var result = await objects.GetKeyAsync(new ObjectRequest(entry.Document), cdn: null, priority, token).ConfigureAwait(false);
                if (!result.IsPresent || result.Bytes is not { } bytes) continue;
                try { read.Add(ListfileRelease.Parse(bytes)); return true; }
                catch (ListfileFetchException) { }
            }
            return false;
        }
    }

    /// <summary>A read-only stream over chunks, for <see cref="ListfileDelta.EncodeAsync"/>. A chunk is copied out before the next is asked for.</summary>
    private sealed class ChunkStream(IAsyncEnumerable<ReadOnlyMemory<byte>> source) : Stream
    {
        private IAsyncEnumerator<ReadOnlyMemory<byte>>? chunks;
        private ReadOnlyMemory<byte> current;
        private bool ended;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            // The source carries the caller's token.
            chunks ??= source.GetAsyncEnumerator(cancellationToken);
            while (current.IsEmpty && !ended)
            {
                if (await chunks.MoveNextAsync().ConfigureAwait(false)) current = chunks.Current;
                else ended = true;
            }
            var count = Math.Min(buffer.Length, current.Length);
            current[..count].CopyTo(buffer);
            current = current[count..];
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Read it asynchronously.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask DisposeAsync()
        {
            if (chunks is not null) await chunks.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static FileNamesStatus Status(Snapshot current, Failure? last)
    {
        if (current.Copy is { } copy)
            return new(FileNamesState.Ready, copy.Release.Tag, copy.Release.PublishedAt, copy.FetchedAt, current.CheckedAt, (int)copy.Names.Count,
                last?.Code, last?.Message, last?.ObservedAt);
        if (current.Unreadable is { } unreadable)
            return new(FileNamesState.NotReady, Code: "LISTFILE_UNREADABLE", Message: $"The community file names couldn’t be loaded. {unreadable}");
        return last is null ? new(FileNamesState.NotReady, CheckedAt: current.CheckedAt)
            : new(FileNamesState.Failed, CheckedAt: current.CheckedAt, Code: last.Code, Message: last.Message, ObservedAt: last.ObservedAt);
    }

    private async Task<bool> OnAsync(CancellationToken token)
    {
        if (names is null || store is null) return false;
        bool on;
        try { on = await names.NamesEnabledAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { on = false; }
        // A switch change reads history again rather than serve a failed load.
        if (Interlocked.Exchange(ref lastOn, on ? 1 : 0) != (on ? 1 : 0)) unread = null;
        return on;
    }

    private static ListfileFetchException Mapped(ObservationResult result) => result.Code switch
    {
        "CDN_OFFLINE" => new("LISTFILE_FETCH_OFFLINE", "VibeMiner couldn’t reach GitHub to fetch the community listfile.", result.ObservedAt),
        "CDN_NOT_FOUND" => NotFound(result.ObservedAt),
        "CDN_TOO_LARGE" => TooLarge(result.ObservedAt),
        // A transport failure (CDN_*) comes from the layer 1 wow-listfile client, whose message is already the whole sentence.
        { } code when code.StartsWith("CDN_", StringComparison.Ordinal) && result.Message is { Length: > 0 } message =>
            new("LISTFILE_FETCH_FAILED", message, result.ObservedAt),
        _ => Failed(result.Message ?? $"The fetch ended with {result.Code ?? result.State.ToString()}.", result.ObservedAt),
    };

    internal static ListfileFetchException Failed(string detail, DateTimeOffset? observedAt = null) =>
        new("LISTFILE_FETCH_FAILED", $"Fetching the community listfile from GitHub failed. {detail}", observedAt);
    internal static ListfileFetchException Invalid(string detail, DateTimeOffset? observedAt = null) =>
        new("LISTFILE_INVALID", $"The community listfile couldn’t be used. {detail}", observedAt);
    internal static ListfileFetchException TooLarge(DateTimeOffset? observedAt = null) =>
        new("LISTFILE_TOO_LARGE", "The newest community listfile is larger than VibeMiner allows (512 MiB), so it wasn’t used.", observedAt);
    internal static ListfileFetchException NotFound(DateTimeOffset? observedAt = null) =>
        new("LISTFILE_NOT_FOUND", "GitHub has no community listfile release to fetch.", observedAt);
}
