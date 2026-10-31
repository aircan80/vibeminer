using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

/// <summary>A table definition fetched from WoWDBDefs and kept in the verified cache (D081). FetchedAt is when
/// these exact bytes were first fetched; CheckedAt is when WoWDBDefs was last asked for a newer one.</summary>
public sealed record FetchedTableDefinition(string TableId, string TableName, string Sha256, long ByteLength,
    string SourceUrl, string Revision, DateTimeOffset FetchedAt, DateTimeOffset CheckedAt, DbdDocument Document)
{
    public TableDefinitionFetched Info => new(Revision, FetchedAt);
}

/// <summary>The newest verified definition this engine has for a table, and why it isn't newer when a refresh
/// failed. Both are null only when nothing was ever fetched and the fetch failed: then Failure says why.</summary>
/// Asked is true when this call contacted WoWDBDefs (successfully or not).
public sealed record FetchedDefinitionOutcome(FetchedTableDefinition? Definition, DiscoveryException? Failure, bool Asked = false);

/// <summary>
/// Fetches one table's .dbd from the official WoWDBDefs repository on GitHub when no bundled definition covers a file
/// (D081). The bytes are untrusted: HTTPS only, two fixed hosts, no redirects, at most 1 MiB, one time budget per fetch,
/// cancellable, and parsed only by the original bounded DBD reader. Nothing is executed. The definition's commit is
/// recorded. Fetched files live under the data folder with a record of their SHA-256, source URL and fetch time, are
/// verified on every load, and are refreshed at most once per refresh interval per table, so a cached definition keeps
/// working offline. Definitions describe column layouts only; all game data still comes from Blizzard's CDN.
/// </summary>
public sealed class WoWDBDefsDefinitions
{
    public const string ApiHost = "api.github.com";
    public const string RawHost = "raw.githubusercontent.com";
    public const string Repository = "wowdev/WoWDBDefs";
    public const string Branch = "master";
    public const string FolderName = "table-definitions-v1";
    public const int MaxDefinitionBytes = 1 << 20;
    public const int MaxCommitResponseBytes = 256 * 1024;
    public const int MaxRecordBytes = 16 * 1024;
    public const int MaxConcurrentFetches = 2;
    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan DefaultFetchBudget = TimeSpan.FromSeconds(30);
    /// <summary>The shortest time between two upstream checks of one table when its cache doesn't cover a build.</summary>
    public static readonly TimeSpan DefaultMinimumGap = TimeSpan.FromMinutes(15);
    private const int RecordFormat = 1;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string folder;
    private readonly HttpClient client;
    private readonly TimeProvider time;
    private readonly TimeSpan refresh, budget, gap;
    private readonly SemaphoreSlim fetches = new(MaxConcurrentFetches, MaxConcurrentFetches);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> tables = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FetchedTableDefinition> loaded = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, DiscoveryException Failure)> failures = new(StringComparer.Ordinal);
    private readonly WoWDBDefsConnections? connections;

    /// <summary>Pass <paramref name="connections"/> when <paramref name="client"/> runs on <see cref="CreateHandler"/>.</summary>
    public WoWDBDefsDefinitions(string dataDirectory, HttpClient client, TimeProvider? clock = null,
        TimeSpan? refreshInterval = null, TimeSpan? fetchBudget = null, TimeSpan? minimumGap = null, WoWDBDefsConnections? connections = null)
    {
        this.connections = connections;
        folder = Path.Combine(Path.GetFullPath(dataDirectory), FolderName, "wowdbdefs");
        this.client = client; time = clock ?? TimeProvider.System;
        refresh = refreshInterval ?? DefaultRefreshInterval; budget = fetchBudget ?? DefaultFetchBudget; gap = minimumGap ?? DefaultMinimumGap;
    }

    public static Uri CommitsUrl(string tableName) =>
        new($"https://{ApiHost}/repos/{Repository}/commits?path=definitions/{tableName}.dbd&sha={Branch}&per_page=1");
    public static string DefinitionUrl(string revision, string tableName) =>
        $"https://{RawHost}/{Repository}/{revision}/definitions/{tableName}.dbd";

    /// <summary>
    /// The definitions' own HTTP handler: no redirects or decompression, two connections per host, a 10 s connect timeout,
    /// and every new connection, also to a proxy, taken from <paramref name="connections"/>. .NET's pool opens a new
    /// connection again when one closes before any response, so one check through a proxy that drops connections opened a
    /// dozen or more within milliseconds (#124).
    /// </summary>
    public static SocketsHttpHandler CreateHandler(WoWDBDefsConnections connections) => new()
    {
        AllowAutoRedirect = false, MaxConnectionsPerServer = MaxConcurrentFetches, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10), AutomaticDecompression = DecompressionMethods.None,
        ConnectCallback = async (context, token) =>
        {
            await connections.TakeAsync(token);
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, token);
                return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        },
    };

    /// <summary>The verified cached definition, or null. Local only: never contacts WoWDBDefs.</summary>
    public async Task<FetchedTableDefinition?> CachedAsync(string tableId, string tableName, CancellationToken token)
    {
        Validate(tableId, tableName);
        if (loaded.TryGetValue(tableId, out var known) && known.TableName == tableName) return known;
        var definition = await LoadAsync(tableId, tableName, token);
        if (definition is not null) loaded[tableId] = definition;
        return definition;
    }

    /// <summary>The cached definition while it covers what the caller needs and is younger than the refresh interval.
    /// A cached definition that doesn't cover it is checked again upstream, but at most once per minimum gap, so a new
    /// build opens as soon as WoWDBDefs lists it without asking GitHub on every attempt. A failed refresh keeps the
    /// cached definition and says why. Cancellation stops the fetch. A deliberate request (a first open or Retry)
    /// skips the wait after a failure, so a reconnected network works at once.</summary>
    public async Task<FetchedDefinitionOutcome> EnsureAsync(string tableId, string tableName, Func<DbdDocument, bool> covers,
        Func<string, Task> progress, CancellationToken token, bool deliberate = false)
    {
        Validate(tableId, tableName);
        var gate = tables.GetOrAdd(tableId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            var cached = await CachedAsync(tableId, tableName, token);
            var now = time.GetUtcNow();
            // After a failed attempt, re-checks wait the same minimum gap, so an unreachable GitHub costs one attempt per
            // gap rather than one per table load. A deliberate request asks again at once.
            if (!deliberate && failures.TryGetValue(tableId, out var last) && last.At <= now && now - last.At < gap) return new(cached, last.Failure);
            if (!Due(cached, covers, now)) return new(cached, null);
            try
            {
                var fresh = await FetchAsync(tableId, tableName, cached, progress, token);
                failures.TryRemove(tableId, out _);
                return new(fresh, null, true);
            }
            catch (DiscoveryException failure) { failures[tableId] = (time.GetUtcNow(), failure); return new(cached, failure, true); }
        }
        finally { gate.Release(); }
    }

    /// <summary>True when <see cref="EnsureAsync"/> would ask WoWDBDefs now. Local only.</summary>
    public async Task<bool> RefreshDueAsync(string tableId, string tableName, Func<DbdDocument, bool> covers, CancellationToken token)
    {
        Validate(tableId, tableName);
        var cached = await CachedAsync(tableId, tableName, token);
        var now = time.GetUtcNow();
        if (failures.TryGetValue(tableId, out var last) && last.At <= now && now - last.At < gap) return false;
        return Due(cached, covers, now);
    }

    /// <summary>The outcome of the last time WoWDBDefs was asked about this table (#81), or null when it never was. Local
    /// only. The cached definition and its check time survive a restart; a failure is kept only while the engine runs,
    /// and a first open after a restart asks again anyway. NextCheckAfter is when opening the table's data asks again by
    /// itself: the gap after a failure (as re-checks wait; Retry table data asks at once), else the gap or, for a covering
    /// cache, the refresh interval after the cache's last check.</summary>
    public async Task<TableDefinitionCheck?> LastCheckAsync(string tableId, string tableName, Func<DbdDocument, bool> covers, CancellationToken token)
    {
        Validate(tableId, tableName);
        var cached = await CachedAsync(tableId, tableName, token);
        var failed = failures.TryGetValue(tableId, out var last);
        if (cached is null && !failed) return null;
        var now = time.GetUtcNow();
        // A success clears the failure, so a kept failure is always the newest attempt.
        var checkedAt = failed ? last.At : cached!.CheckedAt;
        var next = failed ? checkedAt + gap : cached!.CheckedAt > now ? now
            : cached.CheckedAt + (covers(cached.Document) && refresh > gap ? refresh : gap);
        return new(checkedAt, cached?.Info, failed ? last.Failure.Code : null, next);
    }

    // A cache that covers what's needed is asked about again after the refresh interval; one that doesn't, after the
    // minimum gap. A check time in the future (a clock moved back) is due.
    private bool Due(FetchedTableDefinition? cached, Func<DbdDocument, bool> covers, DateTimeOffset now)
    {
        if (cached is null || cached.CheckedAt > now) return true;
        var age = now - cached.CheckedAt;
        return !(age < refresh && covers(cached.Document) || age < gap);
    }

    private async Task<FetchedTableDefinition> FetchAsync(string tableId, string tableName, FetchedTableDefinition? cached,
        Func<string, Task> progress, CancellationToken token)
    {
        await fetches.WaitAsync(token);
        try
        {
            using var limit = new CancellationTokenSource(budget, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, limit.Token);
            try
            {
                await progress($"Checking WoWDBDefs for the newest {tableName} definition");
                var revision = await LatestRevisionAsync(tableName, linked.Token);
                var now = time.GetUtcNow();
                // The same commit is the same bytes: only the check time moves.
                if (cached is not null && cached.Revision == revision) return await SaveAsync(cached with { CheckedAt = now }, null, linked.Token);
                await progress($"Fetching the {tableName} definition from WoWDBDefs");
                var url = DefinitionUrl(revision, tableName);
                var bytes = await DownloadAsync(new Uri(url), MaxDefinitionBytes, tableName, linked.Token);
                DbdDocument document;
                try { document = DbdDefinitionReader.Parse(bytes, token: linked.Token); }
                catch (DiscoveryException failure) when (failure.Code.StartsWith("DBD_", StringComparison.Ordinal))
                { throw Invalid(tableName, failure.Code); }
                var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
                // Unchanged bytes keep their first revision and fetch time, so rows read with them stay current.
                var definition = cached is not null && cached.Sha256 == sha
                    ? cached with { CheckedAt = now }
                    : new FetchedTableDefinition(tableId, tableName, sha, bytes.Length, url, revision, now, now, document);
                return await SaveAsync(definition, cached?.Sha256 == sha ? null : bytes, linked.Token);
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested && !token.IsCancellationRequested)
            { throw Failed(tableName, $"WoWDBDefs didn’t answer within {budget.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds."); }
        }
        finally { fetches.Release(); }
    }

    // The last commit that changed this table's file on the default branch.
    private async Task<string> LatestRevisionAsync(string tableName, CancellationToken token)
    {
        var bytes = await DownloadAsync(CommitsUrl(tableName), MaxCommitResponseBytes, tableName, token, api: true);
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array) throw Failed(tableName, "GitHub sent an answer VibeMiner doesn’t understand.");
            if (root.GetArrayLength() == 0) throw NotFound(tableName);
            var first = root[0];
            if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("sha", out var sha) || sha.ValueKind != JsonValueKind.String ||
                sha.GetString() is not { Length: 40 } revision || !revision.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw Failed(tableName, "GitHub sent an answer VibeMiner doesn’t understand.");
            return revision;
        }
        catch (JsonException) { throw Failed(tableName, "GitHub sent an answer VibeMiner doesn’t understand."); }
    }

    private async Task<byte[]> DownloadAsync(Uri uri, int maximum, string tableName, CancellationToken token, bool api = false)
    {
        // Only these two fixed HTTPS hosts on the default port. Redirects are never followed.
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.Host is not (ApiHost or RawHost) || !string.IsNullOrEmpty(uri.UserInfo))
            throw Failed(tableName, "The definition address isn’t one VibeMiner allows.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("VibeMiner/0.1 (+https://github.com/aircan80/vibeminer)");
        request.Headers.Accept.ParseAdd(api ? "application/vnd.github+json" : "text/plain");
        if (api) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        HttpResponseMessage response;
        // The request may open connections until its answer's headers arrive (#124).
        using (connections?.Grant())
        {
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token); }
            catch (HttpRequestException error) when (Unreachable(error)) { throw Offline(tableName); }
            catch (HttpRequestException) { throw Failed(tableName, "The connection to GitHub failed."); }
            // A connect timeout surfaces as a cancellation wrapping a TimeoutException; the caller didn't cancel.
            catch (OperationCanceledException error) when (!token.IsCancellationRequested && error.InnerException is TimeoutException)
            { throw Offline(tableName); }
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound) throw NotFound(tableName);
            if ((int)response.StatusCode is >= 300 and < 400) throw Failed(tableName, "GitHub redirected the request, and VibeMiner doesn’t follow redirects.");
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                throw Failed(tableName, "GitHub is limiting requests right now.");
            if (!response.IsSuccessStatusCode)
                throw Failed(tableName, $"GitHub answered HTTP {((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)}.");
            if (response.Content.Headers.ContentLength > maximum) throw api ? Failed(tableName, "GitHub sent a larger answer than VibeMiner allows.") : TooLarge(tableName);
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[Math.Min(maximum + 1, (int?)response.Content.Headers.ContentLength + 1 ?? 64 * 1024)];
                var length = 0;
                while (true)
                {
                    if (length == buffer.Length)
                    {
                        if (length > maximum) break;
                        Array.Resize(ref buffer, Math.Min(maximum + 1, buffer.Length * 2));
                    }
                    var read = await stream.ReadAsync(buffer.AsMemory(length), token);
                    if (read == 0) break;
                    length += read;
                }
                if (length > maximum) throw api ? Failed(tableName, "GitHub sent a larger answer than VibeMiner allows.") : TooLarge(tableName);
                if (response.Content.Headers.ContentLength is { } declared && declared != length) throw Failed(tableName, "The download ended early.");
                return buffer[..length];
            }
            catch (HttpRequestException) { throw Failed(tableName, "The download from GitHub was interrupted."); }
            catch (IOException) { throw Failed(tableName, "The download from GitHub was interrupted."); }
        }
    }

    /// <summary>
    /// No answer came back: the name didn't resolve, no connection opened, or the connection, also one to a proxy, was
    /// closed or reset before any response (#125). The pool reports a reset as an unknown error over a socket error.
    /// A certificate failure or an answer VibeMiner can't read is a failure, not offline.
    /// </summary>
    private static bool Unreachable(HttpRequestException error) =>
        error.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded
        || HasSocketError(error.InnerException);

    private static bool HasSocketError(Exception? error)
    {
        for (var depth = 0; error is not null && depth < 8; error = error.InnerException, depth++)
            if (error is System.Net.Sockets.SocketException) return true;
        return false;
    }

    private string RecordPath(string tableId) => Path.Combine(folder, tableId + ".json");
    private string DefinitionPath(string tableId, string sha) => Path.Combine(folder, tableId + "-" + sha + ".dbd");

    // Bytes first, then the record that names them, each through a flushed temporary file. A crash between the two
    // leaves an unreferenced file that the next save removes; the record never names bytes that aren't there.
    private async Task<FetchedTableDefinition> SaveAsync(FetchedTableDefinition definition, byte[]? bytes, CancellationToken token)
    {
        try
        {
            Directory.CreateDirectory(folder);
            if (bytes is not null) await ReplaceAsync(DefinitionPath(definition.TableId, definition.Sha256), bytes, token);
            var record = JsonSerializer.SerializeToUtf8Bytes(new Record(RecordFormat, definition.TableId, definition.TableName, definition.Sha256,
                definition.ByteLength, definition.SourceUrl, definition.Revision,
                definition.FetchedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                definition.CheckedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)), Json);
            await ReplaceAsync(RecordPath(definition.TableId), record, token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Failed(definition.TableName, "The definition couldn’t be saved in the storage folder."); }
        loaded[definition.TableId] = definition;
        foreach (var stale in Directory.EnumerateFiles(folder, definition.TableId + "-*.dbd"))
            if (!string.Equals(Path.GetFileName(stale), Path.GetFileName(DefinitionPath(definition.TableId, definition.Sha256)), StringComparison.Ordinal))
                try { File.Delete(stale); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Removed by a later save. */ }
        return definition;
    }

    private static async Task ReplaceAsync(string path, byte[] bytes, CancellationToken token)
    {
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
        catch
        {
            try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* The next save replaces it. */ }
            throw;
        }
    }

    // Anything that doesn't verify is treated as never fetched, so the next fetch replaces it.
    private async Task<FetchedTableDefinition?> LoadAsync(string tableId, string tableName, CancellationToken token)
    {
        try
        {
            var raw = await ReadBoundedAsync(RecordPath(tableId), MaxRecordBytes, token);
            if (raw is null) return null;
            Record? record;
            try { record = JsonSerializer.Deserialize<Record>(raw, Json); } catch (JsonException) { return null; }
            if (record is null || record.Format != RecordFormat || record.TableId != tableId || record.TableName != tableName ||
                record.Sha256 is not { Length: 64 } sha || !sha.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                record.Revision is not { Length: 40 } revision || !revision.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                record.SourceUrl != DefinitionUrl(revision, tableName) || record.ByteLength is < 1 or > MaxDefinitionBytes ||
                !Instant(record.FetchedAt, out var fetchedAt) || !Instant(record.CheckedAt, out var checkedAt) || checkedAt < fetchedAt)
                return null;
            var bytes = await ReadBoundedAsync(DefinitionPath(tableId, sha), MaxDefinitionBytes, token);
            if (bytes is null || bytes.Length != record.ByteLength || Convert.ToHexStringLower(SHA256.HashData(bytes)) != sha) return null;
            DbdDocument document;
            try { document = DbdDefinitionReader.Parse(bytes, token: token); }
            catch (DiscoveryException failure) when (failure.Code.StartsWith("DBD_", StringComparison.Ordinal)) { return null; }
            return new(tableId, tableName, sha, bytes.Length, record.SourceUrl, revision, fetchedAt, checkedAt, document);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static async Task<byte[]?> ReadBoundedAsync(string path, int maximum, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (stream.Length is < 1 || stream.Length > maximum) return null;
            var bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, token);
            return await stream.ReadAsync(new byte[1], token) == 0 ? bytes : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (EndOfStreamException) { return null; }
    }

    private static bool Instant(string? text, out DateTimeOffset value) =>
        DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out value) && value.Offset == TimeSpan.Zero;

    private static void Validate(string tableId, string tableName)
    {
        if (tableId is not { Length: 8 } || !tableId.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f') ||
            tableName is not { Length: > 0 and <= 128 } || tableName.Any(ch => ch is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-')))
            throw new ArgumentException("A pinned provider table identity and name are required.");
    }

    private sealed record Record(int Format, string TableId, string TableName, string Sha256, long ByteLength,
        string SourceUrl, string Revision, string FetchedAt, string CheckedAt);

    internal static DiscoveryException Offline(string table) => new("DEFINITION_FETCH_OFFLINE",
        $"VibeMiner couldn’t reach WoWDBDefs on GitHub to fetch a newer {table} definition.", true);
    internal static DiscoveryException Failed(string table, string detail) => new("DEFINITION_FETCH_FAILED",
        $"Fetching a newer {table} definition from WoWDBDefs failed. {detail}", true);
    internal static DiscoveryException TooLarge(string table) => new("DEFINITION_FETCH_TOO_LARGE",
        $"The newest WoWDBDefs {table} definition is larger than VibeMiner allows (1 MiB), so it wasn’t used.");
    internal static DiscoveryException Invalid(string table, string code) => new("DEFINITION_FETCH_INVALID",
        $"The newest WoWDBDefs {table} definition couldn’t be read safely ({code}), so it wasn’t used.");
    internal static DiscoveryException NotFound(string table) => new("DEFINITION_FETCH_NOT_FOUND",
        $"WoWDBDefs on GitHub has no {table}.dbd file to fetch, so only the bundled {table} definition can be used.");
}

/// <summary>
/// New connections the WoWDBDefs client may open (#124). Each request grants <see cref="PerRequest"/> while it waits for
/// its answer, and the grant ends with the last request in flight, so the client never opens more than that many per
/// request and none for a request it didn't make. One connection per request opens at once; any other starts at least
/// <see cref="Spacing"/> after the connection before it, so a proxy that drops connections sees two a second apart
/// instead of a burst. Waiting is cancellable.
/// </summary>
public sealed class WoWDBDefsConnections(TimeProvider? clock = null)
{
    /// <summary>The first connection and one reconnect.</summary>
    public const int PerRequest = 2;
    public static readonly TimeSpan Spacing = TimeSpan.FromSeconds(1);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Lock gate = new();
    private int requests, left, firsts;
    private long last;

    internal IDisposable Grant()
    {
        lock (gate) { requests++; left += PerRequest; firsts++; }
        return new Granted(this);
    }

    private void End() { lock (gate) if (--requests == 0) left = firsts = 0; }

    /// <summary>Waits for this connection's turn, then takes it, or refuses as a connection error when none is left.</summary>
    internal async ValueTask TakeAsync(CancellationToken token)
    {
        TimeSpan wait;
        lock (gate)
        {
            if (left == 0) throw Refused();
            var now = time.GetTimestamp();
            if (firsts > 0) { firsts--; left--; last = Math.Max(last, now); return; }
            // A reconnect: its turn comes a spacing after the connection before it.
            var start = Math.Max(now, last + (long)(Spacing.TotalSeconds * time.TimestampFrequency));
            last = start;
            wait = TimeSpan.FromSeconds((start - now) / (double)time.TimestampFrequency);
        }
        if (wait > TimeSpan.Zero) await Task.Delay(wait, time, token);
        // A request that ended or was cancelled while this waited took its grant with it.
        lock (gate) { if (left == 0) throw Refused(); left--; }
    }

    private static HttpRequestException Refused() =>
        new(HttpRequestError.ConnectionError, "The WoWDBDefs requests used the connections they may open.");

    private sealed class Granted(WoWDBDefsConnections owner) : IDisposable
    {
        private int done;
        public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) owner.End(); }
    }
}
