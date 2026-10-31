using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Objects;

namespace VibeMiner.Objects.Testing;

/// <summary>Authored synthetic objects that pass their key type's checks. Random bytes only; no game content.</summary>
public static class Synthetic
{
    public static byte[] Random(int seed, int length) { var b = new byte[length]; new Random(seed).NextBytes(b); return b; }

    /// <summary>A headerless BLTE object of exactly <paramref name="length"/> bytes (at least 9).</summary>
    public static PutObject Headerless(int seed, int length = 200)
    {
        var b = Random(seed, length);
        "BLTE"u8.CopyTo(b); b.AsSpan(4, 4).Clear(); b[8] = (byte)'N';
        return new(new ObjectKey(ObjectKeyType.Encoded, MD5.HashData(b)), b.Length, b);
    }

    /// <summary>A chunked BLTE object: header, then chunks of the given encoded sizes (each starting with mode 'N').</summary>
    public static PutObject Chunked(int seed, params int[] chunkSizes)
    {
        var headerLength = 12 + 24 * chunkSizes.Length;
        var b = new byte[headerLength + chunkSizes.Sum()];
        new Random(seed).NextBytes(b.AsSpan(headerLength));
        "BLTE"u8.CopyTo(b);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)headerLength);
        b[8] = 0x0F; b[9] = (byte)(chunkSizes.Length >> 16); b[10] = (byte)(chunkSizes.Length >> 8); b[11] = (byte)chunkSizes.Length;
        var at = headerLength;
        for (var i = 0; i < chunkSizes.Length; i++)
        {
            b[at] = (byte)'N';
            var d = b.AsSpan(12 + i * 24);
            BinaryPrimitives.WriteUInt32BigEndian(d, (uint)chunkSizes[i]);
            BinaryPrimitives.WriteUInt32BigEndian(d[4..], (uint)(chunkSizes[i] - 1));
            MD5.HashData(b.AsSpan(at, chunkSizes[i])).CopyTo(d[8..]);
            at += chunkSizes[i];
        }
        return new(new ObjectKey(ObjectKeyType.Encoded, MD5.HashData(b.AsSpan(0, headerLength))), b.Length, b);
    }

    public static PutObject Config(int seed, int length = 300)
    {
        // The seed is in the text, so two seeds never make the same config (and so the same key).
        var head = $"# synthetic {seed}\n";
        var b = System.Text.Encoding.ASCII.GetBytes(head + string.Concat(Enumerable.Range(0, Math.Max(0, length - head.Length)).Select(i => (char)('a' + (seed + i) % 26))));
        return new(new ObjectKey(ObjectKeyType.Config, MD5.HashData(b)), b.Length, b);
    }

    /// <summary>A native archive index (offset width 4) or loose file-index (offset width 0) over random sorted keys.</summary>
    public static PutObject Index(int seed, int entries, int offsetWidth = 4)
    {
        var rng = new Random(seed);
        var keys = Enumerable.Range(0, entries).Select(_ => { var k = new byte[16]; rng.NextBytes(k); k[0] |= 1; return k; })
            .OrderBy(k => Convert.ToHexString(k), StringComparer.Ordinal).ToArray();
        var record = 20 + offsetWidth; var perPage = 4096 / record; var pages = (entries + perPage - 1) / perPage;
        var b = new byte[pages * 4096 + pages * 24 + 28];
        for (var i = 0; i < entries; i++)
        {
            var at = i / perPage * 4096 + i % perPage * record;
            keys[i].CopyTo(b, at);
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at + 16), (uint)(100 + i));
            if (offsetWidth == 4) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at + 20), (uint)(i * 1000));
        }
        var toc = pages * 4096;
        for (var p = 0; p < pages; p++)
        {
            keys[Math.Min(entries, (p + 1) * perPage) - 1].CopyTo(b, toc + p * 16);
            MD5.HashData(b.AsSpan(p * 4096, 4096)).AsSpan(0, 8).CopyTo(b.AsSpan(toc + pages * 16 + p * 8));
        }
        var footer = b.AsSpan(b.Length - 28);
        MD5.HashData(b.AsSpan(toc, pages * 24)).AsSpan(0, 8).CopyTo(footer);
        footer[8] = 1; footer[11] = 4; footer[12] = (byte)offsetWidth; footer[13] = 4; footer[14] = 16; footer[15] = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(footer[16..], (uint)entries);
        MD5.HashData(footer[8..]).AsSpan(0, 8).CopyTo(footer[20..]);   // checksum bytes are still zero here
        return new(new ObjectKey(offsetWidth == 0 ? ObjectKeyType.FileIndex : ObjectKeyType.ArchiveIndex, MD5.HashData(footer)), b.Length, b);
    }

    public static HistoryObservation Observation(int seed, string product = "wow", string region = "us", HistoryKind kind = HistoryKind.Versions, DateTimeOffset? at = null) =>
        new(kind, product, region, at ?? DateTimeOffset.FromUnixTimeMilliseconds(1_790_000_000_000 + seed * 1000L),
            $"https://{region}.version.battle.net/v2/products/{product}/{kind.ToString().ToLowerInvariant()}",
            System.Text.Encoding.UTF8.GetBytes($"Region!STRING:0|BuildConfig!HEX:16\n## seqn = {seed}\n{region}|{seed:x32}\n"));
}
