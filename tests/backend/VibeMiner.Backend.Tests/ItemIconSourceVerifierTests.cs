using System.Security.Cryptography;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ItemIconSourceVerifierTests
{
    [Fact]
    public async Task Full_receipt_requires_actual_strict_BLTE_CKey_and_preserves_original_physical_provenance()
    {
        await using var h = new Harness(); var decoded = Enumerable.Range(0, 148).Select(value => (byte)value).ToArray();
        var (expected, physical) = await h.Publish(decoded);
        var source = await ItemIconSourceVerifier.VerifyAsync(h.Objects, new(), expected, physical, default);
        Assert.Null(source.Projection); Assert.Equal("content-blte-v1", source.VerifierRevision);
        Assert.Equal(expected.ContentKey, source.ContentKey); Assert.Equal(physical.Sha256, source.Sha256);
        Assert.Equal(physical.SourceUrl, source.SourceUrl); Assert.Equal(physical.RetrievedAt, source.RetrievedAt); Assert.Equal(physical.Location, source.Location);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(h.Path, "staging-v1")));
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => ItemIconSourceVerifier.VerifyAsync(h.Objects, new(),
            expected with { ContentKey = new('f', 32) }, physical, default));
        Assert.Equal("BLTE_INTEGRITY", error.Code); Assert.Empty(Directory.EnumerateFiles(Path.Combine(h.Path, "staging-v1")));
    }
    [Fact]
    public async Task Encoded_alias_substitution_and_resource_bounds_fail_before_staging()
    {
        await using var h = new Harness(); var (expected, physical) = await h.Publish(new byte[148]);
        Assert.Equal("ITEM_ICON_SOURCE_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => ItemIconSourceVerifier.VerifyAsync(
            h.Objects, new(), expected, physical with { EncodingKey = new('f', 32) }, default))).Code);
        Assert.Equal("ASSET_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => ItemIconSourceVerifier.VerifyAsync(
            h.Objects, new(), expected with { DecodedBytes = IconArtifacts.MaxSourceBytes + 1 }, physical, default))).Code);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(h.Path, "staging-v1")));
    }
    [Fact]
    public async Task Protected_payload_never_becomes_a_full_receipt_and_cancellation_cleans_staging()
    {
        await using var h = new Harness(); byte[] encrypted = [.. "BLTE"u8, 0, 0, 0, 0, (byte)'E', 8, 1, 2, 3, 4, 5, 6, 7, 8, 4, 0, 0, 0, 0, (byte)'S', 99];
        var (expected, physical) = await h.Publish(new byte[148], encrypted);
        Assert.Equal("BLTE_ENCRYPTED", (await Assert.ThrowsAsync<DiscoveryException>(() => ItemIconSourceVerifier.VerifyAsync(h.Objects, new(), expected, physical, default))).Code);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(h.Path, "staging-v1")));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ItemIconSourceVerifier.VerifyAsync(h.Objects, new(), expected, physical, canceled.Token));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(h.Path, "staging-v1")));
    }
    [Theory]
    [InlineData("BLTE_ENCRYPTED", ItemIconStatus.Protected)]
    [InlineData("BLP2_UNSUPPORTED", ItemIconStatus.Unsupported)]
    [InlineData("BLP2_HEADER", ItemIconStatus.Corrupt)]
    [InlineData("LOCAL_OBJECT_CORRUPT", ItemIconStatus.Corrupt)]
    [InlineData("ITEM_ICON_ASSOCIATION_INVALID", ItemIconStatus.Corrupt)]
    [InlineData("LOCAL_OBJECT_MISSING", ItemIconStatus.Unavailable)]
    [InlineData("CONTENT_UNAVAILABLE", ItemIconStatus.Unavailable)]
    public void Optional_failure_categories_remain_distinct(string code, ItemIconStatus status) => Assert.Equal(status, ItemIconsStore.Classify(code));
    [Fact]
    public void Recovered_source_associations_share_the_same_bounded_batch_allowance_without_variant_truncation()
    {
        var fixture = new VibeMiner.Formats.Wow.RootTargetMatch(0, 0, new(1, new('a', 32), 0, 0, new(0, 0, 0), null));
        var memberships = Enumerable.Repeat(fixture, 4096).ToArray();
        var association = new AssetSourceAssociation(new("wow", new('a', 64), "us"), "enUS", new("1", "us", new('b', 64), 0, new('c', 64), 0),
            [], null!, null!, AssetResolutionSession.VariantPolicy, memberships);
        var result = new AssetResolutionResult(1, AssetResolutionStatus.Ready, null, association);
        var budget = new ItemIconBatchSources(); var admitted = 0;
        for (var i = 0; i < 50; i++) if (budget.TryAdmit(result)) admitted++;
        Assert.InRange(admitted, 1, 8); Assert.Equal(4096, result.Association.Memberships.Count);
        Assert.False(budget.TryAdmit(result)); Assert.True(new ItemIconBatchSources().TryAdmit(result));
    }
    [Theory]
    [InlineData("equivalent", true)][InlineData("offset", false)][InlineData("length", false)]
    [InlineData("archive-key", false)][InlineData("archive", false)][InlineData("archive-url", false)]
    [InlineData("pinned-url", false)][InlineData("unofficial-url", false)][InlineData("date", false)][InlineData("sha", false)]
    [InlineData("role", false)][InlineData("root-v1", true)][InlineData("download-v1", true)]
    public void Legacy_metadata_location_compatibility_is_exact_and_preserves_source_receipts(string kind, bool expected)
    {
        var key = new string('a', 32); var other = new string('b', 32);
        var original = new MetadataReceipt("encoding", other, key, new('c', 64), "100", "200",
            $"https://us.cdn.blizzard.com/tpr/wow/data/aa/aa/{key}", "2026-10-19T00:00:00Z", "2026-10-19T00:00:00Z", MetadataAcquirer.SourceRevision);
        var current = original with { Location = new("loose", "0", "100", null), VerifiedAt = "2026-10-20T00:00:00Z" };
        switch (kind)
        {
            case "offset": current = current with { Location = current.Location with { Offset = "1" } }; break;
            case "length": current = current with { Location = current.Location with { SourceBytes = "101" } }; break;
            case "archive-key": current = current with { Location = current.Location with { ArchiveKey = other } }; break;
            case "archive": current = current with { Location = new("archive", "0", "100", other) }; break;
            case "archive-url": original = original with { SourceUrl = $"https://us.cdn.blizzard.com/tpr/wow/data/bb/bb/{other}" }; current = current with { SourceUrl = original.SourceUrl }; break;
            case "pinned-url": current = current with { SourceUrl = $"https://eu.cdn.blizzard.com/tpr/wow/data/aa/aa/{key}" }; break;
            case "unofficial-url": original = original with { SourceUrl = $"https://example.com/tpr/wow/data/aa/aa/{key}" }; current = current with { SourceUrl = original.SourceUrl }; break;
            case "date": current = current with { RetrievedAt = "2026-10-20T00:00:00Z" }; break;
            case "sha": current = current with { Sha256 = new('d', 64) }; break;
            case "role": current = current with { Role = "root" }; break;
            // D082: the writer's parser revision labels a copy; it is not the copy's identity.
            case "root-v1": current = current with { ParserRevision = "root-v1" }; break;
            case "download-v1": current = current with { ParserRevision = "download-v1" }; break;
        }
        Assert.Equal(expected, MetadataReceiptIdentity.Same(original, current));
        Assert.Equal(expected, MetadataReceiptIdentity.Same(current, original));
        Assert.Null(original.Location); Assert.NotNull(current.Location);
    }
    private sealed class Harness : IAsyncDisposable
    {
        internal string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vm-icon-source-" + Guid.NewGuid().ToString("N"));
        internal VerifiedObjectStore Objects;
        internal Harness() => Objects = new(Path);
        internal async Task<(MetadataExpectation, EncodedReceipt)> Publish(byte[] decoded, byte[]? encoded = null)
        {
            encoded ??= [.. "BLTE"u8, 0, 0, 0, 0, (byte)'N', .. decoded];
            string path; await using (var stage = await Objects.CreateStagingFileAsync(default)) { path = stage.Name; await stage.WriteAsync(encoded); }
            var sha = await Objects.PublishFileAsync(path, encoded.Length, default);
            var ekey = Convert.ToHexStringLower(MD5.HashData(encoded)); var ckey = Convert.ToHexStringLower(MD5.HashData(decoded));
            var receipt = new EncodedReceipt(ekey, sha, encoded.Length, "https://us.cdn.blizzard.com/tpr/wow/data/authored", "2026-10-19T00:00:00Z",
                "2026-10-19T00:00:00Z", "blte-encoded-v1", new("loose", "0", encoded.Length.ToString(), null));
            return (new("asset", ckey, ekey, encoded.Length, decoded.Length), receipt);
        }
        public async ValueTask DisposeAsync() { await Objects.DisposeAsync(); Directory.Delete(Path, true); }
    }
}
