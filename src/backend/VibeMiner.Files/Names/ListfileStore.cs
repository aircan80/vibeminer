using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Generated;
using VibeMiner.Generated.RecordFiles;

namespace VibeMiner.Files.Names;

/// <summary>The release a persisted names index was built from: GitHub's tag and SHA-256 (lowercase hex), when it was published and fetched.</summary>
internal sealed record ListfileStoreRelease(string Tag, string Sha256, DateTimeOffset PublishedAt, DateTimeOffset FetchedAt)
{
    internal void Validate()
    {
        if (!ListfileRelease.ValidTag(Tag)) throw new ArgumentException("The tag isn't 1-64 of [a-z0-9_-].", nameof(Tag));
        if (!ValidSha(Sha256)) throw new ArgumentException("The SHA-256 isn't 64 lowercase hex digits.", nameof(Sha256));
    }

    internal static bool ValidSha(string sha) => sha is { Length: 64 } && sha.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>The outcome of <see cref="ListfileStore.OpenAsync"/>. <see cref="Index"/> is set only for Present.</summary>
internal sealed record ListfileStoreOpen(GeneratedReadState State, ListfileStore? Index = null, string? Problem = null);

/// <summary>
/// The listfile's names persisted in the generated store (#186), as two record files. <b>names</b>: one record per FileDataID, in
/// ascending order, holding the UTF-8 name. <b>paths</b>: one record per 32-bit seeded hash of the folded name (ASCII case and slash
/// direction ignored, as <see cref="NameFolding"/> matches), holding the varint-delta ascending FileDataIDs of every name with that
/// hash; a hash hit is confirmed against the names, so collisions never give a wrong answer. Both manifests carry the same metadata
/// (<see cref="Metadata"/>); the paths manifest is written last and is the commit point. Memory is the two files' manifest
/// directories; every lookup reads the id chunk and block it needs. A read that comes back damaged throws
/// <see cref="GeneratedStoreException"/> (Corrupt), never a wrong name. Thread-safe.
/// </summary>
internal sealed class ListfileStore
{
    /// <summary>A 32-bit hash of a name that ignores ASCII case and slash direction, keyed by the index's seed.</summary>
    internal delegate uint PathHasher(ulong seed, ReadOnlySpan<byte> name);

    public const ushort FormatVersion = 1;
    private const int FeedSlice = 64 << 10;
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private static readonly UTF8Encoding Strict = new(false, true);

    internal static readonly RecordFileOptions NamesOptions = new() { MaxRecords = ListfileParser.MaxNames, MaxRecordBytes = ListfileParser.MaxNameBytes };
    // A paths record lists every id of one hash; many ids can share one name, so records may be large.
    internal static readonly RecordFileOptions PathsOptions = new() { MaxRecords = ListfileParser.MaxNames, MaxRecordBytes = 16 << 20 };

    private readonly RecordFile names, paths;
    private readonly PathHasher hasher;

    private ListfileStore(RecordFile names, RecordFile paths, Metadata metadata, PathHasher hasher)
    {
        this.names = names; this.paths = paths; this.hasher = hasher;
        Seed = metadata.Seed;
        Tag = metadata.Tag; Sha256 = metadata.Sha256; PublishedAt = metadata.PublishedAt; FetchedAt = metadata.FetchedAt;
        Count = metadata.Count;
        Keys = [.. names.Keys.Concat(paths.Keys).Distinct()];
    }

    public long Count { get; }
    public string Tag { get; }
    public string Sha256 { get; }
    public DateTimeOffset PublishedAt { get; }
    public DateTimeOffset FetchedAt { get; }
    internal ulong Seed { get; }
    /// <summary>
    /// Both files' manifests and segments. Segments are content-addressed and the names file doesn't depend on the seed, so another
    /// release's index can share names segments: deleting these keys can break it. Delete them only from a collector that checks
    /// which manifests still reference each segment (as <see cref="RecordFile.Keys"/> warns).
    /// </summary>
    public IReadOnlyList<GeneratedKey> Keys { get; }

