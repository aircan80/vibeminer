using System.Globalization;
using System.Security.Cryptography;
using VibeMiner.Builds;
using VibeMiner.Cdn;
using VibeMiner.Files.Testing;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using L3 = VibeMiner.Builds.Testing;

namespace VibeMiner.Files.Tests;

/// <summary>
/// Builds the same synthetic build for the fake and for the real <see cref="FileSystem"/> over the layer 3 and layer 2 fakes
/// (D137): no object store, no sockets. Every file is one ROOT variant.
/// </summary>
internal static class FilesFixture
{
    public static readonly BuildRef Build = new("wow", new string('a', 32), new string('b', 32));
    public static readonly BuildRef Other = new("wow", new string('c', 32), new string('d', 32));

    /// <summary>The decoded bytes of one readable BLTE chunk the fixture builds; holes are split the same way.</summary>
    public const int ChunkBytes = 128;

    /// <summary>The key name a <see cref="FileReadState.WrongKey"/> file is encrypted with; the real side is given a wrong key for it.</summary>
    public const ulong WrongKeyName = 0x0BAD0BAD0BAD0BAD;

    /// <summary>The content key the fake and the fixture give a file without one or content.</summary>
    public static string ContentKeyOf(uint id) => id.ToString("x32", CultureInfo.InvariantCulture);

    /// <summary>The content key the fake and the fixture give bytes: their MD5.</summary>
    public static string ContentKeyOf(byte[] content) => Convert.ToHexStringLower(MD5.HashData(content));

    /// <summary>The content key both sides list for a file.</summary>
    public static string ContentKeyOf(FakeFile f) =>
        f.ContentKey?.ToLowerInvariant() ?? (f.Content is { } content ? ContentKeyOf(content) : ContentKeyOf(f.FileDataId));

    /// <summary>A distinct encoded key per file, never equal to its content key.</summary>
    public static CdnKey EKeyOf(uint id) => CdnKey.Parse("e" + id.ToString("x31", CultureInfo.InvariantCulture));

    public static ObjectKey ObjectKeyOf(uint id) => new(ObjectKeyType.Encoded, EKeyOf(id).ToBytes());

    public static uint LocaleMask(IReadOnlyList<string>? locales) =>
        (locales ?? [.. FileLocales.Names]).Aggregate(0u, (mask, l) => mask | FileLocales.BitOf(l));

    /// <summary>The layer 3 file for a file without content: the generated EKey, its listed size and a 64-byte encoded size.</summary>
    public static L3.FakeFile ToLayer3(FakeFile f) => ToLayer3(f, f.NotInEncoding ? null : EKeyOf(f.FileDataId), f.Size, f.NotInEncoding ? null : 64);

    private static L3.FakeFile ToLayer3(FakeFile f, CdnKey? eKey, long? decodedSize, long? encodedSize)
    {
        var flags = f.Encrypted ? 0x8000000UL : 0UL;
        var variant = new FileVariant(LocaleMask(f.Locales), flags, CdnKey.Parse(ContentKeyOf(f)), decodedSize, eKey, encodedSize, null);
        return new(f.FileDataId, f.Path, [variant]);
    }

    public static BuildMetadata Metadata(IReadOnlyList<string> locales, int fileCount) =>
        new("WOW-1.0.0.1", CdnKey.Parse(new string('1', 32)), CdnKey.Parse(new string('2', 32)), CdnKey.Parse(new string('3', 32)), 0,
            fileCount, locales);

