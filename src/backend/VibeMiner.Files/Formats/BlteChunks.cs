using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using VibeMiner.Cdn;

namespace VibeMiner.Files.Formats;

/// <summary>
/// One BLTE chunk. <see cref="Ordinal"/> is its 0-based chunk table index (0 when headerless), which also feeds the 'E' nonce.
/// Offsets are within the encoded object and the decoded file. <see cref="Hash"/> is the table's chunk MD5, null when headerless.
/// </summary>
internal sealed record BlteChunk(int Ordinal, long EncodedOffset, int EncodedSize, long DecodedOffset, int DecodedSize, byte[]? Hash);

/// <summary>A parsed BLTE layout: the chunk table (one chunk when headerless), with the header already proved against the EKey.</summary>
internal sealed record BlteLayout(bool Headerless, int HeaderLength, long EncodedLength, long DecodedLength, IReadOnlyList<BlteChunk> Chunks)
{
    /// <summary>
    /// The index of the chunk holding decoded byte <paramref name="decodedOffset"/>. Never an empty chunk: an empty chunk shares
    /// its offset with the next one, and the last chunk starting at or before the offset wins. Throws outside the decoded file.
    /// </summary>
    public int IndexAt(long decodedOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decodedOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(decodedOffset, DecodedLength);
        int low = 0, high = Chunks.Count - 1;
        while (low < high)
        {
            var mid = low + (high - low + 1) / 2;
            if (Chunks[mid].DecodedOffset <= decodedOffset) low = mid; else high = mid - 1;
        }
        return low;
    }
}

/// <summary>How a chunk decode ended: decoded into the destination, or a hole needing the TACT key <see cref="KeyName"/> (16 uppercase hex).</summary>
internal readonly record struct ChunkResult(string? KeyName)
{
    public static ChunkResult Decoded => default;
    public static ChunkResult Hole(string keyName) => new(keyName);
    public bool IsHole => KeyName is not null;
}

/// <summary>
/// BLTE bytes failed a check. <see cref="State"/> is Corrupt (<c>BLTE_INVALID</c>, <c>BLTE_INTEGRITY</c>, <c>BLTE_LIMIT</c>),
/// Unsupported (<c>BLTE_UNSUPPORTED</c>) or WrongKey (<c>BLTE_KEY_MISMATCH</c>).
/// </summary>
internal sealed class BlteFormatException(FileReadState state, string code, string message) : Exception(message)
{
    public FileReadState State { get; } = state;
    public string Code { get; } = code;
}

/// <summary>
/// Chunk-at-a-time BLTE: parse the header once, then decode any chunk from just its own encoded bytes. Pure and synchronous;
/// it never fetches. Modes 'N', 'Z' and 'E' (Salsa20 over an inner 'N' or 'Z') decode; 'F' and ARC4 are Unsupported. Every chunk's
/// bytes are proved by their MD5 before their mode byte is believed. A key this build doesn't know makes a hole, never zeros.
/// On a throw the destination's contents are undefined.
/// </summary>
internal static class BlteChunks
{
    public const int MaxChunks = 65_536;
    public const int MaxChunkBytes = 16 * 1024 * 1024;
    public const int MaxHeaderLength = 12 + 24 * MaxChunks;
    /// <summary>The 'E' envelope: mode, key name length, key name, IV length, IV, cipher.</summary>
    public const int EnvelopeLength = 16;
    /// <summary>A headerless object is one chunk, decoded whole, so both its sizes are capped (by default 256 MiB).</summary>
    public const long DefaultMaxHeaderlessBytes = 256L << 20;

    /// <summary>
    /// The header length from the first 8 encoded bytes, 0 when headerless. Checks the magic and bounds the length by
    /// <paramref name="encodedLength"/> and the chunk limit.
    /// </summary>
    public static int HeaderLength(ReadOnlySpan<byte> prefix8, long encodedLength)
    {
        if (prefix8.Length != 8) throw Invalid("The BLTE prefix is 8 bytes.");
        if (!prefix8[..4].SequenceEqual("BLTE"u8)) throw Invalid("BLTE magic is missing.");
        var length = BinaryPrimitives.ReadUInt32BigEndian(prefix8[4..]);
        if (length == 0) return 0;
        if (length > MaxHeaderLength) throw Limit("BLTE header is over the chunk limit.");
        if (length < 12 || length > encodedLength) throw Invalid("BLTE header length is invalid.");
        return (int)length;
    }

