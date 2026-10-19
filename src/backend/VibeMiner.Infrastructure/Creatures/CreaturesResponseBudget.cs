using System.Text.Json;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed class CreaturesResponseCounter : Stream
{
    private int count;
    private void Count(int bytes) { if (bytes > (512 << 10) - 1 - count) throw new DiscoveryException("CREATURES_RESULT_LIMIT", "The complete Creature result exceeds its bounded response frame."); count += bytes; }
    internal static async Task RequireAsync<T>(T result, CancellationToken token)
    {
        using var stream = new CreaturesResponseCounter();
        await JsonSerializer.SerializeAsync(stream, new { id = new string('r', 128), result }, CreaturesPolicy.Json, token);
    }
    public override void Write(byte[] buffer, int offset, int count) => Count(count);
    public override void Write(ReadOnlySpan<byte> buffer) => Count(buffer.Length);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Count(buffer.Length); return ValueTask.CompletedTask; }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); Count(count); return Task.CompletedTask; }
    public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
    public override long Length => count; public override long Position { get => count; set => throw new NotSupportedException(); }
    public override void Flush() { } public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
}
