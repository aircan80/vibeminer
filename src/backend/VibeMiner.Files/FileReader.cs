using System.Buffers;
using System.Security.Cryptography;
using VibeMiner.Builds;
using VibeMiner.Cdn;
using VibeMiner.Files.Formats;
using VibeMiner.ObjectAccess;

namespace VibeMiner.Files;

/// <summary>
/// A read ended in a state that carries nothing. <see cref="FileReader"/> throws it; callers turn it into a
/// <see cref="FileRead"/> or a <see cref="FileReadException"/>, keeping layer 2's code and time.
/// </summary>
internal sealed class FileReadFailure(FileReadState state, string? code, string? message, DateTimeOffset? observedAt = null)
    : Exception(message ?? code ?? state.ToString())
{
    public FileReadState State { get; } = state;
    public string? Code { get; } = code;
    public DateTimeOffset? ObservedAt { get; } = observedAt;
}

/// <summary>
/// Parsed BLTE layouts by EKey for one handle, so repeated ranges of a file read its header once. Bounded by entries and by
/// total chunks (a 65,536-chunk layout holds about 6 MiB); the least recently used goes first, the newest always stays.
/// Thread safe.
/// </summary>
internal sealed class LayoutCache(int maxEntries = 64, long maxChunks = 262_144)
{
    private readonly Lock gate = new();
    private readonly Dictionary<CdnKey, LinkedListNode<(CdnKey Key, BlteLayout Layout)>> byKey = [];
    private readonly LinkedList<(CdnKey Key, BlteLayout Layout)> order = new();
    private long chunks;

    public BlteLayout? Find(CdnKey eKey)
    {
        lock (gate)
        {
            if (!byKey.TryGetValue(eKey, out var node)) return null;
            order.Remove(node);
            order.AddFirst(node);
            return node.Value.Layout;
        }
    }

    public void Add(CdnKey eKey, BlteLayout layout)
    {
        lock (gate)
        {
            if (byKey.Remove(eKey, out var old)) { order.Remove(old); chunks -= old.Value.Layout.Chunks.Count; }
            byKey[eKey] = order.AddFirst((eKey, layout));
            chunks += layout.Chunks.Count;
            while (order.Count > 1 && (order.Count > maxEntries || chunks > maxChunks))
            {
                var last = order.Last!;
                order.RemoveLast();
                byKey.Remove(last.Value.Key);
                chunks -= last.Value.Layout.Chunks.Count;
            }
        }
    }
}

/// <summary>
/// One file's encoded copy as layer 2 serves it: the request (EKey, declared encoded size, archive location), the build's CDN
/// context and the TACT keys. Reads the BLTE prefix and header once per handle (through <see cref="LayoutCache"/>), then only
/// the encoded bytes of the chunks a range overlaps, at most <see cref="MaxRangeBytes"/> per layer 2 call. Methods throw
/// <see cref="FileReadFailure"/> and <see cref="BlteFormatException"/>; cancellation throws <see cref="OperationCanceledException"/>.
/// </summary>
internal sealed class FileReader
{
    /// <summary>The most encoded bytes one layer 2 range call reads. A bigger chunk is read in pieces into one buffer.</summary>
    public const int MaxRangeBytes = 4 << 20;

    private readonly IObjectSource objects;
    private readonly CdnContext? cdn;
    private readonly ITactKeys keys;
    private readonly LayoutCache layouts;
    private readonly long maxReadBytes;
    private readonly Action decoded;

    /// <param name="variant">An encoded variant with a declared decoded size.</param>
    /// <param name="decoded">Called once per chunk decode.</param>
    public FileReader(IObjectSource objects, FileVariant variant, CdnContext? cdn, ITactKeys keys, LayoutCache layouts, long maxReadBytes,
        Action decoded)
    {
        EKey = variant.EKey ?? throw new ArgumentException("The variant has no encoded copy.", nameof(variant));
        Size = variant.DecodedSize ?? throw new ArgumentException("The variant has no declared size.", nameof(variant));
        CKey = variant.CKey;
        Request = Variants.RequestOf(variant);
        this.objects = objects; this.cdn = cdn; this.keys = keys; this.layouts = layouts; this.maxReadBytes = maxReadBytes; this.decoded = decoded;
    }

