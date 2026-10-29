using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>
/// The community listfile (#21, D105): FileDataID → name labels from wowdev/wow-listfile, downloaded at runtime from its
/// GitHub releases and never bundled. The bytes are untrusted: HTTPS only, fixed hosts, one redirect to GitHub's release
/// asset hosts, a size bound, the SHA-256 GitHub publishes for the asset, an idle time budget per read, cancellable, and
/// read only by the original bounded CSV parser. A verified copy is kept under <see cref="FolderName"/> with a record of
/// its release, digests and fetch time, verified again when the engine starts, and used offline. A download that stops
/// (cancelled, offline, engine closed) resumes from its saved bytes with an HTTP range. One background loop owns all
/// of it; names are labels only and never select, download or verify anything. Nothing is fetched until names are
/// first needed: the loop starts when Files asks for names, Settings asks for a download or the switch is turned on,
/// or at engine start when a copy or a partial download is already kept.
/// </summary>
public sealed class CommunityListfile : IFileNameSource, ICommunityListfileControl, IAsyncDisposable
{
    public const string ApiHost = "api.github.com";
    public const string DownloadHost = "github.com";
    public static readonly IReadOnlyList<string> AssetHosts = ["release-assets.githubusercontent.com", "objects.githubusercontent.com"];
    public const string Repository = "wowdev/wow-listfile";
    public const string AssetName = "community-listfile-withcapitals.csv";
    public const string FolderName = "community-listfile-v1";
    public const long MaxAssetBytes = 512L << 20;
    public const int MaxReleaseResponseBytes = 1 << 20;
    public const int MaxRecordBytes = 16 * 1024;
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromDays(7);
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DefaultMinimumGap = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LongestSleep = TimeSpan.FromHours(1);
    private const int RecordFormat = 1;
    private const int ChunkBytes = 1 << 20;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string folder;
    private readonly HttpClient client;
    private readonly ICommunityListfilePolicy policy;
    private readonly TimeProvider time;
    private readonly TimeSpan refresh, budget, gap;
    private readonly int block;
    private readonly object gate = new();
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim steps = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private Task? loop;
    // Guarded by gate.
    private CommunityListfileActivity activity;
    private ListfileIndex? index;
    private string? pendingTag;
    private long? completed, total;
    private DateTimeOffset? checkedAt, failedAt;
    private DiscoveryException? problem;
    private (string Sha, DiscoveryException Failure)? refused;
    private bool loaded, deliberate;
    private readonly bool background;
    private readonly Uri? origin;
    private readonly ListfileConnectionBudget? connections;
    private CancellationTokenSource? attempt;
    private bool started;

    public CommunityListfile(string dataDirectory, HttpClient client, ICommunityListfilePolicy policy, TimeProvider? clock = null,
        TimeSpan? refreshInterval = null, TimeSpan? readBudget = null, TimeSpan? minimumGap = null, int indexBlock = ListfileIndex.DefaultBlock,
        bool background = true, Uri? testOrigin = null, ListfileConnectionBudget? connections = null)
    {
        this.background = background;
        // Tests only: a loopback origin every GitHub request goes to instead, so no test run reaches GitHub (D105).
        if (testOrigin is not null && !LoopbackOrigin(testOrigin)) throw new ArgumentException("A listfile test origin must be a loopback http(s) origin.");
        origin = testOrigin;
        this.connections = connections;
        folder = Path.Combine(Path.GetFullPath(dataDirectory), FolderName);
        this.client = client; this.policy = policy; time = clock ?? TimeProvider.System;
        refresh = refreshInterval ?? DefaultRefreshInterval; budget = readBudget ?? DefaultBudget; gap = minimumGap ?? DefaultMinimumGap;
        block = indexBlock;
    }

    /// <summary>True for a bare http(s) origin on 127.0.0.1, ::1 or localhost: the only thing a test origin may be.</summary>
    public static bool LoopbackOrigin(Uri uri) => uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.IsLoopback && string.IsNullOrEmpty(uri.UserInfo) && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public static Uri LatestReleaseUrl { get; } = new($"https://{ApiHost}/repos/{Repository}/releases/latest");
    public static string AssetUrl(string tag) => $"https://{DownloadHost}/{Repository}/releases/download/{tag}/{AssetName}";

