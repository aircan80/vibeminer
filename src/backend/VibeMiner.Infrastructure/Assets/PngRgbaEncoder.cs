using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>Original PNG subset: RGBA8, filter0, no interlace/color conversion. W3C PNG3 2025-06-24;
/// RFC1950 zlib is supplied by the platform. vmPR is private ancillary, unsafe to copy after edits.</summary>
internal static class PngRgbaEncoder
{
    internal const string Revision = "png-rgba8-filter0-v1";
    internal const int MaxProofBytes = 8192, IdatBytes = 65536;
    private static readonly byte[] Signature = [137,80,78,71,13,10,26,10];
    private static readonly uint[] CrcTable = MakeCrcTable();
    private static DiscoveryException Invalid(string message) => new("ASSET_PNG_INVALID", message);
    private static DiscoveryException Integrity(string message) => new("ASSET_PNG_INTEGRITY", message);

    internal static async Task WriteAsync(Stream output, int width, int height, ReadOnlyMemory<byte> rgba,
        ReadOnlyMemory<byte> proof, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(output); token.ThrowIfCancellationRequested();
        if (width is < 1 or > IconArtifacts.MaxDimension || height is < 1 or > IconArtifacts.MaxDimension
            || width * (long)height > IconArtifacts.MaxPixels || rgba.Length != width * (long)height * 4
            || proof.Length is < 1 or > MaxProofBytes) throw Invalid("PNG dimensions/pixels/proof exceed the admitted exact geometry.");
        var counted = new BoundedOutput(output); await counted.WriteAsync(Signature, token);
        var ihdr = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width); BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height); ihdr[8] = 8; ihdr[9] = 6;
        await WriteChunkAsync(counted, "IHDR", ihdr, token); await WriteChunkAsync(counted, "vmPR", proof, token);
        var idat = new IdatOutput(counted);
        await using (var zlib = new ZLibStream(idat, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var filter = new byte[1]; var stride = width * 4;
            for (var row = 0; row < height; row++)
            {
                token.ThrowIfCancellationRequested(); await zlib.WriteAsync(filter, token); await zlib.WriteAsync(rgba.Slice(row * stride, stride), token);
                if ((row & 31) == 31) await Task.Yield();
            }
        }
        await idat.CompleteAsync(token); await WriteChunkAsync(counted, "IEND", ReadOnlyMemory<byte>.Empty, token);
    }

    internal static async Task ValidateAsync(Stream input, IconPngProof proof, ReadOnlyMemory<byte> expectedProof, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!input.CanRead || !input.CanSeek || input.Length is < 45 or > IconArtifacts.MaxPngBytes || expectedProof.Length is < 1 or > MaxProofBytes)
            throw Invalid("PNG source/proof exceeds its bounded framing contract.");
        input.Position = 0; var length = input.Length;
        if (!(await ReadAsync(input, 8, token)).AsSpan().SequenceEqual(Signature)) throw Invalid("PNG signature is invalid.");
        var header = await ReadChunkAsync(input, 13, token);
        if (header.Type != "IHDR" || header.Bytes.Length != 13 || BinaryPrimitives.ReadUInt32BigEndian(header.Bytes) != proof.Width
            || BinaryPrimitives.ReadUInt32BigEndian(header.Bytes.AsSpan(4)) != proof.Height || header.Bytes[8] != 8 || header.Bytes[9] != 6
            || header.Bytes[10] != 0 || header.Bytes[11] != 0 || header.Bytes[12] != 0)
            throw Invalid("PNG IHDR differs from the exact RGBA8 icon proof.");
        var metadata = await ReadChunkAsync(input, MaxProofBytes, token);
        if (metadata.Type != "vmPR" || !metadata.Bytes.AsSpan().SequenceEqual(expectedProof.Span))
            throw Integrity("PNG embedded proof differs from its engine-owned source/interpretation binding.");
        using var compressed = new MemoryStream(); var count = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested(); var chunk = await ReadChunkAsync(input, IdatBytes, token);
            if (chunk.Type == "IEND") { if (chunk.Bytes.Length != 0 || count == 0) throw Invalid("PNG IEND framing is invalid."); break; }
            if (chunk.Type != "IDAT" || chunk.Bytes.Length == 0 || ++count > 128 || chunk.Bytes.Length > IconArtifacts.MaxPngBytes - compressed.Length)
                throw Invalid("PNG has unsupported chunks, empty IDAT or excessive compressed data.");
            await compressed.WriteAsync(chunk.Bytes, token);
        }
        if (await input.ReadAsync(new byte[1], token) != 0 || input.Length != length) throw Invalid("PNG has trailing bytes or changed source length.");
        using var zinput = new ExactZlibInput(compressed.ToArray(), token); using var inflater = new ZLibStream(zinput, CompressionMode.Decompress, leaveOpen: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var rowBytes = new byte[checked(proof.Width * 4 + 1)];
        try
        {
            for (var row = 0; row < proof.Height; row++)
            {
                token.ThrowIfCancellationRequested(); inflater.ReadExactly(rowBytes);
                if (rowBytes[0] != 0) throw Invalid("PNG filter differs from its admitted deterministic encoder policy.");
                hash.AppendData(rowBytes, 1, rowBytes.Length - 1); if ((row & 31) == 31) await Task.Yield();
            }
            if (inflater.ReadByte() != -1 || zinput.ReadPastEnd || zinput.Position != compressed.Length)
                throw Invalid("PNG zlib has incorrect output length, truncation or trailing member bytes.");
        }
        catch (InvalidDataException) { throw Invalid("PNG zlib data is invalid."); }
        catch (EndOfStreamException) { throw Invalid("PNG pixels ended before their exact declared geometry."); }
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != proof.RgbaSha256) throw Integrity("PNG pixels differ from their authenticated RGBA digest.");
    }

    private static async Task<(string Type, byte[] Bytes)> ReadChunkAsync(Stream source, int bound, CancellationToken token)
    {
        var header = await ReadAsync(source, 8, token); var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > bound || length > source.Length - source.Position - 4) throw Invalid("PNG chunk exceeds its exact bounded source span.");
        var bytes = await ReadAsync(source, (int)length, token); var crc = await ReadAsync(source, 4, token);
        if (BinaryPrimitives.ReadUInt32BigEndian(crc) != Crc(header.AsSpan(4,4), bytes, token)) throw Integrity("PNG chunk CRC does not match.");
        return (Encoding.ASCII.GetString(header, 4, 4), bytes);
    }
    private static async Task<byte[]> ReadAsync(Stream source, int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (count < 0 || count > source.Length - source.Position) throw Invalid("PNG bytes are truncated.");
        var result = new byte[count]; try { await source.ReadExactlyAsync(result, token); } catch (EndOfStreamException) { throw Invalid("PNG bytes ended unexpectedly."); } return result;
    }
    private static async Task WriteChunkAsync(Stream output, string name, ReadOnlyMemory<byte> data, CancellationToken token)
    {
        var type = Encoding.ASCII.GetBytes(name); var word = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(word, (uint)data.Length);
        await output.WriteAsync(word, token); await output.WriteAsync(type, token); await output.WriteAsync(data, token);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc(type, data.Span, token)); await output.WriteAsync(word, token);
    }
    private static uint Crc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data, CancellationToken token)
    {
        uint crc = uint.MaxValue; foreach (var value in type) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        for (var index = 0; index < data.Length; index++) { if ((index & 4095) == 0) token.ThrowIfCancellationRequested(); crc = CrcTable[(crc ^ data[index]) & 255] ^ (crc >> 8); }
        return ~crc;
    }
    private static uint[] MakeCrcTable()
    {
        var table = new uint[256]; for (uint index = 0; index < table.Length; index++) { var value = index; for (var bit = 0; bit < 8; bit++) value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xEDB88320u; table[index] = value; } return table;
    }
    private sealed class BoundedOutput(Stream target) : Stream
    {
        private long written;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); if (buffer.Length > IconArtifacts.MaxPngBytes - written) throw new DiscoveryException("ASSET_LIMIT", "PNG output exceeds its byte budget."); await target.WriteAsync(buffer, cancellationToken); written += buffer.Length; }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => written; public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class IdatOutput(Stream target) : Stream
    {
        private readonly byte[] buffer = new byte[IdatBytes]; private int used;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            while (!bytes.IsEmpty) { cancellationToken.ThrowIfCancellationRequested(); var count = Math.Min(bytes.Length, buffer.Length - used); bytes[..count].CopyTo(buffer.AsMemory(used)); used += count; bytes = bytes[count..]; if (used == buffer.Length) await CompleteAsync(cancellationToken); }
        }
        internal async Task CompleteAsync(CancellationToken token) { if (used != 0) { await WriteChunkAsync(target, "IDAT", buffer.AsMemory(0, used), token); used = 0; } }
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] bytes, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] bytes, int offset, int count) => throw new NotSupportedException();
    }
    // Bound framework inflater read-ahead: a valid member must consume its final checksum byte;
    // extra bytes finish before that byte and truncated members ask to read past physical EOF.
    private sealed class ExactZlibInput(byte[] bytes, CancellationToken token) : Stream
    {
        private int offset; internal bool ReadPastEnd;
        public override int Read(byte[] buffer, int start, int count) => Read(buffer.AsSpan(start, count));
        public override int Read(Span<byte> buffer)
        {
            token.ThrowIfCancellationRequested(); if (buffer.Length == 0) return 0; if (offset == bytes.Length) { ReadPastEnd = true; return 0; }
            var count = Math.Min(buffer.Length, offset == bytes.Length - 1 ? 1 : bytes.Length - offset - 1); bytes.AsSpan(offset, count).CopyTo(buffer); offset += count; return count;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => bytes.Length; public override long Position { get => offset; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long value, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int start, int count) => throw new NotSupportedException();
    }
}
