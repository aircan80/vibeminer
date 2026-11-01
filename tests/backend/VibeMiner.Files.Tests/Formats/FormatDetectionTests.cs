using System.Text;
using VibeMiner.Files.Formats;
using Xunit;

namespace VibeMiner.Files.Tests.Formats;

/// <summary>Format detection over authored header bytes (a few bytes each, never real game files) and listfile names.</summary>
public sealed class FormatDetectionTests
{
    /// <summary>A 64-byte head: <paramref name="parts"/> laid out from offset 0, the rest zero.</summary>
    private static byte[] Head(params object[] parts)
    {
        var bytes = new List<byte>();
        foreach (var part in parts)
        {
            switch (part)
            {
                case string ascii: bytes.AddRange(Encoding.ASCII.GetBytes(ascii)); break;
                case uint u32: bytes.AddRange(BitConverter.GetBytes(u32)); break;
                case byte[] raw: bytes.AddRange(raw); break;
                default: throw new ArgumentException("Unsupported part.", nameof(parts));
            }
        }
        while (bytes.Count < FormatDetection.HeadBytes) bytes.Add(0);
        return [.. bytes];
    }

    private static readonly byte[] Mp3Sync = [0xFF, 0xFB, 0x90, 0x64]; // MPEG-1 Layer III, 128 kbit/s, 44.1 kHz

