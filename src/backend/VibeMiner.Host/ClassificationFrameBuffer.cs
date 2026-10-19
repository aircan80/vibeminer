using VibeMiner.Domain;

namespace VibeMiner.Host;

/// <summary>A finite serialization sink; its contents are published only after complete encoding.</summary>
internal sealed class ClassificationFrameBuffer : Stream
{
    private readonly byte[] bytes;
    private readonly string limitCode;
    internal ClassificationFrameBuffer(int capacity, bool creatureFrame = false)
        : this(capacity, creatureFrame ? "CREATURES_RESULT_LIMIT" : "ITEM_CLASSIFICATIONS_RESULT_LIMIT") { }
    internal ClassificationFrameBuffer(int capacity, string limitCode)
    { bytes = new byte[capacity]; this.limitCode = limitCode; }
    private int length;
    internal ReadOnlyMemory<byte> Written => bytes.AsMemory(0, length);
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => length;
    public override long Position { get => length; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length > bytes.Length - length)
            throw new DiscoveryException(limitCode, "The complete result exceeds its bounded frame limit.");
        buffer.CopyTo(bytes.AsSpan(length)); length += buffer.Length;
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); Write(buffer, offset, count); return Task.CompletedTask; }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
