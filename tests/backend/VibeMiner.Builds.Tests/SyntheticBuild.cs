using System.Security.Cryptography;
using System.Text;
using VibeMiner.Builds.Formats;
using VibeMiner.Builds.Testing;
using VibeMiner.Builds.Tests.Formats;
using VibeMiner.Cdn;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;

namespace VibeMiner.Builds.Tests;

/// <summary>Where a synthetic variant's object lives.</summary>
internal enum Placement { Archived, LooseIndexed, Loose, NotInEncoding }

public enum RootMode { Normal, Encrypted, Unsupported }

internal sealed record SyntheticVariant(uint Locale, uint Flags, Placement Placement, int Archive = 0);

internal sealed record SyntheticFile(uint FileDataId, string Path, params SyntheticVariant[] Variants);

/// <summary>
/// A whole synthetic build written with the Formats writers: build and CDN configs, BLTE-wrapped ENCODING and ROOT (v2, one
/// block per locale and flags, with name hashes), two archive indexes and a file-index. No game content. <see cref="Variants"/>
/// is what a resolver must answer for a file, and <see cref="ToFake"/> the same build for <see cref="InMemoryBuilds"/>.
/// </summary>
internal sealed class SyntheticBuild
{
    public const uint EnUs = 0x2, DeDe = 0x20;

    /// <summary>100: two locales (archived, loose in the file-index); 200: archived in the second archive; 300: loose in the
    /// file-index; 400: loose, in no index; 500: in ROOT but not in ENCODING.</summary>
    public static readonly SyntheticFile[] StandardFiles =
    [
        new(100, "interface/icons/one.blp", V(EnUs, 0, Placement.Archived, 0), V(DeDe, 0x8, Placement.LooseIndexed)),
        new(200, "world/two.m2", V(EnUs, 0, Placement.Archived, 1)),
        new(300, "sound/three.ogg", V(EnUs, 0, Placement.LooseIndexed)),
        new(400, "sound/four.ogg", V(EnUs, 0, Placement.Loose)),
        new(500, "sound/five.ogg", V(EnUs, 0, Placement.NotInEncoding)),
    ];

    private static SyntheticVariant V(uint locale, uint flags, Placement placement, int archive = 0) => new(locale, flags, placement, archive);

    private readonly Dictionary<ObjectKey, byte[]> objects = [];
    private readonly Dictionary<(uint Id, uint Locale), (CdnKey CKey, CdnKey EKey, long Encoded, long Decoded)> keys = [];