    /// <summary>The names and paths manifest keys of one release.</summary>
    public static (GeneratedKey Names, GeneratedKey Paths) ManifestKeys(string tag, string sha256)
    {
        var identity = $"wowlistfile-names-v{FormatVersion}|{tag}|{sha256}|";
        return (GeneratedKey.Hash(Encoding.UTF8.GetBytes(identity + "names")), GeneratedKey.Hash(Encoding.UTF8.GetBytes(identity + "paths")));
    }

    /// <summary>
    /// Opens the index of one release. Present only when both manifests read and carry the same metadata, matching the release, with
    /// counts that fit. No paths manifest is Absent, whatever the names file holds (a build that stopped before its commit point).
    /// Store failures throw. <paramref name="hasher"/> replaces the path hash, for collision tests.
    /// </summary>
    public static async Task<ListfileStoreOpen> OpenAsync(IGeneratedStore store, string tag, string sha256, CancellationToken cancellationToken = default, PathHasher? hasher = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(sha256);
        var (namesKey, pathsKey) = ManifestKeys(tag, sha256);
        var paths = await RecordFile.OpenAsync(store, pathsKey, cancellationToken).ConfigureAwait(false);
        if (paths.State == GeneratedReadState.Absent) return new(GeneratedReadState.Absent);
        if (paths.State != GeneratedReadState.Present) return Corrupt($"The paths file reads corrupt: {paths.Problem}");
        var names = await RecordFile.OpenAsync(store, namesKey, cancellationToken).ConfigureAwait(false);
        if (names.State == GeneratedReadState.Absent) return Corrupt("The paths file has no names file.");
        if (names.State != GeneratedReadState.Present) return Corrupt($"The names file reads corrupt: {names.Problem}");
        if (!names.File!.Metadata.Span.SequenceEqual(paths.File!.Metadata.Span)) return Corrupt("The names and paths files carry different metadata.");
        if (!Metadata.TryDecode(paths.File.Metadata.Span, out var metadata, out var problem)) return Corrupt(problem);
        if (metadata.Tag != tag || metadata.Sha256 != sha256) return Corrupt("The metadata names another release.");
        if (metadata.Count != names.File.Count || paths.File.Count < 1 || paths.File.Count > metadata.Count)
            return Corrupt("The record counts don't match the metadata.");
        return new(GeneratedReadState.Present, new ListfileStore(names.File, paths.File, metadata, hasher ?? PathHash));
    }

    private static ListfileStoreOpen Corrupt(string problem) => new(GeneratedReadState.Corrupt, Problem: $"Listfile names index: {problem}");

