using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace VibeMiner.Files.Formats;

/// <summary>What the first decoded bytes say about a file.</summary>
/// <param name="Format">The format the bytes prove (when <paramref name="Strong"/>) or hint at (weak MP3 frame sync); else Unknown.</param>
/// <param name="Strong">The bytes prove <paramref name="Format"/>.</param>
/// <param name="MverChunked">A valid MVER header followed by a second chunk id this code doesn't map: some chunked map or WMO file, kind unproved.</param>
internal readonly record struct ByteEvidence(FileFormat Format, bool Strong, bool MverChunked)
{
    public static ByteEvidence None => default;
}

/// <summary>
/// Pure-CPU format detection from at most the first <see cref="HeadBytes"/> decoded bytes, with the listfile name as the
/// fallback. A strong magic wins over the name; weak evidence never overrides or upgrades a name.
/// </summary>
internal static partial class FormatDetection
{
    /// <summary>How many leading decoded bytes detection looks at.</summary>
    public const int HeadBytes = 64;

    // Tags as the little-endian u32 of their bytes in file order.
    private const uint Wdbc = 0x43424457, Wdb2 = 0x32424457, Wdb3 = 0x33424457, Wdb4 = 0x34424457, Wdb5 = 0x35424457,
        Wdb6 = 0x36424457, Wdc1 = 0x31434457, Wdc2 = 0x32434457, Wdc3 = 0x33434457, Wdc4 = 0x34434457, Wdc5 = 0x35434457,
        Cls1 = 0x434C5331; // "1SLC"
    private const uint Blp1 = 0x31504C42, Blp2 = 0x32504C42, Md21 = 0x3132444D, Md20 = 0x3032444D, Skin = 0x4E494B53;
    private const uint Revm = 0x4D564552, Dhom = 0x4D4F4844, Pgom = 0x4D4F4750, Rdhm = 0x4D484452, Dhpm = 0x4D504844, Dhlm = 0x4D4C4844;
    private const uint OggS = 0x5367674F, Riff = 0x46464952, Wave = 0x45564157;
    private const uint SfntTrueType = 0x00000100, TrueTag = 0x65757274, Otto = 0x4F54544F, Ttcf = 0x66637474;

    private static readonly uint[] Db2Magics = [Wdbc, Wdb2, Wdb3, Wdb4, Wdb5, Wdb6, Wdc1, Wdc2, Wdc3, Wdc4, Cls1];

    private static ByteEvidence StrongOf(FileFormat format) => new(format, true, false);

