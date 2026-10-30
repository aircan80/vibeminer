using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests;

/// <summary>#116: limit words and headlines come from the real readers' refusals, not copied message strings.</summary>
public sealed class CreatureGeometryLimitWordsTests
{
    private const string Key = "00000000000000000000000000000000";
    private const int Mib = 1 << 20;

    // Every BLTE_LIMIT refusal of the three real BLTE readers, and the cap BlteCap must name for it.
    public static TheoryData<string, string> BlteRefusals() => new()
    {
        { "decode object", "object size" }, { "decode header", "chunk header size" }, { "decode count", "chunk count" },
        { "decode totals", "object size" }, { "decode chunk memory", "chunk memory" },
        { "projection object", "object size" }, { "projection header", "chunk header size" }, { "projection count", "chunk count" },
        { "projection chunk size", "chunk size" }, { "projection totals", "object size" }, { "projection ranges", "readable range count" },
        { "verifier object", "object size" }, { "verifier header", "chunk header size" }, { "verifier count", "chunk count" },
        { "verifier totals", "object size" }
    };

    [Theory]
    [MemberData(nameof(BlteRefusals))]
    public async Task Each_real_BLTE_reader_refusal_names_its_cap(string refusal, string cap)
    {
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => Refuse(refusal));
        Assert.Equal("BLTE_LIMIT", error.Code);
        var failure = GeometrySourceClosure.Failure(error, CreatureGeometryDependency.Skin, []);
        Assert.Equal($"The SKIN file is over the BLTE {cap} limit. This is a limit, not a corrupt file.", failure.Problem.Message);
        Assert.Equal(CreatureGeometryState.ResourceLimit, failure.State);
        Assert.Equal(CreatureGeometryHeadline.ModelTooLarge, failure.Headline);
    }

    [Fact]
    public void The_BLTE_refusal_cases_cover_every_BLTE_LIMIT_throw_in_the_readers()
    {
        // A new BLTE_LIMIT refusal needs a case here, so BlteCap is checked against it.
        var root = RepositoryPaths.Root;
        var throws = new[] { "BlteDecoder.cs", "BlteProjectionDecoder.cs", "BlteEncodedVerifier.cs" }
            .Sum(name => File.ReadAllText(Path.Combine(root, "src/backend/VibeMiner.Protocols.Tact", name)).Split("\"BLTE_LIMIT\"").Length - 1);
        Assert.Equal(BlteRefusals().Count, throws);
    }

    [Fact]
    public async Task A_ROOT_per_file_variant_refusal_names_the_file_not_the_ROOT_list()
    {
        // The real batch lookup, one file with one more variant than it keeps.
        var perFile = await Assert.ThrowsAsync<DiscoveryException>(() => RootBatchLookup.ReadAsync(new MemoryStream(Root(
            Enumerable.Repeat(new uint[] { 5 }, RootBatchLookup.MaxMatchesPerTarget + 1))), new uint[] { 5 }, new()));
        var failure = GeometrySourceClosure.Failure(perFile, CreatureGeometryDependency.Model, []);
        Assert.Equal("ROOT_LIMIT", failure.Problem.Code);
        Assert.Equal("The model file has more than 4,096 ROOT variants; geometry reads at most 4,096. This is a limit, not a corrupt file.", failure.Problem.Message);
        Assert.Equal(CreatureGeometryHeadline.ModelTooLarge, failure.Headline);
        // The single-file target lookup has the same cap.
        var target = await Assert.ThrowsAsync<DiscoveryException>(() => RootTargetLookup.ReadAsync(new MemoryStream(Root(
            Enumerable.Repeat(new uint[] { 5 }, RootTargetLookup.MaxMatches + 1))), new uint[] { 5 }, new()));
        Assert.Equal("The SKIN file has more than 4,096 ROOT variants; geometry reads at most 4,096.",
            GeometrySourceClosure.NamedLimit(target.Code, CreatureGeometryDependency.Skin, target.Message));
    }

    [Fact]
    public async Task ROOT_lookup_bounds_shared_with_other_files_blame_neither_the_file_nor_the_ROOT_list()
    {
        var ids = Enumerable.Range(1, 9).Select(i => (uint)i).ToArray();
        var blocks = Enumerable.Repeat(ids[..8], RootBatchLookup.MaxMatches / 8).Append([9]);
        var batch = await Assert.ThrowsAsync<DiscoveryException>(() => RootBatchLookup.ReadAsync(new MemoryStream(Root(blocks)), ids, new()));
        var failure = GeometrySourceClosure.Failure(batch, CreatureGeometryDependency.Model, []);
        Assert.Equal("The ROOT lookup shared with other files found more than 32,768 variants in all, so the model file was not read. This is a limit, not a corrupt file.",
            failure.Problem.Message);
        Assert.Equal(CreatureGeometryHeadline.NotDrawn, failure.Headline);
        var many = await Assert.ThrowsAsync<DiscoveryException>(() => RootBatchLookup.ReadAsync(new MemoryStream(Root([])),
            Enumerable.Range(1, RootBatchLookup.MaxTargets + 1).Select(i => (uint)i).ToArray(), new()));
        Assert.Equal("The ROOT lookup asked for more than 128 files at once, so the model file was not read.",
            GeometrySourceClosure.NamedLimit(many.Code, CreatureGeometryDependency.Model, many.Message));
    }

    [Fact]
    public async Task A_ROOT_list_refusal_still_names_the_build_ROOT_list()
    {
        var list = await Assert.ThrowsAsync<DiscoveryException>(() => RootBatchLookup.ReadAsync(new MemoryStream(Root([[5]])), new uint[] { 5 }, new(MaxRecords: 0)));
        var failure = GeometrySourceClosure.Failure(list, CreatureGeometryDependency.Texture, []);
        Assert.Equal("The build's ROOT file list is over its limit, so the texture file was not read. This is a limit, not a corrupt file.", failure.Problem.Message);
        Assert.Equal(CreatureGeometryHeadline.BuildListTooLarge, failure.Headline);
    }

    // Every code NamedLimit names, with the owner of its cap: the build's list, the file, a full shared queue, or neither.
    [Theory]
    [InlineData("EN_LIMIT", "", "The build's encoding manifest is over its limit, so the model file was not read.", CreatureGeometryHeadline.BuildListTooLarge)]
    [InlineData("RESOLVER_LIMIT", "", "The build's encoding manifest is over its limit, so the model file was not read.", CreatureGeometryHeadline.BuildListTooLarge)]
    [InlineData("IN_LIMIT", "", "The build's install manifest is over its limit, so the model file was not read.", CreatureGeometryHeadline.BuildListTooLarge)]
    [InlineData("ROOT_LIMIT", "ROOT block count exceeds the limit.", "The build's ROOT file list is over its limit, so the model file was not read.", CreatureGeometryHeadline.BuildListTooLarge)]
    [InlineData("ROOT_SCOPE_LIMIT", "", "The build's ROOT file list is over its limit, so the model file was not read.", CreatureGeometryHeadline.BuildListTooLarge)]
    [InlineData("ASSET_SOURCE_LIMIT", "", "The build's ROOT file list is over its limit, so the model file was not read.", CreatureGeometryHeadline.BuildListTooLarge)]
    [InlineData("CONFIG_LIMIT", "", "The build's CDN configuration is over its limit, so the model file was not read.", CreatureGeometryHeadline.BuildListTooLarge)]
    [InlineData("ARCHIVE_INDEX_LIMIT", "", "The build's archive index lookup is over its limit, so the model file was not read.", CreatureGeometryHeadline.BuildListTooLarge)]
    [InlineData("CONTENT_LOCATOR_LIMIT", "", "The build's archive index lookup is over its limit, so the model file was not read.", CreatureGeometryHeadline.BuildListTooLarge)]
    [InlineData("BULK_LOCATOR_LIMIT", "", "The build's archive index lookup is over its limit, so the model file was not read.", CreatureGeometryHeadline.BuildListTooLarge)]
    [InlineData("CONTENT_LIMIT", "An index read exceeds its bounded allocation.", "The build's archive index lookup is over its limit, so the model file was not read.", CreatureGeometryHeadline.BuildListTooLarge)]
    [InlineData("METADATA_LIMIT", "", "The build's metadata is over its limit, so the model file was not read.", CreatureGeometryHeadline.BuildListTooLarge)]
    [InlineData("INVENTORY_LIMIT", "", "The build's file inventory is over its limit, so the model file was not read.", CreatureGeometryHeadline.BuildListTooLarge)]
    [InlineData("FILE_CATALOG_LIMIT", "", "The build's file catalog is over its limit, so the model file was not read.", CreatureGeometryHeadline.BuildListTooLarge)]
    [InlineData("DOWNLOAD_LEDGER_LIMIT", "", "The build's download list is over its limit, so the model file was not read.", CreatureGeometryHeadline.BuildListTooLarge)]
    [InlineData("CONTENT_LIMIT", "A source range exceeds its admitted bounds.", "The model file is over the download size limit.", CreatureGeometryHeadline.ModelTooLarge)]
    [InlineData("CONTENT_VARIANT_LIMIT", "", "The model file has more ROOT variants than one lookup keeps.", CreatureGeometryHeadline.ModelTooLarge)]
    [InlineData("GEOMETRY_ARTIFACT_LIMIT", "", "The model file is over the geometry artifact size limit.", CreatureGeometryHeadline.ModelTooLarge)]
    [InlineData("ACQUISITION_CAPACITY", "", "The engine's download queue is full, so the model file was not read.", CreatureGeometryHeadline.NotDrawn)]
    [InlineData("CONTENT_LOCATOR_CAPACITY", "", "The engine's archive lookup cache is full, so the model file was not read.", CreatureGeometryHeadline.NotDrawn)]
    [InlineData("ENCODED_SOURCE_CAPACITY", "", "The engine's download source pool is full, so the model file was not read.", CreatureGeometryHeadline.NotDrawn)]
    [InlineData("CONTENT_STORE_CAPACITY", "", "The engine's file check queue is full, so the model file was not read.", CreatureGeometryHeadline.NotDrawn)]
    [InlineData("ASSET_CONTEXT_CAPACITY", "", "The engine's build source pool is full, so the model file was not read.", CreatureGeometryHeadline.NotDrawn)]
    [InlineData("ASSET_HANDLE_CAPACITY", "", "The engine's asset handle pool is full, so the model file was not read.", CreatureGeometryHeadline.NotDrawn)]
    [InlineData("ASSET_BATCH_CAPACITY", "", "The engine's asset lookup queue is full, so the model file was not read.", CreatureGeometryHeadline.NotDrawn)]
    [InlineData("RETENTION_CAPACITY", "", "The engine's retention capacity is full, so the model file was not read.", CreatureGeometryHeadline.NotDrawn)]
    // A code the engine has no words for names no owner it can't know.
    [InlineData("ASSET_BATCH_LIMIT", "", "The engine's asset batch limit was reached, so the model file was not read.", CreatureGeometryHeadline.NotDrawn)]
    [InlineData("BLTE_FRAME_LIMIT", "", "The engine's BLTE frame limit was reached, so the model file was not read.", CreatureGeometryHeadline.NotDrawn)]
    public void Every_named_limit_names_the_owner_of_its_cap(string code, string detail, string message, CreatureGeometryHeadline headline)
    {
        // A capacity that is full now is retryable as the engine raises it; the words and headline hold either way.
        foreach (var retryable in new[] { false, true })
        {
            var failure = GeometrySourceClosure.Failure(new DiscoveryException(code, detail, retryable), CreatureGeometryDependency.Model, []);
            Assert.Equal(CreatureGeometryState.ResourceLimit, failure.State);
            Assert.Equal(retryable ? message : message + " This is a limit, not a corrupt file.", failure.Problem.Message);
            Assert.Equal(retryable ? CreatureGeometryHeadline.NotDrawn : headline, failure.Headline);
        }
    }

    [Theory]
    [InlineData("MODEL_LIMIT", false, CreatureGeometryHeadline.ModelTooLarge)]
    [InlineData("CREATURE_GEOMETRY_RESOURCE_LIMIT", false, CreatureGeometryHeadline.ModelTooLarge)]
    [InlineData("CREATURE_GEOMETRY_RESOURCE_LIMIT", true, CreatureGeometryHeadline.NotDrawn)]
    [InlineData("BLTE_ENCRYPTED", false, CreatureGeometryHeadline.Encrypted)]
    [InlineData("BLTE_KEY_UNAVAILABLE", true, CreatureGeometryHeadline.Encrypted)]
    [InlineData("MODEL_UNSUPPORTED", false, CreatureGeometryHeadline.Unsupported)]
    [InlineData("MODEL_INVALID", false, CreatureGeometryHeadline.NotDrawn)]
    [InlineData("NETWORK_UNAVAILABLE", true, CreatureGeometryHeadline.NotDrawn)]
    public void The_engine_picks_each_headline(string code, bool retryable, CreatureGeometryHeadline headline) =>
        Assert.Equal(headline, GeometrySourceClosure.Failure(new DiscoveryException(code, "Reader detail.", retryable), CreatureGeometryDependency.Model, []).Headline);

    private static async Task Refuse(string refusal)
    {
        var decoded = new MemoryStream();
        switch (refusal)
        {
            case "decode object": await BlteDecoder.DecodeAsync(new MemoryStream(), decoded, Key, Key, BlteDecoder.MaxEncodedBytes + 1, 1); break;
            case "decode header": await BlteDecoder.DecodeAsync(Input(Prefix(12 + 24 * BlteDecoder.MaxChunks + 1)), decoded, Key, Key, 1 << 24, 1); break;
            case "decode count": await BlteDecoder.DecodeAsync(Input([.. Prefix(36), .. Table(BlteDecoder.MaxChunks + 1, 1)]), decoded, Key, Key, 1000, 1); break;
            case "decode totals": await BlteDecoder.DecodeAsync(Input([.. Prefix(60), .. Table(2, 2, (1, uint.MaxValue), (1, uint.MaxValue))]), decoded, Key, Key, 1000, 1); break;
            case "decode chunk memory":
                await BlteDecoder.DecodeAsync(Input([.. Prefix(0), (byte)'Z']), decoded, Key, Key, 9L + BlteDecoder.MaxCompressedChunkBytes + 1, 1); break;
            case "projection object": await BlteDecoder.DecodeAvailableAsync(new MemoryStream(), decoded, Key, Key, BlteDecoder.MaxEncodedBytes + 1, 1); break;
            case "projection header": await BlteDecoder.DecodeAvailableAsync(Input(Prefix(12 + 24 * BlteDecoder.MaxProjectionChunks + 1)), decoded, Key, Key, 1 << 24, 1); break;
            case "projection count": await BlteDecoder.DecodeAvailableAsync(Input([.. Prefix(36), .. Table(BlteDecoder.MaxProjectionChunks + 1, 1)]), decoded, Key, Key, 1000, 1); break;
            case "projection chunk size":
                await BlteDecoder.DecodeAvailableAsync(Input([.. Prefix(36), .. Table(1, 1, ((uint)BlteDecoder.MaxCompressedChunkBytes + 1, 1))]), decoded, Key, Key, 1000, 1); break;
            case "projection totals":
                var chunks = Enumerable.Repeat(((uint)(16 * Mib), 0u), 33).ToArray();
                await BlteDecoder.DecodeAvailableAsync(Input([.. Prefix(12 + 24 * 33), .. Table(33, 33, chunks)]), decoded, Key, Key, 1000, 1); break;
            case "projection ranges":
                // One more clear/encrypted range than the projection keeps, each authenticated so the cap is the one that trips.
                var blocks = Enumerable.Range(0, BlteDecoder.MaxProjectionRanges + 1).Select(i => i % 2 == 0 ? "Nx"u8.ToArray() : Encrypted()).ToArray();
                var (bytes, ekey) = Headered(blocks);
                await BlteDecoder.DecodeAvailableAsync(Input(bytes), decoded, ekey, Key, bytes.Length, blocks.Length); break;
            case "verifier object": await BlteEncodedVerifier.VerifyAsync(new MemoryStream(), Key, BlteEncodedVerifier.MaxEncodedBytes + 1); break;
            case "verifier header": await BlteEncodedVerifier.VerifyAsync(Input(Prefix(12 + 24 * BlteEncodedVerifier.MaxChunks + 1)), Key, 1 << 24); break;
            case "verifier count": await BlteEncodedVerifier.VerifyAsync(Input([.. Prefix(36), .. Table(BlteEncodedVerifier.MaxChunks + 1, 1)]), Key, 1000); break;
            case "verifier totals": await BlteEncodedVerifier.VerifyAsync(Input([.. Prefix(36), .. Table(1, 1, (uint.MaxValue, 1))]), Key, 1000); break;
            default: throw new ArgumentOutOfRangeException(nameof(refusal));
        }
    }
    private static MemoryStream Input(byte[] bytes) => new(bytes, false);
    private static byte[] Prefix(long header)
    {
        var bytes = new byte[8]; "BLTE"u8.CopyTo(bytes); BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), (uint)header); return bytes;
    }
    // The chunk table after the prefix: format 15, the declared count, then `written` 24-byte descriptors.
    private static byte[] Table(int declared, int written, params (uint Encoded, uint Decoded)[] chunks)
    {
        var table = new byte[4 + 24 * written]; table[0] = 15; table[1] = (byte)(declared >> 16); table[2] = (byte)(declared >> 8); table[3] = (byte)declared;
        for (var i = 0; i < chunks.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(4 + 24 * i), chunks[i].Encoded);
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8 + 24 * i), chunks[i].Decoded);
        }
        return table;
    }
    private static (byte[] Bytes, string EncodingKey) Headered(byte[][] blocks)
    {
        var header = 12 + 24 * blocks.Length; var table = new byte[4 + 24 * blocks.Length];
        table[0] = 15; table[1] = (byte)(blocks.Length >> 16); table[2] = (byte)(blocks.Length >> 8); table[3] = (byte)blocks.Length;
        for (var i = 0; i < blocks.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(4 + 24 * i), (uint)blocks[i].Length);
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8 + 24 * i), 1);
            MD5.HashData(blocks[i]).CopyTo(table.AsSpan(12 + 24 * i));
        }
        byte[] head = [.. Prefix(header), .. table];
        return ([.. head, .. blocks.SelectMany(b => b)], Convert.ToHexStringLower(MD5.HashData(head)));
    }
    private static byte[] Encrypted() => [(byte)'E', 8, 1, 2, 3, 4, 5, 6, 7, 8, 4, 0x11, 0x22, 0x33, 0x44, (byte)'S', 0x99];
    // A version-2 TSFM ROOT: one block per entry list, every row with the same content key and no names.
    private static byte[] Root(IEnumerable<uint[]> blocks)
    {
        var list = blocks.ToArray();
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write("TSFM"u8); writer.Write(24U); writer.Write(2U); writer.Write((uint)list.Sum(b => b.Length)); writer.Write(0U); writer.Write(0U);
        foreach (var rows in list)
        {
            writer.Write((uint)rows.Length); writer.Write(2U); writer.Write(RootInventoryReader.NoNames); writer.Write(0U); writer.Write((byte)0);
            ulong next = 0;
            foreach (var id in rows) { writer.Write(checked((uint)(id - next))); next = (ulong)id + 1; }
            foreach (var _ in rows) writer.Write(Enumerable.Repeat((byte)0x11, 16).ToArray());
        }
        return output.ToArray();
    }
}
