using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class SelectedContentTests
{
    [Fact]
    public async Task SelectedFileUsesExactRootLocaleAndGenericReceiptWithoutResearchSqlOrUnrelatedDownloads()
    {
        await using var h = await Harness.CreateAsync();
        await using var selected = await h.AcquireAsync();
        Assert.Equal(h.Data.PayloadA.ContentKey, selected.Receipt.ContentKey);
        Assert.Equal(h.Data.PayloadA.EncodingKey, selected.Receipt.EncodingKey);
        Assert.Equal("content-blte-v1", selected.Receipt.VerifierRevision);
        Assert.Null(selected.Receipt.Projection);
        Assert.Equal("loose", selected.Receipt.Location.Kind);
        Assert.Equal("0", selected.Receipt.Location.Offset);
        Assert.Equal("1001", selected.Association.FileDataId);
        Assert.Equal("enUS", selected.Association.ResearchLocale);
        Assert.Equal("2", Assert.Single(selected.Association.Memberships).LocaleFlags);
        Assert.Equal("enus-consistent-root-variants-v1", selected.Association.VariantPolicy);
        Assert.Equal(0, selected.Decoded.Position);
        using var bytes = new MemoryStream(); await selected.Decoded.CopyToAsync(bytes);
        Assert.Equal(h.Data.PayloadA.Decoded, bytes.ToArray());
        Assert.Equal(5, h.Handler.Requests.Count);
        foreach (var role in new[] { "install", "download", "size" })
            Assert.DoesNotContain(InventoryFixture.Url("data", h.Data.Metadata[role].EncodingKey), h.Handler.Requests);
        Assert.Equal(1, await h.ScalarAsync("SELECT count(*) FROM content_objects"));
        Assert.Equal(0, await h.ScalarAsync($"SELECT count(*) FROM metadata_objects WHERE ekey='{h.Data.PayloadA.EncodingKey}'"));
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM inventory_origins"));
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM en_content"));
        Assert.Equal("CONTENT_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.AcquireAsync())).Code);
    }

    [Fact]
    public async Task RestartRevalidatesRetainedBytesOfflineAndPreservesOriginalContentAndMetadataReceipts()
    {
        await using var h = await Harness.CreateAsync();
        ContentReceipt original; SelectedFileAssociation association;
        await using (var selected = await h.AcquireAsync()) { original = selected.Receipt; association = selected.Association; }
        h.Handler.BlockAll = true; await h.RestartAsync();
        await using (var reopened = await h.AcquireAsync())
        {
            Assert.Equal(original.SourceUrl, reopened.Receipt.SourceUrl);
            Assert.Equal(original.RetrievedAt, reopened.Receipt.RetrievedAt);
            Assert.Equal(original.Sha256, reopened.Receipt.Sha256);
            Assert.Equal(association.EncodingSource.RetrievedAt, reopened.Association.EncodingSource.RetrievedAt);
            Assert.Equal(association.RootSource.RetrievedAt, reopened.Association.RootSource.RetrievedAt);
            Assert.Equal(original, await h.Catalog.FindContentAsync(original.EncodingKey, default));
        }
        Assert.Equal(5, h.Handler.Requests.Count); Assert.Empty(h.StagingFiles());
    }

    [Fact]
    public async Task MissingEligibleVariantRetainsVerifiedMetadataAndDoesNotDownloadAnotherLocale()
    {
        await using var h = await Harness.CreateAsync();
        Assert.Equal("CONTENT_VARIANT_MISSING", (await Assert.ThrowsAsync<DiscoveryException>(() => h.AcquireAsync(1002))).Code);
        Assert.Equal(4, h.Handler.Requests.Count);
        Assert.Equal(2, await h.ScalarAsync("SELECT count(*) FROM metadata_objects"));
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM content_objects"));
        await using (var retry = await h.AcquireAsync()) Assert.Equal(h.Data.PayloadA.ContentKey, retry.Receipt.ContentKey);
        Assert.Equal(5, h.Handler.Requests.Count); Assert.Empty(h.StagingFiles());
    }

    [Fact]
    public async Task FreshMetadataAndPayloadFallbackUseOnlyOtherExplicitlyAdvertisedHttpsRoutes()
    {
        await using var h = await Harness.CreateAsync(alternate: true);
        await using var selected = await h.AcquireAsync();
        Assert.StartsWith("https://us.cdn.blizzard.com/", selected.Receipt.SourceUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://us.cdn.blizzard.com/", selected.Association.EncodingSource.SourceUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://us.cdn.blizzard.com/", selected.Association.RootSource.SourceUrl, StringComparison.Ordinal);
        Assert.Contains(h.Handler.Requests, url => url.StartsWith("https://level3.ssl.blizzard.com/tpr/wow/data/", StringComparison.Ordinal));
        Assert.All(h.Handler.Requests, url => Assert.Contains(new Uri(url).Host,
            new[] { "level3.ssl.blizzard.com", "us.cdn.blizzard.com" }));
        Assert.Equal(8, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task AuthenticatedArchiveFallbackRetainsExactSpanAndReopensOffline()
    {
        await using var h = await Harness.CreateAsync(archive: true);
        ContentReceipt receipt;
        await using (var selected = await h.AcquireAsync())
        {
            receipt = selected.Receipt;
            Assert.Equal("archive", receipt.Location.Kind);
            Assert.Equal("19", receipt.Location.Offset);
            Assert.Equal(h.Archive!.Key, receipt.Location.ArchiveKey);
            Assert.Equal(h.Archive.Body.Length.ToString(), receipt.Location.SourceBytes);
            using var bytes = new MemoryStream(); await selected.Decoded.CopyToAsync(bytes);
            Assert.Equal(h.Data.PayloadA.Decoded, bytes.ToArray());
        }
        Assert.Equal(1, await h.ScalarAsync("SELECT count(*) FROM content_locators"));
        Assert.Equal(1, await h.ScalarAsync("SELECT count(*) FROM content_objects"));
        var requests = h.Handler.Requests.Count;
        h.Handler.BlockAll = true; await h.RestartAsync();
        await using (var reopened = await h.AcquireAsync()) Assert.Equal(receipt.SourceUrl, reopened.Receipt.SourceUrl);
        Assert.Equal(requests, h.Handler.Requests.Count); Assert.Empty(h.StagingFiles());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingArchiveIndexesOrChildOnOneMirrorCanUseTheNextAdvertisedMirror(bool missingIndex)
    {
        await using var h = await Harness.CreateAsync(archive: true, alternate: true);
        h.Handler.PrimaryMirrorsData = true;
        h.Handler.PrimaryLooseMissing = true;
        h.Handler.PrimaryIndexMissing = missingIndex;
        h.Handler.PrimaryArchiveMissing = !missingIndex;
        await using var selected = await h.AcquireAsync();
        Assert.Equal("archive", selected.Receipt.Location.Kind);
        Assert.StartsWith("https://us.cdn.blizzard.com/", selected.Receipt.SourceUrl, StringComparison.Ordinal);
        Assert.Contains(h.Handler.Requests, url => url.StartsWith("https://level3.ssl.blizzard.com/", StringComparison.Ordinal) &&
            url.EndsWith(".index", StringComparison.Ordinal));
        if (!missingIndex)
            Assert.DoesNotContain(h.Handler.Requests, url => url.StartsWith("https://us.cdn.blizzard.com/", StringComparison.Ordinal) &&
                url.EndsWith(".index", StringComparison.Ordinal)); // Successful proof survives the first archive-body404.
        Assert.Equal(1, await h.ScalarAsync("SELECT count(*) FROM content_locators"));
        Assert.Equal(1, await h.ScalarAsync("SELECT count(*) FROM content_objects"));
    }

    [Fact]
    public async Task SavedArchiveProofIsReusableWithoutHttpEvenWhenDecodedContentWasNeverPublished()
    {
        await using var h = await Harness.CreateAsync(archive: true);
        var transport = new OfficialSourceTransport(h.Client);
        var cdn = TactConfigurationParser.Parse(Encoding.UTF8.GetBytes($"archives = {h.Archive!.Key}\narchives-index-size = 1\n"));
        var sha = new string('a', 64);
        var first = await new ArchivedContentLocator(h.Catalog, h.Objects, transport).LocateAsync(
            new("https://us.cdn.blizzard.com/tpr/wow/"), cdn, sha, h.Data.PayloadA.EncodingKey,
            h.Data.PayloadA.Encoded.Length, _ => Task.CompletedTask, default);
        Assert.Equal(19, first.Offset); Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM content_objects"));
        var requests = h.Handler.Requests.Count; h.Handler.BlockAll = true;
        var second = await new ArchivedContentLocator(h.Catalog, h.Objects, transport).LocateAsync(
            new("https://us.cdn.blizzard.com/tpr/wow/"), cdn, sha, h.Data.PayloadA.EncodingKey,
            h.Data.PayloadA.Encoded.Length, _ => Task.CompletedTask, default);
        Assert.Equal(first, second); Assert.Equal(requests, h.Handler.Requests.Count); Assert.Empty(h.StagingFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationNeverPublishesUnverifiedSelectedContent(bool cancel)
    {
        await using var h = await Harness.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Acquirer.AcquireAsync(h.Request, 1001, "enUS",
                stage => { if (stage == "Verifying and decoding selected file") cancellation.Cancel(); return Task.CompletedTask; }, cancellation.Token));
        else
        {
            h.Handler.CorruptPayload = true;
            Assert.Equal("BLTE_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.AcquireAsync())).Code);
        }
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM content_objects"));
        Assert.Empty(h.StagingFiles());
        h.Handler.CorruptPayload = false;
        await using (var retry = await h.AcquireAsync()) Assert.Equal(h.Data.PayloadA.ContentKey, retry.Receipt.ContentKey);
        Assert.Equal(6, h.Handler.Requests.Count); Assert.Empty(h.StagingFiles());
    }

    [Fact]
    public async Task CorruptRetainedContentCannotBeCreditedFromItsPriorReceipt()
    {
        await using var h = await Harness.CreateAsync();
        ContentReceipt receipt;
        await using (var selected = await h.AcquireAsync()) receipt = selected.Receipt;
        var path = h.ObjectPath(receipt.Sha256); var bytes = await File.ReadAllBytesAsync(path); bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(path, bytes); h.Handler.BlockAll = true;
        Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() => h.AcquireAsync())).Code);
        Assert.Equal(5, h.Handler.Requests.Count); Assert.Empty(h.StagingFiles());
    }

    [Fact]
    public async Task InvalidLocaleAndZeroFileDataIdCannotIssueAcquisitionRequests()
    {
        await using var h = await Harness.CreateAsync();
        Assert.Equal("CONTENT_LOCALE_UNSUPPORTED", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Acquirer.AcquireAsync(h.Request, 1001, "enGB", _ => Task.CompletedTask, default))).Code);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => h.AcquireAsync(0))).Code);
        Assert.Empty(h.Handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EncryptedChunksPublishFullEncodedProofAndOnlyExposeAuthenticatedReadableRanges(bool archive)
    {
        await using var h = await Harness.CreateAsync(archive: archive, partial: true);
        await using var selected = await h.AcquireAsync();
        var projection = Assert.IsType<ContentProjection>(selected.Receipt.Projection);
        Assert.Equal("content-blte-projection-v1", selected.Receipt.VerifierRevision);
        Assert.Equal("blte-clear-chunks-v1", projection.Policy);
        Assert.Equal(ContentProjectionIdentity.Compute(selected.Receipt.ContentKey, selected.Receipt.EncodingKey,
            selected.Receipt.DecodedBytes, projection), projection.IdentitySha256);
        var readable = Assert.Single(projection.ReadableRanges);
        var unavailable = Assert.Single(projection.UnavailableRanges);
        var readableLength = h.Payload.Decoded.Length - 3;
        Assert.Equal("0", readable.Offset); Assert.Equal(readableLength.ToString(), readable.ByteLength);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(h.Payload.Decoded.AsSpan(0, readableLength))), readable.Sha256);
        Assert.Equal(readableLength.ToString(), unavailable.Offset); Assert.Equal("3", unavailable.ByteLength);
        Assert.Equal(1, unavailable.ChunkOrdinal); Assert.Equal("0102030405060708", unavailable.KeyName);
        Assert.True(selected.Decoded.CanSeek); Assert.False(selected.Decoded.CanWrite);
        Assert.Equal(h.Payload.Decoded.Length, selected.Decoded.Length);
        var bytes = new byte[readableLength]; await selected.Decoded.ReadExactlyAsync(bytes);
        Assert.Equal(h.Payload.Decoded.AsSpan(0, readableLength).ToArray(), bytes);
        Assert.Equal("BLTE_RANGE_UNAVAILABLE", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            selected.Decoded.ReadAsync(new byte[1]).AsTask())).Code);
        selected.Decoded.Position = readableLength - 1;
        Assert.Equal("BLTE_RANGE_UNAVAILABLE", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            selected.Decoded.ReadAsync(new byte[2]).AsTask())).Code);
        Assert.Equal(readableLength - 1, selected.Decoded.Position);
        await using var encoded = await h.Objects.OpenVerifiedAsync(selected.Receipt.Sha256, h.Payload.Encoded.Length, default);
        using var stored = new MemoryStream(); await encoded.CopyToAsync(stored);
        Assert.Equal(h.Payload.Encoded, stored.ToArray());
        Assert.Equal(1, await h.ScalarAsync("SELECT count(*) FROM content_objects"));
        Assert.Equal(0, await h.ScalarAsync($"SELECT count(*) FROM metadata_objects WHERE ekey='{h.Payload.EncodingKey}'"));
    }

    [Fact]
    public async Task PartialReceiptReplaysEncodedProofOfflineAndPreservesOriginalSourceEvidence()
    {
        await using var h = await Harness.CreateAsync(partial: true);
        ContentReceipt original;
        await using (var selected = await h.AcquireAsync()) original = selected.Receipt;
        h.Handler.BlockAll = true; await h.RestartAsync();
        await using (var reopened = await h.AcquireAsync())
        {
            Assert.Equal(original.SourceUrl, reopened.Receipt.SourceUrl);
            Assert.Equal(original.RetrievedAt, reopened.Receipt.RetrievedAt);
            Assert.Equal(original.Sha256, reopened.Receipt.Sha256);
            Assert.Equal(original.Projection!.IdentitySha256, reopened.Receipt.Projection!.IdentitySha256);
            var hole = reopened.Receipt.Projection.UnavailableRanges[0];
            reopened.Decoded.Position = long.Parse(hole.Offset);
            Assert.Equal("BLTE_RANGE_UNAVAILABLE", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                reopened.Decoded.ReadAsync(new byte[1]).AsTask())).Code);
        }
        Assert.Equal(5, h.Handler.Requests.Count); Assert.Empty(h.StagingFiles());
    }

    [Fact]
    public async Task ACorruptLateEncryptedChunkCannotPublishAPartialReceiptForAnEarlierReadablePrefix()
    {
        await using var h = await Harness.CreateAsync(partial: true);
        h.Handler.CorruptPayload = true;
        Assert.Equal("BLTE_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.AcquireAsync())).Code);
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM content_objects"));
        Assert.Empty(h.StagingFiles());
        h.Handler.CorruptPayload = false;
        await using (var retry = await h.AcquireAsync()) Assert.NotNull(retry.Receipt.Projection);
        Assert.Equal(6, h.Handler.Requests.Count); Assert.Empty(h.StagingFiles());
    }

    [Fact]
    public async Task CancellationAtProjectionFallbackDiscardsStrictDecoderPrefixAndAllowsRetry()
    {
        await using var h = await Harness.CreateAsync(partial: true);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Acquirer.AcquireAsync(h.Request, 1001, "enUS",
            stage =>
            {
                if (stage == "Verifying available chunks and encrypted ranges") cancel.Cancel();
                return Task.CompletedTask;
            }, cancel.Token));
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM content_objects"));
        Assert.Empty(h.StagingFiles());
        await using (var retry = await h.AcquireAsync()) Assert.NotNull(retry.Receipt.Projection);
        Assert.Equal(6, h.Handler.Requests.Count); Assert.Empty(h.StagingFiles());
    }

    [Fact]
    public async Task SelfConsistentForgedProjectionCannotReplaceActualRetainedChunkHashes()
    {
        await using var h = await Harness.CreateAsync(partial: true);
        ContentReceipt original;
        await using (var selected = await h.AcquireAsync()) original = selected.Receipt;
        var projection = original.Projection! with
        { ReadableRanges = [original.Projection!.ReadableRanges[0] with { Sha256 = new string('a', 64) }] };
        projection = projection with { IdentitySha256 = ContentProjectionIdentity.Compute(original.ContentKey,
            original.EncodingKey, original.DecodedBytes, projection) };
        await h.ReplaceReceiptAsync(original with { Projection = projection });
        h.Handler.BlockAll = true;
        Assert.Equal("CONTENT_PROJECTION_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.AcquireAsync())).Code);
        Assert.Equal(5, h.Handler.Requests.Count); Assert.Empty(h.StagingFiles());
    }

    private static InventoryFixture.FixtureObject PartialPayload(InventoryFixture.FixtureObject original)
    {
        var clear = new byte[] { (byte)'N' }.Concat(original.Decoded[..^3]).ToArray();
        byte[] encrypted = [(byte)'E', 8, 8, 7, 6, 5, 4, 3, 2, 1, 4, 0, 0, 0, 0, (byte)'S', 0xa1, 0xb2, 0xc3];
        var header = new byte[60]; "BLTE"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)header.Length);
        header[8] = 15; header[11] = 2;
        foreach (var (chunk, decoded, offset) in new[] { (clear, original.Decoded.Length - 3, 12), (encrypted, 3, 36) })
        {
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(offset), (uint)chunk.Length);
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(offset + 4), (uint)decoded);
            MD5.HashData(chunk).CopyTo(header, offset + 8);
        }
        return original with { EncodingKey = Convert.ToHexStringLower(MD5.HashData(header)), Encoded = [.. header, .. clear, .. encrypted] };
    }

    private static InventoryFixture.FixtureObject EncodingMetadata(IEnumerable<InventoryFixture.FixtureObject> objects)
    {
        var rows = objects.ToArray(); var contents = rows.OrderBy(row => row.ContentKey, StringComparer.Ordinal).ToArray();
        var encodings = rows.OrderBy(row => row.EncodingKey, StringComparer.Ordinal).ToArray();
        var contentPage = new byte[1024]; var encodingPage = new byte[1024]; var at = 0;
        foreach (var row in contents)
        {
            contentPage[at] = 1; Put40(contentPage.AsSpan(at + 1), row.Decoded.Length);
            Convert.FromHexString(row.ContentKey).CopyTo(contentPage, at + 6);
            Convert.FromHexString(row.EncodingKey).CopyTo(contentPage, at + 22); at += 38;
        }
        at = 0;
        foreach (var row in encodings)
        { Convert.FromHexString(row.EncodingKey).CopyTo(encodingPage, at); Put40(encodingPage.AsSpan(at + 20), row.Encoded.Length); at += 25; }
        var header = new byte[23]; "EN"u8.CopyTo(header); header[2] = 1; header[3] = header[4] = 16;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(5), 1); BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(7), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(9), 1); BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(13), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(18), 1);
        byte[] decoded = [.. header, .. Convert.FromHexString(contents[0].ContentKey), .. MD5.HashData(contentPage), .. contentPage,
            .. Convert.FromHexString(encodings[0].EncodingKey), .. MD5.HashData(encodingPage), .. encodingPage];
        byte[] encoded = [.. "BLTE"u8.ToArray(), 0, 0, 0, 0, (byte)'N', .. decoded];
        return new("encoding", Convert.ToHexStringLower(MD5.HashData(decoded)), Convert.ToHexStringLower(MD5.HashData(encoded)), decoded, encoded);
    }
    private static void Put40(Span<byte> target, int value)
    { target[0] = 0; BinaryPrimitives.WriteUInt32BigEndian(target[1..], (uint)value); }

    private sealed record AuthoredArchive(string Key, byte[] Index, byte[] Body)
    {
        public static AuthoredArchive Create(InventoryFixture.FixtureObject payload)
        {
            var page = new byte[4096]; Convert.FromHexString(payload.EncodingKey).CopyTo(page, 0);
            BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(16), (uint)payload.Encoded.Length);
            BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(20), 19);
            var toc = new byte[24]; Convert.FromHexString(payload.EncodingKey).CopyTo(toc, 0);
            MD5.HashData(page).AsSpan(0, 8).CopyTo(toc.AsSpan(16));
            var footer = new byte[28]; MD5.HashData(toc).AsSpan(0, 8).CopyTo(footer);
            new byte[] { 1, 0, 0, 4, 4, 4, 16, 8 }.CopyTo(footer, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(16), 1);
            MD5.HashData(footer.AsSpan(8, 20)).AsSpan(0, 8).CopyTo(footer.AsSpan(20));
            var body = new byte[19 + payload.Encoded.Length + 7]; payload.Encoded.CopyTo(body, 19);
            return new(Convert.ToHexStringLower(MD5.HashData(footer)), [.. page, .. toc, .. footer], body);
        }
    }
    private sealed class Handler(InventoryFixture data, InventoryFixture.FixtureObject payload, AuthoredArchive? archive, bool alternate) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public bool BlockAll, CorruptPayload, PrimaryMirrorsData, PrimaryLooseMissing, PrimaryIndexMissing, PrimaryArchiveMissing;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); if (BlockAll) throw new InvalidOperationException("Authored offline test forbids HTTP.");
            var url = request.RequestUri!.AbsoluteUri; lock (Requests) Requests.Add(url);
            var canonical = url.Replace("level3.ssl.blizzard.com", "us.cdn.blizzard.com", StringComparison.Ordinal);
            if (alternate && request.RequestUri.Host == "level3.ssl.blizzard.com" && url.Contains("/data/", StringComparison.Ordinal))
            {
                if (!PrimaryMirrorsData) return Task.FromResult(Response(request, HttpStatusCode.Forbidden, []));
                if (PrimaryLooseMissing && canonical == InventoryFixture.Url("data", payload.EncodingKey) ||
                    archive is not null && PrimaryIndexMissing && canonical == InventoryFixture.Url("data", archive.Key) + ".index" ||
                    archive is not null && PrimaryArchiveMissing && canonical == InventoryFixture.Url("data", archive.Key))
                    return Task.FromResult(Response(request, HttpStatusCode.NotFound, []));
            }
            if (canonical == InventoryFixture.Url("data", payload.EncodingKey))
            {
                if (archive is not null) return Task.FromResult(Response(request, HttpStatusCode.NotFound, []));
                var body = payload.Encoded.ToArray(); if (CorruptPayload) body[^1] ^= 1;
                return Task.FromResult(Response(request, HttpStatusCode.OK, body));
            }
            byte[] bytes;
            if (archive is not null && canonical == InventoryFixture.Url("data", archive.Key) + ".index") bytes = archive.Index;
            else if (archive is not null && canonical == InventoryFixture.Url("data", archive.Key)) bytes = archive.Body;
            else if (!data.Responses.TryGetValue(canonical, out bytes!)) throw new InvalidOperationException("Unexpected authored request.");
            if (request.Headers.Range is { } range)
            {
                var part = Assert.Single(range.Ranges);
                var from = part.From ?? bytes.Length - part.To!.Value;
                var to = part.From is null ? bytes.Length - 1 : part.To ?? bytes.Length - 1;
                var response = Response(request, HttpStatusCode.PartialContent, bytes.AsSpan((int)from, (int)(to - from + 1)).ToArray());
                response.Content.Headers.ContentRange = new(from, to, bytes.Length); return Task.FromResult(response);
            }
            return Task.FromResult(Response(request, HttpStatusCode.OK, bytes));
        }
        private static HttpResponseMessage Response(HttpRequestMessage request, HttpStatusCode status, byte[] body) =>
            new(status) { RequestMessage = request, Content = new ByteArrayContent(body) };
    }
    private sealed class Harness : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "vibeminer-selected-" + Guid.NewGuid().ToString("N"));
        public InventoryFixture Data { get; private set; } = null!;
        public InventoryFixture.FixtureObject Payload { get; private set; } = null!;
        public AuthoredArchive? Archive { get; private set; }
        public DiscoveryCatalog Catalog { get; private set; } = null!;
        public VerifiedObjectStore Objects { get; private set; } = null!;
        public Handler Handler { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public SelectedFileAcquirer Acquirer { get; private set; } = null!;
        public WorkspaceRequest Request { get; private set; } = null!;
        public static async Task<Harness> CreateAsync(bool archive = false, bool alternate = false, bool partial = false)
        {
            var data = InventoryFixture.Create(); var payload = partial ? PartialPayload(data.PayloadA) : data.PayloadA;
            var buildBytes = data.BuildConfig;
            if (partial)
            {
                var originalEncoding = data.Metadata["encoding"];
                var encoding = EncodingMetadata(new[] { payload, data.PayloadB }.Concat(data.Metadata.Values.Where(row => row.Role != "encoding")));
                data.Metadata["encoding"] = encoding;
                data.Responses[InventoryFixture.Url("data", encoding.EncodingKey)] = encoding.Encoded;
                buildBytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data.BuildConfig)
                    .Replace(originalEncoding.ContentKey, encoding.ContentKey, StringComparison.Ordinal)
                    .Replace(originalEncoding.EncodingKey, encoding.EncodingKey, StringComparison.Ordinal)
                    .Replace($"encoding-size = {originalEncoding.Decoded.Length} {originalEncoding.Encoded.Length}",
                        $"encoding-size = {encoding.Decoded.Length} {encoding.Encoded.Length}", StringComparison.Ordinal));
            }
            var buildKey = Convert.ToHexStringLower(MD5.HashData(buildBytes));
            data.Responses[InventoryFixture.Url("config", buildKey)] = buildBytes;
            var authored = archive ? AuthoredArchive.Create(payload) : null;
            var cdnBytes = authored is null ? data.CdnConfig : Encoding.UTF8.GetBytes($"archives = {authored.Key}\narchives-index-size = 1\n");
            var cdnKey = Convert.ToHexStringLower(MD5.HashData(cdnBytes));
            data.Responses[InventoryFixture.Url("config", cdnKey)] = cdnBytes;
            var original = data.Discovery; var version = original.Documents.Single(document => document.Role == "versions");
            var versions = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(version.Bytes).Replace(data.CdnKey, cdnKey, StringComparison.Ordinal)
                .Replace(data.BuildKey, buildKey, StringComparison.Ordinal));
            var fields = MetadataParser.Parse(versions).Rows.Single().Fields;
            var cdns = original.Documents.Single(document => document.Role == "cdns");
            var cdnsBytes = alternate ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(cdns.Bytes).Replace(
                "https://us.cdn.blizzard.com/", "https://level3.ssl.blizzard.com/ https://us.cdn.blizzard.com/", StringComparison.Ordinal)) : cdns.Bytes;
            var route = MetadataParser.Parse(cdnsBytes).Rows.Single().Fields;
            var identity = BuildIdentity.Create("wow", fields);
            var discovery = original with
            {
                Documents = [version with { Bytes = versions, Sha256 = Convert.ToHexStringLower(SHA256.HashData(versions)) },
                    cdns with { Bytes = cdnsBytes, Sha256 = Convert.ToHexStringLower(SHA256.HashData(cdnsBytes)) }],
                Observations = [original.Observations.Single() with { Identity = identity, OriginalFields = fields, RouteFields = route }]
            };
            var h = new Harness { Data = data, Payload = payload, Archive = authored, Request = new("wow", identity.Id, "us") };
            h.Catalog = await DiscoveryCatalog.OpenAsync(h.Directory); h.Objects = new(h.Directory);
            h.Handler = new(data, payload, authored, alternate); h.Client = new(h.Handler); h.Acquirer = new(h.Catalog, h.Objects, h.Client);
            var generation = await h.Catalog.BeginRefreshAsync("wow", default);
            await h.Catalog.CommitAsync("wow", generation, discovery, DateTimeOffset.UtcNow.ToString("O"), default);
            return h;
        }
        public Task<SelectedContentLease> AcquireAsync(uint id = 1001) => Acquirer.AcquireAsync(Request, id, "enUS", _ => Task.CompletedTask, default);
        public async Task RestartAsync()
        {
            Acquirer.Dispose(); await Objects.DisposeAsync(); await Catalog.DisposeAsync();
            Catalog = await DiscoveryCatalog.OpenAsync(Directory); Objects = new(Directory); Acquirer = new(Catalog, Objects, Client);
        }
        public string ObjectPath(string sha) => Path.Combine(Directory, "objects-v1", sha[..2], sha[2..]);
        public IEnumerable<string> StagingFiles() => System.IO.Directory.EnumerateFiles(Path.Combine(Directory, "staging-v1"));
        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(Directory, "discovery-v1.sqlite3"), Pooling = false }.ToString());
            await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }
        public async Task ReplaceReceiptAsync(ContentReceipt receipt)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(Directory, "discovery-v1.sqlite3"), Pooling = false }.ToString());
            await connection.OpenAsync(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE content_objects SET receipt=$receipt WHERE ekey=$key";
            command.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            command.Parameters.AddWithValue("$key", receipt.EncodingKey); Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        public async ValueTask DisposeAsync()
        { Acquirer.Dispose(); Client.Dispose(); await Objects.DisposeAsync(); await Catalog.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }
}
