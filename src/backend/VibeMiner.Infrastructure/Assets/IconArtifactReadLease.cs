namespace VibeMiner.Infrastructure;

public sealed partial class IconArtifacts
{
    private sealed class ReadLease(Stream backing, Action release) : Stream
    {
        private int closed;
        public override bool CanRead => Volatile.Read(ref closed) == 0 && backing.CanRead;
        public override bool CanSeek => Volatile.Read(ref closed) == 0 && backing.CanSeek;
        public override bool CanWrite => false;
        public override long Length => backing.Length;
        public override long Position { get => backing.Position; set => backing.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => backing.Seek(offset, origin);
        public override int Read(byte[] buffer, int offset, int count) => backing.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => backing.Read(buffer);
        public override int ReadByte() => backing.ReadByte();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => backing.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => backing.ReadAsync(buffer, cancellationToken);
        public override void Flush() { ObjectDisposedException.ThrowIf(Volatile.Read(ref closed) != 0, this); }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        { if (Interlocked.Exchange(ref closed, 1) == 0) { try { if (disposing) backing.Dispose(); } finally { release(); } } base.Dispose(disposing); }
        public override async ValueTask DisposeAsync()
        { if (Interlocked.Exchange(ref closed, 1) == 0) { try { await backing.DisposeAsync(); } finally { release(); } } GC.SuppressFinalize(this); }
    }
}