    public SyntheticBuild(string product = "wow", RootMode root = RootMode.Normal, bool corruptEncoding = false, SyntheticFile[]? files = null,
        string buildName = "WOW-1000patch1.0.0_Synthetic", string? region = null, bool rootInEncoding = true, uint[]? noEncodedRow = null)
    {
        Files = files ?? StandardFiles;
        NoEncodedRow = noEncodedRow ?? [];
        var unsized = NoEncodedRow.SelectMany(id => Files.Single(f => f.FileDataId == id).Variants.Select(v => (id, v.Locale))).ToHashSet();
        foreach (var f in Files)
            foreach (var v in f.Variants)
            {
                var content = Encoding.ASCII.GetBytes($"synthetic file {f.FileDataId} locale {v.Locale}");
                keys[(f.FileDataId, v.Locale)] = (new(MD5.HashData(content)), new(MD5.HashData([.. "ekey "u8, .. content])),
                    100 + f.FileDataId + v.Locale, content.Length);
            }

        var blocks = Files.SelectMany(f => f.Variants.Select(v => (File: f, Variant: v)))
            .GroupBy(x => (x.Variant.Locale, x.Variant.Flags)).OrderBy(g => g.Key.Locale)
            .Select(g =>
            {
                var rows = g.OrderBy(x => x.File.FileDataId).ToArray();
                return new RootBlock(RootWriter.Deltas(rows.Select(r => r.File.FileDataId).ToArray()), g.Key.Locale, g.Key.Flags,
                    Names: rows.Select(r => RootNameHash.Of(r.File.Path)).ToArray(),
                    Keys: rows.Select(r => keys[(r.File.FileDataId, r.Variant.Locale)].CKey).ToArray());
            }).ToArray();
        var rootBytes = RootWriter.Build(root == RootMode.Unsupported ? 3 : 2, 24, blocks);
        var rootBlte = root == RootMode.Encrypted ? BlteWriter.Chunked(BlteWriter.E(rootBytes)) : BlteWriter.Chunked(BlteWriter.Z(rootBytes));
        RootCKey = rootBlte.CKey;
        Add(new(ObjectKeyType.Encoded, rootBlte.EKey.ToBytes()), rootBlte.Encoded);

        var rows = Files.SelectMany(f => f.Variants.Where(v => v.Placement != Placement.NotInEncoding).Select(v => keys[(f.FileDataId, v.Locale)]))
            .Select(k => (k.CKey, k.Decoded, k.EKey, k.Encoded))
            .Concat(rootInEncoding ? [(rootBlte.CKey, (long)rootBlte.Content.Length, rootBlte.EKey, (long)rootBlte.Encoded.Length)] : []);
        // Without an encoded row for some EKeys, write one record per page so the content rows still list them.
        var encodingBytes = unsized.Count == 0 ? EncodingWriter.FromFiles(rows) : EncodingWriter.Build(
            rows.OrderBy(r => r.CKey).Select(r => EncodingWriter.Page(EncodingWriter.Content(r.CKey, r.Decoded, r.EKey))).ToArray(),
            rows.Where(r => !unsized.Any(u => keys[u].EKey == r.EKey)).OrderBy(r => r.EKey)
                .Select(r => EncodingWriter.Page(EncodingWriter.Encoded(r.EKey, r.Encoded, 0))).ToArray(), Encoding.ASCII.GetBytes("n\0"));
        if (corruptEncoding) encodingBytes[^1] ^= 0xff;
        var encodingBlte = BlteWriter.Chunked(BlteWriter.Z(encodingBytes));
        EncodingCKey = encodingBlte.CKey; EncodingEKey = encodingBlte.EKey;
        Add(new(ObjectKeyType.Encoded, encodingBlte.EKey.ToBytes()), encodingBlte.Encoded);

        var archiveCount = Math.Max(2, Files.SelectMany(f => f.Variants).Where(v => v.Placement == Placement.Archived).Select(v => v.Archive + 1).DefaultIfEmpty(0).Max());
        var archives = new List<CdnKey>();
        Locations = [];
        for (var a = 0; a < archiveCount; a++)
        {
            var entries = Files.SelectMany(f => f.Variants.Where(v => v.Placement == Placement.Archived && v.Archive == a).Select(v => keys[(f.FileDataId, v.Locale)]))
                .OrderBy(k => k.EKey).ToList();
            // An archive index needs a row; a filler row keeps every archive listed.
            var filler = new CdnKey(MD5.HashData(Encoding.ASCII.GetBytes($"filler {a}")));
            var indexRows = entries.Select((k, i) => (k.EKey, (uint)k.Encoded, (uint)(i * 4096))).Append((filler, 7u, 0x100000u)).OrderBy(r => r.Item1).ToArray();
            var (bytes, key) = ArchiveIndexWriter.Write(indexRows, ArchiveIndexKind.Archive);
            archives.Add(key);
            Add(new(ObjectKeyType.ArchiveIndex, key.ToBytes()), bytes);
            for (var i = 0; i < entries.Count; i++) Locations[entries[i].EKey] = new(key, i * 4096, entries[i].Encoded);
        }
        Archives = archives;
        var looseRows = Files.SelectMany(f => f.Variants.Where(v => v.Placement == Placement.LooseIndexed).Select(v => keys[(f.FileDataId, v.Locale)]))
            .OrderBy(k => k.EKey).Select(k => (k.EKey, (uint)k.Encoded, 0u)).ToArray();
        var (fileIndexBytes, fileIndexKey) = ArchiveIndexWriter.Write(looseRows, ArchiveIndexKind.FileIndex);
        FileIndexKey = fileIndexKey;
        Add(new(ObjectKeyType.FileIndex, fileIndexKey.ToBytes()), fileIndexBytes);

        var buildConfig = Encoding.ASCII.GetBytes($"# Build Configuration\n\nroot = {rootBlte.CKey.Hex}\ninstall = {new string('1', 32)}\n" +
            $"encoding = {encodingBlte.CKey.Hex} {encodingBlte.EKey.Hex}\nencoding-size = {encodingBlte.Content.Length} {encodingBlte.Encoded.Length}\n" +
            $"build-name = {buildName}\n");
        var cdnConfig = Encoding.ASCII.GetBytes($"# CDN Configuration\n\narchives = {string.Join(' ', archives.Select(a => a.Hex))}\n" +
            $"file-index = {fileIndexKey.Hex}\nfile-index-size = {fileIndexBytes.Length}\n");
        BuildConfigKey = new(ObjectKeyType.Config, MD5.HashData(buildConfig));
        CdnConfigKey = new(ObjectKeyType.Config, MD5.HashData(cdnConfig));
        Add(BuildConfigKey, buildConfig);
        Add(CdnConfigKey, cdnConfig);
        Ref = new(product, BuildConfigKey.KeyHex, CdnConfigKey.KeyHex, region);
        BuildName = buildName;
        RootKey = new(ObjectKeyType.Encoded, rootBlte.EKey.ToBytes());
        EncodingKey = new(ObjectKeyType.Encoded, encodingBlte.EKey.ToBytes());
    }