    /// <summary>
    /// Builds the index of <paramref name="release"/> from its CSV, read once in chunks of any size, and returns it opened. An index
    /// already Present is returned as it is, without reading the chunks, unless <paramref name="rebuild"/> is set: that is the repair
    /// for a Corrupt read under manifests that open. Otherwise both manifests are deleted first, and of the segments they still name
    /// only those that read back other than Present (already broken for every file sharing them), so a rebuild neither keeps damaged
    /// bytes by content address nor breaks another release's index that shares segments. Only one build may run per store and
    /// release at a time: the caller serialises them. Memory is one 64 KiB
    /// slice of names in flight, the writers' open blocks and segments, and 8 bytes a name for the paths (sorted at the end). The
    /// CSV's SHA-256 must match the release. A refused CSV throws <see cref="ListfileFormatException"/> and writes no manifest
    /// (segments already put stay unreferenced); store failures throw <see cref="GeneratedStoreException"/>.
    /// </summary>
    public static async Task<ListfileStore> BuildAsync(IGeneratedStore store, ListfileStoreRelease release, IAsyncEnumerable<ReadOnlyMemory<byte>> csv,
        CancellationToken cancellationToken = default, bool rebuild = false, PathHasher? hasher = null, int maxNames = ListfileParser.MaxNames,
        RecordFileOptions? namesOptions = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(csv);
        release.Validate();
        hasher ??= PathHash;
        var existing = await OpenAsync(store, release.Tag, release.Sha256, cancellationToken, hasher).ConfigureAwait(false);
        if (existing.State == GeneratedReadState.Present && !rebuild) return existing.Index!;
        var (namesKey, pathsKey) = ManifestKeys(release.Tag, release.Sha256);
        await DeleteLeftoversAsync(store, pathsKey, cancellationToken).ConfigureAwait(false);
        await DeleteLeftoversAsync(store, namesKey, cancellationToken).ConfigureAwait(false);

        var seed = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
        var pending = new Pending();
        var pairs = new ulong[1024];
        long count = 0;
        var parser = new ListfileParser((id, name) =>
        {
            if (count == pairs.Length) Array.Resize(ref pairs, (int)Math.Min(pairs.Length * 2L, maxNames));
            pairs[count++] = (ulong)hasher(seed, name) << 32 | id;
            pending.Add(id, name);
        }, maxNames);

        byte[] metadata;
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        using (var namesWriter = new RecordFileWriter(store, namesOptions ?? NamesOptions))
        {
            await foreach (var chunk in csv.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                sha.AppendData(chunk.Span);
                for (var at = 0; at < chunk.Length; at += FeedSlice)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    parser.Feed(chunk.Span.Slice(at, Math.Min(FeedSlice, chunk.Length - at)));
                    await pending.DrainAsync(namesWriter, cancellationToken).ConfigureAwait(false);
                }
            }
            parser.Finish();
            await pending.DrainAsync(namesWriter, cancellationToken).ConfigureAwait(false);
            if (Convert.ToHexStringLower(sha.GetHashAndReset()) != release.Sha256)
                throw new ListfileFormatException("The file's SHA-256 doesn't match its release.");
            metadata = new Metadata(seed, count, release.Tag, release.Sha256, release.PublishedAt, release.FetchedAt).Encode();
            await namesWriter.CompleteAsync(namesKey, metadata, cancellationToken).ConfigureAwait(false);
        }

        // (hash << 32 | id): sorted, each hash's ids come out ascending.
        Array.Sort(pairs, 0, (int)count);
        using (var pathsWriter = new RecordFileWriter(store, PathsOptions))
        {
            var buffer = new byte[64];
            for (long i = 0; i < count;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = (uint)(pairs[i] >> 32);
                var length = 0;
                uint previous = 0;
                for (; i < count && (uint)(pairs[i] >> 32) == hash; i++)
                {
                    var id = (uint)pairs[i];
                    if (buffer.Length - length < 5) Array.Resize(ref buffer, buffer.Length * 2);
                    length = WriteVar(buffer, length, id - previous);
                    previous = id;
                }
                await pathsWriter.AddAsync(hash, buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            }
            pairs = [];
            await pathsWriter.CompleteAsync(pathsKey, metadata, cancellationToken).ConfigureAwait(false);
        }

        var opened = await OpenAsync(store, release.Tag, release.Sha256, cancellationToken, hasher).ConfigureAwait(false);
        if (opened.State != GeneratedReadState.Present)
            throw new GeneratedStoreException(GeneratedStoreCodes.Corrupt, opened.Problem ?? "Listfile names index: the built index doesn't open.");
        return opened.Index!;
    }

