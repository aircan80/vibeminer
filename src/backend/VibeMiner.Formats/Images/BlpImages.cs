using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Formats.Generated;
using VibeMiner.Generated;

namespace VibeMiner.Formats.Images;

/// <summary>
/// Layer 5's BLP2 entry point (#199, D390): reads a texture through layer 4 and decodes it with <see cref="Blp2Decoder"/>, caching
/// decoded images in the generated store.
/// <para>
/// Holes: the 148-byte header must be clear, then the palette (palette images) and the decoded level's declared range; a level
/// whose bytes are clear decodes even when other levels lie in holes. Refusals the header decides (encoding, descriptors, the
/// level's length, limits) come first, as in the decoder. Nothing is zero-filled. The largest-within variant plans its level from
/// the header alone and never steps down past a protected level, so which image a build gets doesn't depend on its keys.
/// </para>
/// <para>
/// Cache: identity text <c>vibeminer-generated-image v1</c>, the file's content key, the decoder revision, the level decoded and a
/// SHA-256 of the limits; no build, no locale. An image read from a stream with any encrypted chunk (decrypted or a hole) is filed
/// under the scoped identity instead (plus <c>encoding-in=</c> product, build config, CDN config and each chunk key's status, as
/// D350) and never shared. <see cref="DecodeAsync"/> tries the shared entry right after the stat, before any file read;
/// <see cref="DecodeLargestWithinAsync"/> reads the 148-byte header first to plan its level. Both try the scoped one after the open. Only decoded images are stored; a damaged or malformed entry is deleted and decoded again. There is no single flight:
/// two calls decoding one image at once both decode, and the second store keeps the first.
/// </para>
/// Calls may run concurrently. Store read failures other than Corrupt throw <see cref="GeneratedStoreException"/>; a failed store
/// write still returns the image (<see cref="BlpImageCache.StoreFailed"/>).
/// </summary>
public sealed class BlpImages
{
    public const string IdentityFormat = "vibeminer-generated-image v1";
    public const int HeaderBytes = 148;
    private const int PaletteBytes = 1024;

    private readonly IGeneratedStore store;
    private readonly Blp2Limits limits;
    private readonly Func<BuildRef, ITactKeyStatus> keys;
    private readonly string limitsHash;

