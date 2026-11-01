using System.Buffers.Binary;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;

namespace VibeMiner.Primitives.Tests;

/// <summary>The chunk table proof a range read of a large loose object relies on (D340).</summary>
public sealed class BlteChunkTableTests
{
    private static byte[] HeaderOf(PutObject o, out int length)
    {
        length = (int)BinaryPrimitives.ReadUInt32BigEndian(o.Bytes.Span[4..]);
        return o.Bytes[..length].ToArray();
    }

    [Fact]
    public void AProvenTableLocatesAndProvesEachChunk()
    {
        var o = Synthetic.Chunked(1, 300, 500, 700);
        var header = HeaderOf(o, out var length);
        Assert.Null(BlteChunkTable.ReadHeaderLength(o.Bytes.Span[..BlteChunkTable.PrefixBytes], o.DeclaredLength, out var claimed));
        Assert.Equal(length, claimed);
        Assert.Null(BlteChunkTable.Prove(o.Key, o.DeclaredLength, header, out var table));

        Assert.Equal((length, 3), (table!.HeaderLength, table.ChunkCount));
        Assert.Equal(header, table.Header.ToArray());
        Assert.Equal([length, length + 300, length + 800], Enumerable.Range(0, 3).Select(table.ChunkOffset));
        Assert.Equal([300L, 500, 700], Enumerable.Range(0, 3).Select(table.ChunkLength));
        Assert.Equal([0, 0, 1, 1, 2, 2], new long[] { length, length + 299, length + 300, length + 799, length + 800, o.DeclaredLength - 1 }.Select(table.ChunkAt));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.ChunkAt(length - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => table.ChunkAt(o.DeclaredLength));
        for (var i = 0; i < 3; i++)
            Assert.Null(table.CheckChunk(i, o.Bytes.Span.Slice((int)table.ChunkOffset(i), (int)table.ChunkLength(i))));
    }

    [Fact]
    public void ABadChunkOrAWrongSizeIsRefused()
    {
        var o = Synthetic.Chunked(2, 300, 500);
        Assert.Null(BlteChunkTable.Prove(o.Key, o.DeclaredLength, HeaderOf(o, out _), out var table));
        var chunk = o.Bytes.Slice((int)table!.ChunkOffset(1), 500).ToArray();
        chunk[100] ^= 1;
        Assert.Equal(ObjectRefusalCodes.BlteInvalid, table.CheckChunk(1, chunk)?.Code);
        Assert.Equal(ObjectRefusalCodes.SizeMismatch, table.CheckChunk(0, o.Bytes.Span.Slice((int)table.ChunkOffset(0), 299))?.Code);
    }

    [Fact]
    public void AHeaderThatIsNotTheKeysIsRefused()
    {
        var o = Synthetic.Chunked(3, 300, 500);
        var header = HeaderOf(o, out _);
        var other = Synthetic.Chunked(4, 300, 500);
        Assert.Equal(ObjectRefusalCodes.KeyMismatch, BlteChunkTable.Prove(other.Key, o.DeclaredLength, header, out var none)?.Code);
        Assert.Null(none);
        header[20] ^= 1;   // a chunk MD5 in the table
        Assert.Equal(ObjectRefusalCodes.KeyMismatch, BlteChunkTable.Prove(o.Key, o.DeclaredLength, header, out _)?.Code);
        // The right header with the wrong declared length: the sizes no longer add up.
        Assert.Equal(ObjectRefusalCodes.SizeMismatch, BlteChunkTable.Prove(o.Key, o.DeclaredLength + 1, HeaderOf(o, out _), out _)?.Code);
        // Not exactly the header.
        Assert.Equal(ObjectRefusalCodes.BlteInvalid, BlteChunkTable.Prove(o.Key, o.DeclaredLength, o.Bytes.Span[..(HeaderOf(o, out _).Length + 1)], out _)?.Code);
        Assert.Equal(ObjectRefusalCodes.KeyTypeUnsupported, BlteChunkTable.Prove(new ObjectKey(ObjectKeyType.Config, o.Key.KeyBytes()), o.DeclaredLength, HeaderOf(o, out _), out _)?.Code);
    }

    [Fact]
    public void APrefixClaimsAHeaderLengthOrRefuses()
    {
        var headerless = Synthetic.Headerless(5, 400);
        Assert.Null(BlteChunkTable.ReadHeaderLength(headerless.Bytes.Span, headerless.DeclaredLength, out var zero));
        Assert.Equal(0, zero);
        Assert.Equal(ObjectRefusalCodes.BlteInvalid, BlteChunkTable.Prove(headerless.Key, headerless.DeclaredLength, headerless.Bytes.Span, out _)?.Code);

        var bytes = Synthetic.Chunked(6, 300).Bytes.ToArray();
        Assert.Equal(ObjectRefusalCodes.BlteInvalid, BlteChunkTable.ReadHeaderLength(bytes.AsSpan(0, 7), bytes.Length, out _)?.Code);
        Assert.Equal(ObjectRefusalCodes.BlteInvalid, BlteChunkTable.ReadHeaderLength(bytes, 36, out _)?.Code);   // header as long as the object
        bytes[0] = (byte)'X';
        Assert.Equal(ObjectRefusalCodes.BlteInvalid, BlteChunkTable.ReadHeaderLength(bytes, bytes.Length, out _)?.Code);
    }

    [Fact]
    public void TheStoreCheckGivesTheSameAnswersAfterSharingTheParse()
    {
        var o = Synthetic.Chunked(7, 300, 500, 700);
        Assert.Null(ObjectVerification.Default.Check(o.Key, o.Bytes.Span, o.DeclaredLength));
        var bad = o.Bytes.ToArray(); bad[^10] ^= 1;
        Assert.Equal(ObjectRefusalCodes.BlteInvalid, ObjectVerification.Default.Check(o.Key, bad, o.DeclaredLength)?.Code);
        using var check = ObjectVerification.Default.Begin(o.Key, o.DeclaredLength);
        Assert.Null(check.Append(o.Bytes.Span[..(int)(o.DeclaredLength - 700)]));
        Assert.Equal(o.DeclaredLength - 700, check.ProvenLength);
        Assert.Null(check.Append(o.Bytes.Span[(int)(o.DeclaredLength - 700)..]));
        Assert.Null(check.Complete());
    }
}
