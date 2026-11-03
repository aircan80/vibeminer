using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

namespace VibeMiner.Objects;

/// <summary>
/// Pack format 1 (docs/STORAGE.md, layer 1). All integers little-endian. A pack is a 64-byte header followed by entries;
/// an entry is a 96-byte header, the payload exactly as served, then one CRC-32C per 64 KiB block of the payload.
/// </summary>
internal static class PackFormat
{
    public const ushort Version = 1;
    public const int PackHeaderBytes = 64;
    public const int EntryHeaderBytes = 96;
    public const int BlockShift = 16;
    public const int BlockBytes = 1 << BlockShift;
    public const long MaxPayloadBytes = 512L << 20;
    public static ReadOnlySpan<byte> PackMagic => "VMPK"u8;
    public static ReadOnlySpan<byte> EntryMagic => "VMOE"u8;
    public const byte TombstoneFlag = 1;

    public static int BlockCount(long length) => (int)((length + BlockBytes - 1) >> BlockShift);
    public static long TableBytes(long length) => 4L * BlockCount(length);
    public static long EntryBytes(long length) => EntryHeaderBytes + length + TableBytes(length);

    // ---- CRC-32C (Castagnoli), hardware accelerated through BitOperations where the CPU has it ----
    public static uint Crc32C(ReadOnlySpan<byte> data, uint crc = 0)
    {
        crc = ~crc;
        var words = MemoryMarshal.Cast<byte, ulong>(data);
        foreach (var w in words) crc = BitOperations.Crc32C(crc, BitConverter.IsLittleEndian ? w : BinaryPrimitives.ReverseEndianness(w));
        foreach (var b in data[(words.Length * 8)..]) crc = BitOperations.Crc32C(crc, b);
        return ~crc;
    }