    /// <param name="keys">The TACT key statuses a build's images are filed under; null knows no keys. Take the same snapshot the
    /// build's layer 4 handle decrypts with, as tables do.</param>
    public BlpImages(IGeneratedStore store, Blp2Limits? limits = null, Func<BuildRef, ITactKeyStatus>? keys = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.limits = limits ?? new Blp2Limits();
        this.keys = keys ?? (_ => TactKeyStatus.None);
        limitsHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this.limits)));
    }

    public Blp2Limits Limits => limits;

    /// <summary>SHA-256 (lower hex) of the canonical JSON of <see cref="Limits"/>, part of the cache identity.</summary>
    public string LimitsHash => limitsHash;

    /// <summary>
    /// Header facts for a detail view. Stats the file and reads only its first 148 bytes with
    /// <see cref="IBuildFiles.ReadRangeAsync"/>; never more. The descriptors pass the decoder's own checks (for the file size the
    /// stat declares); a level's length is checked only when it is decoded.
    /// </summary>
    public async Task<BlpInspection> InspectAsync(IBuildFiles files, FileRef file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        var stat = await files.StatAsync(file, cancellationToken).ConfigureAwait(false);
        if (Unlisted(stat) is { } failed) return failed;
        return await InspectAsync(files, stat.File!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Decodes <paramref name="mipLevel"/> (0-15). No other level is substituted.</summary>
    public async Task<BlpImageResult> DecodeAsync(IBuildFiles files, FileRef file, int mipLevel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (mipLevel is < 0 or > 15) throw new ArgumentOutOfRangeException(nameof(mipLevel));
        var stat = await files.StatAsync(file, cancellationToken).ConfigureAwait(false);
        if (Unlisted(stat) is { } failed) return BlpImageResult.From(failed);
        return await DecodeLevelAsync(files, stat.File!, mipLevel, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Decodes the largest declared level whose sides are both at most <paramref name="maxSide"/>, skipping compact levels, as
    /// <see cref="Blp2Decoder.DecodeLargestWithinAsync"/> plans it. The plan reads the header first (148 bytes), before the cache
    /// lookup, so a header layer 4 can't give (not stored offline, in a hole) answers FileFailed or Protected even when
    /// <see cref="DecodeAsync"/> at that level would hit the shared entry. A planned level in a hole is Protected, never a smaller clear one.
    /// </summary>
    public async Task<BlpImageResult> DecodeLargestWithinAsync(IBuildFiles files, FileRef file, int maxSide, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSide, 1);
        var stat = await files.StatAsync(file, cancellationToken).ConfigureAwait(false);
        if (Unlisted(stat) is { } failed) return BlpImageResult.From(failed);
        var inspection = await InspectAsync(files, stat.File!, cancellationToken).ConfigureAwait(false);
        if (inspection.State != BlpImageState.Ready) return BlpImageResult.From(inspection);
        // The decoder's plan (Blp2Decoder.DecodeLargestWithinAsync), from the header only.
        var level = inspection.Mips!.ToList().FindIndex(mip => mip.Width <= maxSide && mip.Height <= maxSide && mip.Storage != Blp2MipStorage.Compact);
        if (level < 0)
            return new(BlpImageState.MipUnavailable, null, BlpImageCache.None, [], null, "BLP_MIP_UNAVAILABLE",
                "No declared mip fits the requested size; no level was resized or substituted.", null);
        return await DecodeLevelAsync(files, stat.File!, level, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The shared identity text of one decoded level.</summary>
    public string IdentityText(string contentKey, int mipLevel) =>
        string.Join('\n', IdentityFormat, contentKey, Blp2Decoder.Revision, "mip=" + mipLevel, "limits=" + limitsHash);

    /// <summary>
    /// The scoped identity text: <see cref="IdentityText"/>, <c>encoding-in=</c> product, build config and CDN config, then one
    /// <c>key NAME=STATUS</c> line per chunk key (a fingerprint, or <c>?</c> when unknown).
    /// </summary>
    public static string ScopedIdentityText(string identity, BuildRef build, IReadOnlyList<string> chunkKeys, ITactKeyStatus status)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(status);
        var text = new StringBuilder(identity).Append("\nencoding-in=").Append(build.Product).Append(' ').Append(build.BuildConfig).Append(' ').Append(build.CdnConfig);
        foreach (var name in chunkKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            TactKeyNames.Check(name, nameof(chunkKeys));
            var fingerprint = status.FingerprintOf(name);
            if (fingerprint is not null && !TactKeyNames.IsFingerprint(fingerprint))
                throw new InvalidOperationException("The key status gave a fingerprint that isn't 1-128 printable ASCII characters without spaces, and not \"?\".");
            text.Append("\nkey ").Append(name).Append('=').Append(fingerprint ?? "?");
        }
        return text.ToString();
    }

    private static GeneratedKey KeyOf(string identity) => GeneratedKey.Hash(Encoding.UTF8.GetBytes(identity));

    private async Task<BlpInspection> InspectAsync(IBuildFiles files, BuildFile listed, CancellationToken ct)
    {
        // The decoder refuses a short file before reading it, so a short file is Invalid even when its bytes lie in a hole.
        // A Found file always has an encoded copy; ENCODING listing it without a decoded size is what layer 4's reads answer Corrupt.
        if (listed.Size is not { } size)
            return FileFailed(FileReadState.Corrupt, "FILES_NO_DECLARED_SIZE", "ENCODING lists the file without a decoded size.", null);
        if (size < HeaderBytes) return Refused(null, new BlpFormatException("BLP_INVALID", "BLP2 core header is truncated."));
        var read = await files.ReadRangeAsync(FileRef.Id(listed.FileDataId), 0, HeaderBytes, ct).ConfigureAwait(false);
        if (read.State is FileReadState.Partial or FileReadState.Protected)
            return Protected([.. read.Holes.Select(hole => hole.KeyName)]);
        if (read.State != FileReadState.Complete) return FileFailed(read.State, read.Code, read.Message, read.ObservedAt);
        var header = read.Bytes!.Value.ToArray();
        if (header.Length < HeaderBytes) return Refused(null, new BlpFormatException("BLP_INVALID", "BLP2 core header is truncated."));
        var facts = Facts(header);
        try
        {
            await using var view = new HeaderView(header, size);
            var mips = await Blp2Decoder.ReadMipsAsync(view, limits, ct).ConfigureAwait(false);
            return new(BlpImageState.Ready, facts, EncodingOf(facts!), mips, [], null, null, null, null);
        }
        catch (BlpFormatException refused) { return Refused(facts, refused); }
    }

    private async Task<BlpImageResult> DecodeLevelAsync(IBuildFiles files, BuildFile listed, int level, CancellationToken ct)
    {
        var identity = IdentityText(listed.ContentKey, level);
        var shared = KeyOf(identity);
        if (await CachedAsync(shared, level, ct).ConfigureAwait(false) is { } hit) return BlpImageResult.Decoded(hit, BlpImageCache.Hit);
        var open = await files.OpenReadAsync(FileRef.Id(listed.FileDataId), ct).ConfigureAwait(false);
        if (open.Stream is not { } stream) return BlpImageResult.From(FileFailed(open.State, open.Code, open.Message, open.ObservedAt));
        await using (stream.ConfigureAwait(false))
        {
            var key = shared;
            if (stream.ChunkKeys is { Count: > 0 } chunkKeys)
            {
                key = KeyOf(ScopedIdentityText(identity, files.Build, chunkKeys, keys(files.Build)));
                if (await CachedAsync(key, level, ct).ConfigureAwait(false) is { } scopedHit) return BlpImageResult.Decoded(scopedHit, BlpImageCache.Hit);
            }
            Blp2Image? image = null;
            FileHole? holeHit = null;
            try { image = await Blp2Decoder.DecodeAsync(stream, level, limits, ct).ConfigureAwait(false); }
            catch (FileHoleException hole) { holeHit = hole.Hole; }
            catch (BlpFormatException refused) { return BlpImageResult.From(Refused(null, refused)); }
            catch (FileReadException failed) { return BlpImageResult.From(FileFailed(failed.State, failed.Code, failed.Message, failed.ObservedAt)); }
            if (holeHit is null) return BlpImageResult.Decoded(image!, await StoreAsync(key, image!, ct).ConfigureAwait(false));
            // Reading the header again for the hole keys can fail in its own right; that failure is the answer.
            try { return BlpImageResult.From(Protected(await NeededHoleKeysAsync(stream, level, holeHit, ct).ConfigureAwait(false))); }
            catch (FileReadException failed) { return BlpImageResult.From(FileFailed(failed.State, failed.Code, failed.Message, failed.ObservedAt)); }
        }
    }

    /// <summary>The keys of the holes over the header, the palette and the level's declared range; the hole hit when none match.</summary>
    private static async Task<IReadOnlyList<string>> NeededHoleKeysAsync(FileReadStream stream, int level, FileHole hit, CancellationToken ct)
    {
        var holes = stream.Holes;
        List<(long From, long To)> needed = [(0, HeaderBytes)];
        if (!Overlaps(holes, 0, HeaderBytes))
        {
            var header = new byte[HeaderBytes];
            try
            {
                stream.Position = 0;
                await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
            }
            catch (FileHoleException) { return [hit.KeyName]; }
            if (header[8] == 1) needed.Add((HeaderBytes, HeaderBytes + PaletteBytes));
            long offset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20 + level * 4)), length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84 + level * 4));
            needed.Add((offset, offset + length));
        }
        var names = holes.Where(hole => needed.Any(range => hole.Offset < range.To && range.From < hole.Offset + hole.Length))
            .Select(hole => hole.KeyName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return names.Length > 0 ? names : [hit.KeyName];
    }

    private static bool Overlaps(IReadOnlyList<FileHole> holes, long from, long to) => holes.Any(hole => hole.Offset < to && from < hole.Offset + hole.Length);

    private async Task<Blp2Image?> CachedAsync(GeneratedKey key, int level, CancellationToken ct)
    {
        var read = await store.ReadAsync(key, ct).ConfigureAwait(false);
        if (read.State == GeneratedReadState.Absent) return null;
        if (read.State == GeneratedReadState.Present && BlpImagePayload.Read(read.Bytes!, level, limits) is { } image) return image;
        await store.DeleteAsync([key], ct).ConfigureAwait(false);
        return null;
    }

    private async Task<BlpImageCache> StoreAsync(GeneratedKey key, Blp2Image image, CancellationToken ct)
    {
        if (BlpImagePayload.Size(image) > GeneratedLimits.MaxObjectBytes) return BlpImageCache.TooLarge;
        try
        {
            await store.PutAsync([new GeneratedPut(key, BlpImagePayload.Write(image))], ct).ConfigureAwait(false);
            return BlpImageCache.Stored;
        }
        catch (GeneratedStoreException) { return BlpImageCache.StoreFailed; }
    }

    private static BlpInspection? Unlisted(FileStat stat) => stat.State switch
    {
        FileLookupState.Found => null,
        FileLookupState.NotInLocale => FileFailed(FileReadState.NotInLocale, null, "The build lists the file, but not for this locale.", null),
        FileLookupState.NotInEncoding => FileFailed(FileReadState.NotInEncoding, null, "The build has no encoded copy of the file.", null),
        _ => FileFailed(FileReadState.UnknownFile, null, "The build doesn't list the file.", null),
    };

    private static BlpInspection FileFailed(FileReadState state, string? code, string? message, DateTimeOffset? observedAt) =>
        new(BlpImageState.FileFailed, null, null, null, [], state, code, message, observedAt);

    private static BlpInspection Protected(IReadOnlyList<string> holeKeys) =>
        new(BlpImageState.Protected, null, null, null, [.. holeKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], null, null,
            "Bytes this image needs are encrypted with TACT keys that aren't known.", null);

    private static BlpInspection Refused(BlpHeader? header, BlpFormatException refused) =>
        new(refused.Code switch
        {
            "BLP_UNSUPPORTED" => BlpImageState.Unsupported,
            "BLP_MIP_UNAVAILABLE" => BlpImageState.MipUnavailable,
            "BLP_LIMIT" => BlpImageState.LimitExceeded,
            _ => BlpImageState.Invalid,
        }, header, null, null, [], null, refused.Code, refused.Message, null);

    private static BlpHeader? Facts(byte[] header)
    {
        if (!header.AsSpan(0, 4).SequenceEqual("BLP2"u8) || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != 1) return null;
        var declared = 0;
        while (declared < 16 && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20 + declared * 4)) != 0
            && BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84 + declared * 4)) != 0) declared++;
        return new(header[8], header[9], header[10], header[11], BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)),
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16)), declared);
    }

    // Only reached once the decoder accepted the header, so exactly one candidate is the supported profile.
    private static Blp2Encoding EncodingOf(BlpHeader header) => header.ColorEncoding switch
    {
        1 => Blp2Encoding.Paletted,
        3 => Blp2Encoding.Bgra8,
        _ => new[] { Blp2Encoding.Bc1, Blp2Encoding.Bc2, Blp2Encoding.Bc3 }.First(e => Blp2Decoder.IsSupported(e, header.AlphaDepth, header.AlphaEncoding)),
    };

    /// <summary>
    /// The first 148 bytes of a file whose declared length is <paramref name="length"/>, for the decoder's header checks. Any read
    /// past the header throws: nothing else is there to read, and nothing is filled in.
    /// </summary>
    private sealed class HeaderView(byte[] header, long length) : Stream
    {
        private long position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => position = value; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length == 0 || position >= length) return ValueTask.FromResult(0);
            if (position >= header.Length) throw new InvalidOperationException("Only the header was read.");
            var count = (int)Math.Min(buffer.Length, header.Length - position);
            header.AsSpan((int)position, count).CopyTo(buffer.Span);
            position += count;
            return ValueTask.FromResult(count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Reads are async only.");
        public override long Seek(long offset, SeekOrigin origin) => position = origin switch
        {
            SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, _ => length + offset,
        };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}

