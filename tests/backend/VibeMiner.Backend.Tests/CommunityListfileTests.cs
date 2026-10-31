using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Host;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

/// <summary>#21 / D105: community file names from wow-listfile, against a local fake GitHub and a tiny authored listfile.</summary>
public sealed class CommunityListfileTests
{
    private const string Tag = "202601010000";
    private const string Newer = "202601080000";
    private const string AssetHost = "release-assets.githubusercontent.com";
    // Authored names, not from the real listfile.
    private static readonly byte[] Csv = Encoding.UTF8.GetBytes("1;authored/one.txt\r\n7;Authored/Seven.blp\r\n8;authored/eight.m2\r\n40;authored/forty.ogg\r\n4000000000;authored/last.txt\r\n");

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Policy(bool enabled) : ICommunityListfilePolicy
    {
        public bool Enabled = enabled;
        public Task<bool> CommunityListfileEnabledAsync(CancellationToken token) => Task.FromResult(Enabled);
    }

    /// <summary>A local fake of api.github.com, github.com and the release asset host, with HTTP ranges.</summary>
    private sealed class GitHub : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        public string ReleaseTag = Tag;
        public byte[] Bytes = Csv;
        public string? Digest;
        public string RedirectHost = AssetHost;
        public Exception? Failure;
        /// <summary>When set, the asset sends this many bytes and then waits until cancelled.</summary>
        public int? StallAfter;
        public bool IgnoreRange;
        /// <summary>The size the release lists, when it isn't the bytes' own.</summary>
        public long? ListedSize;
        /// <summary>Bytes sent after the listed ones.</summary>
        public byte[] Extra = [];
        public bool WrongRange;
        public readonly TaskCompletionSource Stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Release() => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["tag_name"] = ReleaseTag, ["published_at"] = "2026-01-01T00:00:00Z", ["body"] = "authored",
            ["assets"] = new object[]
            {
                new Dictionary<string, object> { ["name"] = "lookup.csv", ["size"] = 3, ["digest"] = "sha256:" + new string('0', 64), ["browser_download_url"] = "https://github.com/x/y" },
                new Dictionary<string, object> { ["name"] = CommunityListfile.AssetName, ["size"] = ListedSize ?? Bytes.Length,
                    ["digest"] = "sha256:" + (Digest ?? Convert.ToHexStringLower(SHA256.HashData(Bytes))), ["browser_download_url"] = CommunityListfile.AssetUrl(ReleaseTag) },
            },
        });
        public int Downloads => Requests.Count(request => request.RequestUri!.Host == AssetHost);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Assert.Equal("https", uri.Scheme);
            lock (Requests) Requests.Add(request);
            if (Failure is not null) throw Failure;
            if (uri.AbsoluteUri == CommunityListfile.LatestReleaseUrl.AbsoluteUri) return new(HttpStatusCode.OK) { Content = new StringContent(Release()) };
            if (uri.AbsoluteUri == CommunityListfile.AssetUrl(ReleaseTag))
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri($"https://{RedirectHost}/authored/{ReleaseTag}?sig=1");
                return redirect;
            }
            if (uri.Host == AssetHost)
            {
                var from = IgnoreRange ? 0 : request.Headers.Range?.Ranges.Single().From ?? 0;
                var body = Bytes[(int)from..].Concat(Extra).ToArray();
                HttpContent content = StallAfter is { } stall ? new StallingContent(body[..Math.Min(stall, body.Length)], Stalled) : new ByteArrayContent(body);
                var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
                if (StallAfter is null) response.Content.Headers.ContentLength = body.Length;
                if (from > 0) response.Content.Headers.ContentRange = WrongRange ? new ContentRangeHeaderValue(0, Bytes.Length - 1, Bytes.Length) : new ContentRangeHeaderValue(from, Bytes.Length - 1, Bytes.Length);
                return response;
            }
            await Task.Yield();
            return new(HttpStatusCode.NotFound);
        }
    }

    private sealed class StallingContent(byte[] prefix, TaskCompletionSource stalled) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await stream.WriteAsync(prefix);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Stalling(prefix, stalled));
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        private sealed class Stalling(byte[] prefix, TaskCompletionSource stalled) : Stream
        {
            private int position;
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            {
                if (position < prefix.Length)
                {
                    var count = Math.Min(buffer.Length, prefix.Length - position);
                    prefix.AsMemory(position, count).CopyTo(buffer); position += count; return count;
                }
                stalled.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return 0;
            }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException(); public override long Position { get => position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private static CommunityListfile Listfile(string directory, GitHub github, Policy? policy = null, Clock? clock = null, int block = 2, TimeSpan? budget = null) =>
        new(directory, new HttpClient(github), policy ?? new Policy(true), clock ?? new Clock(), readBudget: budget, indexBlock: block, background: false);

    // ---------- the CSV parser ----------

    private static ListfileCsvParser Parse(string text, int chunk = 3)
    {
        var parser = new ListfileCsvParser();
        var bytes = Encoding.UTF8.GetBytes(text);
        for (var at = 0; at < bytes.Length; at += chunk) parser.Feed(bytes.AsSpan(at, Math.Min(chunk, bytes.Length - at)));
        parser.Complete();
        return parser;
    }

    [Theory]
    [InlineData("1;a\n2;b\n")]
    [InlineData("1;a\r\n2;b\r\n")]
    [InlineData("1;a\n\n2;b")]
    public void The_parser_reads_lf_crlf_blank_lines_and_a_last_line_without_a_newline(string text)
    {
        var parser = Parse(text);
        Assert.Equal(2, parser.Count);
        Assert.Equal(2, parser.NameBytes);
        Assert.Equal("ab", Encoding.UTF8.GetString(parser.Names.ToArray()));
        Assert.Equal(16, parser.Entries.Length);
    }

    [Theory]
    [InlineData("2;a\n1;b\n", "order")]
    [InlineData("1;a\n1;b\n", "order")]
    [InlineData("01;a\n", "leading zero")]
    [InlineData("4294967296;a\n", "above")]
    [InlineData("12345678901;a\n", "semicolon")]
    [InlineData("a;b\n", "semicolon")]
    [InlineData("1,a\n", "semicolon")]
    [InlineData("1;\n", "name of 0 bytes")]
    [InlineData("1;a\tb\n", "control")]
    [InlineData("", "no names")]
    [InlineData("\n\n", "no names")]
    public void The_parser_refuses_anything_outside_the_format(string text, string detail)
    {
        var failure = Assert.Throws<DiscoveryException>(() => Parse(text));
        Assert.Equal("LISTFILE_INVALID", failure.Code);
        Assert.Contains(detail, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_parser_removes_bidirectional_formatting_characters_from_names()
    {
        // U+202E (right-to-left override), U+2066 (left-to-right isolate), U+200F and U+061C around an authored name.
        var parser = Parse("1;a\u202Eb\u2066c\u200Fd\u061Ce.txt\n2;\u2069\u202Ax\n", 5);
        Assert.Equal("abcde.txtx", Encoding.UTF8.GetString(parser.Names.ToArray()));
        Assert.Equal(10, parser.NameBytes);
        // Other non-ASCII text is kept as it is.
        Assert.Equal("é→", Encoding.UTF8.GetString(Parse("1;é→\n").Names.ToArray()));
        Assert.Contains("only of direction marks", Assert.Throws<DiscoveryException>(() => Parse("1;\u202E\u2066\n")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_parser_refuses_invalid_utf8_and_overlong_lines()
    {
        var parser = new ListfileCsvParser();
        Assert.Equal("LISTFILE_INVALID", Assert.Throws<DiscoveryException>(() => parser.Feed([(byte)'1', (byte)';', 0xC3, 0x28, (byte)'\n'])).Code);
        var longer = new ListfileCsvParser();
        var line = Encoding.ASCII.GetBytes("1;" + new string('a', ListfileCsvParser.MaxNameBytes + 1));
        Assert.Contains("name of 1025 bytes", Assert.Throws<DiscoveryException>(() => { longer.Feed(line); longer.Feed("\n"u8); }).Message, StringComparison.Ordinal);
        // A line with no newline in sight is refused once it passes the line bound, before it is held whole.
        var endless = new ListfileCsvParser();
        Assert.Contains("longer than", Assert.Throws<DiscoveryException>(() => { for (var i = 0; i < 20; i++) endless.Feed(new byte[100]); }).Message, StringComparison.Ordinal);
        var name = new ListfileCsvParser();
        name.Feed(Encoding.ASCII.GetBytes("1;" + new string('a', ListfileCsvParser.MaxNameBytes) + "\n"));
        Assert.Equal(1, name.Count);
    }

    // ---------- download, verify, serve ----------

    [Fact]
    public async Task A_release_is_downloaded_verified_and_names_label_fileDataIds_across_index_blocks()
    {
        using var directory = new TempDirectory(); var github = new GitHub(); var clock = new Clock();
        await using var listfile = Listfile(directory.Path, github, clock: clock);
        Assert.Equal("notReady", (await listfile.LookupAsync([7], default)).Source.State);
        await listfile.EnsureAsync(false, default);
        var lookup = await listfile.LookupAsync([0, 1, 7, 8, 9, 40, 4000000000, 4294967295], default);
        Assert.Equal("ready", lookup.Source.State);
        Assert.Equal(Tag, lookup.Source.Tag);
        Assert.Equal(new Dictionary<long, string> { [1] = "authored/one.txt", [7] = "Authored/Seven.blp", [8] = "authored/eight.m2", [40] = "authored/forty.ogg", [4000000000] = "authored/last.txt" }, lookup.Names);
        var status = await listfile.StatusAsync(default);
        Assert.Equal(CommunityListfileActivity.Idle, status.Activity);
        Assert.Null(status.Problem);
        Assert.Equal(new CommunityListfileRelease(Tag, new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), clock.Now, Convert.ToHexStringLower(SHA256.HashData(Csv)), Csv.Length, 5, CommunityListfile.AssetUrl(Tag)), status.Release);
        Assert.Equal(clock.Now, status.CheckedAt);
        // Only the three fixed hosts, the asset through one redirect; the download asked for the asset as bytes.
        Assert.Equal([CommunityListfile.ApiHost, CommunityListfile.DownloadHost, AssetHost], github.Requests.Select(r => r.RequestUri!.Host));
        // Nothing but the index and its record is kept: the downloaded CSV is gone.
        var folder = Path.Combine(directory.Path, CommunityListfile.FolderName);
        Assert.Equal(["names-" + Tag + ".vlf", "release.json"], Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_kept_copy_is_verified_at_start_and_used_offline_without_asking_GitHub_until_a_check_is_due()
    {
        using var directory = new TempDirectory(); var github = new GitHub(); var clock = new Clock();
        await using (var first = Listfile(directory.Path, github, clock: clock)) await first.EnsureAsync(false, default);
        var offline = new GitHub { Failure = new HttpRequestException(HttpRequestError.ConnectionError, "offline") };
        await using var restarted = Listfile(directory.Path, offline, clock: clock);
        clock.Now += TimeSpan.FromDays(6);
        var wait = await restarted.EnsureAsync(false, default);
        Assert.Empty(offline.Requests);
        Assert.True(wait <= TimeSpan.FromHours(1));
        Assert.Equal("Authored/Seven.blp", (await restarted.LookupAsync([7], default)).Names[7]);
        // A week after the last check it asks; offline keeps the copy in use and says why it isn't newer.
        clock.Now += TimeSpan.FromDays(1);
        await restarted.EnsureAsync(false, default);
        Assert.Single(offline.Requests);
        var status = await restarted.StatusAsync(default);
        Assert.Equal("LISTFILE_FETCH_OFFLINE", status.Problem!.Code);
        Assert.Equal(Tag, status.Release!.Tag);
        Assert.Equal("ready", (await restarted.LookupAsync([7], default)).Source.State);
    }

    [Fact]
    public async Task The_same_release_is_not_downloaded_again_and_a_newer_one_replaces_the_old_index()
    {
        using var directory = new TempDirectory(); var github = new GitHub(); var clock = new Clock();
        await using var listfile = Listfile(directory.Path, github, clock: clock);
        await listfile.EnsureAsync(false, default);
        clock.Now += TimeSpan.FromMinutes(1);
        await listfile.EnsureAsync(true, default);
        Assert.Equal(1, github.Downloads);
        Assert.Equal(clock.Now, (await listfile.StatusAsync(default)).CheckedAt);
        github.ReleaseTag = Newer; github.Bytes = Encoding.UTF8.GetBytes("7;authored/renamed.blp\n");
        await listfile.EnsureAsync(true, default);
        var lookup = await listfile.LookupAsync([1, 7], default);
        Assert.Equal(Newer, lookup.Source.Tag);
        Assert.Equal(new Dictionary<long, string> { [7] = "authored/renamed.blp" }, lookup.Names);
        Assert.Equal(["names-" + Newer + ".vlf", "release.json"],
            Directory.EnumerateFiles(Path.Combine(directory.Path, CommunityListfile.FolderName)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Off_asks_nothing_and_serves_no_names_and_turning_off_keeps_the_copy_for_later()
    {
        using var directory = new TempDirectory(); var github = new GitHub(); var policy = new Policy(false);
        await using var listfile = Listfile(directory.Path, github, policy);
        Assert.Equal("LISTFILE_OFF", (await Assert.ThrowsAsync<DiscoveryException>(() => listfile.CheckAsync(default))).Code);
        Assert.Same(FileNameLookup.Off, await listfile.LookupAsync([7], default));
        Assert.False((await listfile.StatusAsync(default)).Enabled);
        Assert.Empty(github.Requests);
        policy.Enabled = true;
        await listfile.EnsureAsync(false, default);
        policy.Enabled = false;
        Assert.Equal("off", (await listfile.LookupAsync([7], default)).Source.State);
        Assert.Empty((await listfile.LookupAsync([7], default)).Names);
        policy.Enabled = true;
        Assert.Equal("Authored/Seven.blp", (await listfile.LookupAsync([7], default)).Names[7]);
    }

    [Fact]
    public async Task Turning_off_stops_a_download_and_turning_on_resumes_it_from_the_kept_bytes()
    {
        using var directory = new TempDirectory(); var github = new GitHub { StallAfter = 20 }; var policy = new Policy(true);
        await using var listfile = Listfile(directory.Path, github, policy);
        var running = listfile.EnsureAsync(false, default);
        await github.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var downloading = await listfile.StatusAsync(default);
        Assert.Equal(CommunityListfileActivity.Downloading, downloading.Activity);
        Assert.Equal(Tag, downloading.PendingTag);
        Assert.Equal(20, downloading.Completed); Assert.Equal(Csv.Length, downloading.Total);
        policy.Enabled = false; listfile.SettingChanged(false);
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null((await listfile.StatusAsync(default)).Problem);
        Assert.Equal(20, new FileInfo(Path.Combine(directory.Path, CommunityListfile.FolderName, "download.csv.partial")).Length);
        github.StallAfter = null; policy.Enabled = true;
        // The kept bytes alone make the next step due, and it asks only for the rest.
        await listfile.EnsureAsync(false, default);
        var resumed = github.Requests.Last(request => request.RequestUri!.Host == AssetHost);
        Assert.Equal(20, resumed.Headers.Range!.Ranges.Single().From);
        Assert.Equal("authored/forty.ogg", (await listfile.LookupAsync([40], default)).Names[40]);
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_starts_again_from_the_first_byte()
    {
        using var directory = new TempDirectory(); var github = new GitHub { StallAfter = 10 }; var policy = new Policy(true);
        await using var listfile = Listfile(directory.Path, github, policy);
        var running = listfile.EnsureAsync(false, default);
        await github.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        listfile.SettingChanged(false);
        await running;
        github.StallAfter = null; github.IgnoreRange = true;
        await listfile.EnsureAsync(false, default);
        Assert.Equal("authored/one.txt", (await listfile.LookupAsync([1], default)).Names[1]);
    }

    [Theory]
    [InlineData("reset", "LISTFILE_FETCH_OFFLINE")]
    [InlineData("ended", "LISTFILE_FETCH_OFFLINE")]
    [InlineData("tls-reset", "LISTFILE_FETCH_OFFLINE")]
    [InlineData("certificate", "LISTFILE_FETCH_FAILED")]
    [InlineData("garbled", "LISTFILE_FETCH_FAILED")]
    public async Task A_connection_closed_before_any_answer_is_offline(string shape, string code)
    {
        // #127: a proxy's reset surfaced as an unknown error over a socket error and read as a failed fetch.
        using var directory = new TempDirectory(); var github = new GitHub { Failure = ConnectionFailures.Of(shape) };
        await using var listfile = Listfile(directory.Path, github);
        await listfile.EnsureAsync(true, default);
        var problem = (await listfile.StatusAsync(default)).Problem!;
        Assert.Equal(code, problem.Code); Assert.True(problem.Retryable);
        Assert.Single(github.Requests);
    }

    [Fact]
    public async Task Bytes_that_dont_match_GitHubs_digest_are_never_read()
    {
        using var directory = new TempDirectory(); var github = new GitHub { Digest = new string('a', 64) };
        await using var listfile = Listfile(directory.Path, github);
        await listfile.EnsureAsync(false, default);
        var status = await listfile.StatusAsync(default);
        Assert.Equal("LISTFILE_FETCH_FAILED", status.Problem!.Code);
        Assert.Contains("SHA-256", status.Problem.Message, StringComparison.Ordinal);
        Assert.Null(status.Release);
        Assert.Equal("failed", (await listfile.LookupAsync([7], default)).Source.State);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, CommunityListfile.FolderName)));
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_refused()
    {
        using var directory = new TempDirectory(); var github = new GitHub { RedirectHost = "example.com" };
        await using var listfile = Listfile(directory.Path, github);
        await listfile.EnsureAsync(false, default);
        Assert.Contains("doesn’t allow", (await listfile.StatusAsync(default)).Problem!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(github.Requests, request => request.RequestUri!.Host == "example.com");
    }

    [Fact]
    public async Task A_listfile_that_cant_be_read_is_refused_and_the_same_bytes_are_not_downloaded_again()
    {
        using var directory = new TempDirectory(); var github = new GitHub { Bytes = "7;b\n1;a\n"u8.ToArray() }; var clock = new Clock();
        await using var listfile = Listfile(directory.Path, github, clock: clock);
        await listfile.EnsureAsync(false, default);
        Assert.Equal("LISTFILE_INVALID", (await listfile.StatusAsync(default)).Problem!.Code);
        await listfile.EnsureAsync(true, default);
        Assert.Equal(1, github.Downloads);
        Assert.Equal("LISTFILE_INVALID", (await listfile.StatusAsync(default)).Problem!.Code);
    }

    [Fact]
    public async Task After_a_failure_automatic_attempts_wait_the_gap_and_a_deliberate_check_asks_at_once()
    {
        using var directory = new TempDirectory(); var github = new GitHub { Failure = new HttpRequestException(HttpRequestError.NameResolutionError, "dns") }; var clock = new Clock();
        await using var listfile = Listfile(directory.Path, github, clock: clock);
        Assert.Equal(CommunityListfile.DefaultMinimumGap, await listfile.EnsureAsync(false, default));
        Assert.Equal("LISTFILE_FETCH_OFFLINE", (await listfile.StatusAsync(default)).Problem!.Code);
        clock.Now += TimeSpan.FromMinutes(5);
        Assert.Equal(TimeSpan.FromMinutes(10), await listfile.EnsureAsync(false, default));
        Assert.Single(github.Requests);
        github.Failure = null;
        await listfile.EnsureAsync(true, default);
        Assert.Null((await listfile.StatusAsync(default)).Problem);
        Assert.Equal("ready", (await listfile.LookupAsync([7], default)).Source.State);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("record")]
    public async Task A_kept_copy_that_doesnt_verify_is_never_used_and_is_downloaded_again(string damage)
    {
        using var directory = new TempDirectory(); var github = new GitHub(); var clock = new Clock();
        await using (var first = Listfile(directory.Path, github, clock: clock)) await first.EnsureAsync(false, default);
        var folder = Path.Combine(directory.Path, CommunityListfile.FolderName);
        if (damage == "index")
        {
            var path = Path.Combine(folder, "names-" + Tag + ".vlf");
            var bytes = await File.ReadAllBytesAsync(path); bytes[^1] ^= 0x20; await File.WriteAllBytesAsync(path, bytes);
        }
        else await File.WriteAllTextAsync(Path.Combine(folder, "release.json"), "{\"format\":1}");
        var offline = new GitHub { Failure = new HttpRequestException(HttpRequestError.ConnectionError, "offline") };
        await using (var restarted = Listfile(directory.Path, offline, clock: clock))
        {
            await restarted.EnsureAsync(false, default);
            Assert.Equal("failed", (await restarted.LookupAsync([7], default)).Source.State);
            Assert.Single(offline.Requests);
        }
        await using var online = Listfile(directory.Path, new GitHub(), clock: clock);
        await online.EnsureAsync(false, default);
        Assert.Equal("authored/last.txt", (await online.LookupAsync([4000000000], default)).Names[4000000000]);
    }

    [Fact]
    public async Task A_release_without_the_asset_or_without_a_digest_is_refused()
    {
        using var directory = new TempDirectory();
        var missing = new GitHub();
        var answer = missing.Release().Replace(CommunityListfile.AssetName, "other.csv", StringComparison.Ordinal);
        var handler = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer) }));
        await using var listfile = new CommunityListfile(directory.Path, new HttpClient(handler), new Policy(true), new Clock());
        await listfile.EnsureAsync(false, default);
        Assert.Equal("LISTFILE_NOT_FOUND", (await listfile.StatusAsync(default)).Problem!.Code);
        var noDigest = missing.Release().Replace("\"digest\":\"sha256:" + Convert.ToHexStringLower(SHA256.HashData(Csv)) + "\",", "", StringComparison.Ordinal);
        var second = new Handler((request, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(noDigest) }));
        await using var other = new CommunityListfile(Path.Combine(directory.Path, "b"), new HttpClient(second), new Policy(true), new Clock());
        await other.EnsureAsync(false, default);
        Assert.Contains("didn’t publish a SHA-256", (await other.StatusAsync(default)).Problem!.Message, StringComparison.Ordinal);
    }

    private static async Task<CommunityListfileStatus> Until(CommunityListfile listfile, Func<CommunityListfileStatus, bool> done)
    {
        for (var i = 0; i < 500; i++)
        {
            var status = await listfile.StatusAsync(default);
            if (done(status)) return status;
            await Task.Delay(20);
        }
        throw new TimeoutException("The listfile loop didn't get there.");
    }

    [Fact]
    public async Task Nothing_is_fetched_until_Files_asks_for_names_and_a_kept_copy_starts_the_loop_at_the_next_launch()
    {
        using var directory = new TempDirectory(); var github = new GitHub(); var clock = new Clock();
        await using (var listfile = new CommunityListfile(directory.Path, new HttpClient(github), new Policy(true), clock, indexBlock: 2))
        {
            listfile.StartIfKept();
            await Task.Delay(100);
            Assert.Empty(github.Requests);
            Assert.False((await listfile.StatusAsync(default)).Started);
            // Files' first page is the demand: it gets no names yet, and the download starts behind it.
            Assert.Equal("notReady", (await listfile.LookupAsync([7], default)).Source.State);
            Assert.True((await Until(listfile, status => status.Release is not null)).Started);
            Assert.Equal("Authored/Seven.blp", (await listfile.LookupAsync([7], default)).Names[7]);
        }
        var later = new GitHub();
        await using var restarted = new CommunityListfile(directory.Path, new HttpClient(later), new Policy(true), clock, indexBlock: 2);
        restarted.StartIfKept();
        Assert.True((await Until(restarted, status => status.Release is not null)).Started);
        Assert.Empty(later.Requests);
    }

    [Fact]
    public async Task Turning_the_switch_on_or_asking_from_Settings_starts_the_download()
    {
        using var directory = new TempDirectory(); var github = new GitHub();
        await using (var listfile = new CommunityListfile(directory.Path, new HttpClient(github), new Policy(true), new Clock(), indexBlock: 2))
        {
            var asked = await listfile.CheckAsync(default);
            Assert.True(asked.Started);
            await Until(listfile, status => status.Release is not null && status.Activity == CommunityListfileActivity.Idle);
        }
        // Without the loop running yet, the reply already says a check is under way.
        using var quiet = new TempDirectory();
        await using (var idle = Listfile(quiet.Path, new GitHub()))
            Assert.Equal(CommunityListfileActivity.Checking, (await idle.CheckAsync(default)).Activity);
        using var other = new TempDirectory();
        await using var switched = new CommunityListfile(other.Path, new HttpClient(new GitHub()), new Policy(true), new Clock(), indexBlock: 2);
        switched.SettingChanged(true);
        await Until(switched, status => status.Release is not null);
    }

    [Fact]
    public async Task A_download_that_stops_sending_fails_after_the_idle_budget_and_keeps_its_bytes()
    {
        using var directory = new TempDirectory(); var github = new GitHub { StallAfter = 12 };
        await using var listfile = Listfile(directory.Path, github, budget: TimeSpan.FromMilliseconds(300));
        await listfile.EnsureAsync(false, default).WaitAsync(TimeSpan.FromSeconds(10));
        var problem = (await listfile.StatusAsync(default)).Problem!;
        Assert.Equal("LISTFILE_FETCH_FAILED", problem.Code);
        Assert.Contains("stopped sending", problem.Message, StringComparison.Ordinal);
        Assert.True(problem.Retryable);
        Assert.Equal(12, new FileInfo(Path.Combine(directory.Path, CommunityListfile.FolderName, "download.csv.partial")).Length);
        Assert.Equal("failed", (await listfile.LookupAsync([7], default)).Source.State);
    }

    [Fact]
    public async Task A_release_listing_more_than_512_MiB_is_too_large_and_never_downloaded()
    {
        using var directory = new TempDirectory(); var github = new GitHub { ListedSize = CommunityListfile.MaxAssetBytes + 1 };
        await using var listfile = Listfile(directory.Path, github);
        await listfile.EnsureAsync(false, default);
        var problem = (await listfile.StatusAsync(default)).Problem!;
        Assert.Equal("LISTFILE_TOO_LARGE", problem.Code);
        Assert.False(problem.Retryable);
        Assert.Equal(0, github.Downloads);
        // 512 MiB itself is allowed: the bound is inclusive, and only the bytes' own length then fails.
        github.ListedSize = CommunityListfile.MaxAssetBytes;
        await listfile.EnsureAsync(true, default);
        Assert.NotEqual("LISTFILE_TOO_LARGE", (await listfile.StatusAsync(default)).Problem!.Code);
    }

    [Fact]
    public async Task A_body_longer_than_the_release_lists_is_refused()
    {
        using var directory = new TempDirectory(); var github = new GitHub { Extra = "9;authored/extra\n"u8.ToArray(), StallAfter = int.MaxValue };
        await using var listfile = Listfile(directory.Path, github);
        await listfile.EnsureAsync(false, default).WaitAsync(TimeSpan.FromSeconds(10));
        var problem = (await listfile.StatusAsync(default)).Problem!;
        Assert.Equal("LISTFILE_FETCH_FAILED", problem.Code);
        Assert.Contains("more bytes than the release lists", problem.Message, StringComparison.Ordinal);
        Assert.Null((await listfile.StatusAsync(default)).Release);
    }

    [Fact]
    public async Task A_declared_length_other_than_the_release_lists_is_refused_before_reading()
    {
        using var directory = new TempDirectory(); var github = new GitHub { Extra = "9;x\n"u8.ToArray() };
        await using var listfile = Listfile(directory.Path, github);
        await listfile.EnsureAsync(false, default);
        Assert.Contains("another size than the release lists", (await listfile.StatusAsync(default)).Problem!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_resume_answered_at_another_place_is_refused_and_the_kept_bytes_stay()
    {
        using var directory = new TempDirectory(); var github = new GitHub { StallAfter = 20 }; var policy = new Policy(true);
        await using var listfile = Listfile(directory.Path, github, policy);
        var running = listfile.EnsureAsync(false, default);
        await github.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        listfile.SettingChanged(false); await running;
        github.StallAfter = null; github.WrongRange = true;
        await listfile.EnsureAsync(false, default);
        Assert.Contains("resumed the download at another place", (await listfile.StatusAsync(default)).Problem!.Message, StringComparison.Ordinal);
        Assert.Equal(20, new FileInfo(Path.Combine(directory.Path, CommunityListfile.FolderName, "download.csv.partial")).Length);
    }

    [Fact]
    public async Task A_new_engine_resumes_a_partial_download_kept_from_before_a_restart()
    {
        using var directory = new TempDirectory(); var github = new GitHub { StallAfter = 20 }; var clock = new Clock();
        // The first engine closes while downloading: shutdown cancels it, and only download.json and the bytes remain.
        await using (var first = new CommunityListfile(directory.Path, new HttpClient(github), new Policy(true), clock, indexBlock: 2))
        {
            first.Start();
            await github.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        var folder = Path.Combine(directory.Path, CommunityListfile.FolderName);
        Assert.Equal(["download.csv.partial", "download.json"], Directory.EnumerateFiles(folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var later = new GitHub();
        await using var restarted = new CommunityListfile(directory.Path, new HttpClient(later), new Policy(true), clock, indexBlock: 2);
        restarted.StartIfKept();
        await Until(restarted, status => status.Release is not null);
        Assert.Equal(20, later.Requests.Single(request => request.RequestUri!.Host == AssetHost).Headers.Range!.Ranges.Single().From);
        Assert.Equal("authored/forty.ogg", (await restarted.LookupAsync([40], default)).Names[40]);
    }

    [Fact]
    public async Task A_loopback_test_origin_takes_every_request_and_anything_else_is_refused()
    {
        Assert.True(CommunityListfile.LoopbackOrigin(new Uri("http://127.0.0.1:9/")));
        foreach (var bad in new[] { "https://example.com/", "http://127.0.0.1:9/path", "http://10.0.0.1/", "http://user@localhost/" })
            Assert.False(CommunityListfile.LoopbackOrigin(new Uri(bad)), bad);
        using var directory = new TempDirectory();
        Assert.Throws<ArgumentException>(() => new CommunityListfile(directory.Path, new HttpClient(new GitHub()), new Policy(true), testOrigin: new Uri("https://example.com/")));
        var seen = new List<Uri>();
        var stub = new Handler((request, _) => { lock (seen) seen.Add(request.RequestUri!); throw new HttpRequestException(HttpRequestError.ConnectionError, "nothing listens"); });
        await using var listfile = new CommunityListfile(directory.Path, new HttpClient(stub), new Policy(true), new Clock(), background: false, testOrigin: new Uri("http://127.0.0.1:9/"));
        await listfile.EnsureAsync(false, default);
        Assert.Equal("http://127.0.0.1:9/repos/wowdev/wow-listfile/releases/latest", Assert.Single(seen).AbsoluteUri);
        Assert.Equal("LISTFILE_FETCH_OFFLINE", (await listfile.StatusAsync(default)).Problem!.Code);
    }

    [Fact]
    public async Task An_index_that_cant_be_read_gives_no_names_instead_of_breaking_the_lookup()
    {
        using var directory = new TempDirectory(); var github = new GitHub();
        await using var listfile = Listfile(directory.Path, github);
        await listfile.EnsureAsync(false, default);
        // The open index's file is cut short under it: reads past the end can't fill a block.
        var path = Path.Combine(directory.Path, CommunityListfile.FolderName, "names-" + Tag + ".vlf");
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) stream.SetLength(ListfileIndex.HeaderBytes);
        var lookup = await listfile.LookupAsync([4000000000], default);
        Assert.Empty(lookup.Names);
    }

    [Fact]
    public async Task A_proxy_that_drops_every_connection_costs_one_attempt_a_bounded_number_of_connections()
    {
        // A refusing proxy that closes each connection before answering. Without the budget, .NET's pool opened 16 of them
        // for one request; QA saw that through a real proxy.
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var accepted = 0;
        using var stop = new CancellationTokenSource();
        var dropping = Task.Run(async () =>
        {
            try { while (true) { using var client = await listener.AcceptTcpClientAsync(stop.Token); Interlocked.Increment(ref accepted); client.Client.Close(); } }
            catch (OperationCanceledException) { }
        });
        using var directory = new TempDirectory();
        var budget = new ListfileConnectionBudget();
        using var handler = CommunityListfile.CreateHandler(budget);
        handler.Proxy = new WebProxy($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}"); handler.UseProxy = true;
        await using var listfile = new CommunityListfile(directory.Path, new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, new Policy(true), new Clock(), background: false, connections: budget);
        await listfile.EnsureAsync(false, default);
        Assert.InRange(Volatile.Read(ref accepted), 1, ListfileConnectionBudget.PerAttempt);
        Assert.StartsWith("LISTFILE_FETCH_", (await listfile.StatusAsync(default)).Problem!.Code, StringComparison.Ordinal);
        // The next attempt gets a fresh budget, and stays within it too.
        var before = Volatile.Read(ref accepted);
        await listfile.EnsureAsync(true, default);
        Assert.InRange(Volatile.Read(ref accepted) - before, 1, ListfileConnectionBudget.PerAttempt);
        stop.Cancel(); listener.Stop(); await dropping;
    }

    // ---------- Files and the host ----------

    private sealed class Names(FileNameLookup lookup) : IFileNameSource
    {
        public readonly List<long[]> Asked = [];
        public Task<FileNameLookup> LookupAsync(IReadOnlyList<long> fileDataIds, CancellationToken token) { Asked.Add([.. fileDataIds]); return Task.FromResult(lookup); }
    }

    private sealed class Catalog : IInventoryCatalog
    {
        private static InventoryFile File(string key, string family, string? id, string? name) => new(key, family, id, name, 1, 1, 1, "1", "1", 0, family == "root" ? "2" : null, 0);
        public Task<InventoryFilesPage> ReadInventoryFilesAsync(InventoryFilesRequest request, CancellationToken token) => Task.FromResult(new InventoryFilesPage(
            request.ProductId, request.BuildIdentity, request.SourceRegion, request.Generation,
            [File("root:7", "root", "7", null), File("root:9", "root", "9", null), File("install:3", "install", null, "authored/install.bin")], "3", null, false));
        public Task<InventoryFileDetail> ReadInventoryFileAsync(InventoryFileRequest request, CancellationToken token) => Task.FromResult(new InventoryFileDetail(
            request.ProductId, request.BuildIdentity, request.SourceRegion, request.Generation, request.File, request.File.StartsWith("root:", StringComparison.Ordinal) ? "root" : "install",
            request.File.StartsWith("root:", StringComparison.Ordinal) ? request.File[5..] : null, request.File.StartsWith("root:", StringComparison.Ordinal) ? null : "authored/install.bin", 1, 1, [], null));
        public Task<FileCatalogStatus> ReadFileCatalogAsync(FileCatalogRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<FileCatalogList> ListFileCatalogsAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<FileCatalogStatus> SetFileCatalogAsync(FileCatalogSetRequest request, CancellationToken token) => throw new NotSupportedException();
        public Task<InventoryPage> ReadInventoryAsync(InventoryReadRequest request, CancellationToken token) => throw new NotSupportedException();
    }

    private static readonly string Build = new('d', 64);
    private static readonly CommunityNamesSource Ready = new("ready", Tag, new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Files_rows_get_a_community_name_beside_their_identity_and_install_names_are_never_touched()
    {
        var names = new Names(new(Ready, new Dictionary<long, string> { [7] = "authored/seven.blp", [3] = "never/an/install.name" }));
        var service = new InventoryService(new Catalog(), names);
        var page = await service.ReadFilesAsync(new("wow", Build, "us", "1", new()));
        Assert.Equal([7L, 9L], names.Asked.Single());
        Assert.Same(Ready, page.CommunityNames);
        Assert.Equal(("root:7", "7", (string?)null, "authored/seven.blp"), (page.Entries[0].Key, page.Entries[0].FileDataId, page.Entries[0].Name, page.Entries[0].CommunityName));
        Assert.Null(page.Entries[1].CommunityName);
        Assert.Equal(("authored/install.bin", (string?)null), (page.Entries[2].Name, page.Entries[2].CommunityName));
        var detail = await service.ReadFileAsync(new("wow", Build, "us", "1", "root:7", new()));
        Assert.Equal(("7", (string?)null, "authored/seven.blp"), (detail.FileDataId, detail.Name, detail.CommunityName));
        var install = await service.ReadFileAsync(new("wow", Build, "us", "1", "install:3", new()));
        Assert.Null(install.CommunityName);
        Assert.Equal("authored/install.bin", install.Name);
        // Without a source every page says names are off.
        Assert.Equal("off", (await new InventoryService(new Catalog()).ReadFilesAsync(new("wow", Build, "us", "1", new()))).CommunityNames.State);
    }

    private sealed class Broken : IFileNameSource
    {
        public Task<FileNameLookup> LookupAsync(IReadOnlyList<long> fileDataIds, CancellationToken token) => throw new IOException("authored read failure");
    }

    [Fact]
    public async Task A_name_source_that_fails_to_read_never_breaks_the_Files_page()
    {
        var service = new InventoryService(new Catalog(), new Broken());
        var page = await service.ReadFilesAsync(new("wow", Build, "us", "1", new()));
        Assert.Equal(3, page.Entries.Count);
        Assert.Equal("unreadable", page.CommunityNames.State);
        Assert.All(page.Entries, entry => Assert.Null(entry.CommunityName));
        Assert.Equal("unreadable", (await service.ReadFileAsync(new("wow", Build, "us", "1", "root:7", new()))).CommunityNames.State);
    }

    [Fact]
    public async Task The_host_reports_the_listfile_and_Files_pages_carry_their_name_source()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        var settings = new SettingsService(new JsonSettingsStore(directory.Path), new DataDirectoryMeasurer(directory.Path), new(4, 2, null, false));
        var clock = new Clock();
        await using var listfile = new CommunityListfile(directory.Path, new HttpClient(new GitHub()), settings, clock, indexBlock: 2);
        await listfile.EnsureAsync(false, default);
        var replies = await Run(new ProtocolHost(new DiscoveryService(new Unused(), new Unused()), inventory: new InventoryService(new Catalog(), listfile), settings: settings, listfile: listfile),
            Request("hello", "hello", new { }), Request("status", "settings.listfile.read", new { }), Request("extra", "settings.listfile.read", new { all = true }),
            Request("files", "inventory.files", new { productId = "wow", buildIdentity = Build, sourceRegion = "us", generation = "1" }),
            Request("file", "inventory.file", new { productId = "wow", buildIdentity = Build, sourceRegion = "us", generation = "1", file = "root:7" }));
        Assert.Contains(replies["hello"].GetProperty("result").GetProperty("capabilities").EnumerateArray(), item => item.GetString() == "community-listfile-v1");
        var status = replies["status"].GetProperty("result");
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal("idle", status.GetProperty("activity").GetString());
        var release = status.GetProperty("release");
        Assert.Equal(Tag, release.GetProperty("tag").GetString());
        Assert.Equal(Csv.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), release.GetProperty("byteLength").GetString());
        Assert.Equal("5", release.GetProperty("nameCount").GetString());
        Assert.EndsWith("+00:00", release.GetProperty("fetchedAt").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("problem").ValueKind);
        Assert.Equal("INVALID_REQUEST", replies["extra"].GetProperty("error").GetProperty("code").GetString());
        var files = replies["files"].GetProperty("result");
        Assert.Equal("Authored/Seven.blp", files.GetProperty("entries")[0].GetProperty("communityName").GetString());
        Assert.Equal(JsonValueKind.Null, files.GetProperty("entries")[0].GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, files.GetProperty("entries")[1].GetProperty("communityName").ValueKind);
        Assert.Equal("ready", files.GetProperty("communityNames").GetProperty("state").GetString());
        Assert.Equal(Tag, files.GetProperty("communityNames").GetProperty("tag").GetString());
        Assert.Equal("Authored/Seven.blp", replies["file"].GetProperty("result").GetProperty("communityName").GetString());
        // Turning the switch off removes the names from the next page at once.
        await settings.SetAsync(SettingsService.CommunityListfile, false);
        var off = (await new InventoryService(new Catalog(), listfile).ReadFilesAsync(new("wow", Build, "us", "1", new())));
        Assert.Equal("off", off.CommunityNames.State);
        Assert.All(off.Entries, entry => Assert.Null(entry.CommunityName));
    }

    [Fact]
    public async Task The_switch_is_on_by_default_reads_as_off_when_unreadable_and_a_change_is_announced()
    {
        using var directory = new TempDirectory(); Directory.CreateDirectory(directory.Path);
        SettingsService Service() => new(new JsonSettingsStore(directory.Path), new DataDirectoryMeasurer(directory.Path), new(4, 2, null, false));
        var service = Service();
        Assert.Equal(new BooleanSetting(true, true, SettingSource.Default, null), (await service.ReadAsync()).CommunityListfile);
        Assert.True(await service.CommunityListfileEnabledAsync(default));
        var changes = new List<(string, bool)>();
        service.Changed += (name, value) => changes.Add((name, value));
        var off = await service.SetAsync(SettingsService.CommunityListfile, false);
        Assert.Equal(new BooleanSetting(false, true, SettingSource.Saved, null), off.CommunityListfile);
        Assert.Equal(SettingSource.Default, off.FetchTableDefinitions.Source);
        Assert.Equal([(SettingsService.CommunityListfile, false)], changes);
        Assert.False(await Service().CommunityListfileEnabledAsync(default));
        foreach (var (content, code) in new[] { ("{\"version\":1,\"values\":{\"communityListfile\":1}}", "SETTING_UNREADABLE"),
            ("{\"version\":2,\"values\":{\"communityListfile\":true}}", "SETTINGS_VERSION"), ("{broken", "SETTINGS_UNREADABLE") })
        {
            await File.WriteAllTextAsync(Path.Combine(directory.Path, JsonSettingsStore.FileName), content);
            var snapshot = (await Service().ReadAsync()).CommunityListfile;
            Assert.Equal((false, true, SettingSource.Unreadable, code), (snapshot.Value, snapshot.DefaultValue, snapshot.Source, snapshot.Problem!.Code));
            Assert.False(await Service().CommunityListfileEnabledAsync(default));
        }
        // A damaged file is replaced with every switch as shown, so this one stays off when another is switched.
        await Service().SetAsync(SettingsService.CommunityBuildHistory, true);
        Assert.Equal(new BooleanSetting(false, true, SettingSource.Saved, null), (await Service().ReadAsync()).CommunityListfile);
    }

    private static object Request(string id, string method, object parameters) => new { protocolVersion = 1, id, method, @params = parameters };
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static async Task<Dictionary<string, JsonElement>> Run(ProtocolHost host, params object[] requests)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient(); await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var server = await listener.AcceptTcpClientAsync();
        var completion = host.RunAsync(server.GetStream(), server.GetStream());
        var stream = client.GetStream();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        foreach (var request in requests) await writer.WriteLineAsync(JsonSerializer.Serialize(request, Json));
        await writer.FlushAsync();
        var replies = new Dictionary<string, JsonElement>();
        while (replies.Count < requests.Length)
        {
            using var document = JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
            var frame = document.RootElement.Clone();
            if (frame.TryGetProperty("id", out var id)) replies[id.GetString()!] = frame;
        }
        client.Client.Shutdown(SocketShutdown.Send);
        await completion.WaitAsync(TimeSpan.FromSeconds(10));
        return replies;
    }

    private sealed class Unused : IDiscoverySource, IDiscoveryCatalog
    {
        public Task<DiscoveryAttempt> FetchAsync(string product, CancellationToken token) => throw new InvalidOperationException();
        public Task<long> BeginRefreshAsync(string product, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryPage> CommitAsync(string product, long generation, DiscoveryAttempt attempt, string completedAt, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryPage> ReadAsync(string product, string filter, int limit, string? cursor, CancellationToken token) => throw new InvalidOperationException();
        public Task<DiscoveryCatalogPage> ReadAllAsync(string? product, string filter, int limit, string? cursor, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