    /// <summary>
    /// The real file system over fakes, holding exactly the given files. A file with <see cref="FakeFile.Content"/> becomes a real
    /// BLTE object in layer 2 (stored when <see cref="FakeFile.Storage"/> says so, else served on the first read): readable bytes
    /// in alternating 'N' and 'Z' chunks of <see cref="ChunkBytes"/>, each hole in 'E' chunks of its key with a generated TACT key
    /// the file system isn't given, cut at every hole edge. A scripted <see cref="FakeFile.ReadFailure"/> becomes what layer 2 or
    /// the bytes would answer: Missing, NeedsNetwork, Unavailable and StoreFailed are layer 2 scripts (the last two with the
    /// fake's code; the first two with layer 2's own, <c>CDN_NOT_FOUND</c> and <c>OBJECT_ACCESS_NEEDS_NETWORK</c>);
    /// Corrupt is a header that doesn't hash to its EKey; Unsupported is an unknown chunk table format; WrongKey is content
    /// encrypted with <see cref="WrongKeyName"/>, whose key the file system is given wrong. A file without content answers
    /// NeedsNetwork (or its scripted failure), except a stored one, which gets placeholder bytes for listing tests only (the fake
    /// rejects it). A WrongKey file needs <paramref name="options"/> without keys. A layer 2 failure with
    /// <see cref="FakeReadFailure.OnStreamRead"/> isn't scripted until <see cref="RealFiles.FailStreamReads"/> (after the stream opened),
    /// so reads succeed until then; Corrupt or Unsupported on stream reads isn't built.
    /// </summary>
    public static RealFiles Real(BuildRef build, IReadOnlyList<string> locales, IReadOnlyList<FakeFile> files, FileSystemOptions? options = null)
    {
        var objects = new InMemoryObjectSource();
        var encoded = new Dictionary<uint, (ObjectKey Key, byte[] Bytes)>();
        var onStreamRead = new Dictionary<uint, (ObjectKey Key, FakeReadFailure Failure)>();
        var layer3 = new List<L3.FakeFile>(files.Count);
        var wrongKey = false;
        foreach (var f in files)
        {
            if (f.NotInEncoding) { layer3.Add(ToLayer3(f)); continue; }
            var failure = f.ReadFailure?.State;
            if (f.Content is not { } content)
            {
                layer3.Add(ToLayer3(f));
                var placeholder = ObjectKeyOf(f.FileDataId);
                if (f.Storage == FileStorage.Stored) objects.AddStored(placeholder, [1, 2, 3]);
                else if (failure is FileReadState.Corrupt or FileReadState.Unsupported or FileReadState.WrongKey)
                    throw new ArgumentException($"File {f.FileDataId}: a {failure} file needs content on the real side.", nameof(files));
                else Script(objects, placeholder, f.ReadFailure ?? new(FileReadState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK"));
                continue;
            }
            if (f.ReadFailure is { OnStreamRead: true } r && r.State is FileReadState.Corrupt or FileReadState.Unsupported)
                throw new ArgumentException($"File {f.FileDataId}: {r.State} on stream reads isn't built on the real side.", nameof(files));
            wrongKey |= failure == FileReadState.WrongKey;
            var o = Blte(content, f.Holes ?? [], failure);
            // A Corrupt file is filed under an EKey its header doesn't hash to.
            var eKey = failure == FileReadState.Corrupt ? EKeyOf(f.FileDataId) : o.EKey;
            var key = new ObjectKey(ObjectKeyType.Encoded, eKey.ToBytes());
            layer3.Add(ToLayer3(f, eKey, content.Length, o.Encoded.Length));
            encoded[f.FileDataId] = (key, o.Encoded);
            if (f.Storage == FileStorage.Stored) objects.AddStored(key, o.Encoded); else objects.Add(key, o.Encoded);
            if (f.ReadFailure is { IsLayer2: true } layer2)
            {
                if (layer2.OnStreamRead) onStreamRead[f.FileDataId] = (key, layer2);
                else Script(objects, key, layer2);
            }
        }
        var builds = new L3.InMemoryBuilds();
        builds.Add(build, new L3.FakeBuild(Metadata(locales, files.Count), layer3));
        if (wrongKey)
        {
            options ??= new();
            if (options.Keys != TactKeys.None) throw new ArgumentException("A WrongKey file needs options without keys.", nameof(options));
            options = options with { Keys = new TactKeys().Add(WrongKeyName, SecretOf(WrongKeyName ^ 1)) };
        }
        return new(new FileSystem(builds, objects, options), builds, objects, encoded, onStreamRead);
    }

    internal static void Script(InMemoryObjectSource objects, ObjectKey key, FakeReadFailure failure) => _ = failure.State switch
    {
        FileReadState.Missing => objects.NotFound(key),
        FileReadState.NeedsNetwork => objects.NeedsNetwork(key),
        FileReadState.Unavailable => objects.Unavailable(key, failure.Code),
        FileReadState.StoreFailed => objects.StoreFailed(key, failure.Code),
        _ => throw new ArgumentException($"A {failure.State} read isn't a layer 2 script.", nameof(failure)),
    };

    /// <summary>The 16-byte TACT key the fixture encrypts a key name's chunks with.</summary>
    private static byte[] SecretOf(ulong keyName) => BlteBuilder.Bytes(16, (int)(keyName % 1000) + 7);

    private static BlteObject Blte(byte[] content, IReadOnlyList<FileHole> holes, FileReadState? failure)
    {
        if (content.Length == 0) return BlteBuilder.Headerless(BlteChunkSpec.Raw([]));
        var chunks = new List<BlteChunkSpec>();
        void Add(long from, long to, string? keyName)
        {
            for (var at = from; at < to; at += ChunkBytes)
            {
                var bytes = content.AsSpan((int)at, (int)Math.Min(ChunkBytes, to - at)).ToArray();
                var plain = chunks.Count % 2 == 0 ? BlteChunkSpec.Raw(bytes) : BlteChunkSpec.Zlib(bytes);
                var name = keyName is not null ? ulong.Parse(keyName, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
                    : failure == FileReadState.WrongKey ? WrongKeyName : (ulong?)null;
                chunks.Add(name is { } n ? BlteChunkSpec.Encrypted(n, SecretOf(n), [1, 2, 3, 4], plain) : plain);
            }
        }
        long next = 0;
        foreach (var hole in holes)
        {
            Add(next, hole.Offset, null);
            Add(hole.Offset, hole.Offset + hole.Length, hole.KeyName);
            next = hole.Offset + hole.Length;
        }
        Add(next, content.Length, null);
        // An unknown chunk table format, in a header that still hashes to its EKey.
        return BlteBuilder.Chunked(chunks, failure == FileReadState.Unsupported ? header => header[8] = 0x10 : null);
    }
}

/// <summary>The real file system the fixture built, with the layer 3 and layer 2 fakes under it and each file's BLTE object.</summary>
internal sealed class RealFiles(FileSystem files, L3.InMemoryBuilds builds, InMemoryObjectSource objects,
    IReadOnlyDictionary<uint, (ObjectKey Key, byte[] Bytes)> encoded, IReadOnlyDictionary<uint, (ObjectKey Key, FakeReadFailure Failure)> onStreamRead)
{
    public FileSystem Files { get; } = files;
    public L3.InMemoryBuilds Builds { get; } = builds;
    public InMemoryObjectSource Objects { get; } = objects;

    public void Deconstruct(out FileSystem files, out L3.InMemoryBuilds builds, out InMemoryObjectSource objects) =>
        (files, builds, objects) = (Files, Builds, Objects);

    /// <summary>Scripts a file's layer 2 failure on stream reads now, once its stream is open; nothing for any other file.</summary>
    public void FailStreamReads(uint fileDataId)
    {
        if (onStreamRead.TryGetValue(fileDataId, out var pending)) FilesFixture.Script(Objects, pending.Key, pending.Failure);
    }

    /// <summary>The layer 2 key of a file's encoded copy (the placeholder key for a file without content).</summary>
    public ObjectKey KeyOf(uint fileDataId) => encoded.TryGetValue(fileDataId, out var o) ? o.Key : FilesFixture.ObjectKeyOf(fileDataId);

    /// <summary>Puts a file's encoded copy in the store, as a download would (placeholder bytes for a file without content).</summary>
    public void MarkStored(uint fileDataId)
    {
        if (encoded.TryGetValue(fileDataId, out var o)) Objects.AddStored(o.Key, o.Bytes);
        else Objects.AddStored(FilesFixture.ObjectKeyOf(fileDataId), [1, 2, 3]);
    }
}