/// <summary>
/// A cached image: "VMIM", version 1, then every <see cref="Blp2Image"/> field (little-endian), each declared mip, and the RGBA.
/// <see cref="Read"/> answers null for anything that isn't exactly that shape for the level asked for.
/// </summary>
internal static class BlpImagePayload
{
    private const int Version = 1;
    private const int Fixed = 4 + 4 + 4 * 3 + 4 + 4 * 3 + 4 + 4;
    private const int MipBytes = 4 * 3 + 8 * 2 + 1;

    public static long Size(Blp2Image image) => Fixed + (long)image.Mips.Count * MipBytes + image.Rgba.Length;

    public static byte[] Write(Blp2Image image)
    {
        var bytes = new byte[Size(image)];
        "VMIM"u8.CopyTo(bytes);
        var at = 4;
        void I(int value) { BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), value); at += 4; }
        void L(long value) { BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(at), value); at += 8; }
        I(Version); I(image.Width); I(image.Height); I(image.RowStride);
        bytes[at++] = (byte)image.Encoding; bytes[at++] = image.AlphaDepth; bytes[at++] = image.AlphaEncoding; bytes[at++] = (byte)image.MipFlags;
        I(image.MipLevel); I(image.BaseWidth); I(image.BaseHeight); I(image.Mips.Count);
        foreach (var mip in image.Mips) { I(mip.Level); I(mip.Width); I(mip.Height); L(mip.Offset); L(mip.ByteLength); bytes[at++] = (byte)mip.Storage; }
        I(image.Rgba.Length);
        image.Rgba.CopyTo(bytes, at);
        return bytes;
    }

    public static Blp2Image? Read(byte[] bytes, int level, Blp2Limits limits)
    {
        if (bytes.Length < Fixed || !bytes.AsSpan(0, 4).SequenceEqual("VMIM"u8)) return null;
        var at = 4;
        int I() { var v = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at)); at += 4; return v; }
        long L() { var v = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at)); at += 8; return v; }
        if (I() != Version) return null;
        int width = I(), height = I(), stride = I();
        var encoding = (Blp2Encoding)bytes[at++]; byte depth = bytes[at++], alphaEncoding = bytes[at++], flags = bytes[at++];
        int mipLevel = I(), baseWidth = I(), baseHeight = I(), count = I();
        if (!Enum.IsDefined(encoding) || !Blp2Decoder.IsSupported(encoding, depth, alphaEncoding) || !Blp2Decoder.IsMipFlags(flags)
            || count is < 1 or > 16 || mipLevel != level || mipLevel >= count
            || bytes.Length < Fixed + (long)count * MipBytes) return null;
        var mips = new Blp2Mip[count];
        for (var i = 0; i < count; i++)
        {
            int mipNumber = I(), mipWidth = I(), mipHeight = I(); long offset = L(), length = L(); var storage = (Blp2MipStorage)bytes[at++];
            if (mipNumber != i || !Enum.IsDefined(storage) || mipWidth < 1 || mipHeight < 1 || offset < 1 || length < 1) return null;
            mips[i] = new(mipNumber, mipWidth, mipHeight, offset, length, storage);
        }
        var rgbaLength = I();
        var selected = mips[mipLevel];
        if (width != selected.Width || height != selected.Height || selected.Storage != Blp2MipStorage.Exact
            || baseWidth != mips[0].Width || baseHeight != mips[0].Height || baseWidth > limits.MaxDimension || baseHeight > limits.MaxDimension
            || stride != (long)width * 4 || rgbaLength != (long)width * height * 4 || rgbaLength > limits.MaxRgbaBytes
            || bytes.Length - at != rgbaLength) return null;
        return new(width, height, stride, bytes.AsSpan(at).ToArray(), encoding, depth, alphaEncoding, mipLevel, baseWidth, baseHeight, mips.AsReadOnly(), flags);
    }
}