    public SyntheticFile[] Files { get; }
    /// <summary>Files whose EKeys have no ENCODING encoded row, so a resolver takes their size from an index.</summary>
    public uint[] NoEncodedRow { get; }
    public BuildRef Ref { get; }
    public string BuildName { get; }
    public CdnKey RootCKey { get; }
    public CdnKey EncodingCKey { get; }
    public CdnKey EncodingEKey { get; }
    public IReadOnlyList<CdnKey> Archives { get; }
    public CdnKey FileIndexKey { get; }
    public Dictionary<CdnKey, ArchiveLocation> Locations { get; }
    public ObjectKey BuildConfigKey { get; }
    public ObjectKey CdnConfigKey { get; }
    public ObjectKey EncodingKey { get; }
    public ObjectKey RootKey { get; }
    public IReadOnlyList<ObjectKey> IndexKeys =>
        [.. Archives.Select(a => new ObjectKey(ObjectKeyType.ArchiveIndex, a.ToBytes())), new ObjectKey(ObjectKeyType.FileIndex, FileIndexKey.ToBytes())];
    public IReadOnlyDictionary<ObjectKey, byte[]> Objects => objects;

    public BuildMetadata Metadata => new(BuildName, RootCKey, EncodingCKey, EncodingEKey, Archives.Count, Files.Length,
        BuildLocales.Of(Files.SelectMany(f => f.Variants).Aggregate(0u, (u, v) => u | v.Locale)));

    /// <summary>Loads every object into <paramref name="source"/>; <paramref name="stored"/> marks them as already in layer 1.</summary>
    public SyntheticBuild LoadInto(InMemoryObjectSource source, bool stored = true)
    {
        foreach (var (key, bytes) in objects)
            if (stored) source.AddStored(key, bytes); else source.Add(key, bytes);
        return this;
    }

    /// <summary>The variants a resolver must return for <paramref name="fileDataId"/>, in ROOT order (block order).</summary>
    public IReadOnlyList<FileVariant> Variants(uint fileDataId)
    {
        var file = Files.Single(f => f.FileDataId == fileDataId);
        return file.Variants.OrderBy(v => v.Locale).Select(v =>
        {
            var k = keys[(fileDataId, v.Locale)];
            return v.Placement == Placement.NotInEncoding
                ? new FileVariant(v.Locale, v.Flags, k.CKey, null, null, null, null)
                : new FileVariant(v.Locale, v.Flags, k.CKey, k.Decoded, k.EKey,
                    NoEncodedRow.Contains(fileDataId) && v.Placement == Placement.Loose ? null : k.Encoded,
                    v.Placement == Placement.Archived ? Locations[k.EKey] : null);
        }).ToArray();
    }

    public FakeBuild ToFake(CdnContext? cdn = null) =>
        new(Metadata, Files.Select(f => new FakeFile(f.FileDataId, f.Path, Variants(f.FileDataId))).ToArray(), cdn);

    private void Add(ObjectKey key, byte[] bytes) => objects[key] = bytes;
}
