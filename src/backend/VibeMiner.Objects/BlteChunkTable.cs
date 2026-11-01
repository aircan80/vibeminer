using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VibeMiner.Objects;

/// <summary>
/// The chunk table of a chunked BLTE object, proven against its EKey (D122): the header MD5 is the key and the chunk sizes
/// add up to the declared length. With it, one chunk can be proven by its own MD5 without the rest of the object, so a
/// range of a large loose object can be fetched and checked chunk by chunk (D340). The store's BLTE check uses the same
/// parse. A headerless object has no table: its only proof is the MD5 of the whole object.
/// </summary>
public sealed class BlteChunkTable
{
    /// <summary>Magic and header size: the bytes that say how long the header is.</summary>
    public const int PrefixBytes = 8;
    internal const int MaxChunks = 65_536;
    private readonly byte[] header;
    private readonly long[] offsets;

    private BlteChunkTable(ObjectKey key, long declared, byte[] header, long[] offsets)
    { Key = key; DeclaredLength = declared; this.header = header; this.offsets = offsets; }

    public ObjectKey Key { get; }
    public long DeclaredLength { get; }
    /// <summary>The proven header bytes; they are the object's first <see cref="HeaderLength"/> bytes.</summary>
    public ReadOnlyMemory<byte> Header => header;
    public int HeaderLength => header.Length;
    public int ChunkCount => offsets.Length;
    /// <summary>Where chunk <paramref name="index"/> starts in the object.</summary>
    public long ChunkOffset(int index) => offsets[index];
    /// <summary>The encoded size of chunk <paramref name="index"/>, mode byte included.</summary>
    public long ChunkLength(int index) => BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12 + index * 24));

    /// <summary>The chunk holding byte <paramref name="offset"/>, which lies past the header and inside the object.</summary>
    public int ChunkAt(long offset)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(offset, HeaderLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, DeclaredLength);
        var i = Array.BinarySearch(offsets, offset);
        return i >= 0 ? i : ~i - 1;
    }

    /// <summary>Null when <paramref name="bytes"/> are exactly chunk <paramref name="index"/>, else the refusal.</summary>
    public ObjectRefusal? CheckChunk(int index, ReadOnlySpan<byte> bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, ChunkCount);
        if (bytes.Length != ChunkLength(index)) return new(ObjectRefusalCodes.SizeMismatch, $"BLTE chunk {index} is not its declared size.");
        Span<byte> md5 = stackalloc byte[16];
        MD5.HashData(bytes, md5);
        return ChunkMatches(index, md5) ? null : new(ObjectRefusalCodes.BlteInvalid, $"BLTE chunk {index} MD5 mismatch.");
    }

    internal bool ChunkMatches(int index, ReadOnlySpan<byte> md5) => md5.SequenceEqual(header.AsSpan(12 + index * 24 + 8, 16));

    /// <summary>
    /// The header length the first <see cref="PrefixBytes"/> of an object of <paramref name="declaredLength"/> bytes claim, or
    /// the refusal when they can't start a BLTE object. 0 is a headerless object. The claim is unproven until <see cref="Prove"/>.
    /// </summary>
    public static ObjectRefusal? ReadHeaderLength(ReadOnlySpan<byte> prefix, long declaredLength, out long headerLength)
    {
        headerLength = -1;
        try { headerLength = HeaderLengthOf(prefix, declaredLength); return null; }
        catch (VerificationFailure f) { return new(f.Code, f.Message); }
    }

    /// <summary>
    /// Proves <paramref name="header"/> (exactly the header, as long as its prefix claims) is the chunk table of the encoded
    /// object <paramref name="key"/> of <paramref name="declaredLength"/> bytes. No store size limit applies: nothing is stored.
    /// </summary>
    public static ObjectRefusal? Prove(ObjectKey key, long declaredLength, ReadOnlySpan<byte> header, out BlteChunkTable? table)
    {
        table = null;
        if (key.Type != ObjectKeyType.Encoded) return new(ObjectRefusalCodes.KeyTypeUnsupported, "Only encoded objects are BLTE.");
        if (key.Namespace != 0) return new(ObjectRefusalCodes.NamespaceUnsupported, "Only namespace 0 is stored in format 1.");
        if (declaredLength <= 0) return new(ObjectRefusalCodes.TooLarge, "The declared length is zero.");
        try
        {
            var length = HeaderLengthOf(header, declaredLength);
            if (length == 0) return new(ObjectRefusalCodes.BlteInvalid, "A headerless BLTE object has no chunk table.");
            if (header.Length != length) return new(ObjectRefusalCodes.BlteInvalid, "The bytes are not exactly the BLTE header.");
            table = Parse(key, declaredLength, header.ToArray());
            return null;
        }
        catch (VerificationFailure f) { return new(f.Code, f.Message); }
    }

    /// <summary>The header length the prefix claims; throws <see cref="VerificationFailure"/> when it can't start a BLTE object.</summary>
    internal static long HeaderLengthOf(ReadOnlySpan<byte> prefix, long declared)
    {
        if (prefix.Length < PrefixBytes) throw new VerificationFailure(ObjectRefusalCodes.BlteInvalid, "BLTE is truncated.");
        if (!prefix[..4].SequenceEqual("BLTE"u8)) throw new VerificationFailure(ObjectRefusalCodes.BlteInvalid, "Not a BLTE object.");
        long length = BinaryPrimitives.ReadUInt32BigEndian(prefix[4..]);
        if (length == 0)
        {
            if (declared < 9) throw new VerificationFailure(ObjectRefusalCodes.BlteInvalid, "A headerless BLTE object needs a mode byte.");
            return 0;
        }
        if (length < 12 + 24 || length > 12 + 24L * MaxChunks || length >= declared)
            throw new VerificationFailure(ObjectRefusalCodes.BlteInvalid, "BLTE header length is invalid.");
        return length;
    }

    /// <summary>Proves a whole header whose length <see cref="HeaderLengthOf"/> accepted; throws <see cref="VerificationFailure"/>.</summary>
    internal static BlteChunkTable Parse(ObjectKey key, long declared, byte[] header)
    {
        Span<byte> k = stackalloc byte[16]; key.WriteKey(k);
        if (!CryptographicOperations.FixedTimeEquals(MD5.HashData(header), k))
            throw new VerificationFailure(ObjectRefusalCodes.KeyMismatch, "The BLTE header MD5 is not the key.");
        if (header[8] != 0x0F) throw new VerificationFailure(ObjectRefusalCodes.BlteUnsupported, "Unsupported BLTE chunk table format.");
        var count = (header[9] << 16) | (header[10] << 8) | header[11];
        if (count == 0 || header.Length != 12 + 24L * count) throw new VerificationFailure(ObjectRefusalCodes.BlteInvalid, "BLTE chunk table size is invalid.");
        var offsets = new long[count];
        long total = header.Length;
        for (var i = 0; i < count; i++)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12 + i * 24));
            if (size == 0) throw new VerificationFailure(ObjectRefusalCodes.BlteInvalid, "A BLTE chunk lacks its mode byte.");
            offsets[i] = total;
            total += size;
        }
        if (total != declared) throw new VerificationFailure(ObjectRefusalCodes.SizeMismatch, "BLTE chunk sizes don't add up to the declared length.");
        return new(key, declared, header, offsets);
    }
}
