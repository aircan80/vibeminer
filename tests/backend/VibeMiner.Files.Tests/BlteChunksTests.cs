using System.Buffers.Binary;
using VibeMiner.Cdn;
using VibeMiner.Files.Formats;
using Xunit;
using static VibeMiner.Files.Tests.BlteChunkSpec;

namespace VibeMiner.Files.Tests;

/// <summary>
/// <see cref="BlteChunks"/> over synthetic objects from <see cref="BlteBuilder"/>: every mode, the 'E' nonce, holes, every
/// error code and state, headerless objects, <see cref="BlteLayout.IndexAt"/> and <see cref="BlteChunks.Peek"/>.
/// </summary>
public sealed class BlteChunksTests
{
    private const ulong KeyName = 0x1C43CC2976172D0C, OtherName = 0x0123456789ABCDEF;
    private static readonly byte[] Key = BlteBuilder.Bytes(16, seed: 7);
    private static readonly byte[] Iv = [0xA1, 0xB2, 0xC3, 0xD4];
    private static readonly ITactKeys Keys = new TactKeys().Add(KeyName, Key);

    private static byte[] Decode(BlteObject blte, BlteLayout layout, BlteChunk chunk, ITactKeys? keys = null)
    {
        var destination = new byte[chunk.DecodedSize];
        var result = BlteChunks.Decode(layout, chunk, blte.ChunkBytes(chunk), blte.EKey, keys ?? Keys, destination);
        Assert.False(result.IsHole);
        return destination;
    }

    private static byte[] DecodeAll(BlteObject blte, ITactKeys? keys = null)
    {
        var layout = blte.Parse();
        return [.. layout.Chunks.SelectMany(chunk => Decode(blte, layout, chunk, keys))];
    }

    private static void Fails(FileReadState state, string code, Action action)
    {
        var e = Assert.Throws<BlteFormatException>(action);
        Assert.Equal((state, code), (e.State, e.Code));
    }

    private static void ChunkFails(BlteObject blte, int index, FileReadState state, string code, ITactKeys? keys = null)
    {
        var layout = blte.Parse();
        Fails(state, code, () => Decode(blte, layout, layout.Chunks[index], keys));
    }

    private static BlteChunkSpec Enc(BlteChunkSpec inner, byte cipher = (byte)'S') => Encrypted(KeyName, Key, Iv, inner, cipher);

    private static byte[] Envelope(byte nameLength = 8, byte ivLength = 4, byte cipher = (byte)'S') =>
        [(byte)'E', nameLength, 0x0c, 0x2d, 0x17, 0x76, 0x29, 0xcc, 0x43, 0x1c, ivLength, .. Iv, cipher];

    // Success paths.

    [Fact]
    public void RawAndZlibChunksDecodeWithTheirOffsets()
    {
        var blte = BlteBuilder.Chunked([Raw(BlteBuilder.Bytes(100, 1)), Zlib(BlteBuilder.Bytes(5000, 2)), Raw([]), Zlib([]), Raw(BlteBuilder.Bytes(3, 3))]);
        var layout = blte.Parse();
        Assert.False(layout.Headerless);
        Assert.Equal((blte.HeaderLength, (long)blte.Encoded.Length, 5103L, 5), (layout.HeaderLength, layout.EncodedLength, layout.DecodedLength, layout.Chunks.Count));
        Assert.Equal([0L, 100, 5100, 5100, 5100], layout.Chunks.Select(c => c.DecodedOffset));
        Assert.Equal(blte.HeaderLength, layout.Chunks[0].EncodedOffset);
        Assert.Equal(layout.Chunks[0].EncodedOffset + 101, layout.Chunks[1].EncodedOffset);
        Assert.Equal(Enumerable.Range(0, 5), layout.Chunks.Select(c => c.Ordinal));
        Assert.Equal(blte.Decoded, DecodeAll(blte));
    }

    [Fact]
    public void ParseWithoutADecodedLengthTakesTheTableTotal()
    {
        var blte = BlteBuilder.Chunked([Raw(BlteBuilder.Bytes(10))]);
        Assert.Equal(10, BlteChunks.Parse(blte.Header, blte.EKey, blte.Encoded.Length, null).DecodedLength);
    }

