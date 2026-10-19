using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class MetadataAcquirerTests
{
    private const string C1 = "11111111111111111111111111111111";
    private const string C2 = "22222222222222222222222222222222";
    private const string C3 = "33333333333333333333333333333333";
    private static readonly Uri Origin = new("https://us.cdn.blizzard.com/tpr/wow/");
    private const string Basic = "root = " + C1 + "\nencoding = " + C2 + " " + C3 + "\nencoding-size = 20 29\ninstall = " + C1 + "\n";

    [Fact]
    public void MapsDirectAndContentOnlyDeclarationsWithoutConfusingDownloadSizeAndSizeManifest()
    {
        var plan = Parse(Basic + "download = " + C2 + " " + C3 + "\ndownload-size = 50 59\nsize = " + C1 + "\nsize-size = 100 109\n");
        Assert.Equal(C1, plan.RootContentKey);
        Assert.Equal(new MetadataExpectation("encoding", C2, C3, 29, 20), plan.Encoding);
        Assert.Null(plan.Install.DirectExpectation);
        Assert.Equal(C1, plan.Install.ContentKey);
        Assert.Equal(new MetadataExpectation("download", C2, C3, 59, 50), plan.Download!.DirectExpectation);
        Assert.Equal(new MetadataDeclaration("size", C1, null, 109, 100), plan.Size);
        Assert.Null(plan.Size!.DirectExpectation);
        Assert.Null(Parse(Basic).Download);
        Assert.Null(Parse(Basic).Size);
    }

    [Fact]
    public void PreservesOpaquePatchVfsAndInformationalPropertiesWithoutAcquisition()
    {
        var configuration = TactConfigurationParser.Parse(Encoding.UTF8.GetBytes(Basic +
            "patch = " + C1 + "\npatch-size = 4\nvfs-root = " + C1 + " " + C2 +
            "\nvfs-999-size = 5 6\nbuild-name = " + C3 + "\nunknown-boolean = true\nunknown-note = hello\n"));
        var plan = MetadataDeclarations.Parse(configuration);
        Assert.Equal(C1, plan.RootContentKey);
        Assert.Equal(C1, configuration.Fields["patch"][0]);
        Assert.Equal(11, configuration.Fields.Count);
    }

    [Theory]
    [InlineData("root = ", "METADATA_UNSUPPORTED")]
    [InlineData("root = 00000000000000000000000000000000", "METADATA_UNSUPPORTED")]
    [InlineData("root-size = 1 2", "METADATA_UNSUPPORTED")]
    [InlineData("download-size = 1 2", "METADATA_INVALID")]
    [InlineData("size = ", "METADATA_INVALID")]
    [InlineData("install = 00000000000000000000000000000000", "METADATA_INVALID")]
    [InlineData("install = 11111111111111111111111111111111 22222222222222222222222222222222", "METADATA_INVALID")]
    [InlineData("encoding-size = 01 20", "METADATA_INVALID")]
    [InlineData("encoding-size = 0 20", "METADATA_INVALID")]
    [InlineData("encoding-size = 9223372036854775808 20", "METADATA_INVALID")]
    [InlineData("encoding-size = 1073741825 20", "METADATA_LIMIT")]
    [InlineData("encoding-size = 20 536870913", "METADATA_LIMIT")]
    [InlineData("future-manifest = maybe", "METADATA_UNSUPPORTED")]
    [InlineData("future = 11111111111111111111111111111111", "METADATA_UNSUPPORTED")]
    [InlineData("future = yes\nfuture-size = 2", "METADATA_UNSUPPORTED")]
    public void RejectsMalformedOrUnclassifiedReferenceLikeDeclarations(string replacement, string code)
    {
        var field = replacement.Split('=', 2)[0].Trim();
        var source = string.Join('\n', Basic.Split('\n').Where(line => !line.StartsWith(field + " =", StringComparison.Ordinal))) + "\n" + replacement;
        Assert.Equal(code, Assert.Throws<DiscoveryException>(() => Parse(source)).Code);
    }

    [Fact]
    public void MissingRequiredRolesCannotBecomeOptionalAndParsingObservesCancellation()
    {
        Assert.Equal("METADATA_MISSING", Assert.Throws<DiscoveryException>(() => Parse(Basic.Replace("install = " + C1, "note = absent"))).Code);
        Assert.Equal("METADATA_MISSING", Assert.Throws<DiscoveryException>(() => Parse(Basic.Replace("encoding = " + C2 + " " + C3, "note = absent").Replace("encoding-size = 20 29", ""))).Code);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MetadataDeclarations.Parse(TactConfigurationParser.Parse(Encoding.UTF8.GetBytes(Basic)), cancel.Token));
    }

    [Fact]
    public async Task DownloadsOnlyExactLooseMetadataVerifiesPublishesAndDisposesDecodedTemporaryFile()
    {
        await using var fixture = new Fixture();
        var acquired = Authored("original metadata body");
        using var client = new HttpClient(new Handler(request => Response(request, acquired.Bytes, "application/binary")));
        var progress = new List<string>();
        var acquirer = new MetadataAcquirer(client, fixture.Store);
        var verified = await acquirer.AcquireAsync(Origin, acquired.Expected, stage => { progress.Add(stage); return Task.CompletedTask; }, default);
        var receipt = verified.Receipt;
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(acquired.Bytes)), receipt.Sha256);
        Assert.Equal(acquired.Expected.ContentKey, receipt.ContentKey);
        Assert.Equal($"{Origin}data/{acquired.Expected.EncodingKey[..2]}/{acquired.Expected.EncodingKey[2..4]}/{acquired.Expected.EncodingKey}", receipt.SourceUrl);
        Assert.Equal(MetadataAcquirer.SourceRevision, receipt.ParserRevision);
        Assert.Equal("Downloading encoding metadata", progress[0]);
        Assert.Equal("Verifying encoding metadata", progress[^1]);
        Assert.Contains(progress, stage => stage.Contains($"of {acquired.Expected.EncodedBytes:N0} bytes · 100%", StringComparison.Ordinal));
        string decodedPath;
        await using (var decoded = verified.OpenDecoded())
        {
            decodedPath = decoded.Name;
            using var copy = new MemoryStream();
            await decoded.CopyToAsync(copy);
            Assert.Equal("original metadata body", Encoding.UTF8.GetString(copy.ToArray()));
        }
        Assert.True(File.Exists(decodedPath));
        await using (var encoded = await fixture.Store.OpenVerifiedAsync(receipt.Sha256, acquired.Bytes.Length, default))
            Assert.Equal(acquired.Bytes.Length, encoded.Length);
        await verified.DisposeAsync();
        await verified.DisposeAsync();
        Assert.False(File.Exists(decodedPath));
        Assert.Throws<ObjectDisposedException>(() => verified.OpenDecoded());
        Assert.Empty(fixture.StagingFiles());
    }

    [Fact]
    public async Task RetainedSourceRevalidatesOfflinePreservesOriginalReceiptAndDoesNotRepairMissingBytes()
    {
        await using var fixture = new Fixture();
        var authored = Authored("retained source");
        var handler = new Handler(request => Response(request, authored.Bytes));
        using var client = new HttpClient(handler);
        var acquirer = new MetadataAcquirer(client, fixture.Store);
        MetadataReceipt receipt;
        await using (var fresh = await acquirer.AcquireAsync(Origin, authored.Expected, _ => Task.CompletedTask, default))
            receipt = fresh.Receipt with { ParserRevision = "encoding-v1" };
        using var offline = new HttpClient(new Handler(_ => throw new InvalidOperationException("No network is permitted.")));
        var offlineAcquirer = new MetadataAcquirer(offline, fixture.Store);
        await using (var retained = await offlineAcquirer.AcquireAsync(new Uri("https://eu.cdn.blizzard.com/tpr/wow/"), authored.Expected,
            _ => Task.CompletedTask, default, receipt))
        {
            Assert.Equal(receipt.SourceUrl, retained.Receipt.SourceUrl);
            Assert.Equal(receipt.RetrievedAt, retained.Receipt.RetrievedAt);
            Assert.Equal(receipt.Sha256, retained.Receipt.Sha256);
        }
        Assert.Equal(1, handler.Requests);
        File.Delete(fixture.ObjectPath(receipt.Sha256));
        await Assert.ThrowsAsync<DiscoveryException>(() => offlineAcquirer.AcquireAsync(Origin, authored.Expected,
            _ => Task.CompletedTask, default, receipt));
        Assert.Empty(fixture.StagingFiles());
    }

    [Theory]
    [InlineData("encoding", 0)]
    [InlineData("encoding", 1)]
    [InlineData("encoding", 2)]
    [InlineData("root", 0)]
    [InlineData("root", 1)]
    [InlineData("root", 2)]
    public async Task SharedRevalidationPreservesRetainedSourceHistoryTogetherAndRefreshesVerification(string role, int locationKind)
    {
        await using var fixture = new Fixture();
        var authored = Authored("authored retained shared metadata");
        var expected = authored.Expected with { Role = role };
        var sha = await Publish(fixture, authored.Bytes);
        const string originalTime = "2026-10-17T12:00:00.0000000+00:00";
        const string physicalTime = "2026-10-19T12:00:00.0000000+00:00";
        var verifiedAt = DateTimeOffset.Parse("2026-10-19T13:00:00.0000000+00:00");
        ContentLocation? originalLocation = locationKind switch
        {
            1 => new("loose", "0", expected.EncodedBytes.ToString(), null),
            2 => new("archive", "17", (expected.EncodedBytes + 49).ToString(), new string('b', 32)),
            _ => null
        };
        var originalKey = originalLocation?.ArchiveKey ?? expected.EncodingKey;
        var originalUrl = $"{Origin}data/{originalKey[..2]}/{originalKey[2..4]}/{originalKey}";
        var retained = new MetadataReceipt(role, expected.ContentKey, expected.EncodingKey, sha,
            expected.EncodedBytes.ToString(), expected.DecodedBytes.ToString(), originalUrl, originalTime, originalTime,
            role + "-v1", originalLocation);
        var physical = new EncodedReceipt(expected.EncodingKey, sha, expected.EncodedBytes,
            $"https://eu.cdn.blizzard.com/tpr/wow/data/{expected.EncodingKey[..2]}/{expected.EncodingKey[2..4]}/{expected.EncodingKey}",
            physicalTime, physicalTime, "blte-encoded-v1", new("loose", "0", expected.EncodedBytes.ToString(), null));
        var shared = new SharedPhysical(physical);
        using var offline = new HttpClient(new Handler(_ => throw new InvalidOperationException("The shared path cannot perform separate HTTP.")));
        var acquirer = new MetadataAcquirer(offline, fixture.Store, new FixedClock(verifiedAt), shared);
        var source = new EncodedSourceContext("wow", new string('a', 64), "us", new string('c', 64));
        await using (var result = await acquirer.AcquireAsync(Origin, expected, _ => Task.CompletedTask, default, retained, source))
        {
            Assert.Equal(retained.SourceUrl, result.Receipt.SourceUrl);
            Assert.Equal(retained.RetrievedAt, result.Receipt.RetrievedAt);
            Assert.Equal(retained.Location, result.Receipt.Location);
            Assert.Equal(retained.Sha256, result.Receipt.Sha256);
            Assert.Equal(verifiedAt.ToString("O"), result.Receipt.VerifiedAt);
            await using var decoded = result.OpenDecoded();
            using var content = new MemoryStream(); await decoded.CopyToAsync(content);
            Assert.Equal("authored retained shared metadata", Encoding.UTF8.GetString(content.ToArray()));
        }
        Assert.Equal(1, shared.Calls);
        Assert.Empty(fixture.StagingFiles());
        // A preserved historical receipt cannot bypass fresh physical integrity checks.
        var corrupt = authored.Bytes.ToArray(); corrupt[^1] ^= 1;
        await File.WriteAllBytesAsync(fixture.ObjectPath(sha), corrupt);
        Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            acquirer.AcquireAsync(Origin, expected, _ => Task.CompletedTask, default, retained, source))).Code);
        Assert.Empty(fixture.StagingFiles());
    }

    [Theory]
    [InlineData("sha")]
    [InlineData("key")]
    [InlineData("length")]
    public async Task SharedPhysicalIdentityConflictCannotBeAssociatedWithTheRetainedSource(string conflict)
    {
        await using var fixture = new Fixture();
        var authored = Authored("exact retained identity");
        var sha = await Publish(fixture, authored.Bytes);
        const string time = "2026-10-19T12:00:00.0000000+00:00";
        var expected = authored.Expected;
        var url = $"{Origin}data/{expected.EncodingKey[..2]}/{expected.EncodingKey[2..4]}/{expected.EncodingKey}";
        var retained = new MetadataReceipt("encoding", expected.ContentKey, expected.EncodingKey, sha,
            expected.EncodedBytes.ToString(), expected.DecodedBytes.ToString(), url, time, time, "encoding-v1");
        var physical = new EncodedReceipt(expected.EncodingKey, sha, expected.EncodedBytes, url, time, time,
            "blte-encoded-v1", new("loose", "0", expected.EncodedBytes.ToString(), null));
        physical = conflict switch
        {
            "sha" => physical with { Sha256 = new string('f', 64) },
            "key" => physical with { EncodingKey = new string('e', 32) },
            _ => physical with { EncodedBytes = expected.EncodedBytes + 1 }
        };
        using var offline = new HttpClient(new Handler(_ => throw new InvalidOperationException("No fallback transfer is permitted.")));
        var acquirer = new MetadataAcquirer(offline, fixture.Store, shared: new SharedPhysical(physical));
        Assert.Equal("METADATA_IDENTITY_CONFLICT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            acquirer.AcquireAsync(Origin, expected, _ => Task.CompletedTask, default, retained,
                new("wow", new string('a', 64), "us", new string('c', 64))))).Code);
        Assert.Empty(fixture.StagingFiles());
    }

    [Theory]
    [InlineData("text/html", "METADATA_CONTENT_TYPE")]
    [InlineData("application/json", "METADATA_CONTENT_TYPE")]
    public async Task RejectsUnsupportedResponseTypeWithoutPublishing(string media, string code)
    {
        await using var fixture = new Fixture();
        var authored = Authored("body");
        using var client = new HttpClient(new Handler(request => Response(request, authored.Bytes, media)));
        await Failure(fixture, client, authored.Expected, code);
    }

    [Fact]
    public async Task RedirectPartialUnavailableAndConflictingLengthsRemainDistinctFailures()
    {
        var authored = Authored("response geometry");
        var cases = new (Func<HttpRequestMessage, HttpResponseMessage> Respond, string Code)[]
        {
            (request => Response(request, authored.Bytes, status: HttpStatusCode.Redirect), "SOURCE_REDIRECT"),
            (request => Response(new HttpRequestMessage(HttpMethod.Get, "https://evil.invalid/body"), authored.Bytes), "SOURCE_REDIRECT"),
            (request => Response(request, authored.Bytes, status: HttpStatusCode.NotFound), "METADATA_UNAVAILABLE"),
            (request => Response(request, authored.Bytes, status: HttpStatusCode.PartialContent), "METADATA_UNAVAILABLE"),
            (request => { var response = Response(request, authored.Bytes); response.Content.Headers.ContentLength = 1; return response; }, "METADATA_INTEGRITY"),
            (request => { var response = Response(request, authored.Bytes); response.Content.Headers.ContentEncoding.Add("gzip"); return response; }, "METADATA_RESPONSE")
        };
        foreach (var (respond, code) in cases)
        {
            await using var fixture = new Fixture();
            using var client = new HttpClient(new Handler(respond));
            await Failure(fixture, client, authored.Expected, code);
        }
    }

    [Fact]
    public async Task StreamingLengthAndBlteIntegrityFailuresRetireBothTemporaryFiles()
    {
        var authored = Authored("bounded streamed source");
        foreach (var bytes in new[] { authored.Bytes[..^1], authored.Bytes.Concat(new byte[] { 0 }).ToArray(),
            authored.Bytes[..^1].Concat(new byte[] { 1 }).ToArray() })
        {
            await using var fixture = new Fixture();
            using var client = new HttpClient(new Handler(request =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StreamContent(new AsyncInput(bytes, 2)) };
                return response;
            }));
            var code = bytes.Length == authored.Bytes.Length ? "BLTE_INTEGRITY" : "METADATA_INTEGRITY";
            await Failure(fixture, client, authored.Expected, code);
        }
    }

    [Fact]
    public async Task InvalidOriginsExpectationsAndRetainedReceiptsCannotIssueRequests()
    {
        await using var fixture = new Fixture();
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("No network permitted.")));
        var acquirer = new MetadataAcquirer(client, fixture.Store);
        var authored = Authored("validation");
        foreach (var uri in new[] { "http://us.cdn.blizzard.com/tpr/wow/", "https://evil.invalid/tpr/wow/",
            "https://us.cdn.blizzard.com/tpr/wow/?x=y", "https://us.cdn.blizzard.com/tpr/wow/data/", "https://u:p@us.cdn.blizzard.com/tpr/wow/" })
            Assert.Equal("SOURCE_UNSUPPORTED", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                acquirer.AcquireAsync(new Uri(uri), authored.Expected, _ => Task.CompletedTask, default))).Code);
        foreach (var expected in new[] { authored.Expected with { Role = "payload" }, authored.Expected with { EncodedBytes = 8 },
            authored.Expected with { ContentKey = new string('0', 32) }, authored.Expected with { DecodedBytes = 0 } })
            Assert.Equal("METADATA_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                acquirer.AcquireAsync(Origin, expected, _ => Task.CompletedTask, default))).Code);
        var invalidReceipt = new MetadataReceipt("encoding", authored.Expected.ContentKey, authored.Expected.EncodingKey,
            new string('a', 64), authored.Expected.EncodedBytes.ToString(), authored.Expected.DecodedBytes.ToString(),
            "https://evil.invalid/body", "2026-10-18T00:00:00Z", "2026-10-18T00:00:00Z", "encoding-v1");
        Assert.Equal("METADATA_RECEIPT_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            acquirer.AcquireAsync(Origin, authored.Expected, _ => Task.CompletedTask, default, invalidReceipt))).Code);
        Assert.Empty(fixture.StagingFiles());
    }

    [Fact]
    public async Task LeaseBoundsConcurrentSourcesAndCancellationAndProgressFailuresReleaseOwnership()
    {
        await using var fixture = new Fixture();
        var authored = Authored("lease");
        using var client = new HttpClient(new Handler(request => Response(request, authored.Bytes)));
        var acquirer = new MetadataAcquirer(client, fixture.Store);
        var failure = new InvalidOperationException("progress failure");
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            acquirer.AcquireAsync(Origin, authored.Expected, _ => throw failure, default)));
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquirer.AcquireAsync(Origin, authored.Expected,
            _ => { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token));
        await using (var active = await acquirer.AcquireAsync(Origin, authored.Expected, _ => Task.CompletedTask, default))
            Assert.Equal("METADATA_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                acquirer.AcquireAsync(Origin, authored.Expected, _ => Task.CompletedTask, default))).Code);
        await using (var next = await acquirer.AcquireAsync(Origin, authored.Expected, _ => Task.CompletedTask, default))
            Assert.Equal(authored.Expected.ContentKey, next.Receipt.ContentKey);
        Assert.Empty(fixture.StagingFiles());
    }

    [Fact]
    public async Task TransportFailureAndCancellationRetireStagingAndLeaveNoPublishedSource()
    {
        var authored = Authored("failure retirement");
        await using var fixture = new Fixture();
        using var brokenClient = new HttpClient(new Handler(request => new(HttpStatusCode.OK)
        {
            RequestMessage = request, Content = new StreamContent(new FailingInput())
        }));
        await Failure(fixture, brokenClient, authored.Expected, "NETWORK_IO");
        using var disconnectedClient = new HttpClient(new Handler(_ => throw new HttpRequestException("authored disconnect")));
        await Failure(fixture, disconnectedClient, authored.Expected, "NETWORK_UNAVAILABLE");
        using var cancel = new CancellationTokenSource();
        using var cancelClient = new HttpClient(new Handler(request => new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StreamContent(new AsyncInput(authored.Bytes, 1) { OnRead = () => cancel.Cancel() })
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new MetadataAcquirer(cancelClient, fixture.Store).AcquireAsync(Origin, authored.Expected,
                _ => Task.CompletedTask, cancel.Token));
        Assert.Empty(fixture.StagingFiles());
    }

    private static async Task<string> Publish(Fixture fixture, byte[] bytes)
    {
        string path;
        await using (var staged = await fixture.Store.CreateStagingFileAsync(default))
        { path = staged.Name; await staged.WriteAsync(bytes); }
        return await fixture.Store.PublishFileAsync(path, bytes.Length, default);
    }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    private sealed class SharedPhysical(EncodedReceipt receipt) : ISharedAcquisition
    {
        internal int Calls;
        public IAcquisitionSubscription Attach(EncodedRequirement requirement, EncodedSourceContext source,
            AcquisitionPriority priority, Func<AcquisitionProgress, Task>? progress = null) => throw new InvalidOperationException("Only direct acquisition is expected.");
        public Task<EncodedReceipt> AcquireAsync(EncodedRequirement requirement, EncodedSourceContext source,
            AcquisitionPriority priority, Func<AcquisitionProgress, Task>? progress = null, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(receipt); }
    }
    private static MetadataDeclarationSet Parse(string text) => MetadataDeclarations.Parse(TactConfigurationParser.Parse(Encoding.UTF8.GetBytes(text)));
    private static async Task Failure(Fixture fixture, HttpClient client, MetadataExpectation expected, string code)
    {
        Assert.Equal(code, (await Assert.ThrowsAsync<DiscoveryException>(() =>
            new MetadataAcquirer(client, fixture.Store).AcquireAsync(Origin, expected, _ => Task.CompletedTask, default))).Code);
        Assert.Empty(fixture.StagingFiles());
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(fixture.Path, "objects-v1"), "*", SearchOption.AllDirectories),
            p => !p.EndsWith(".owner", StringComparison.Ordinal));
    }
    private static (byte[] Bytes, MetadataExpectation Expected) Authored(string text)
    {
        var content = Encoding.UTF8.GetBytes(text);
        byte[] bytes = [.. "BLTE"u8.ToArray(), 0, 0, 0, 0, (byte)'N', .. content];
        return (bytes, new("encoding", Convert.ToHexStringLower(MD5.HashData(content)), Convert.ToHexStringLower(MD5.HashData(bytes)), bytes.Length, content.Length));
    }
    private static HttpResponseMessage Response(HttpRequestMessage request, byte[] bytes,
        string media = "application/octet-stream", HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(media);
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Requests++; Assert.Equal(HttpMethod.Get, request.Method); return Task.FromResult(respond(request)); }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vibeminer-metadata-" + Guid.NewGuid().ToString("N"));
        public VerifiedObjectStore Store { get; }
        public Fixture() { Directory.CreateDirectory(Path); Store = new(Path); }
        public IEnumerable<string> StagingFiles() => Directory.Exists(System.IO.Path.Combine(Path, "staging-v1"))
            ? Directory.EnumerateFiles(System.IO.Path.Combine(Path, "staging-v1")) : [];
        public string ObjectPath(string sha) => System.IO.Path.Combine(Path, "objects-v1", sha[..2], sha[2..]);
        public async ValueTask DisposeAsync() { await Store.DisposeAsync(); Directory.Delete(Path, true); }
    }
    private sealed class AsyncInput(byte[] bytes, int fragment) : Stream
    {
        private int at;
        public Action? OnRead { get; init; }
        public override async ValueTask<int> ReadAsync(Memory<byte> target, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            OnRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(Math.Min(fragment, target.Length), bytes.Length - at);
            bytes.AsMemory(at, count).CopyTo(target);
            at += count;
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Async reads required.");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class FailingInput : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("authored read failure"));
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Async reads required.");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
