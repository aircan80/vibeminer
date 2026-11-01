using VibeMiner.Files.Formats;

namespace VibeMiner.Files;

/// <summary>
/// <see cref="FileSystem"/>'s <see cref="FileReadStream"/>: one file's layout and holes, fixed at open, and its
/// <see cref="FileReader"/>, which needs nothing from the handle, so the stream outlives it. A read decodes the chunk at the
/// position and returns bytes up to that chunk's end; the last decoded chunk is kept while reads stay in it. Not safe for
/// concurrent use: one read at a time.
/// </summary>
internal sealed class BuildFileStream : FileReadStream
{
    /// <summary>The most encoded bytes one hole-listing call reads.</summary>
    public const int PeekWindow = 1 << 20;
    /// <summary>The biggest chunk read whole while listing holes; a bigger one gets only its envelope read, so opening doesn't read most of the file.</summary>
    public const int PeekWholeChunk = 64 << 10;

    private readonly FileReader reader;
    private readonly BlteLayout layout;
    private readonly FileHole[] holes;
    private long position;
    private bool disposed;
    private byte[]? chunk;
    private int chunkIndex = -1;

    private BuildFileStream(FileReader reader, BlteLayout layout, FileHole[] holes)
    {
        this.reader = reader; this.layout = layout; this.holes = holes;
    }