    public static TheoryData<byte[], FileFormat> StrongHeads => new()
    {
        { Head("WDBC"), FileFormat.Db2 },
        { Head("WDB2"), FileFormat.Db2 },
        { Head("WDB3"), FileFormat.Db2 },
        { Head("WDB4"), FileFormat.Db2 },
        { Head("WDB5"), FileFormat.Db2 },
        { Head("WDB6"), FileFormat.Db2 },
        { Head("WDC1"), FileFormat.Db2 },
        { Head("WDC2"), FileFormat.Db2 },
        { Head("WDC3"), FileFormat.Db2 },
        { Head("WDC4"), FileFormat.Db2 },
        { Head("WDC5", 5u), FileFormat.Db2 },
        { Head("1SLC"), FileFormat.Db2 },
        { Head("BLP1"), FileFormat.Blp },
        { Head("BLP2"), FileFormat.Blp },
        { Head("MD21", 100u, "MD20"), FileFormat.M2Chunked },
        { Head("MD20"), FileFormat.M2Legacy },
        { Head("SKIN"), FileFormat.Skin },
        { Head("REVM", 4u, 17u, "DHOM"), FileFormat.WmoRoot },
        { Head("REVM", 4u, 17u, "PGOM"), FileFormat.WmoGroup },
        { Head("REVM", 4u, 18u, "RDHM"), FileFormat.AdtRoot },
        { Head("REVM", 4u, 18u, "DHPM"), FileFormat.Wdt },
        { Head("REVM", 4u, 18u, "DHLM"), FileFormat.AdtLod },
        { Head("OggS", new byte[] { 0 }), FileFormat.Ogg },
        { Head("RIFF", 36u, "WAVE"), FileFormat.Wav },
        { Head("ID3", new byte[] { 3, 0, 0, 0, 0, 0x10, 0x7F }), FileFormat.Mp3 },
        { Head("ID3", new byte[] { 2 }), FileFormat.Mp3 },
        { Head("ID3", new byte[] { 4 }), FileFormat.Mp3 },
        { Head(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x10 }), FileFormat.Ttf },  // 1 table, searchRange 16
        { Head(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x20 }), FileFormat.Ttf },  // 2 tables, searchRange 32
        { Head(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x03, 0x00, 0x20 }), FileFormat.Ttf },  // 3 tables, searchRange 32
        { Head(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x11, 0x01, 0x00 }), FileFormat.Ttf },  // 17 tables, searchRange 256
        { Head(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x40, 0x04, 0x00 }), FileFormat.Ttf },  // 64 tables, searchRange 1024
        { Head("true"), FileFormat.Ttf },
        { Head("OTTO"), FileFormat.Otf },
        { Head("ttcf"), FileFormat.Ttc },
        { Head(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }), FileFormat.Png },
        { Head(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }), FileFormat.Jpeg },
    };

    [Theory]
    [MemberData(nameof(StrongHeads))]
    public void A_magic_is_strong(byte[] head, FileFormat expected)
    {
        Assert.Equal(new ByteEvidence(expected, true, false), FormatDetection.FromBytes(head));
        Assert.Equal((expected, FileFormatSource.Magic), FormatDetection.Detect(head, null));
    }

    public static TheoryData<byte[]> NoEvidenceHeads => new()
    {
        Head("WDC5", 4u),
        Head("WDC5", 6u),
        Head("MD21", 100u, "MD2X"),
        Head("REVM", 5u, 18u, "RDHM"),
        Head("OggS", new byte[] { 1 }),
        Head("RIFF", 36u, "AVI "),
        Head("ID3", new byte[] { 5 }),
        Head("ID3", new byte[] { 3, 0xFF }),
        Head("ID3", new byte[] { 3, 0, 0, 0x80 }),
        Head("ID3", new byte[] { 3, 0, 0, 0, 0, 0, 0x80 }),
        Head(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0B }),
        Head(new byte[] { 0xFF, 0xD8, 0xFE }),
        Head(new byte[] { 0x00, 0x01, 0x00, 0x00 }),                                      // 0 tables
        Head(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x41, 0x04, 0x00 }),              // 65 tables
        Head(new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x11, 0x01, 0x10 }),              // wrong searchRange
        new byte[] { 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00 },                          // too short
        Head("wdc3"),
        Head("ANIM"),
        Head("-- lua source"),
        Head("<?xml version"),
        new byte[FormatDetection.HeadBytes],
        new byte[3],
    };

    [Theory]
    [MemberData(nameof(NoEvidenceHeads))]
    public void Near_misses_and_unchecked_formats_are_no_evidence(byte[] head)
    {
        Assert.Equal(ByteEvidence.None, FormatDetection.FromBytes(head));
        Assert.Equal((FileFormat.Unknown, FileFormatSource.None), FormatDetection.Detect(head, null));
    }

    [Fact]
    public void Heads_shorter_than_their_magic_needs_are_no_evidence()
    {
        Assert.Equal(ByteEvidence.None, FormatDetection.FromBytes([]));
        Assert.Equal(ByteEvidence.None, FormatDetection.FromBytes("BLP"u8));
        Assert.Equal(ByteEvidence.None, FormatDetection.FromBytes("WDC5\x05\0\0"u8));
        Assert.Equal(ByteEvidence.None, FormatDetection.FromBytes("MD21\0\0\0\0MD2"u8));
        Assert.Equal(ByteEvidence.None, FormatDetection.FromBytes("REVM\x04\0\0\0\0\0\0\0DHO"u8));
        Assert.Equal(ByteEvidence.None, FormatDetection.FromBytes("OggS"u8));
        Assert.Equal(ByteEvidence.None, FormatDetection.FromBytes("RIFF\0\0\0\0WAV"u8));
        Assert.Equal(ByteEvidence.None, FormatDetection.FromBytes("ID3\x03\0\0\0\0\0"u8));
        Assert.Equal(ByteEvidence.None, FormatDetection.FromBytes([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A]));
        Assert.Equal(new ByteEvidence(FileFormat.Blp, true, false), FormatDetection.FromBytes("BLP2"u8));
    }

    [Fact]
    public void Bytes_past_the_head_are_ignored()
    {
        var head = new byte[FormatDetection.HeadBytes + 16];
        "REVM\x04\0\0\0"u8.CopyTo(head);
        "DHOM"u8.CopyTo(head.AsSpan(12));
        Assert.True(FormatDetection.FromBytes(head).Strong);
        Assert.Equal(ByteEvidence.None, FormatDetection.FromBytes(new byte[200]));
    }

    [Fact]
    public void Mver_with_an_unmapped_second_chunk_is_weak()
    {
        Assert.Equal(new ByteEvidence(FileFormat.Unknown, false, true), FormatDetection.FromBytes(Head("REVM", 4u, 18u, "XETM")));
    }

    [Fact]
    public void A_bare_mp3_frame_sync_is_weak()
    {
        Assert.Equal(new ByteEvidence(FileFormat.Mp3, false, false), FormatDetection.FromBytes(Head(Mp3Sync)));
        Assert.Equal(new ByteEvidence(FileFormat.Mp3, false, false), FormatDetection.FromBytes(Head(new byte[] { 0xFF, 0xF3, 0x50 }))); // MPEG-2
        Assert.Equal(new ByteEvidence(FileFormat.Mp3, false, false), FormatDetection.FromBytes(Head(new byte[] { 0xFF, 0xE3, 0x50 }))); // MPEG-2.5
    }

    [Theory]
    [InlineData(0xFF, 0xEB, 0x90)] // reserved version
    [InlineData(0xFF, 0xFD, 0x90)] // Layer II
    [InlineData(0xFF, 0xFF, 0x90)] // Layer I
    [InlineData(0xFF, 0xF9, 0x90)] // reserved layer
    [InlineData(0xFF, 0xFB, 0x00)] // free bitrate
    [InlineData(0xFF, 0xFB, 0xF0)] // bad bitrate
    [InlineData(0xFF, 0xFB, 0x9C)] // reserved sample rate
    [InlineData(0xFF, 0xDB, 0x90)] // sync short of 11 bits
    [InlineData(0xFE, 0xFB, 0x90)]
    public void A_broken_mp3_frame_header_is_no_evidence(byte b0, byte b1, byte b2)
    {
        Assert.Equal(ByteEvidence.None, FormatDetection.FromBytes(Head(new[] { b0, b1, b2, (byte)0 })));
    }

    [Theory]
    [InlineData("dbfilesclient/map.db2", FileFormat.Db2)]
    [InlineData("DBFilesClient\\Map.DBC", FileFormat.Db2)]
    [InlineData("textures/a.blp", FileFormat.Blp)]
    [InlineData("creature/a/a.m2", FileFormat.M2)]
    [InlineData("Creature\\A\\A.M2", FileFormat.M2)]
    [InlineData("creature/a/a00.skin", FileFormat.Skin)]
    [InlineData("creature/a/a0060-00.anim", FileFormat.Anim)]
    [InlineData("creature/a/a_01.bone", FileFormat.Bone)]
    [InlineData("creature/a/a.phys", FileFormat.Phys)]
    [InlineData("creature/a/a.skel", FileFormat.Skel)]
    [InlineData("world/maptextures/a/a_1_2.tex", FileFormat.Tex)]
    [InlineData("world/wmo/a/a.wmo", FileFormat.WmoRoot)]
    [InlineData("world/wmo/a/a_000.wmo", FileFormat.WmoGroup)]
    [InlineData("WORLD\\WMO\\A\\A_012_LOD1.WMO", FileFormat.WmoGroup)]
    [InlineData("world/wmo/a/a_12.wmo", FileFormat.WmoRoot)]
    [InlineData("world/wmo/a/a_0123.wmo", FileFormat.WmoRoot)]
    [InlineData("world/wmo/a/a_000_lod.wmo", FileFormat.WmoRoot)]
    [InlineData("world/maps/a/a_30_40.adt", FileFormat.AdtRoot)]
    [InlineData("world/maps/a/a_30_40_tex0.adt", FileFormat.AdtTex)]
    [InlineData("World\\Maps\\A\\A_30_40_TEX1.ADT", FileFormat.AdtTex)]
    [InlineData("world/maps/a/a_30_40_obj0.adt", FileFormat.AdtObj)]
    [InlineData("world/maps/a/a_30_40_obj1.adt", FileFormat.AdtObj)]
    [InlineData("world/maps/a/a_30_40_lod.adt", FileFormat.AdtLod)]
    [InlineData("world/maps/a/a_30_40_tex.adt", FileFormat.AdtRoot)]
    [InlineData("world/maps/a/a.wdt", FileFormat.Wdt)]
    [InlineData("world/maps/a/a_occ.wdt", FileFormat.WdtSplit)]
    [InlineData("world/maps/a/a_lgt.wdt", FileFormat.WdtSplit)]
    [InlineData("world/maps/a/a_fogs.wdt", FileFormat.WdtSplit)]
    [InlineData("world/maps/a/a_mpv.wdt", FileFormat.WdtSplit)]
    [InlineData("World\\Maps\\A\\A_PRELOAD.WDT", FileFormat.WdtSplit)]
    [InlineData("world/maps/a/a_other.wdt", FileFormat.Wdt)]
    [InlineData("world/maps/a/a.wdl", FileFormat.Wdl)]
    [InlineData("shaders/pixel/a.bls", FileFormat.Bls)]
    [InlineData("a/b.wwf", FileFormat.Wwf)]
    [InlineData("sound/a.ogg", FileFormat.Ogg)]
    [InlineData("sound/a.MP3", FileFormat.Mp3)]
    [InlineData("sound/a.wav", FileFormat.Wav)]
    [InlineData("fonts/a.ttf", FileFormat.Ttf)]
    [InlineData("fonts/a.otf", FileFormat.Otf)]
    [InlineData("fonts/a.ttc", FileFormat.Ttc)]
    [InlineData("interface/a.png", FileFormat.Png)]
    [InlineData("interface/a.jpg", FileFormat.Jpeg)]
    [InlineData("interface/a.jpeg", FileFormat.Jpeg)]
    [InlineData("interface/a.lua", FileFormat.Lua)]
    [InlineData("interface/a.xml", FileFormat.Xml)]
    [InlineData("interface/a.xsd", FileFormat.Xml)]
    [InlineData("interface/a.toc", FileFormat.Toc)]
    [InlineData("a.txt", FileFormat.Txt)]
    [InlineData("a.html", FileFormat.Html)]
    [InlineData("a.HTM", FileFormat.Html)]
    [InlineData("a.unknown", FileFormat.Unknown)]
    [InlineData("noextension", FileFormat.Unknown)]
    [InlineData("dir.blp/noextension", FileFormat.Unknown)]
    [InlineData("dir.blp\\noextension", FileFormat.Unknown)]
    [InlineData("a.", FileFormat.Unknown)]
    [InlineData("", FileFormat.Unknown)]
    [InlineData(null, FileFormat.Unknown)]
    public void The_name_maps_to_a_format(string? path, FileFormat expected)
    {
        Assert.Equal(expected, FormatDetection.FromName(path));
        var source = expected == FileFormat.Unknown ? FileFormatSource.None : FileFormatSource.Extension;
        Assert.Equal((expected, source), FormatDetection.Detect(null, path));
    }

    [Fact]
    public void A_strong_magic_beats_a_wrong_name()
    {
        Assert.Equal((FileFormat.Blp, FileFormatSource.Magic), FormatDetection.Detect(Head("BLP2"), "sound/a.ogg"));
        Assert.Equal((FileFormat.WmoGroup, FileFormatSource.Magic), FormatDetection.Detect(Head("REVM", 4u, 17u, "PGOM"), "world/wmo/a/a.wmo"));
        Assert.Equal((FileFormat.M2Legacy, FileFormatSource.Magic), FormatDetection.Detect(Head("MD20"), "creature/a/a.m2"));
        Assert.Equal((FileFormat.M2Chunked, FileFormatSource.Magic), FormatDetection.Detect(Head("MD21", 100u, "MD20"), "creature/a/a.m2"));
    }

    [Fact]
    public void An_m2_name_without_byte_proof_is_m2()
    {
        Assert.Equal((FileFormat.M2, FileFormatSource.Extension), FormatDetection.Detect(null, "creature/a/a.m2"));
        // MD21 without MD20 at 8 proves nothing.
        Assert.Equal((FileFormat.M2, FileFormatSource.Extension), FormatDetection.Detect(Head("MD21", 100u, "XXXX"), "creature/a/a.m2"));
    }

    [Theory]
    [InlineData("sound/a.ogg", FileFormat.Ogg)]
    [InlineData("a.OgG", FileFormat.Ogg)]
    [InlineData("dir.blp/noext", FileFormat.Unknown)]
    [InlineData("a.", FileFormat.Unknown)]
    [InlineData("", FileFormat.Unknown)]
    public void The_span_overload_matches_the_string_one(string path, FileFormat expected)
    {
        Assert.Equal(expected, FormatDetection.FromName(path.AsSpan()));
        Assert.Equal(expected, FormatDetection.FromName(path));
    }

    [Fact]
    public void Weak_evidence_never_beats_or_upgrades_a_name()
    {
        var mp3 = Head(Mp3Sync);
        Assert.Equal((FileFormat.Mp3, FileFormatSource.Extension), FormatDetection.Detect(mp3, "sound/a.mp3"));
        Assert.Equal((FileFormat.Ogg, FileFormatSource.Extension), FormatDetection.Detect(mp3, "sound/a.ogg"));
        Assert.Equal((FileFormat.Unknown, FileFormatSource.None), FormatDetection.Detect(mp3, null));

        var mver = Head("REVM", 4u, 18u, "XETM");
        Assert.Equal((FileFormat.AdtTex, FileFormatSource.Extension), FormatDetection.Detect(mver, "world/maps/a/a_1_2_tex0.adt"));
        Assert.Equal((FileFormat.Blp, FileFormatSource.Extension), FormatDetection.Detect(mver, "a.blp"));
        Assert.Equal((FileFormat.Unknown, FileFormatSource.None), FormatDetection.Detect(mver, null));
    }

    [Fact]
    public void No_evidence_in_the_bytes_falls_back_to_the_name()
    {
        Assert.Equal((FileFormat.Lua, FileFormatSource.Extension), FormatDetection.Detect(Head("-- lua"), "interface/a.lua"));
        Assert.Equal((FileFormat.Db2, FileFormatSource.Extension), FormatDetection.Detect(new byte[FormatDetection.HeadBytes], "a.db2"));
        Assert.Equal((FileFormat.Db2, FileFormatSource.Extension), FormatDetection.Detect(Array.Empty<byte>(), "a.db2"));
    }

    [Fact]
    public void A_bare_sfnt_version_with_junk_does_not_beat_the_name()
    {
        var head = Head(new byte[] { 0x00, 0x01, 0x00, 0x00, 0xDE, 0xAD, 0xBE, 0xEF });
        Assert.Equal((FileFormat.Anim, FileFormatSource.Extension), FormatDetection.Detect(head, "creature/a/x.anim"));
    }

    [Fact]
    public void A_null_head_never_matches_magic()
    {
        Assert.Equal((FileFormat.Unknown, FileFormatSource.None), FormatDetection.Detect(null, null));
        Assert.Equal((FileFormat.Ogg, FileFormatSource.Extension), FormatDetection.Detect(null, "sound/a.ogg"));
    }
}