    /// <summary>The evidence in <paramref name="head"/> (little-endian, offsets from the file start). Bytes past <see cref="HeadBytes"/> are ignored.</summary>
    public static ByteEvidence FromBytes(ReadOnlySpan<byte> head)
    {
        if (head.Length > HeadBytes) head = head[..HeadBytes];
        if (head.Length < 4) return ByteEvidence.None;

        var m0 = BinaryPrimitives.ReadUInt32LittleEndian(head);
        if (Array.IndexOf(Db2Magics, m0) >= 0) return StrongOf(FileFormat.Db2);
        if (m0 == Wdc5) return head.Length >= 8 && U32(head, 4) == 5 ? StrongOf(FileFormat.Db2) : ByteEvidence.None;

        if (m0 is Blp1 or Blp2) return StrongOf(FileFormat.Blp);
        if (m0 == Md21) return head.Length >= 12 && U32(head, 8) == Md20 ? StrongOf(FileFormat.M2Chunked) : ByteEvidence.None;
        if (m0 == Md20) return StrongOf(FileFormat.M2Legacy);
        if (m0 == Skin) return StrongOf(FileFormat.Skin);

        if (m0 == Revm)
        {
            if (head.Length < 16 || U32(head, 4) != 4) return ByteEvidence.None;
            var second = U32(head, 12);
            if (second == Dhom) return StrongOf(FileFormat.WmoRoot);
            if (second == Pgom) return StrongOf(FileFormat.WmoGroup);
            if (second == Rdhm) return StrongOf(FileFormat.AdtRoot);
            if (second == Dhpm) return StrongOf(FileFormat.Wdt);
            if (second == Dhlm) return StrongOf(FileFormat.AdtLod);
            return new(FileFormat.Unknown, false, true);
        }

        if (m0 == OggS) return head.Length >= 5 && head[4] == 0 ? StrongOf(FileFormat.Ogg) : ByteEvidence.None;
        if (m0 == Riff) return head.Length >= 12 && U32(head, 8) == Wave ? StrongOf(FileFormat.Wav) : ByteEvidence.None;

        if (head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3')
        {
            return head.Length >= 10 && head[3] is 2 or 3 or 4 && head[4] != 0xFF
                && head[6] < 0x80 && head[7] < 0x80 && head[8] < 0x80 && head[9] < 0x80
                ? StrongOf(FileFormat.Mp3) : ByteEvidence.None;
        }

        if (m0 == SfntTrueType) return IsSfntTableDirectory(head) ? StrongOf(FileFormat.Ttf) : ByteEvidence.None;
        if (m0 == TrueTag) return StrongOf(FileFormat.Ttf);
        if (m0 == Otto) return StrongOf(FileFormat.Otf);
        if (m0 == Ttcf) return StrongOf(FileFormat.Ttc);
        if (head.Length >= 8 && head[..8].SequenceEqual(PngSignature)) return StrongOf(FileFormat.Png);
        if (head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return StrongOf(FileFormat.Jpeg);

        if (IsMp3FrameSync(head)) return new(FileFormat.Mp3, false, false);
        return ByteEvidence.None;
    }

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// An sfnt table directory after version 00 01 00 00 (that tag alone is just u32 256): big-endian numTables at 4 is 1..64 and
    /// searchRange at 6 is 16 times the largest power of 2 not over numTables.
    /// </summary>
    private static bool IsSfntTableDirectory(ReadOnlySpan<byte> head)
    {
        if (head.Length < 8) return false;
        var numTables = BinaryPrimitives.ReadUInt16BigEndian(head[4..]);
        if (numTables is < 1 or > 64) return false;
        var searchRange = BinaryPrimitives.ReadUInt16BigEndian(head[6..]);
        return searchRange == 16 * (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)numTables + 1) / 2;
    }

    /// <summary>A bare MPEG audio Layer III frame header with a valid version, bitrate and sample rate.</summary>
    private static bool IsMp3FrameSync(ReadOnlySpan<byte> head)
    {
        byte b0 = head[0], b1 = head[1], b2 = head[2];
        return b0 == 0xFF && (b1 & 0xE0) == 0xE0
            && ((b1 >> 3) & 3) != 1
            && ((b1 >> 1) & 3) == 1
            && (b2 >> 4) is not (0 or 15)
            && ((b2 >> 2) & 3) != 3;
    }

    /// <summary>
    /// The format the listfile name's extension says (after the last '.' of the file name, any case; slash direction doesn't
    /// matter), or Unknown. A bare .m2 name is <see cref="FileFormat.M2"/>: only the bytes tell chunked from legacy.
    /// </summary>
    public static FileFormat FromName(string? path) => FromName(path.AsSpan());

    /// <inheritdoc cref="FromName(string?)"/>
    public static FileFormat FromName(ReadOnlySpan<char> path)
    {
        var name = path[(path.LastIndexOfAny('/', '\\') + 1)..];
        var dot = name.LastIndexOf('.');
        if (dot < 0) return FileFormat.Unknown;
        var ext = name[(dot + 1)..];
        if (Is(ext, "db2") || Is(ext, "dbc")) return FileFormat.Db2;
        if (Is(ext, "blp")) return FileFormat.Blp;
        if (Is(ext, "m2")) return FileFormat.M2;
        if (Is(ext, "skin")) return FileFormat.Skin;
        if (Is(ext, "anim")) return FileFormat.Anim;
        if (Is(ext, "bone")) return FileFormat.Bone;
        if (Is(ext, "phys")) return FileFormat.Phys;
        if (Is(ext, "skel")) return FileFormat.Skel;
        if (Is(ext, "tex")) return FileFormat.Tex;
        if (Is(ext, "wmo")) return WmoGroupName().IsMatch(name) ? FileFormat.WmoGroup : FileFormat.WmoRoot;
        if (Is(ext, "adt"))
        {
            return AdtTexName().IsMatch(name) ? FileFormat.AdtTex
                : AdtObjName().IsMatch(name) ? FileFormat.AdtObj
                : AdtLodName().IsMatch(name) ? FileFormat.AdtLod
                : FileFormat.AdtRoot;
        }
        if (Is(ext, "wdt")) return WdtSplitName().IsMatch(name) ? FileFormat.WdtSplit : FileFormat.Wdt;
        if (Is(ext, "wdl")) return FileFormat.Wdl;
        if (Is(ext, "bls")) return FileFormat.Bls;
        if (Is(ext, "wwf")) return FileFormat.Wwf;
        if (Is(ext, "ogg")) return FileFormat.Ogg;
        if (Is(ext, "mp3")) return FileFormat.Mp3;
        if (Is(ext, "wav")) return FileFormat.Wav;
        if (Is(ext, "ttf")) return FileFormat.Ttf;
        if (Is(ext, "otf")) return FileFormat.Otf;
        if (Is(ext, "ttc")) return FileFormat.Ttc;
        if (Is(ext, "png")) return FileFormat.Png;
        if (Is(ext, "jpg") || Is(ext, "jpeg")) return FileFormat.Jpeg;
        if (Is(ext, "lua")) return FileFormat.Lua;
        if (Is(ext, "xml") || Is(ext, "xsd")) return FileFormat.Xml;
        if (Is(ext, "toc")) return FileFormat.Toc;
        if (Is(ext, "txt")) return FileFormat.Txt;
        if (Is(ext, "html") || Is(ext, "htm")) return FileFormat.Html;
        return FileFormat.Unknown;
    }

    private static bool Is(ReadOnlySpan<char> extension, string wanted) => extension.Equals(wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The file's format and what said so. <paramref name="head"/> is null when the bytes weren't read (not stored, an encrypted
    /// first chunk without its key, missing, corrupt); magic is never matched then. A strong magic wins even when the name
    /// disagrees. Weak evidence (a bare MP3 frame sync, an MVER header with an unmapped second chunk) never proves a kind, so it
    /// neither overrides nor upgrades the name: the result is then the name's format from the extension, or Unknown.
    /// </summary>
    public static (FileFormat Format, FileFormatSource Source) Detect(ReadOnlyMemory<byte>? head, string? path)
    {
        if (head is { } bytes && FromBytes(bytes.Span) is { Strong: true } evidence) return (evidence.Format, FileFormatSource.Magic);
        var named = FromName(path);
        return named == FileFormat.Unknown ? (FileFormat.Unknown, FileFormatSource.None) : (named, FileFormatSource.Extension);
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    [GeneratedRegex(@"_\d{3}(_lod\d)?\.wmo$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WmoGroupName();

    [GeneratedRegex(@"_tex\d+\.adt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdtTexName();

    [GeneratedRegex(@"_obj\d+\.adt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdtObjName();

    [GeneratedRegex(@"_lod\.adt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AdtLodName();

    [GeneratedRegex(@"_(occ|lgt|fogs|mpv|preload)\.wdt$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WdtSplitName();
}