    private string RecordPath => Path.Combine(folder, "release.json");
    private string PartialRecordPath => Path.Combine(folder, "download.json");
    private string PartialPath => Path.Combine(folder, "download.csv.partial");
    private string IndexPath(string tag) => Path.Combine(folder, "names-" + tag + ".vlf");

    /// <summary>At engine start: starts the loop only when a copy or a partial download is already kept, so a profile
    /// that never used Files fetches nothing.</summary>
    public void StartIfKept()
    {
        if (File.Exists(RecordPath) || File.Exists(PartialRecordPath)) Start();
    }

    /// <summary>Starts the one background loop. It steps once now, then whenever the switch changes, a check is asked
    /// for, or the next check is due.</summary>
    public void Start() { lock (gate) { started = true; if (background && !stop.IsCancellationRequested) loop ??= Task.Run(() => RunAsync(stop.Token)); } }

    /// <summary>The switch changed. Off stops a running download at once (its bytes are kept); on starts or resumes it.</summary>
    public void SettingChanged(bool on)
    {
        if (!on) lock (gate) attempt?.Cancel();
        else Start();
        wake.Writer.TryWrite(true);
    }

    /// <summary>Asks GitHub for the newest release now, skipping the wait after a failure (a deliberate Retry or Check now).
    /// Returns at once; the status shows the work.</summary>
    public async Task<CommunityListfileStatus> CheckAsync(CancellationToken token)
    {
        if (!await policy.CommunityListfileEnabledAsync(token))
            throw new DiscoveryException("LISTFILE_OFF", "Community file names are off. Turn them on in Settings first.");
        // Shown as checking at once, so the reply already says work started; the loop sets the real stage.
        lock (gate) { deliberate = true; if (activity == CommunityListfileActivity.Idle) activity = CommunityListfileActivity.Checking; }
        Start();
        wake.Writer.TryWrite(true);
        return await StatusAsync(token);
    }

    public async Task<CommunityListfileStatus> StatusAsync(CancellationToken token)
    {
        var enabled = await policy.CommunityListfileEnabledAsync(token);
        lock (gate)
            return new(enabled, activity, index?.Release, pendingTag, completed, total, checkedAt,
                problem is null ? null : new(problem.Code, problem.Message, problem.Retryable)) { Started = started };
    }

