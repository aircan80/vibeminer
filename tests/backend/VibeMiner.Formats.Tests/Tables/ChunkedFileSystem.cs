using System.IO.Compression;
using VibeMiner.Builds;
using VibeMiner.Files;

namespace VibeMiner.Formats.Tests.Tables;

/// <summary>
/// Layer 4 whose streams decode like the real one: the file is split into Deflate-compressed chunks of <paramref name="chunkSize"/>
/// bytes and the stream keeps only the last decoded chunk, so a read in another chunk decodes it again (#192). Counts chunk decodes.
/// <paramref name="failing"/> is a chunk index whose decode fails Corrupt (<c>FILES_CORRUPT</c>). Files with holes aren't modelled.
/// </summary>
public sealed class ChunkedFileSystem(IFileSystem inner, int chunkSize, int? failing = null) : IFileSystem
{
    private readonly int chunkSize = chunkSize;
    private readonly int? failing = failing;
    private long decodes;
    public long Decodes => Interlocked.Read(ref decodes);
    public VibeMiner.Files.Names.IFileNames Names => inner.Names;

    public async Task<FilesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
    {
        var opened = await inner.ForAsync(build, locale, cancellationToken);
        return opened.Files is { } files ? opened with { Files = new Handle(this, files) } : opened;
    }

    private sealed class Handle(ChunkedFileSystem owner, IBuildFiles inner) : IBuildFiles
    {
        public BuildRef Build => inner.Build;
        public string Locale => inner.Locale;
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        public IAsyncEnumerable<BuildFile> ListAsync(FileFilter filter, CancellationToken cancellationToken) => inner.ListAsync(filter, cancellationToken);
        public Task<FileStat> StatAsync(FileRef file, CancellationToken cancellationToken) => inner.StatAsync(file, cancellationToken);
        public Task<bool> ExistsAsync(FileRef file, CancellationToken cancellationToken) => inner.ExistsAsync(file, cancellationToken);
        public Task<EnsureResult> EnsureAsync(EnsureSelection selection, EnsurePriority priority, IProgress<EnsureProgress>? progress, CancellationToken cancellationToken) =>
            inner.EnsureAsync(selection, priority, progress, cancellationToken);
        public Task<FileRead> GetAsync(FileRef file, CancellationToken cancellationToken) => inner.GetAsync(file, cancellationToken);
        public Task<FileRead> ReadRangeAsync(FileRef file, long offset, int length, CancellationToken cancellationToken) =>
            inner.ReadRangeAsync(file, offset, length, cancellationToken);

        public async Task<FileOpen> OpenReadAsync(FileRef file, CancellationToken cancellationToken)
        {
            var opened = await inner.OpenReadAsync(file, cancellationToken);
            if (opened.Stream is not { } stream) return opened;
            await using (stream)
            {
                if (stream.Holes.Count > 0) throw new NotSupportedException("The chunked fake doesn't model holes.");
                var content = new byte[stream.Length];
                for (var filled = 0; filled < content.Length;) filled += await stream.ReadAsync(content.AsMemory(filled), cancellationToken);
                return opened with { Stream = new ChunkedStream(owner, content, owner.chunkSize, owner.failing) };
            }
        }
    }

    private sealed class ChunkedStream : FileReadStream
    {
        private readonly ChunkedFileSystem owner;
        private readonly byte[][] chunks;
        private readonly int chunkSize, length;
        private readonly int? failing;
        private readonly byte[] chunk;
        private int kept = -1;
        private long position;

        public ChunkedStream(ChunkedFileSystem owner, byte[] content, int chunkSize, int? failing)
        {
            this.owner = owner; this.chunkSize = chunkSize; this.failing = failing; length = content.Length;
            chunk = new byte[chunkSize];
            chunks = new byte[(content.Length + chunkSize - 1) / chunkSize][];
            for (var i = 0; i < chunks.Length; i++)
            {
                using var packed = new MemoryStream();
                using (var deflate = new DeflateStream(packed, CompressionLevel.Fastest, leaveOpen: true))
                    deflate.Write(content, i * chunkSize, Math.Min(chunkSize, content.Length - i * chunkSize));
                chunks[i] = packed.ToArray();
            }
        }

        public override IReadOnlyList<FileHole> Holes => [];
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => position; set => position = value; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length == 0 || position >= length) return ValueTask.FromResult(0);
            var index = (int)(position / chunkSize);
            if (index == failing) throw new FileReadException(FileReadState.Corrupt, "FILES_CORRUPT", "A synthetic chunk is corrupt.");
            if (index != kept)
            {
                Interlocked.Increment(ref owner.decodes);
                using var deflate = new DeflateStream(new MemoryStream(chunks[index]), CompressionMode.Decompress);
                deflate.ReadExactly(chunk, 0, Math.Min(chunkSize, length - index * chunkSize));
                kept = index;
            }
            var from = (int)(position - (long)index * chunkSize);
            var count = Math.Min(buffer.Length, Math.Min(chunkSize, length - index * chunkSize) - from);
            chunk.AsSpan(from, count).CopyTo(buffer.Span);
            position += count;
            return ValueTask.FromResult(count);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }
}