    /// <summary>Deletes a manifest and, when it still opens, those of its segments that read back other than Present.</summary>
    private static async Task DeleteLeftoversAsync(IGeneratedStore store, GeneratedKey manifest, CancellationToken cancellationToken)
    {
        var open = await RecordFile.OpenAsync(store, manifest, cancellationToken).ConfigureAwait(false);
        if (open.State == GeneratedReadState.Absent) return;
        var doomed = new List<GeneratedKey> { manifest };
        foreach (var segment in open.File?.Keys.Skip(1) ?? [])
            if ((await store.ReadAsync(segment, cancellationToken).ConfigureAwait(false)).State == GeneratedReadState.Corrupt) doomed.Add(segment);
        await store.DeleteAsync(doomed, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The name of a FileDataID, or null when the listfile doesn't name it.</summary>
    public async Task<string?> NameOfAsync(uint fileDataId, CancellationToken cancellationToken = default)
    {
        var record = await names.FindAsync(fileDataId, cancellationToken).ConfigureAwait(false);
        return record is { } r ? Decode(r.Bytes.Span) : null;
    }

    /// <summary>Every FileDataID named <paramref name="path"/> (ASCII case and slash direction ignored), ascending; empty when none.</summary>
    public async Task<IReadOnlyList<uint>> IdsOfAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        byte[] query;
        try { query = Strict.GetBytes(path); }
        catch (EncoderFallbackException) { return []; }
        if (query.Length is 0 or > ListfileParser.MaxNameBytes) return [];
        var record = await paths.FindAsync(hasher(Seed, query), cancellationToken).ConfigureAwait(false);
        if (record is not { } r) return [];
        List<uint>? found = null;
        foreach (var id in Candidates(r.Bytes))
        {
            var name = await names.FindAsync(id, cancellationToken).ConfigureAwait(false)
                ?? throw Damaged("The paths file names a FileDataID the names file doesn't have.");
            Check(name.Bytes.Span);
            if (NameFolding.SameName(name.Bytes.Span, query)) (found ??= []).Add(id);
        }
        return found ?? (IReadOnlyList<uint>)[];
    }

    /// <summary>Every named FileDataID from <paramref name="fromId"/> up, ascending, with its name, streamed a block run at a time.</summary>
    public async IAsyncEnumerable<(uint Id, string Name)> FromAsync(uint fromId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var first = await names.FindAtOrAfterAsync(fromId, cancellationToken).ConfigureAwait(false);
        if (first is not { } start) yield break;
        long previous = (long)start.Id - 1;
        await foreach (var record in names.ReadFromAsync(start.Ordinal, cancellationToken).ConfigureAwait(false))
        {
            if (record.Id <= previous) throw Damaged("The names aren't in FileDataID order.");
            previous = record.Id;
            yield return (record.Id, Decode(record.Bytes.Span));
        }
    }

    /// <summary>The default path hash: 64-bit FNV-1a over the folded bytes, its offset basis XORed with the seed, folded to 32 bits.</summary>
    internal static uint PathHash(ulong seed, ReadOnlySpan<byte> name)
    {
        var hash = FnvOffset ^ seed;
        foreach (var value in name) hash = (hash ^ NameFolding.Fold(value)) * FnvPrime;
        return (uint)(hash ^ (hash >> 32));
    }

    private static IEnumerable<uint> Candidates(ReadOnlyMemory<byte> bytes)
    {
        var at = 0;
        long id = -1;
        while (at < bytes.Length)
        {
            var delta = ReadVar(bytes.Span, ref at);
            var next = id < 0 ? delta : id + delta;
            if (id >= 0 && delta == 0 || next > uint.MaxValue) throw Damaged("A paths record isn't ascending FileDataIDs.");
            id = next;
            yield return (uint)id;
        }
        if (id < 0) throw Damaged("A paths record is empty.");
    }

    private static void Check(ReadOnlySpan<byte> name)
    {
        if (name.Length is 0 or > ListfileParser.MaxNameBytes) throw Damaged("A name has an impossible length.");
    }

    private static string Decode(ReadOnlySpan<byte> name)
    {
        Check(name);
        try { return Strict.GetString(name); }
        catch (DecoderFallbackException e) { throw Damaged("A name isn't valid UTF-8.", e); }
    }

    private static GeneratedStoreException Damaged(string problem, Exception? inner = null) =>
        new(GeneratedStoreCodes.Corrupt, $"Listfile names index: {problem}", inner);

    private static int WriteVar(byte[] buffer, int at, uint value)
    {
        while (value >= 0x80) { buffer[at++] = (byte)(value | 0x80); value >>= 7; }
        buffer[at++] = (byte)value;
        return at;
    }

    private static uint ReadVar(ReadOnlySpan<byte> source, ref int at)
    {
        uint value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (at >= source.Length) throw Damaged("A paths record ends inside a number.");
            var b = source[at++];
            if (shift == 28 && b > 0x0F) throw Damaged("A paths record has an oversized number.");
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }
        throw Damaged("A paths record has an oversized number.");
    }