    public async Task<FileNameLookup> LookupAsync(IReadOnlyList<long> fileDataIds, CancellationToken token)
    {
        if (fileDataIds.Count > InventoryService.MaxFilesPage) throw new ArgumentException("At most one Files page of FileDataIDs.");
        if (!await policy.CommunityListfileEnabledAsync(token)) return FileNameLookup.Off;
        // Files asking for names is what starts the first download.
        Start();
        // A swap between reading the field and taking the lease retires the old index; the new one is tried once.
        for (var tries = 0; tries < 2; tries++)
        {
            ListfileIndex? current;
            lock (gate) current = index;
            if (current is null) break;
            if (!current.TryAcquire()) continue;
            try
            {
                var names = fileDataIds.Count == 0 ? new Dictionary<long, string>() : await current.LookupAsync(fileDataIds, token);
                return new(CommunityNamesSource.From(current.Release), names);
            }
            // The index can't be read right now: this page has no names, and the next one tries again.
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(CommunityNamesSource.Unreadable, new Dictionary<long, string>()); }
            finally { current.ReleaseLease(); }
        }
        // No copy: a failed attempt with nothing running is told apart from a download still on its way.
        bool failed;
        lock (gate) failed = problem is not null && activity == CommunityListfileActivity.Idle;
        return new(failed ? CommunityNamesSource.Failed : CommunityNamesSource.NotReady, new Dictionary<long, string>());
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var wait = LongestSleep;
            try
            {
                if (await policy.CommunityListfileEnabledAsync(token))
                {
                    bool asked;
                    lock (gate) { asked = deliberate; deliberate = false; }
                    wait = await EnsureAsync(asked, token);
                }
                else lock (gate) activity = CommunityListfileActivity.Idle;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Nothing here may stop the loop; the next wake tries again.
                lock (gate) { problem = Failed("Something went wrong while preparing it."); failedAt = time.GetUtcNow(); activity = CommunityListfileActivity.Idle; }
                wait = gap;
            }
            try
            {
                using var timer = new CancellationTokenSource(wait, time);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timer.Token);
                await wake.Reader.WaitToReadAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (OperationCanceledException) { break; }
            while (wake.Reader.TryRead(out _)) { }
        }
    }

    /// <summary>
    /// One step: verify the kept copy once, then ask GitHub when a check is due (no copy, a saved partial download, a
    /// deliberate request, or the refresh interval passed), and download and import a newer release. After a failure,
    /// automatic attempts wait the minimum gap. Returns how long until the next step is due. Turning the switch off
    /// cancels the attempt; its downloaded bytes are kept for the next one.
    /// </summary>
    public async Task<TimeSpan> EnsureAsync(bool asked, CancellationToken token)
    {
        await steps.WaitAsync(token);
        try
        {
            bool first;
            lock (gate) first = !loaded;
            if (first)
            {
                lock (gate) activity = CommunityListfileActivity.Verifying;
                try
                {
                    var kept = await LoadAsync(token);
                    lock (gate) { index = kept; checkedAt = kept is null ? null : checkedAt; loaded = true; }
                }
                finally { lock (gate) activity = CommunityListfileActivity.Idle; }
            }
            var now = time.GetUtcNow();
            DateTimeOffset? lastCheck, lastFailure;
            bool have;
            lock (gate) { lastCheck = checkedAt; lastFailure = failedAt; have = index is not null; }
            if (!asked && lastFailure is { } failure && failure <= now && now - failure < gap) return gap - (now - failure);
            var partial = File.Exists(PartialRecordPath);
            if (!asked && !partial && have && lastCheck is { } check && check <= now && now - check < refresh)
            {
                var left = refresh - (now - check);
                return left < LongestSleep ? left : LongestSleep;
            }
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            lock (gate) attempt = cancel;
            try
            {
                await AttemptAsync(cancel.Token);
                lock (gate) { problem = null; failedAt = null; }
                return LongestSleep;
            }
            catch (DiscoveryException error)
            {
                lock (gate) { problem = error; failedAt = time.GetUtcNow(); }
                return gap;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return LongestSleep; }
            finally
            {
                lock (gate) { attempt = null; activity = CommunityListfileActivity.Idle; pendingTag = null; completed = null; total = null; }
            }
        }
        finally { steps.Release(); }
    }

    private sealed record Latest(string Tag, DateTimeOffset PublishedAt, string Sha256, long Size, string Url);

    private async Task AttemptAsync(CancellationToken token)
    {
        lock (gate) activity = CommunityListfileActivity.Checking;
        // Each attempt gets a fresh, small connection budget (see CreateHandler).
        connections?.Reset();
        var latest = await LatestAsync(token);
        var now = time.GetUtcNow();
        ListfileIndex? current;
        lock (gate) current = index;
        Directory.CreateDirectory(folder);
        if (current is not null && current.Release.Tag == latest.Tag && current.Release.Sha256 == latest.Sha256)
        {
            await SaveRecordAsync(current.Release, now, token);
            lock (gate) checkedAt = now;
            Delete(PartialPath); Delete(PartialRecordPath);
            return;
        }
        // The same bytes failed to read before: downloading them again can't help.
        (string Sha, DiscoveryException Failure)? bad;
        lock (gate) bad = refused;
        if (bad is { } known && known.Sha == latest.Sha256) throw known.Failure;
        lock (gate) { pendingTag = latest.Tag; activity = CommunityListfileActivity.Downloading; completed = 0; total = latest.Size; }
        await DownloadAsync(latest, token);
        lock (gate) { activity = CommunityListfileActivity.Importing; completed = 0; total = latest.Size; }
        (string Sha, long Length, long Count) built;
        try { built = await ImportAsync(latest, token); }
        catch (DiscoveryException failure) when (failure.Code == "LISTFILE_INVALID")
        {
            lock (gate) refused = (latest.Sha256, failure);
            Delete(PartialPath); Delete(PartialRecordPath);
            throw;
        }
        var fetchedAt = time.GetUtcNow();
        var release = new CommunityListfileRelease(latest.Tag, latest.PublishedAt, fetchedAt, latest.Sha256, latest.Size, built.Count, latest.Url);
        // The index first, then the record that names it; a crash between the two leaves an index no record names,
        // which the next import replaces. The record never names an index that isn't there.
        await SaveRecordAsync(release, fetchedAt, token, built.Sha, built.Length);
        var opened = await ListfileIndex.OpenAsync(IndexPath(latest.Tag), built.Sha, built.Length, built.Count, release, CancellationToken.None)
            ?? throw Failed("The prepared names couldn’t be read back from the storage folder.");
        ListfileIndex? old;
        lock (gate) { old = index; index = opened; checkedAt = fetchedAt; }
        old?.Dispose();
        Delete(PartialPath); Delete(PartialRecordPath);
        foreach (var stale in Directory.EnumerateFiles(folder, "names-*.vlf"))
            if (!string.Equals(Path.GetFileName(stale), Path.GetFileName(IndexPath(latest.Tag)), StringComparison.Ordinal)) Delete(stale);
    }

    // The newest release and its CSV asset, as GitHub describes them.
    private async Task<Latest> LatestAsync(CancellationToken token)
    {
        using var limit = new CancellationTokenSource(budget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, limit.Token);
        byte[] bytes;
        try
        {
            using var request = Request(LatestReleaseUrl, "application/vnd.github+json", null);
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            using var response = await SendAsync(request, token, linked.Token);
            Check(response);
            if (response.Content.Headers.ContentLength > MaxReleaseResponseBytes) throw Failed("GitHub sent a larger answer than VibeMiner allows.");
            bytes = await ReadSmallAsync(response, linked.Token);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !token.IsCancellationRequested) { throw TimedOut(); }
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tag_name", out var tagValue) || tagValue.ValueKind != JsonValueKind.String
                || tagValue.GetString() is not { } tag || !ValidTag(tag)
                || !root.TryGetProperty("published_at", out var published) || published.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(published.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var publishedAt)
                || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                throw Failed("GitHub sent an answer VibeMiner doesn’t understand.");
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != AssetName) continue;
                if (!asset.TryGetProperty("size", out var sizeValue) || !sizeValue.TryGetInt64(out var size) || size < 1)
                    throw Failed("GitHub sent an answer VibeMiner doesn’t understand.");
                if (size > MaxAssetBytes) throw TooLarge();
                // Without GitHub's own digest the bytes can't be checked, so they aren't fetched.
                if (!asset.TryGetProperty("digest", out var digest) || digest.ValueKind != JsonValueKind.String || digest.GetString() is not { Length: 71 } text
                    || !text.StartsWith("sha256:", StringComparison.Ordinal) || !Hex(text[7..]))
                    throw Failed("GitHub didn’t publish a SHA-256 for the file, so it can’t be checked.");
                if (!asset.TryGetProperty("browser_download_url", out var url) || url.ValueKind != JsonValueKind.String || url.GetString() != AssetUrl(tag))
                    throw Failed("The release’s download address isn’t one VibeMiner allows.");
                return new(tag, publishedAt, text[7..], size, AssetUrl(tag));
            }
            throw NotFound();
        }
        catch (JsonException) { throw Failed("GitHub sent an answer VibeMiner doesn’t understand."); }
    }

    // Resumes from the saved partial bytes when they belong to this exact release; otherwise starts again. The whole
    // file is checked against GitHub's SHA-256 before anything reads it.
    private async Task DownloadAsync(Latest latest, CancellationToken token)
    {
        var saved = await ReadJsonAsync<Partial>(PartialRecordPath, token);
        long have = 0;
        if (saved is not null && saved.Format == RecordFormat && saved.Tag == latest.Tag && saved.Sha256 == latest.Sha256 && saved.ByteLength == latest.Size
            && saved.SourceUrl == latest.Url && File.Exists(PartialPath))
            have = new FileInfo(PartialPath).Length;
        else
        {
            Delete(PartialPath);
            await WriteJsonAsync(PartialRecordPath, new Partial(RecordFormat, latest.Tag, latest.Sha256, latest.Size, latest.Url), token);
        }
        if (have > latest.Size) { Delete(PartialPath); have = 0; }
        lock (gate) completed = have;
        if (have < latest.Size)
        {
            try
            {
                await using var output = new FileStream(PartialPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
                output.SetLength(have); output.Position = have;
                using var response = await OpenAssetAsync(new Uri(latest.Url), have, latest.Size, token);
                if (response.StatusCode == HttpStatusCode.OK && have > 0) { output.SetLength(0); have = 0; lock (gate) completed = 0; }
                await using var input = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[1 << 16];
                long unflushed = 0;
                while (true)
                {
                    int read;
                    using (var idle = new CancellationTokenSource(budget, time))
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, idle.Token))
                    {
                        try { read = await input.ReadAsync(buffer, linked.Token); }
                        catch (OperationCanceledException) when (idle.IsCancellationRequested && !token.IsCancellationRequested)
                        { throw Failed($"GitHub stopped sending for {budget.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds. The bytes so far are kept."); }
                    }
                    if (read == 0) break;
                    if (have + read > latest.Size) throw Failed("GitHub sent more bytes than the release lists.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    have += read; unflushed += read;
                    // Saved bytes are flushed every 8 MiB, so an interrupted download keeps most of what it had.
                    if (unflushed >= 8 << 20) { await output.FlushAsync(token); unflushed = 0; }
                    lock (gate) completed = have;
                }
                await output.FlushAsync(CancellationToken.None);
                output.Flush(flushToDisk: true);
            }
            catch (HttpRequestException) { throw Failed("The download from GitHub was interrupted. The bytes so far are kept."); }
            catch (IOException error) when (error is not FileNotFoundException)
            { throw Failed("The download from GitHub was interrupted, or the bytes couldn’t be saved in the storage folder."); }
            catch (UnauthorizedAccessException) { throw Failed("The download couldn’t be saved in the storage folder."); }
            if (have != latest.Size) throw Failed("The download ended early. The bytes so far are kept.");
        }
        string sha;
        await using (var check = new FileStream(PartialPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (check.Length != latest.Size) throw Failed("The download ended early. The bytes so far are kept.");
            sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, token));
        }
        if (sha != latest.Sha256)
        {
            Delete(PartialPath); Delete(PartialRecordPath);
            throw Failed("The downloaded file didn’t match the SHA-256 GitHub published, so it wasn’t used.");
        }
    }

    // github.com answers with one redirect to its release asset host; nothing else is followed.
    private async Task<HttpResponseMessage> OpenAssetAsync(Uri uri, long from, long size, CancellationToken token)
    {
        for (var hop = 0; hop < 2; hop++)
        {
            using var request = Request(uri, "application/octet-stream", from > 0 ? from : null);
            HttpResponseMessage response;
            using (var connect = new CancellationTokenSource(budget, time))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, connect.Token))
            {
                try { response = await SendAsync(request, token, linked.Token); }
                catch (OperationCanceledException) when (connect.IsCancellationRequested && !token.IsCancellationRequested) { throw TimedOut(); }
            }
            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (hop > 0) throw Failed("GitHub redirected the download more than once.");
                if (location is null || !location.IsAbsoluteUri || !(Official(location) && AssetHosts.Contains(location.Host) || AtTestOrigin(location)))
                    throw Failed("GitHub redirected the download to an address VibeMiner doesn’t allow.");
                uri = location;
                continue;
            }
            try
            {
                if (response.StatusCode == HttpStatusCode.PartialContent && from > 0)
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range is not { HasRange: true } || range.From != from || range.To != size - 1 || range.Length != size)
                        throw Failed("GitHub resumed the download at another place.");
                    return response;
                }
                Check(response);
                if (response.StatusCode != HttpStatusCode.OK) throw Failed($"GitHub answered HTTP {status.ToString(CultureInfo.InvariantCulture)}.");
                if (response.Content.Headers.ContentLength is { } length && length != size)
                    throw length > MaxAssetBytes ? TooLarge() : Failed("GitHub sent a file of another size than the release lists.");
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        throw Failed("GitHub redirected the download more than once.");
    }

    // Reads the verified CSV once and writes the index beside it: entries and names to two temporary files, then one
    // file with the header, both and its own SHA-256, flushed and moved into place.
    private async Task<(string Sha, long Length, long Count)> ImportAsync(Latest latest, CancellationToken token)
    {
        string entriesPath = Path.Combine(folder, "entries.tmp"), namesPath = Path.Combine(folder, "names.tmp"), indexTemporary = IndexPath(latest.Tag) + ".tmp";
        try
        {
            var parser = new ListfileCsvParser();
            await using (var input = new FileStream(PartialPath, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var entries = new FileStream(entriesPath, FileMode.Create, FileAccess.Write, FileShare.None, ChunkBytes, FileOptions.Asynchronous))
            await using (var names = new FileStream(namesPath, FileMode.Create, FileAccess.Write, FileShare.None, ChunkBytes, FileOptions.Asynchronous))
            {
                var buffer = new byte[ChunkBytes];
                long position = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, token);
                    if (read == 0) break;
                    parser.Feed(buffer.AsSpan(0, read));
                    await Drain(parser.Entries, entries, token);
                    await Drain(parser.Names, names, token);
                    position += read;
                    lock (gate) completed = position;
                }
                parser.Complete();
                await Drain(parser.Entries, entries, token);
                await Drain(parser.Names, names, token);
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length;
            await using (var output = new FileStream(indexTemporary, FileMode.Create, FileAccess.Write, FileShare.None, ChunkBytes, FileOptions.Asynchronous))
            {
                var header = ListfileIndex.Header(parser.Count, block, parser.NameBytes);
                hash.AppendData(header);
                await output.WriteAsync(header, token);
                var buffer = new byte[ChunkBytes];
                foreach (var part in new[] { entriesPath, namesPath })
                {
                    await using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    int read;
                    while ((read = await input.ReadAsync(buffer, token)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                }
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
                length = output.Length;
            }
            token.ThrowIfCancellationRequested();
            File.Move(indexTemporary, IndexPath(latest.Tag), overwrite: true);
            return (Convert.ToHexStringLower(hash.GetHashAndReset()), length, parser.Count);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Failed("The names couldn’t be saved in the storage folder."); }
        finally { Delete(entriesPath); Delete(namesPath); Delete(indexTemporary); }

        static async Task Drain(MemoryStream source, FileStream target, CancellationToken token)
        {
            if (source.Length == 0) return;
            await target.WriteAsync(source.GetBuffer().AsMemory(0, (int)source.Length), token);
            source.SetLength(0);
        }
    }

    // The kept copy, verified; anything that doesn't verify is treated as never fetched, and the next import replaces it.
    private async Task<ListfileIndex?> LoadAsync(CancellationToken token)
    {
        try
        {
            var record = await ReadJsonAsync<Record>(RecordPath, token);
            if (record is null || record.Format != RecordFormat || record.Tag is not { } tag || !ValidTag(tag) || record.Sha256 is not { } sha || !Hex(sha)
                || record.ByteLength is < 1 or > MaxAssetBytes || record.NameCount is < 1 or > ListfileCsvParser.MaxNames || record.SourceUrl != AssetUrl(tag)
                || record.IndexSha256 is not { } indexSha || !Hex(indexSha) || record.IndexLength < ListfileIndex.HeaderBytes
                || !Instant(record.PublishedAt, out var publishedAt) || !Instant(record.FetchedAt, out var fetchedAt) || !Instant(record.CheckedAt, out var lastCheck) || lastCheck < fetchedAt)
                return null;
            var release = new CommunityListfileRelease(tag, publishedAt, fetchedAt, sha, record.ByteLength, record.NameCount, record.SourceUrl);
            var opened = await ListfileIndex.OpenAsync(IndexPath(tag), indexSha, record.IndexLength, record.NameCount, release, token);
            if (opened is not null) lock (gate) checkedAt = lastCheck;
            return opened;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private async Task SaveRecordAsync(CommunityListfileRelease release, DateTimeOffset checkedNow, CancellationToken token, string? indexSha = null, long? indexLength = null)
    {
        if (indexSha is null)
        {
            var kept = await ReadJsonAsync<Record>(RecordPath, token);
            if (kept is null || kept.Tag != release.Tag || kept.Sha256 != release.Sha256) throw Failed("The record of the kept copy couldn’t be read.");
            indexSha = kept.IndexSha256; indexLength = kept.IndexLength;
        }
        try
        {
            await WriteJsonAsync(RecordPath, new Record(RecordFormat, release.Tag, Stamp(release.PublishedAt), Stamp(release.FetchedAt), Stamp(checkedNow),
                release.Sha256, release.ByteLength, release.NameCount, release.SourceUrl, indexSha, indexLength!.Value), token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw Failed("The record couldn’t be saved in the storage folder."); }
    }

    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, token);
                await stream.FlushAsync(token);
                stream.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        catch { Delete(temporary); throw; }
    }

    private static async Task<T?> ReadJsonAsync<T>(string path, CancellationToken token) where T : class
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (stream.Length is < 1 or > MaxRecordBytes) return null;
            var bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, token);
            try { return JsonSerializer.Deserialize<T>(bytes, Json); } catch (JsonException) { return null; }
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException or EndOfStreamException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* A later attempt removes it. */ }
    }

    // Only fixed HTTPS hosts on the default port.
    private static bool Official(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Host is ApiHost or DownloadHost || AssetHosts.Contains(uri.Host));
    private bool AtTestOrigin(Uri uri) => origin is not null && string.IsNullOrEmpty(uri.UserInfo)
        && string.Equals(uri.GetLeftPart(UriPartial.Authority), origin.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);

    private HttpRequestMessage Request(Uri uri, string accept, long? from)
    {
        if (!Official(uri) && !AtTestOrigin(uri)) throw Failed("The listfile address isn’t one VibeMiner allows.");
        // With a test origin, every official address keeps its path and goes to that origin instead.
        var target = origin is not null && Official(uri) ? new Uri(origin, uri.PathAndQuery) : uri;
        var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.UserAgent.ParseAdd("VibeMiner/0.1 (+https://github.com/aircan80/vibeminer)");
        request.Headers.Accept.ParseAdd(accept);
        if (from is { } start) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, null);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken caller, CancellationToken token)
    {
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
        catch (HttpRequestException error) when (error.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError) { throw Offline(); }
        catch (HttpRequestException) { throw Failed("The connection to GitHub failed."); }
        // A connect timeout surfaces as a cancellation wrapping a TimeoutException; the caller didn't cancel.
        catch (OperationCanceledException error) when (!caller.IsCancellationRequested && error.InnerException is TimeoutException) { throw Offline(); }
    }

    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NotFound) throw NotFound();
        if ((int)response.StatusCode is >= 300 and < 400) throw Failed("GitHub redirected the request, and VibeMiner doesn’t follow that redirect.");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) throw Failed("GitHub is limiting requests right now.");
        if (!response.IsSuccessStatusCode) throw Failed($"GitHub answered HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}.");
    }

    private static async Task<byte[]> ReadSmallAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[64 * 1024];
            var length = 0;
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (length > MaxReleaseResponseBytes) break;
                    Array.Resize(ref buffer, Math.Min(MaxReleaseResponseBytes + 1, buffer.Length * 2));
                }
                var read = await stream.ReadAsync(buffer.AsMemory(length), token);
                if (read == 0) break;
                length += read;
            }
            if (length > MaxReleaseResponseBytes) throw Failed("GitHub sent a larger answer than VibeMiner allows.");
            return buffer[..length];
        }
        catch (Exception error) when (error is HttpRequestException or IOException) { throw Failed("The answer from GitHub was interrupted."); }
    }

    private static bool ValidTag(string tag) => tag.Length is >= 1 and <= 64 && char.IsAsciiLetterOrDigit(tag[0])
        && tag.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-');
    private static bool Hex(string text) => text.Length == 64 && text.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Instant(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out value) && value.Offset == TimeSpan.Zero;

    /// <summary>
    /// The listfile's own HTTP handler: no automatic redirects or decompression, one connection per host, a 10 s connect
    /// timeout, and every new connection taken from <paramref name="budget"/>. .NET's connection pool opens a new
    /// connection again when one closes before any response, so a proxy that drops connections made one request open 16
    /// of them; with the budget an attempt opens at most <see cref="ListfileConnectionBudget.PerAttempt"/>, then fails as
    /// offline until the next attempt.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(ListfileConnectionBudget budget) => new()
    {
        AllowAutoRedirect = false, MaxConnectionsPerServer = 1, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10), AutomaticDecompression = DecompressionMethods.None,
        ConnectCallback = async (context, token) =>
        {
            if (!budget.TryTake())
                throw new HttpRequestException(HttpRequestError.ConnectionError, "The listfile attempt used its connection budget.");
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        Task? running;
        lock (gate) running = loop;
        if (running is not null) try { await running; } catch (OperationCanceledException) { }
        lock (gate) { index?.Dispose(); index = null; }
        stop.Dispose();
    }

    private sealed record Record(int Format, string Tag, string PublishedAt, string FetchedAt, string CheckedAt, string Sha256, long ByteLength,
        long NameCount, string SourceUrl, string IndexSha256, long IndexLength);
    private sealed record Partial(int Format, string Tag, string Sha256, long ByteLength, string SourceUrl);

    internal static DiscoveryException Offline() => new("LISTFILE_FETCH_OFFLINE",
        "VibeMiner couldn’t reach GitHub to get the wow-listfile community file names.", true);
    internal static DiscoveryException Failed(string detail) => new("LISTFILE_FETCH_FAILED",
        $"Getting the wow-listfile community file names from GitHub failed. {detail}", true);
    private DiscoveryException TimedOut() => Failed($"GitHub didn’t answer within {budget.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds.");
    internal static DiscoveryException TooLarge() => new("LISTFILE_TOO_LARGE",
        $"The newest wow-listfile file is larger than VibeMiner allows ({MaxAssetBytes >> 20} MiB), so it wasn’t downloaded.");
    internal static DiscoveryException Invalid(string detail) => new("LISTFILE_INVALID",
        $"The newest wow-listfile file couldn’t be read safely, so it wasn’t used. {detail}");
    internal static DiscoveryException NotFound() => new("LISTFILE_NOT_FOUND",
        $"The newest wow-listfile release on GitHub has no {AssetName} file.");
}

/// <summary>How many new connections one listfile attempt may open (D105). Reset at the start of each attempt.</summary>
public sealed class ListfileConnectionBudget
{
    /// <summary>An attempt needs three (api.github.com, github.com, the asset host); twice that leaves room for one reconnect each.</summary>
    public const int PerAttempt = 6;
    private readonly int limit;
    private int left;
    public ListfileConnectionBudget(int perAttempt = PerAttempt) { limit = perAttempt; left = perAttempt; }
    public bool TryTake() => Interlocked.Decrement(ref left) >= 0;
    public void Reset() => Volatile.Write(ref left, limit);
}