    public CdnKey EKey { get; }
    public CdnKey CKey { get; }
    /// <summary>The decoded size ENCODING declares.</summary>
    public long Size { get; }
    public ObjectRequest Request { get; }

    /// <summary>The file's layout, from the cache or read from layer 2 (8 prefix bytes, then the rest of the header), and checked against the declared sizes.</summary>
    public async Task<BlteLayout> LayoutAsync(CancellationToken cancellationToken)
    {
        if (layouts.Find(EKey) is { } cached) return Checked(cached);
        var declared = Request.DeclaredLength;
        if (declared is < 8) throw Corrupt("The encoded copy is shorter than the BLTE prefix.");
        var prefix = new byte[8];
        ObjectResult got;
        // Without a declared size, a copy under 8 bytes shows only as a range past its end.
        try { got = await RangeAsync(0, prefix, cancellationToken).ConfigureAwait(false); }
        catch (ArgumentOutOfRangeException) when (declared is null) { throw Corrupt("The encoded copy is shorter than the BLTE prefix."); }
        var encodedLength = got.Length ?? declared
            ?? throw new FileReadFailure(FileReadState.Corrupt, "FILES_ENCODED_LENGTH_UNKNOWN", "Neither layer 2 nor ENCODING gives the encoded length.");
        var headerLength = BlteChunks.HeaderLength(prefix, encodedLength);
        var header = prefix;
        if (headerLength > 0)
        {
            header = new byte[headerLength];
            prefix.CopyTo(header, 0);
            await ReadEncodedAsync(8, header.AsMemory(8), cancellationToken).ConfigureAwait(false);
        }
        var layout = BlteChunks.Parse(header, EKey, encodedLength, Size);
        layouts.Add(EKey, layout);
        return Checked(layout);
    }