    [Fact]
    public void EncryptedChunksDecryptWithTheirOrdinalInTheNonce()
    {
        // Ordinals 3 and 258 put the XOR in nonce bytes 0 and 1.
        var specs = Enumerable.Range(0, 260).Select(i => Raw(BlteBuilder.Bytes(2, i))).ToList();
        specs[0] = Enc(Raw(BlteBuilder.Bytes(70, 40)));
        specs[3] = Enc(Raw(BlteBuilder.Bytes(130, 41)));
        specs[258] = Enc(Zlib(BlteBuilder.Bytes(4000, 42)));
        var blte = BlteBuilder.Chunked(specs);
        Assert.Equal(blte.Decoded, DecodeAll(blte));

        // Decryption never touches the caller's input.
        var layout = blte.Parse();
        foreach (var index in new[] { 0, 3, 258 })
        {
            var chunk = layout.Chunks[index];
            var encoded = blte.ChunkBytes(chunk);
            BlteChunks.Decode(layout, chunk, encoded, blte.EKey, Keys, new byte[chunk.DecodedSize]);
            Assert.Equal(blte.ChunkBytes(chunk), encoded);
        }
    }

    [Fact]
    public void TheSameEncryptedChunkAtAnotherOrdinalIsTheWrongKey()
    {
        var blte = BlteBuilder.Chunked([Raw([1]), Raw([2]), Raw([3]), Enc(Raw(BlteBuilder.Bytes(64, 5)))]);
        var layout = blte.Parse();
        Assert.Equal(blte.Decoded[3..], Decode(blte, layout, layout.Chunks[3]));
        Fails(FileReadState.WrongKey, "BLTE_KEY_MISMATCH", () => Decode(blte, layout, layout.Chunks[3] with { Ordinal = 0 }));
    }

    [Fact]
    public void AnUnknownKeyIsAHoleNamedLittleEndianAndLeavesTheDestinationAlone()
    {
        var blte = BlteBuilder.Chunked([Raw([1, 2]), Enc(Raw(BlteBuilder.Bytes(32)))]);
        var layout = blte.Parse();
        var chunk = layout.Chunks[1];
        var encoded = blte.ChunkBytes(chunk);
        Assert.Equal(Envelope(), encoded[..16]);
        var destination = Enumerable.Repeat((byte)0xAA, 32).ToArray();
        var result = BlteChunks.Decode(layout, chunk, encoded, blte.EKey, new TactKeys().Add(OtherName, Key), destination);
        Assert.True(result.IsHole);
        Assert.Equal("1C43CC2976172D0C", result.KeyName);
        Assert.All(destination, b => Assert.Equal(0xAA, b));
        Assert.True(BlteChunks.Decode(layout, chunk, encoded, blte.EKey, TactKeys.None, destination).IsHole);
    }

    // Header and table.