    // ---- pack header ----
    // 0 magic "VMPK" | 4 u16 version | 6 u16 header bytes (64) | 8 u32 pack seq | 12 u32 store kind (0 Cdn, 1 Generated)
    // 16 store id (16) | 32 i64 created unix ms | 40..59 reserved zero | 60 u32 CRC-32C of 0..59
    public static void WritePackHeader(Span<byte> h, int seq, ReadOnlySpan<byte> storeId, long createdMs, ObjectStoreKind kind)
    {
        h[..PackHeaderBytes].Clear();
        PackMagic.CopyTo(h);
        BinaryPrimitives.WriteUInt16LittleEndian(h[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(h[6..], PackHeaderBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(h[8..], (uint)seq);
        BinaryPrimitives.WriteUInt32LittleEndian(h[12..], (uint)kind);
        storeId.CopyTo(h[16..]);
        BinaryPrimitives.WriteInt64LittleEndian(h[32..], createdMs);
        BinaryPrimitives.WriteUInt32LittleEndian(h[60..], Crc32C(h[..60]));
    }

    public enum HeaderCheck { Valid, Torn, Newer }

    /// <summary>An unknown kind at offset 12 is Newer; the caller checks the requested known kind.</summary>
    public static HeaderCheck ReadPackHeader(ReadOnlySpan<byte> h, out int seq, out byte[] storeId, out ObjectStoreKind kind)
    {
        seq = 0; storeId = []; kind = ObjectStoreKind.Cdn;
        if (h.Length < PackHeaderBytes || !h[..4].SequenceEqual(PackMagic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(h[60..]) != Crc32C(h[..60])) return HeaderCheck.Torn;
        if (BinaryPrimitives.ReadUInt16LittleEndian(h[4..]) != Version || BinaryPrimitives.ReadUInt16LittleEndian(h[6..]) != PackHeaderBytes ||
            BinaryPrimitives.ReadUInt32LittleEndian(h[12..]) > (uint)ObjectStoreKind.Library || h[40..60].IndexOfAnyExcept((byte)0) >= 0) return HeaderCheck.Newer;
        seq = (int)BinaryPrimitives.ReadUInt32LittleEndian(h[8..]);
        kind = (ObjectStoreKind)BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
        storeId = h.Slice(16, 16).ToArray();
        return HeaderCheck.Valid;
    }

    // ---- entry header ----
    // 0 magic "VMOE" | 4 u8 version | 5 u8 key type | 6 u8 namespace | 7 u8 flags (bit 0 tombstone)
    // 8 key (16) | 24 u64 payload length | 32 SHA-256 of payload (32) | 64 i64 stored unix ms | 72 u8 origin
    // 73 u8 block shift (16) | 74 u16 reserved | 76 u32 reserved | 80 u64 store sequence | 88 u32 reserved | 92 u32 CRC-32C of 0..91
    public static void WriteEntryHeader(Span<byte> h, in EntryHeader e)
    {
        h[..EntryHeaderBytes].Clear();
        EntryMagic.CopyTo(h);
        h[4] = (byte)Version; h[5] = (byte)e.Key.Type; h[6] = e.Key.Namespace; h[7] = e.Tombstone ? TombstoneFlag : (byte)0;
        e.Key.WriteKey(h[8..]);
        BinaryPrimitives.WriteUInt64LittleEndian(h[24..], (ulong)e.Length);
        e.Sha256.CopyTo(h[32..]);
        BinaryPrimitives.WriteInt64LittleEndian(h[64..], e.StoredAtMs);
        h[72] = (byte)e.Origin; h[73] = BlockShift;
        BinaryPrimitives.WriteUInt64LittleEndian(h[80..], e.Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(h[92..], Crc32C(h[..92]));
    }

    /// <summary>A key type the store's kind doesn't hold reads as Newer, so it is never truncated.</summary>
    public static HeaderCheck ReadEntryHeader(ReadOnlySpan<byte> h, ObjectStoreKind kind, out EntryHeader e)
    {
        e = default;
        if (h.Length < EntryHeaderBytes || !h[..4].SequenceEqual(EntryMagic) ||
            BinaryPrimitives.ReadUInt32LittleEndian(h[92..]) != Crc32C(h[..92])) return HeaderCheck.Torn;
        var flags = h[7];
        var length = BinaryPrimitives.ReadUInt64LittleEndian(h[24..]);
        // A checksummed header this reader doesn't fully understand came from a newer writer: never truncate it.
        if (h[4] != Version || (flags & ~TombstoneFlag) != 0 || h[73] != BlockShift || h[72] > (byte)ObjectOrigin.SharedStore ||
            !ObjectFormats.Known(kind, (ObjectKeyType)h[5]) ||
            BinaryPrimitives.ReadUInt16LittleEndian(h[74..]) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(h[76..]) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(h[88..]) != 0) return HeaderCheck.Newer;
        var tombstone = (flags & TombstoneFlag) != 0;
        if (length > MaxPayloadBytes || tombstone && length != 0 || !tombstone && length == 0) return HeaderCheck.Torn;
        e = new EntryHeader(new ObjectKey((ObjectKeyType)h[5], h.Slice(8, 16), h[6]), (long)length, h.Slice(32, 32).ToArray(),
            BinaryPrimitives.ReadInt64LittleEndian(h[64..]), (ObjectOrigin)h[72], BinaryPrimitives.ReadUInt64LittleEndian(h[80..]), tombstone);
        return HeaderCheck.Valid;
    }

    /// <summary>One CRC-32C per 64 KiB block of the payload, little-endian, the last block short.</summary>
    public static byte[] BlockTable(ReadOnlySpan<byte> payload)
    {
        var table = new byte[TableBytes(payload.Length)];
        for (var i = 0; i * (long)BlockBytes < payload.Length; i++)
        {
            var start = i * BlockBytes;
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(i * 4), Crc32C(payload.Slice(start, Math.Min(BlockBytes, payload.Length - start))));
        }
        return table;
    }
}

internal readonly record struct EntryHeader(ObjectKey Key, long Length, byte[] Sha256, long StoredAtMs, ObjectOrigin Origin, ulong Sequence, bool Tombstone)
{
    public long EntryBytes => PackFormat.EntryBytes(Length);
}