    /// <summary>
    /// Reads the layout and lists every hole from the chunks' unverified envelopes: adjacent chunks up to
    /// <see cref="PeekWholeChunk"/> in windows of <see cref="PeekWindow"/> per layer 2 call, a bigger chunk's envelope alone.
    /// Decodes nothing. A headerless file whose one chunk is over the read limit is TooLarge. Throws
    /// <see cref="FileReadFailure"/> and <see cref="BlteFormatException"/>.
    /// </summary>
    public static async Task<FileOpen> OpenAsync(FileReader reader, CancellationToken cancellationToken)
    {
        if (reader.Size == 0) return new(FileReadState.Complete, new BuildFileStream(reader, new(true, 0, 0, 0, []), []));
        var layout = await reader.LayoutAsync(cancellationToken).ConfigureAwait(false);
        foreach (var c in layout.Chunks) reader.CheckChunk(layout, c);

        var holes = new List<FileHole>();
        var chunks = layout.Chunks;
        var window = new byte[Math.Min(PeekWindow, (int)Math.Min(int.MaxValue, layout.EncodedLength))];
        for (var i = 0; i < chunks.Count;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A big chunk: just its envelope. Otherwise adjacent small chunks up to the window in one call.
            if (chunks[i].EncodedSize > PeekWholeChunk)
            {
                var length = Math.Min(BlteChunks.EnvelopeLength, chunks[i].EncodedSize);
                await reader.ReadEncodedAsync(chunks[i].EncodedOffset, window.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                AddHole(reader, chunks[i], window.AsSpan(0, length), holes);
                i++;
                continue;
            }
            var runEnd = i;
            var runBytes = chunks[i].EncodedSize;
            while (runEnd + 1 < chunks.Count && chunks[runEnd + 1].EncodedSize <= PeekWholeChunk && runBytes + chunks[runEnd + 1].EncodedSize <= PeekWindow)
                runBytes += chunks[++runEnd].EncodedSize;
            await reader.ReadEncodedAsync(chunks[i].EncodedOffset, window.AsMemory(0, runBytes), cancellationToken).ConfigureAwait(false);
            for (int c = i, at = 0; c <= runEnd; at += chunks[c].EncodedSize, c++)
                AddHole(reader, chunks[c], window.AsSpan(at, Math.Min(BlteChunks.EnvelopeLength, chunks[c].EncodedSize)), holes);
            i = runEnd + 1;
        }

        var inHoles = holes.Sum(h => h.Length);
        var state = inHoles == 0 ? FileReadState.Complete : inHoles == layout.DecodedLength ? FileReadState.Protected : FileReadState.Partial;
        return new(state, new BuildFileStream(reader, layout, [.. holes]));
    }

    /// <summary>Adds the chunk's hole, merged into the last one when it is the same key and adjacent. An empty chunk covers nothing.</summary>
    private static void AddHole(FileReader reader, BlteChunk c, ReadOnlySpan<byte> prefix, List<FileHole> holes)
    {
        if (c.DecodedSize == 0 || reader.HoleKeyOf(prefix) is not { } key) return;
        if (holes.Count > 0 && holes[^1] is var last && last.KeyName == key && last.Offset + last.Length == c.DecodedOffset)
            holes[^1] = last with { Length = last.Length + c.DecodedSize };
        else holes.Add(new(c.DecodedOffset, c.DecodedSize, key));
    }

    public override IReadOnlyList<FileHole> Holes => holes;
    public override bool CanRead => !disposed;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => false;
    public override long Length { get { ThrowIfDisposed(); return layout.DecodedLength; } }

    public override long Position
    {
        get { ThrowIfDisposed(); return position; }
        set { ThrowIfDisposed(); ArgumentOutOfRangeException.ThrowIfNegative(value); position = value; }
    }

    /// <summary>A position past the end is allowed and reads 0 bytes; before the start throws <see cref="IOException"/>.</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        ThrowIfDisposed();
        var target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => layout.DecodedLength + offset,
            _ => throw new ArgumentException("An unknown seek origin.", nameof(origin)),
        };
        if (target < 0) throw new IOException("A seek before the start of the stream.");
        return position = target;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>
    /// Up to the end of the chunk at the position. Throws <see cref="FileHoleException"/> (the whole listed hole) when the
    /// position is in a hole, and <see cref="FileReadException"/> when the chunk's bytes fail; the stream stays usable.
    /// </summary>
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.Length == 0 || position >= layout.DecodedLength) return 0;
        if (HoleAt(position) is { } hole) throw new FileHoleException(hole);
        var index = layout.IndexAt(position);
        var c = layout.Chunks[index];
        if (index != chunkIndex) await LoadAsync(index, c, cancellationToken).ConfigureAwait(false);
        var from = (int)(position - c.DecodedOffset);
        var count = Math.Min(buffer.Length, c.DecodedSize - from);
        chunk!.AsSpan(from, count).CopyTo(buffer.Span);
        position += count;
        return count;
    }

    /// <summary>The listed hole holding <paramref name="at"/>, if any.</summary>
    private FileHole? HoleAt(long at)
    {
        int low = 0, high = holes.Length - 1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (at < holes[mid].Offset) high = mid - 1;
            else if (at >= holes[mid].Offset + holes[mid].Length) low = mid + 1;
            else return holes[mid];
        }
        return null;
    }

    /// <summary>
    /// Fetches and decodes one chunk into the kept buffer. A layer 2 failure leaves the kept chunk as it was; a failed decode
    /// drops it.
    /// </summary>
    private async Task LoadAsync(int index, BlteChunk c, CancellationToken cancellationToken)
    {
        var encoded = FileReader.Rent(c.EncodedSize);
        try
        {
            await reader.ReadEncodedAsync(c.EncodedOffset, encoded.AsMemory(0, c.EncodedSize), cancellationToken).ConfigureAwait(false);
            Decode(index, c, encoded);
        }
        catch (FileReadFailure f) { throw new FileReadException(f.State, f.Code, f.Message, f.ObservedAt); }
        catch (BlteFormatException b) { throw new FileReadException(b.State, b.Code, b.Message); }
        finally { FileReader.Return(encoded, c.EncodedSize); }
    }

    private void Decode(int index, BlteChunk c, byte[] encoded)
    {
        chunkIndex = -1;
        // Clear and drop the old buffer before a bigger one, so at most one decoded chunk is held and no decrypted bytes linger.
        if (chunk is null || chunk.Length < c.DecodedSize)
        {
            if (chunk is not null) Array.Clear(chunk);
            chunk = null;
            chunk = GC.AllocateUninitializedArray<byte>(c.DecodedSize);
        }
        var result = reader.Decode(layout, c, encoded.AsSpan(0, c.EncodedSize), chunk.AsSpan(0, c.DecodedSize));
        // Listed as readable at open, but the key is gone now.
        if (result.IsHole) throw new FileHoleException(new(c.DecodedOffset, c.DecodedSize, result.KeyName!));
        chunkIndex = index;
    }

    /// <summary>Async only.</summary>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("The stream reads async only.");
    public override int Read(Span<byte> buffer) => throw new NotSupportedException("The stream reads async only.");
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The stream is read-only.");
    public override void SetLength(long value) => throw new NotSupportedException("The stream is read-only.");
    public override void Flush() { }

    protected override void Dispose(bool disposing)
    {
        disposed = true;
        // It can hold decrypted bytes.
        if (chunk is not null) Array.Clear(chunk);
        chunk = null;
        chunkIndex = -1;
        base.Dispose(disposing);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