    /// <summary>Fills <paramref name="destination"/> with encoded bytes from <paramref name="offset"/>, in calls of at most <see cref="MaxRangeBytes"/>.</summary>
    public async Task ReadEncodedAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        for (var at = 0; at < destination.Length; at += MaxRangeBytes)
            await RangeAsync(offset + at, destination.Slice(at, Math.Min(MaxRangeBytes, destination.Length - at)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary><see cref="BlteChunks.Decode"/>, counted.</summary>
    public ChunkResult Decode(BlteLayout layout, BlteChunk chunk, ReadOnlySpan<byte> encoded, Span<byte> destination)
    {
        decoded();
        return BlteChunks.Decode(layout, chunk, encoded, EKey, keys, destination);
    }

    /// <summary>
    /// A headerless file's one chunk over <c>MaxReadBytes</c> is TooLarge: decoding it needs the whole chunk in memory.
    /// Chunked chunks are bounded by <see cref="BlteChunks.MaxChunkBytes"/>.
    /// </summary>
    public void CheckChunk(BlteLayout layout, BlteChunk chunk)
    {
        if (layout.Headerless && (chunk.DecodedSize > maxReadBytes || chunk.EncodedSize > maxReadBytes))
            throw new FileReadFailure(FileReadState.TooLarge, "FILES_CHUNK_TOO_LARGE",
                $"The file is one {chunk.DecodedSize}-byte BLTE chunk, over the {maxReadBytes} a read decodes.");
    }

    /// <summary>Whether <paramref name="decodedFile"/> (the whole file) hashes to the content key.</summary>
    public bool MatchesContentKey(ReadOnlySpan<byte> decodedFile)
    {
        Span<byte> actual = stackalloc byte[16], expected = stackalloc byte[16];
        MD5.HashData(decodedFile, actual);
        CKey.WriteTo(expected);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Decoded bytes <paramref name="offset"/> to <paramref name="offset"/> + <paramref name="length"/>, already clamped to
    /// <see cref="Size"/>, <paramref name="length"/> over 0. Decodes only the chunks the range overlaps; adjacent ones are read
    /// in one layer 2 call. Returns maximal segments and holes; bytes are never zero-filled.
    /// </summary>
    public async Task<FileRead> ReadAsync(long offset, int length, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + length, Size);
        var layout = await LayoutAsync(cancellationToken).ConfigureAwait(false);
        var end = offset + length;
        int first = layout.IndexAt(offset), last = layout.IndexAt(end - 1);
        for (var c = first; c <= last; c++) CheckChunk(layout, layout.Chunks[c]);

        var built = new RangeBuilder(offset, end);
        for (var i = first; i <= last;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // One run: adjacent chunks up to MaxRangeBytes of encoded bytes, always at least one chunk.
            var runEnd = i;
            long runBytes = layout.Chunks[i].EncodedSize;
            while (runEnd < last && runBytes + layout.Chunks[runEnd + 1].EncodedSize <= MaxRangeBytes) runBytes += layout.Chunks[++runEnd].EncodedSize;
            var encoded = Rent((int)runBytes);
            try
            {
                await ReadEncodedAsync(layout.Chunks[i].EncodedOffset, encoded.AsMemory(0, (int)runBytes), cancellationToken).ConfigureAwait(false);
                DecodeRun(layout, i, runEnd, encoded, built);
            }
            finally { Return(encoded, (int)runBytes); }
            i = runEnd + 1;
        }
        return built.ToRead();
    }

    /// <summary>Decodes chunks <paramref name="from"/> to <paramref name="to"/> from their encoded bytes and adds each one's overlap with the range.</summary>
    private void DecodeRun(BlteLayout layout, int from, int to, byte[] encoded, RangeBuilder built)
    {
        var at = 0;
        for (var c = from; c <= to; c++)
        {
            var chunk = layout.Chunks[c];
            var bytes = encoded.AsSpan(at, chunk.EncodedSize);
            at += chunk.EncodedSize;
            // An empty chunk covers no byte of the range.
            if (chunk.DecodedSize == 0) continue;
            long start = Math.Max(built.Start, chunk.DecodedOffset), stop = Math.Min(built.End, chunk.DecodedOffset + chunk.DecodedSize);
            var whole = start == chunk.DecodedOffset && stop == chunk.DecodedOffset + chunk.DecodedSize;
            if (whole && !LooksLikeHole(bytes))
            {
                // A chunk inside the range decodes straight into the segment.
                var result = Decode(layout, chunk, bytes, built.Destination(start, chunk.DecodedSize));
                if (result.IsHole) built.Hole(start, stop, result.KeyName!); else built.Wrote(chunk.DecodedSize);
                continue;
            }
            var scratch = Rent(chunk.DecodedSize);
            try
            {
                var result = Decode(layout, chunk, bytes, scratch.AsSpan(0, chunk.DecodedSize));
                if (result.IsHole) built.Hole(start, stop, result.KeyName!);
                else
                {
                    var overlap = (int)(stop - start);
                    scratch.AsSpan((int)(start - chunk.DecodedOffset), overlap).CopyTo(built.Destination(start, overlap));
                    built.Wrote(overlap);
                }
            }
            finally { Return(scratch, chunk.DecodedSize); }
        }
    }

    /// <summary>
    /// An 'E' chunk whose key isn't known, from its unverified envelope: only to avoid opening a segment for it. The decode
    /// still decides; a malformed envelope is left for the decode to report after the checksum.
    /// </summary>
    private bool LooksLikeHole(ReadOnlySpan<byte> chunk)
    {
        try { return BlteChunks.Peek(chunk[..Math.Min(chunk.Length, BlteChunks.EnvelopeLength)]) is ((byte)'E', { } name) && keys.Find(name) is null; }
        catch (BlteFormatException) { return false; }
    }

    /// <summary>The key name (16 uppercase hex) of an 'E' chunk, its key known or not, else null; read as <see cref="HoleKeyOf"/> is.</summary>
    public static string? EncryptedKeyOf(ReadOnlySpan<byte> chunkPrefix) =>
        BlteChunks.Peek(chunkPrefix) is ((byte)'E', { } name) ? name.ToString("X16", System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// The key name (16 uppercase hex) of an 'E' chunk whose key isn't known, else null, from the chunk's first
    /// min(<see cref="BlteChunks.EnvelopeLength"/>, EncodedSize) bytes. Unverified, for listing holes; a bad envelope throws
    /// <see cref="BlteFormatException"/>. Not a decode, so not counted.
    /// </summary>
    public string? HoleKeyOf(ReadOnlySpan<byte> chunkPrefix) =>
        BlteChunks.Peek(chunkPrefix) is ((byte)'E', { } name) && keys.Find(name) is null
            ? name.ToString("X16", System.Globalization.CultureInfo.InvariantCulture) : null;

    /// <summary>A pooled buffer up to a chunk's size; a bigger one (a headerless file's chunk) is allocated so the pool doesn't keep it.</summary>
    internal static byte[] Rent(int size) => size <= BlteChunks.MaxChunkBytes ? ArrayPool<byte>.Shared.Rent(size) : GC.AllocateUninitializedArray<byte>(size);

    /// <summary>Returns a <see cref="Rent"/> buffer cleared, so decrypted bytes don't linger in the pool.</summary>
    internal static void Return(byte[] buffer, int size)
    {
        if (size <= BlteChunks.MaxChunkBytes) ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
    }

    private BlteLayout Checked(BlteLayout layout) =>
        layout.DecodedLength == Size && (Request.DeclaredLength is not { } declared || declared == layout.EncodedLength) ? layout
            : throw Corrupt("The BLTE layout disagrees with the sizes ENCODING declares.");

    private async Task<ObjectResult> RangeAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var result = await objects.GetRangeAsync(Request, offset, destination, cdn, FetchPriority.Interactive, cancellationToken).ConfigureAwait(false);
        return result.IsPresent ? result : throw new FileReadFailure(StateOf(result.State), result.Code, result.Message, result.ObservedAt);
    }

    /// <summary>Layer 2's states as read states. Refused means the bytes or declarations failed a check: Corrupt. Unsupported stays Unsupported.</summary>
    private static FileReadState StateOf(ObjectState state) => state switch
    {
        ObjectState.NotFound => FileReadState.Missing,
        ObjectState.NeedsNetwork => FileReadState.NeedsNetwork,
        ObjectState.Unavailable => FileReadState.Unavailable,
        ObjectState.StoreFailed => FileReadState.StoreFailed,
        ObjectState.Refused => FileReadState.Corrupt,
        ObjectState.Unsupported => FileReadState.Unsupported,
        _ => throw new InvalidOperationException($"Layer 2 answered {state} without bytes."),
    };

    private static FileReadFailure Corrupt(string message) => new(FileReadState.Corrupt, "BLTE_INVALID", message);

    /// <summary>
    /// Collects one range's decoded pieces in order into maximal segments and holes. A segment's buffer is sized to the rest of
    /// the range when it opens and trimmed when a hole closes it, so a Complete read is one buffer and never copied.
    /// </summary>
    private sealed class RangeBuilder(long start, long end)
    {
        private readonly List<FileSegment> segments = [];
        private readonly List<FileHole> holes = [];
        private byte[]? buffer;
        private long bufferAt;
        private int used;

        public long Start { get; } = start;
        public long End { get; } = end;

        /// <summary>Where the decoded bytes at <paramref name="at"/> go: the open segment, opened here if needed.</summary>
        public Span<byte> Destination(long at, int count)
        {
            if (buffer is null) { buffer = GC.AllocateUninitializedArray<byte>((int)(End - at)); bufferAt = at; used = 0; }
            if (bufferAt + used != at) throw new InvalidOperationException("Decoded pieces must arrive in order.");
            return buffer.AsSpan(used, count);
        }

        public void Wrote(int count) => used += count;

        public void Hole(long from, long to, string keyName)
        {
            Close();
            if (holes.Count > 0 && holes[^1] is var last && last.KeyName == keyName && last.Offset + last.Length == from)
                holes[^1] = last with { Length = to - last.Offset };
            else holes.Add(new(from, to - from, keyName));
        }

        private void Close()
        {
            if (buffer is not null && used > 0)
                segments.Add(new(bufferAt, used == buffer.Length ? buffer : buffer.AsSpan(0, used).ToArray()));
            buffer = null;
        }

        public FileRead ToRead()
        {
            Close();
            var state = holes.Count == 0 ? FileReadState.Complete : segments.Count == 0 ? FileReadState.Protected : FileReadState.Partial;
            return new(state, Start, End - Start, segments, holes);
        }
    }
}
