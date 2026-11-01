using System.Security.Cryptography;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Files.Names;

/// <summary>
/// The wow-listfile names (D105) over layer 2. A check is one <see cref="ObservationKind.ListfileRelease"/> observation (the
/// releases/latest JSON) and a download is one <see cref="ObservationKind.ListfilePart"/> observation per 8 MiB part of the
/// release's CSV, all recorded in the history log; layer 1 owns the transport rules. The stored copy is that history: walking
/// release observations newest first, each distinct tag once, the first release whose newest part entries p0..pN are all
/// there, each exactly 8 MiB but a last part of the remainder, whose joined bytes match GitHub's SHA-256 and parse, is the
/// copy. Parts are read one at a time, hashed and fed to the parser as they come, so the whole CSV is never in memory. The
/// copy is loaded once and cached; <see cref="UpdateAsync"/> swaps a newer one in. A check is due after a week, or at once
/// when the newest release is neither the loaded copy nor a release this engine refused (a download that stopped resumes
/// from the parts it has); after a failure, unforced updates wait 15 min, and they never fetch the parts of a refused
/// release (tag and SHA-256) again. Failures and refusals stay in memory (D113). A layer 2 exception is a failed attempt
/// in an update and no copy in a load, served for <see cref="UnreadableRetry"/> and then read again (an update or a switch
/// change reads again at once): names never fail a Files page. The
/// switch is read on every call and fails closed.
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

    private readonly IObjectSource objects;
    private readonly IFileNameSwitch? names;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim loading = new(1, 1);
    private readonly SemaphoreSlim updating = new(1, 1);
    private volatile Snapshot? snapshot;
    private volatile Failure? failure;
    private volatile Refusal? refused;
    // The last load that layer 2 failed and when; cleared by an update or a switch change.
    private volatile Unread? unread;
    // The switch as last read: -1 unknown, 0 off, 1 on.
    private int lastOn = -1;
    private int loads;

    private sealed record Copy(ListfileIndex Index, ListfileRelease Release, DateTimeOffset FetchedAt);
    /// <summary>The loaded copy (or none), the newest release observation's time and the newest readable release (tag, SHA-256).
    /// <see cref="Unreadable"/> is set on a load that failed; such a snapshot is kept for <see cref="UnreadableRetry"/> only.</summary>
    private sealed record Snapshot(Copy? Copy, DateTimeOffset? CheckedAt, (string Tag, string Sha256)? Newest, string? Unreadable = null);
    private sealed record Unread(Snapshot Snapshot, DateTimeOffset At);
    private sealed record Failure(DateTimeOffset At, string Code, string Message, DateTimeOffset? ObservedAt);
    /// <summary>A release whose stored bytes were refused after fetching them; unforced attempts don't fetch its parts again.</summary>
    private sealed record Refusal(string Tag, string Sha256, ListfileFetchException Error);

    public FileNames(IObjectSource objects, IFileNameSwitch? names, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        this.objects = objects; this.names = names; this.time = time ?? TimeProvider.System;
    }

    /// <summary>How many times history was read to load a copy (tests: single-flight).</summary>
    internal int Loads => Volatile.Read(ref loads);

    /// <summary>The loaded names, or null when the switch is off or nothing usable is stored. Never fetches.</summary>
    internal async Task<ListfileIndex?> CurrentAsync(CancellationToken cancellationToken)
    {
        if (!await OnAsync(cancellationToken).ConfigureAwait(false)) return null;
        return (await LoadedAsync(cancellationToken).ConfigureAwait(false)).Copy?.Index;
    }

    public async Task<FileNamesStatus> StatusAsync(CancellationToken cancellationToken)
    {
        if (!await OnAsync(cancellationToken).ConfigureAwait(false)) return new(FileNamesState.Off);
        return Status(await LoadedAsync(cancellationToken).ConfigureAwait(false), failure);
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
            // A layer 2 exception (the store, history) is a failed attempt, with the same back-off.
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

    // One check: the newest release, then the parts this history lacks, verified. False when the switch went off meanwhile
    // (the parts fetched so far stay in history for the next attempt).
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
        ListfileFetchException Refuse(string detail)
        {
            var error = Invalid(detail, at);
            refused = new(release.Tag, release.Sha256, error);
            return error;
        }
        var fetched = new HashSet<int>();
        var rechecked = new HashSet<int>();
        var refetched = false;
        while (true)
        {
            var verified = await VerifyAsync(release, FetchPriority.Background, token).ConfigureAwait(false);
            switch (verified.Check)
            {
                case Check.Usable:
                    Swap(s => s with { Copy = new(verified.Index!, release, verified.FetchedAt!.Value) });
                    if (refused is { } old && old.Tag == release.Tag && old.Sha256 == release.Sha256) refused = null;
                    return true;
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
    }

    private async Task<bool> FetchPartAsync(ListfileRelease release, int part, CancellationToken token)
    {
        // Turning the switch off stops a download between parts.
        if (!await OnAsync(token).ConfigureAwait(false)) return false;
        var result = await objects.ObserveAsync(new(ObservationKind.ListfilePart, release.Tag, ObservationRequest.ListfilePartRegion(part)), token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (result.State != ObjectState.Present || result.Observation is null) throw Mapped(result);
        // Parts are read back from history only, so one that isn't recorded can't count.
        if (!result.Recorded) throw Failed($"Part {part} of release {release.Tag} couldn’t be kept.", result.Observation.ObservedAt);
        return true;
    }

    private void Swap(Func<Snapshot, Snapshot> change) => snapshot = change(snapshot!);

    // The cached snapshot, or a load of it. A load that layer 2 failed gives a snapshot without a copy, served for
    // UnreadableRetry (so a listing doesn't walk history every batch), then loaded again.
    private async Task<Snapshot> LoadedAsync(CancellationToken token)
    {
        if (snapshot is { } known) return known;
        if (RecentUnread() is { } failed) return failed;
        await loading.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (snapshot is { } loaded) return loaded;
            if (RecentUnread() is { } stillFailed) return stillFailed;
            Interlocked.Increment(ref loads);
            Snapshot fresh;
            try { fresh = await LoadAsync(token).ConfigureAwait(false); }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                var unreadable = new Snapshot(null, null, null, Unreadable(error));
                unread = new(unreadable, time.GetUtcNow());
                return unreadable;
            }
            snapshot = fresh;
            return fresh;
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

    private async Task<Snapshot> LoadAsync(CancellationToken token)
    {
        var entries = new LinkedList<HistoryEntry>();
        DateTimeOffset? checkedAt = null;
        HistoryEntry? after = null;
        for (var read = 0; ; read++)
        {
            if (read == MaxHistoryPages) return new(null, checkedAt, null, null);
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
        (string, string)? newest = null;
        var documents = new HashSet<ObjectKey>();
        var tags = new HashSet<string>(StringComparer.Ordinal);
        for (var node = entries.Last; node is not null; node = node.Previous)
        {
            if (!documents.Add(node.Value.Document)) continue;
            var result = await objects.GetKeyAsync(new ObjectRequest(node.Value.Document), cdn: null, FetchPriority.Interactive, token).ConfigureAwait(false);
            if (!result.IsPresent || result.Bytes is not { } bytes) continue;
            ListfileRelease release;
            try { release = ListfileRelease.Parse(bytes); }
            catch (ListfileFetchException) { continue; }
            newest ??= (release.Tag, release.Sha256);
            if (!tags.Add(release.Tag)) continue;
            if (tags.Count > MaxTagsTried) break;
            var verified = await VerifyAsync(release, FetchPriority.Interactive, token).ConfigureAwait(false);
            if (verified.Check == Check.Usable) return new(new(verified.Index!, release, verified.FetchedAt!.Value), checkedAt, newest);
        }
        return new(null, checkedAt, newest);
    }

    /// <summary>Missing: parts absent or unreadable. BadPart: one part of the wrong length (refetched alone). Wrong: the whole
    /// file doesn't match the digest (every part refetched). Invalid: GitHub's own bytes don't parse.</summary>
    private enum Check { Usable, Missing, BadPart, Wrong, Invalid }

    private sealed record Verified(Check Check, ListfileIndex? Index = null, DateTimeOffset? FetchedAt = null, IReadOnlyList<int>? Missing = null,
        string? Detail = null);

    // The release's stored copy, from history only: the newest entry of each part, read one at a time.
    private async Task<Verified> VerifyAsync(ListfileRelease release, FetchPriority priority, CancellationToken token)
    {
        var url = WowListfileObservations.AssetUrl(release.Tag);
        var newest = new HistoryEntry?[release.Parts];
        HistoryEntry? after = null;
        for (var read = 0; ; read++)
        {
            if (read == MaxHistoryPages) return new(Check.Missing, Missing: Enumerable.Range(0, release.Parts).ToArray());
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
        var missing = Enumerable.Range(0, release.Parts).Where(part => newest[part] is null).ToArray();
        if (missing.Length > 0) return new(Check.Missing, Missing: missing);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var parser = new ListfileParser(release.Size);
        string? unparsed = null;
        DateTimeOffset fetchedAt = DateTimeOffset.MinValue;
        for (var part = 0; part < release.Parts; part++)
        {
            var entry = newest[part]!;
            var result = await objects.GetKeyAsync(new ObjectRequest(entry.Document), cdn: null, priority, token).ConfigureAwait(false);
            if (!result.IsPresent || result.Bytes is not { } bytes) return new(Check.Missing, Missing: [part]);
            if (bytes.LongLength != release.PartLength(part))
                return new(Check.BadPart, Missing: [part], Detail: $"Part {part} of release {release.Tag} has {bytes.LongLength} bytes, not {release.PartLength(part)}.");
            if (entry.ObservedAt > fetchedAt) fetchedAt = entry.ObservedAt;
            hash.AppendData(bytes);
            // After a refusal, the rest is still hashed: bytes that aren't GitHub's are refetched, GitHub's own bytes aren't.
            if (unparsed is null)
                try { parser.Feed(bytes); }
                catch (ListfileFormatException error) { unparsed = error.Message; }
        }
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != release.Sha256)
            return new(Check.Wrong, Detail: $"The file of release {release.Tag} doesn’t match the SHA-256 GitHub published.");
        if (unparsed is not null) return new(Check.Invalid, Detail: unparsed);
        try { return new(Check.Usable, parser.Complete(), fetchedAt); }
        catch (ListfileFormatException error) { return new(Check.Invalid, Detail: error.Message); }
    }

    private static FileNamesStatus Status(Snapshot current, Failure? last)
    {
        if (current.Copy is { } copy)
            return new(FileNamesState.Ready, copy.Release.Tag, copy.Release.PublishedAt, copy.FetchedAt, current.CheckedAt, copy.Index.Count,
                last?.Code, last?.Message, last?.ObservedAt);
        if (current.Unreadable is { } unreadable)
            return new(FileNamesState.NotReady, Code: "LISTFILE_UNREADABLE", Message: $"The community file names couldn’t be loaded. {unreadable}");
        return last is null ? new(FileNamesState.NotReady, CheckedAt: current.CheckedAt)
            : new(FileNamesState.Failed, CheckedAt: current.CheckedAt, Code: last.Code, Message: last.Message, ObservedAt: last.ObservedAt);
    }

    private async Task<bool> OnAsync(CancellationToken token)
    {
        if (names is null) return false;
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