    /// <summary>
    /// Parses the header (all of it, prefix included; just the 8 prefix bytes when headerless). A chunked header must hash to
    /// <paramref name="eKey"/> before any field is believed. A headerless object needs <paramref name="decodedLength"/> and is
    /// proved only when its one chunk is decoded; both its sizes must be at most <paramref name="maxHeaderlessBytes"/>.
    /// <paramref name="header"/> of the wrong length throws <see cref="ArgumentException"/>.
    /// </summary>
    public static BlteLayout Parse(ReadOnlySpan<byte> header, CdnKey eKey, long encodedLength, long? decodedLength,
        long maxHeaderlessBytes = DefaultMaxHeaderlessBytes)
    {
        if (header.Length < 8) throw new ArgumentException("The BLTE header starts with its 8-byte prefix.", nameof(header));
        var headerLength = HeaderLength(header[..8], encodedLength);
        if (decodedLength < 0) throw Invalid("The declared decoded length is negative.");
        if (headerLength == 0)
        {
            if (header.Length != 8) throw new ArgumentException("A headerless BLTE header is its 8-byte prefix.", nameof(header));
            if (encodedLength < 9) throw Invalid("A headerless BLTE object has no mode byte.");
            if (decodedLength is not { } decoded) throw Invalid("A headerless BLTE object needs a declared decoded length.");
            if (encodedLength - 8 > Math.Min(maxHeaderlessBytes, int.MaxValue) || decoded > Math.Min(maxHeaderlessBytes, int.MaxValue))
                throw Limit("A headerless BLTE object is over the headerless byte limit.");
            return new(true, 0, encodedLength, decoded, [new BlteChunk(0, 8, (int)(encodedLength - 8), 0, (int)decoded, null)]);
        }

        if (header.Length != headerLength) throw new ArgumentException("The BLTE header doesn't match its declared length.", nameof(header));
        // The header is authenticated before any of its claims is believed.
        Verify(header, eKey, "header");
        var table = header[8..];
        if (table[0] != 0x0F) throw Unsupported("BLTE chunk table format is unsupported.");
        var count = (table[1] << 16) | (table[2] << 8) | table[3];
        if (count is < 1 or > MaxChunks || headerLength != 12 + 24 * count) throw Invalid("BLTE chunk table size is invalid.");
        var chunks = new BlteChunk[count];
        long encodedAt = headerLength, decodedAt = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = table.Slice(4 + 24 * i, 24);
            var encodedSize = BinaryPrimitives.ReadUInt32BigEndian(entry);
            var decodedSize = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            if (encodedSize == 0) throw Invalid("A BLTE chunk has no mode byte.");
            if (encodedSize > MaxChunkBytes || decodedSize > MaxChunkBytes) throw Limit("A BLTE chunk is over the chunk byte limit.");
            chunks[i] = new(i, encodedAt, (int)encodedSize, decodedAt, (int)decodedSize, entry.Slice(8, 16).ToArray());
            encodedAt += encodedSize;
            decodedAt += decodedSize;
        }
        if (encodedAt != encodedLength || decodedLength is { } declared && decodedAt != declared)
            throw Invalid("BLTE chunk totals differ from the declared lengths.");
        return new(false, headerLength, encodedLength, decodedAt, chunks);
    }

    /// <summary>
    /// A chunk's mode byte and, for 'E', its key name, from the chunk's first <see cref="EnvelopeLength"/> bytes (or all of a
    /// shorter chunk). Unverified: for listing holes only. A short or malformed envelope throws as <see cref="Decode"/> would.
    /// </summary>
    public static (byte Mode, ulong? KeyName) Peek(ReadOnlySpan<byte> chunkPrefix)
    {
        if (chunkPrefix.IsEmpty) throw Invalid("A BLTE chunk has no mode byte.");
        if (chunkPrefix[0] != 'E') return (chunkPrefix[0], null);
        var (keyName, _) = ReadEnvelope(chunkPrefix);
        return ((byte)'E', keyName);
    }

    /// <summary>
    /// Decodes one chunk: <paramref name="encoded"/> is exactly its encoded bytes and <paramref name="destination"/> exactly its
    /// decoded size. An 'E' chunk whose key <paramref name="keys"/> doesn't have is a hole and leaves the destination untouched.
    /// Decrypted bytes are proven only by the key, and by the caller's CKey check over a complete whole-file read: a wrong key
    /// that happens to give a valid-looking 'N' payload decodes as garbage here.
    /// </summary>
    public static ChunkResult Decode(BlteLayout layout, BlteChunk chunk, ReadOnlySpan<byte> encoded, CdnKey eKey, ITactKeys keys,
        Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(keys);
        if (encoded.Length != chunk.EncodedSize) throw new ArgumentException("The encoded bytes must be exactly the chunk's.", nameof(encoded));
        if (destination.Length != chunk.DecodedSize) throw new ArgumentException("The destination must be exactly the chunk's decoded size.", nameof(destination));

        // Prove the bytes before believing the mode byte.
        if (layout.Headerless)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
            hash.AppendData("BLTE\0\0\0\0"u8);
            hash.AppendData(encoded);
            Span<byte> actual = stackalloc byte[16];
            hash.GetHashAndReset(actual);
            Span<byte> expected = stackalloc byte[16];
            eKey.WriteTo(expected);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw Integrity("BLTE encoding key doesn't match.");
        }
        else
        {
            if (chunk.Hash is not { Length: 16 } chunkHash) throw new ArgumentException("A chunked BLTE chunk has a 16-byte hash.", nameof(chunk));
            Span<byte> actual = stackalloc byte[16];
            MD5.HashData(encoded, actual);
            if (!CryptographicOperations.FixedTimeEquals(actual, chunkHash)) throw Integrity("BLTE chunk checksum doesn't match.");
        }

        switch (encoded[0])
        {
            case (byte)'N':
                if (encoded.Length - 1 != destination.Length) throw Invalid("A BLTE raw chunk length is invalid.");
                encoded[1..].CopyTo(destination);
                return ChunkResult.Decoded;
            case (byte)'Z':
            {
                var compressed = ArrayPool<byte>.Shared.Rent(encoded.Length - 1);
                try
                {
                    encoded[1..].CopyTo(compressed);
                    if (!TryInflate(compressed, 0, encoded.Length - 1, destination)) throw Invalid("BLTE zlib is invalid, truncated, has trailing bytes or has the wrong decoded length.");
                }
                finally { ArrayPool<byte>.Shared.Return(compressed); }
                return ChunkResult.Decoded;
            }
            case (byte)'E':
                return Decrypt(chunk, encoded, keys, destination);
            default:
                throw Unsupported("A BLTE chunk mode is unsupported.");
        }
    }

    private static ChunkResult Decrypt(BlteChunk chunk, ReadOnlySpan<byte> encoded, ITactKeys keys, Span<byte> destination)
    {
        var (keyName, iv) = ReadEnvelope(encoded);
        var cipherLength = encoded.Length - EnvelopeLength;
        if (cipherLength == 0) throw Invalid("A BLTE encrypted chunk has no payload.");
        if (keys.Find(keyName) is not { } key)
            return ChunkResult.Hole(keyName.ToString("X16", System.Globalization.CultureInfo.InvariantCulture));
        if (key.Length != 16) throw new ArgumentException("A TACT key is 16 bytes.", nameof(keys));

        Span<byte> nonce = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(nonce, iv);
        for (var i = 0; i < 4; i++)
            nonce[i] ^= (byte)(chunk.Ordinal >> (8 * i));

        // Decrypt in our own buffer, never in the caller's input.
        var plain = ArrayPool<byte>.Shared.Rent(cipherLength);
        try
        {
            var span = plain.AsSpan(0, cipherLength);
            encoded[EnvelopeLength..].CopyTo(span);
            Salsa20.Transform(key.Span, nonce, span);
            // Salsa20 has no MAC: a wrong key shows only as a bad inner payload, any inner mode but 'N' or 'Z' included.
            switch (span[0])
            {
                case (byte)'N':
                    if (cipherLength - 1 != destination.Length) throw KeyMismatch();
                    span[1..].CopyTo(destination);
                    break;
                case (byte)'Z':
                    if (!TryInflate(plain, 1, cipherLength - 1, destination)) throw KeyMismatch();
                    break;
                default:
                    throw KeyMismatch();
            }
        }
        finally { ArrayPool<byte>.Shared.Return(plain, clearArray: true); }
        return ChunkResult.Decoded;
    }

    /// <summary>The 'E' envelope: key name length 8, key name (little-endian), IV length 4, IV, cipher 'S'.</summary>
    private static (ulong KeyName, uint Iv) ReadEnvelope(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length < 2) throw Invalid("A BLTE encrypted chunk envelope is truncated.");
        if (chunk[1] != 8) throw Unsupported("A BLTE encrypted chunk key name length is unsupported.");
        if (chunk.Length < 11) throw Invalid("A BLTE encrypted chunk envelope is truncated.");
        if (chunk[10] != 4) throw Unsupported("A BLTE encrypted chunk IV length is unsupported.");
        if (chunk.Length < EnvelopeLength) throw Invalid("A BLTE encrypted chunk envelope is truncated.");
        if (chunk[15] != 'S') throw Unsupported("A BLTE encrypted chunk cipher is unsupported.");
        return (BinaryPrimitives.ReadUInt64LittleEndian(chunk[2..]), BinaryPrimitives.ReadUInt32LittleEndian(chunk[11..]));
    }

    /// <summary>Inflates one zlib member that must fill <paramref name="destination"/> exactly and end exactly at the input's end.</summary>
    private static bool TryInflate(byte[] bytes, int offset, int count, Span<byte> destination)
    {
        using var input = new ExactInflaterInput(bytes, offset, count);
        using var inflater = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: true);
        try
        {
            var produced = 0;
            while (produced < destination.Length)
            {
                var read = inflater.Read(destination[produced..]);
                if (read == 0) return false;
                produced += read;
            }
            // One more byte proves the member ends here; reading it also inflates an empty member.
            Span<byte> probe = stackalloc byte[1];
            if (inflater.Read(probe) != 0) return false;
        }
        catch (InvalidDataException) { return false; }
        return !input.ReadPastEnd && input.Consumed == count;
    }

    // The framework inflater reads ahead and otherwise accepts a truncated member at physical EOF. The final byte is
    // fenced off: a valid member must ask for it to finish its checksum, and a member with trailing data finishes first.
    private sealed class ExactInflaterInput(byte[] bytes, int start, int count) : Stream
    {
        public int Consumed { get; private set; }
        public bool ReadPastEnd { get; private set; }
        public override int Read(byte[] buffer, int offset, int length) => Read(buffer.AsSpan(offset, length));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.Length == 0) return 0;
            if (Consumed == count) { ReadPastEnd = true; return 0; }
            var available = Consumed == count - 1 ? 1 : count - Consumed - 1;
            var n = Math.Min(buffer.Length, available);
            bytes.AsSpan(start + Consumed, n).CopyTo(buffer);
            Consumed += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => count;
        public override long Position { get => Consumed; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static void Verify(ReadOnlySpan<byte> data, CdnKey expected, string role)
    {
        Span<byte> actual = stackalloc byte[16];
        MD5.HashData(data, actual);
        Span<byte> key = stackalloc byte[16];
        expected.WriteTo(key);
        if (!CryptographicOperations.FixedTimeEquals(actual, key)) throw Integrity($"BLTE {role} doesn't match the encoding key.");
    }

    private static BlteFormatException Invalid(string message) => new(FileReadState.Corrupt, "BLTE_INVALID", message);
    private static BlteFormatException Integrity(string message) => new(FileReadState.Corrupt, "BLTE_INTEGRITY", message);
    private static BlteFormatException Limit(string message) => new(FileReadState.Corrupt, "BLTE_LIMIT", message);
    private static BlteFormatException Unsupported(string message) => new(FileReadState.Unsupported, "BLTE_UNSUPPORTED", message);
    private static BlteFormatException KeyMismatch() =>
        new(FileReadState.WrongKey, "BLTE_KEY_MISMATCH", "A known TACT key didn't decrypt its BLTE chunk.");
}