    [Fact]
    public void HeaderLengthChecksMagicAndBounds()
    {
        static byte[] Prefix(uint length, string magic = "BLTE")
        {
            var prefix = new byte[8];
            System.Text.Encoding.ASCII.GetBytes(magic).CopyTo(prefix, 0);
            BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(4), length);
            return prefix;
        }
        Assert.Equal(0, BlteChunks.HeaderLength(Prefix(0), 100));
        Assert.Equal(36, BlteChunks.HeaderLength(Prefix(36), 36));
        Assert.Equal(BlteChunks.MaxHeaderLength, BlteChunks.HeaderLength(Prefix(BlteChunks.MaxHeaderLength), long.MaxValue));
        Fails(FileReadState.Corrupt, "BLTE_INVALID", () => BlteChunks.HeaderLength(Prefix(36, "BLTF"), 100));
        Fails(FileReadState.Corrupt, "BLTE_INVALID", () => BlteChunks.HeaderLength(Prefix(36)[..7], 100));
        Fails(FileReadState.Corrupt, "BLTE_INVALID", () => BlteChunks.HeaderLength(Prefix(11), 100));
        Fails(FileReadState.Corrupt, "BLTE_INVALID", () => BlteChunks.HeaderLength(Prefix(101), 100));
        Fails(FileReadState.Corrupt, "BLTE_LIMIT", () => BlteChunks.HeaderLength(Prefix(BlteChunks.MaxHeaderLength + 1), long.MaxValue));
    }

    [Fact]
    public void AHeaderThatDoesNotHashToTheEKeyIsBelievedForNothing()
    {
        // Bad flags too: integrity is checked first.
        var blte = BlteBuilder.Chunked([Raw([1])], h => h[8] = 0x10);
        var other = new CdnKey(new byte[16]);
        Fails(FileReadState.Corrupt, "BLTE_INTEGRITY", () => BlteChunks.Parse(blte.Header, other, blte.Encoded.Length, 1));
    }

    [Fact]
    public void ATableFlagOtherThan0x0FIsUnsupported()
    {
        var blte = BlteBuilder.Chunked([Raw([1])], h => h[8] = 0x10);
        Fails(FileReadState.Unsupported, "BLTE_UNSUPPORTED", () => blte.Parse());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void AChunkCountThatDoesNotFitTheHeaderIsInvalid(int count)
    {
        var blte = BlteBuilder.Chunked([Raw([1]), Raw([2])], h => h[11] = (byte)count);
        Fails(FileReadState.Corrupt, "BLTE_INVALID", () => blte.Parse());
    }

    [Fact]
    public void ChunkTotalsMustMatchTheDeclaredLengths()
    {
        var blte = BlteBuilder.Chunked([Raw([1, 2]), Zlib([3, 4, 5])]);
        Fails(FileReadState.Corrupt, "BLTE_INVALID", () => BlteChunks.Parse(blte.Header, blte.EKey, blte.Encoded.Length + 1, 5));
        Fails(FileReadState.Corrupt, "BLTE_INVALID", () => BlteChunks.Parse(blte.Header, blte.EKey, blte.Encoded.Length - 1, 5));
        Fails(FileReadState.Corrupt, "BLTE_INVALID", () => BlteChunks.Parse(blte.Header, blte.EKey, blte.Encoded.Length, 6));
        Fails(FileReadState.Corrupt, "BLTE_INVALID", () => BlteChunks.Parse(blte.Header, blte.EKey, blte.Encoded.Length, 4));
        Fails(FileReadState.Corrupt, "BLTE_INVALID", () => BlteChunks.Parse(blte.Header, blte.EKey, blte.Encoded.Length, -1));
    }

    [Fact]
    public void AChunkWithNoEncodedBytesIsInvalid()
    {
        var blte = BlteBuilder.Chunked([Raw([1])], h => BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12), 0));
        Fails(FileReadState.Corrupt, "BLTE_INVALID", () => BlteChunks.Parse(blte.Header, blte.EKey, blte.Encoded.Length, null));
    }

    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    public void AChunkOver16MiBIsOverTheLimit(int field)
    {
        var blte = BlteBuilder.Chunked([Raw([1])], h => BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(field), BlteChunks.MaxChunkBytes + 1));
        Fails(FileReadState.Corrupt, "BLTE_LIMIT", () => BlteChunks.Parse(blte.Header, blte.EKey, long.MaxValue, null));
    }

    [Fact]
    public void AHeaderOfTheWrongLengthIsTheCallersMistake()
    {
        var blte = BlteBuilder.Chunked([Raw([1])]);
        Assert.Throws<ArgumentException>(() => BlteChunks.Parse(blte.Header[..^1], blte.EKey, blte.Encoded.Length, 1));
        Assert.Throws<ArgumentException>(() => BlteChunks.Parse(blte.Encoded[..4], blte.EKey, blte.Encoded.Length, 1));
    }

    // Chunk integrity and plain modes.

    [Fact]
    public void AChunkWhoseBytesFailTheirMd5IsCorruptBeforeItsModeIsBelieved()
    {
        var blte = BlteBuilder.Chunked([Raw([1, 2, 3]), Raw([4, 5, 6])]);
        var layout = blte.Parse();
        foreach (var (at, value) in new[] { (1, (byte)9), (0, (byte)'E'), (0, (byte)'F') })
        {
            var encoded = blte.ChunkBytes(layout.Chunks[1]);
            encoded[at] = value;
            Fails(FileReadState.Corrupt, "BLTE_INTEGRITY", () => BlteChunks.Decode(layout, layout.Chunks[1], encoded, blte.EKey, Keys, new byte[3]));
        }
    }

    [Fact]
    public void ARawChunkOfTheWrongLengthIsInvalid()
    {
        ChunkFails(BlteBuilder.Chunked([Arbitrary([(byte)'N', 1, 2], 3)]), 0, FileReadState.Corrupt, "BLTE_INVALID");
        ChunkFails(BlteBuilder.Chunked([Arbitrary([(byte)'N', 1, 2], 1)]), 0, FileReadState.Corrupt, "BLTE_INVALID");
    }

    [Fact]
    public void ZlibMustBeExact()
    {
        var data = BlteBuilder.Bytes(3000);
        var member = BlteBuilder.Compress(data);
        var cases = new[]
        {
            Arbitrary([(byte)'Z', .. member[..^1]], data.Length), // Truncated.
            Arbitrary([(byte)'Z', .. member, 0], data.Length), // Trailing byte.
            Arbitrary([(byte)'Z', .. member], data.Length + 1), // Decodes short.
            Arbitrary([(byte)'Z', .. member], data.Length - 1), // Decodes long.
            Arbitrary([(byte)'Z', 1, 2, 3, 4], 4), // Not zlib.
            Arbitrary([(byte)'Z'], 0), // Empty member.
        };
        foreach (var spec in cases)
            ChunkFails(BlteBuilder.Chunked([spec]), 0, FileReadState.Corrupt, "BLTE_INVALID");
    }

    [Theory]
    [InlineData((byte)'F')]
    [InlineData((byte)'X')]
    public void OtherModesAreUnsupported(byte mode) =>
        ChunkFails(BlteBuilder.Chunked([Arbitrary([mode, 1, 2, 3], 3)]), 0, FileReadState.Unsupported, "BLTE_UNSUPPORTED");

    // Encryption.

    [Fact]
    public void AKnownNameWithTheWrongKeyIsTheWrongKey()
    {
        var blte = BlteBuilder.Chunked([Enc(Raw(BlteBuilder.Bytes(64))), Enc(Zlib(BlteBuilder.Bytes(2000)))]);
        var wrong = new TactKeys().Add(KeyName, BlteBuilder.Bytes(16, seed: 8));
        ChunkFails(blte, 0, FileReadState.WrongKey, "BLTE_KEY_MISMATCH", wrong);
        ChunkFails(blte, 1, FileReadState.WrongKey, "BLTE_KEY_MISMATCH", wrong);
    }

    [Fact]
    public void ABadInnerPayloadUnderAKnownKeyIsTheWrongKey()
    {
        ChunkFails(BlteBuilder.Chunked([Enc(Arbitrary([(byte)'N', 1, 2, 3], 5))]), 0, FileReadState.WrongKey, "BLTE_KEY_MISMATCH");
        ChunkFails(BlteBuilder.Chunked([Enc(Arbitrary([(byte)'Z', 1, 2, 3], 5))]), 0, FileReadState.WrongKey, "BLTE_KEY_MISMATCH");
        ChunkFails(BlteBuilder.Chunked([Enc(Arbitrary([(byte)'Q', 1, 2, 3], 3))]), 0, FileReadState.WrongKey, "BLTE_KEY_MISMATCH");
    }

    [Theory]
    [InlineData((byte)'E')]
    [InlineData((byte)'F')]
    public void AnInnerEOrFIsTheWrongKey(byte inner) =>
        ChunkFails(BlteBuilder.Chunked([Enc(Arbitrary([inner, 1, 2, 3], 3))]), 0, FileReadState.WrongKey, "BLTE_KEY_MISMATCH");

    [Fact]
    public void Arc4IsUnsupportedWhetherOrNotTheKeyIsKnown()
    {
        var blte = BlteBuilder.Chunked([Enc(Raw([1, 2, 3]), cipher: (byte)'A')]);
        ChunkFails(blte, 0, FileReadState.Unsupported, "BLTE_UNSUPPORTED");
        ChunkFails(blte, 0, FileReadState.Unsupported, "BLTE_UNSUPPORTED", TactKeys.None);
    }

    [Fact]
    public void EnvelopeLengthsOtherThan8And4AreUnsupportedAndShortEnvelopesInvalid()
    {
        ChunkFails(BlteBuilder.Chunked([Arbitrary([.. Envelope(nameLength: 16), 1, 2], 2)]), 0, FileReadState.Unsupported, "BLTE_UNSUPPORTED");
        ChunkFails(BlteBuilder.Chunked([Arbitrary([.. Envelope(ivLength: 8), 1, 2], 2)]), 0, FileReadState.Unsupported, "BLTE_UNSUPPORTED");
        foreach (var length in new[] { 1, 2, 10, 15 })
            ChunkFails(BlteBuilder.Chunked([Arbitrary(Envelope()[..length], 0)]), 0, FileReadState.Corrupt, "BLTE_INVALID");
        // A whole envelope with no ciphertext, with or without the key.
        ChunkFails(BlteBuilder.Chunked([Arbitrary(Envelope(), 0)]), 0, FileReadState.Corrupt, "BLTE_INVALID");
        ChunkFails(BlteBuilder.Chunked([Arbitrary(Envelope(), 0)]), 0, FileReadState.Corrupt, "BLTE_INVALID", TactKeys.None);
    }

    // Headerless.

    [Fact]
    public void HeaderlessObjectsDecodeInEveryMode()
    {
        foreach (var spec in new[] { Raw(BlteBuilder.Bytes(50)), Zlib(BlteBuilder.Bytes(6000)), Enc(Zlib(BlteBuilder.Bytes(900))), Enc(Raw(BlteBuilder.Bytes(9))) })
        {
            var blte = BlteBuilder.Headerless(spec);
            var layout = blte.Parse();
            Assert.True(layout.Headerless);
            var chunk = Assert.Single(layout.Chunks);
            Assert.Equal(new BlteChunk(0, 8, blte.Encoded.Length - 8, 0, blte.Decoded.Length, null), chunk);
            Assert.Equal(blte.Decoded, Decode(blte, layout, chunk));
        }
    }

    [Fact]
    public void AHeaderlessObjectIsProvedWholeWhenDecoded()
    {
        var blte = BlteBuilder.Headerless(Raw([1, 2, 3]));
        var layout = blte.Parse();
        var encoded = blte.ChunkBytes(layout.Chunks[0]);
        encoded[2] ^= 1;
        Fails(FileReadState.Corrupt, "BLTE_INTEGRITY", () => BlteChunks.Decode(layout, layout.Chunks[0], encoded, blte.EKey, Keys, new byte[3]));
        var wrongKey = new CdnKey(new byte[16]);
        Fails(FileReadState.Corrupt, "BLTE_INTEGRITY", () => BlteChunks.Decode(layout, layout.Chunks[0], blte.ChunkBytes(layout.Chunks[0]), wrongKey, Keys, new byte[3]));
    }

    [Fact]
    public void AHeaderlessUnknownKeyIsAHole()
    {
        var blte = BlteBuilder.Headerless(Enc(Raw([1, 2, 3])));
        var layout = blte.Parse();
        var result = BlteChunks.Decode(layout, layout.Chunks[0], blte.ChunkBytes(layout.Chunks[0]), blte.EKey, TactKeys.None, new byte[3]);
        Assert.Equal(ChunkResult.Hole("1C43CC2976172D0C"), result);
    }

    [Fact]
    public void HeaderlessLengthsAreChecked()
    {
        var blte = BlteBuilder.Headerless(Raw([1, 2, 3]));
        Fails(FileReadState.Corrupt, "BLTE_INVALID", () => BlteChunks.Parse(blte.Header, blte.EKey, blte.Encoded.Length, null));
        Fails(FileReadState.Corrupt, "BLTE_LIMIT", () => BlteChunks.Parse(blte.Header, blte.EKey, blte.Encoded.Length, int.MaxValue + 1L));
        Fails(FileReadState.Corrupt, "BLTE_LIMIT", () => BlteChunks.Parse(blte.Header, blte.EKey, int.MaxValue + 9L, 3));
        Fails(FileReadState.Corrupt, "BLTE_INVALID", () => BlteChunks.Parse(blte.Header, blte.EKey, 8, 0));
        // The headerless cap, default and given, on both sizes.
        Fails(FileReadState.Corrupt, "BLTE_LIMIT", () => BlteChunks.Parse(blte.Header, blte.EKey, blte.Encoded.Length, BlteChunks.DefaultMaxHeaderlessBytes + 1));
        Fails(FileReadState.Corrupt, "BLTE_LIMIT", () => BlteChunks.Parse(blte.Header, blte.EKey, BlteChunks.DefaultMaxHeaderlessBytes + 9, 3));
        Assert.Equal(BlteChunks.DefaultMaxHeaderlessBytes, BlteChunks.Parse(blte.Header, blte.EKey, BlteChunks.DefaultMaxHeaderlessBytes + 8, BlteChunks.DefaultMaxHeaderlessBytes).DecodedLength);
        Fails(FileReadState.Corrupt, "BLTE_LIMIT", () => BlteChunks.Parse(blte.Header, blte.EKey, blte.Encoded.Length, 3, maxHeaderlessBytes: 2));
        Fails(FileReadState.Corrupt, "BLTE_LIMIT", () => BlteChunks.Parse(blte.Header, blte.EKey, 11, 1, maxHeaderlessBytes: 2));
        Assert.Equal(3, BlteChunks.Parse(blte.Header, blte.EKey, blte.Encoded.Length, 3, maxHeaderlessBytes: 4).DecodedLength);
        Assert.Throws<ArgumentException>(() => BlteChunks.Parse(blte.Encoded, blte.EKey, blte.Encoded.Length, 3));
        // A declared length the bytes don't decode to fails at decode.
        var layout = BlteChunks.Parse(blte.Header, blte.EKey, blte.Encoded.Length, 4);
        Fails(FileReadState.Corrupt, "BLTE_INVALID", () => BlteChunks.Decode(layout, layout.Chunks[0], blte.ChunkBytes(layout.Chunks[0]), blte.EKey, Keys, new byte[4]));
    }

    // IndexAt, Peek and arguments.

    [Fact]
    public void IndexAtSkipsEmptyChunksAndChecksBounds()
    {
        var blte = BlteBuilder.Chunked([Raw([]), Raw(BlteBuilder.Bytes(10)), Zlib([]), Raw(BlteBuilder.Bytes(5)), Raw([])]);
        var layout = blte.Parse();
        Assert.Equal(1, layout.IndexAt(0));
        Assert.Equal(1, layout.IndexAt(9));
        Assert.Equal(3, layout.IndexAt(10));
        Assert.Equal(3, layout.IndexAt(14));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.IndexAt(15));
        Assert.Throws<ArgumentOutOfRangeException>(() => layout.IndexAt(-1));

        var many = BlteBuilder.Chunked([.. Enumerable.Range(0, 100).Select(i => Raw(BlteBuilder.Bytes(i % 3, i)))]).Parse();
        for (long offset = 0; offset < many.DecodedLength; offset++)
        {
            var chunk = many.Chunks[many.IndexAt(offset)];
            Assert.InRange(offset, chunk.DecodedOffset, chunk.DecodedOffset + chunk.DecodedSize - 1);
        }
    }

    [Fact]
    public void PeekReadsTheModeAndKeyName()
    {
        Assert.Equal(((byte)'N', (ulong?)null), BlteChunks.Peek([(byte)'N']));
        Assert.Equal(((byte)'Z', (ulong?)null), BlteChunks.Peek([(byte)'Z', 0x78]));
        Assert.Equal(((byte)'F', (ulong?)null), BlteChunks.Peek([(byte)'F']));
        Assert.Equal(((byte)'E', (ulong?)KeyName), BlteChunks.Peek(Envelope()));
        Assert.Equal(((byte)'E', (ulong?)KeyName), BlteChunks.Peek([.. Envelope(), 1, 2, 3]));
        Fails(FileReadState.Corrupt, "BLTE_INVALID", () => BlteChunks.Peek([]));
        Fails(FileReadState.Corrupt, "BLTE_INVALID", () => BlteChunks.Peek(Envelope()[..11]));
        Fails(FileReadState.Unsupported, "BLTE_UNSUPPORTED", () => BlteChunks.Peek(Envelope(nameLength: 16)));
        Fails(FileReadState.Unsupported, "BLTE_UNSUPPORTED", () => BlteChunks.Peek(Envelope(cipher: (byte)'A')));
    }

    [Fact]
    public void DecodeWantsExactlyTheChunksBytesAndSize()
    {
        var blte = BlteBuilder.Chunked([Raw([1, 2, 3])]);
        var layout = blte.Parse();
        var encoded = blte.ChunkBytes(layout.Chunks[0]);
        Assert.Throws<ArgumentException>(() => BlteChunks.Decode(layout, layout.Chunks[0], encoded[..^1], blte.EKey, Keys, new byte[3]));
        Assert.Throws<ArgumentException>(() => BlteChunks.Decode(layout, layout.Chunks[0], encoded, blte.EKey, Keys, new byte[2]));
    }
}