    /// <summary>Names parsed but not yet written: at most one feed slice's worth.</summary>
    private sealed class Pending
    {
        private byte[] bytes = new byte[FeedSlice];
        private uint[] ids = new uint[1024];
        private int[] ends = new int[1024];
        private int count, length;

        public void Add(uint id, ReadOnlySpan<byte> name)
        {
            if (length + name.Length > bytes.Length) Array.Resize(ref bytes, Math.Max(bytes.Length * 2, length + name.Length));
            if (count == ids.Length) { Array.Resize(ref ids, count * 2); Array.Resize(ref ends, count * 2); }
            name.CopyTo(bytes.AsSpan(length));
            length += name.Length;
            ids[count] = id; ends[count] = length; count++;
        }

        public async ValueTask DrainAsync(RecordFileWriter writer, CancellationToken cancellationToken)
        {
            for (int i = 0, start = 0; i < count; start = ends[i], i++)
                await writer.AddAsync(ids[i], bytes.AsMemory(start, ends[i] - start), cancellationToken).ConfigureAwait(false);
            count = 0; length = 0;
        }
    }

    /// <summary>
    /// Both manifests' metadata, format 1, little-endian: u16 version, u64 seed, u32 count, i64 published UTC ticks, i64 fetched UTC
    /// ticks, 32 bytes SHA-256, u8 tag length, the tag's ASCII bytes. Exactly that long.
    /// </summary>
    internal sealed record Metadata(ulong Seed, long Count, string Tag, string Sha256, DateTimeOffset PublishedAt, DateTimeOffset FetchedAt)
    {
        private const int Fixed = 2 + 8 + 4 + 8 + 8 + 32 + 1;

        public byte[] Encode()
        {
            var tag = Encoding.ASCII.GetBytes(Tag);
            var bytes = new byte[Fixed + tag.Length];
            var span = bytes.AsSpan();
            BinaryPrimitives.WriteUInt16LittleEndian(span, FormatVersion);
            BinaryPrimitives.WriteUInt64LittleEndian(span[2..], Seed);
            BinaryPrimitives.WriteUInt32LittleEndian(span[10..], checked((uint)Count));
            BinaryPrimitives.WriteInt64LittleEndian(span[14..], PublishedAt.UtcTicks);
            BinaryPrimitives.WriteInt64LittleEndian(span[22..], FetchedAt.UtcTicks);
            Convert.FromHexString(Sha256).CopyTo(span[30..]);
            span[62] = (byte)tag.Length;
            tag.CopyTo(span[63..]);
            return bytes;
        }

        public static bool TryDecode(ReadOnlySpan<byte> bytes, out Metadata metadata, out string problem)
        {
            metadata = null!;
            problem = "The metadata is out of shape.";
            if (bytes.Length < Fixed) return false;
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes) != FormatVersion) { problem = "The metadata has an unknown format version."; return false; }
            var tagLength = bytes[62];
            if (bytes.Length != Fixed + tagLength) return false;
            var tag = Encoding.ASCII.GetString(bytes[63..]);
            if (!ListfileRelease.ValidTag(tag)) return false;
            long published = BinaryPrimitives.ReadInt64LittleEndian(bytes[14..]), fetched = BinaryPrimitives.ReadInt64LittleEndian(bytes[22..]);
            if (published is < 0 or > 3155378975999999999 || fetched is < 0 or > 3155378975999999999) return false;
            var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]);
            if (count is 0 or > ListfileParser.MaxNames) return false;
            metadata = new Metadata(BinaryPrimitives.ReadUInt64LittleEndian(bytes[2..]), count, tag, Convert.ToHexStringLower(bytes.Slice(30, 32)),
                new DateTimeOffset(published, TimeSpan.Zero), new DateTimeOffset(fetched, TimeSpan.Zero));
            return true;
        }
    }
}
