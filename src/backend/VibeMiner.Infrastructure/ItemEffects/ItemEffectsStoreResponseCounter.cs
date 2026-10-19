using System.Text.Json;
using System.Text.Json.Serialization;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal static class ItemEffectsStoreResponseCounter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 32, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) } };
    internal static async Task RequireAsync<T>(T result, CancellationToken token, bool reserveTargetFailures = false)
    {
        // Account for fixed final-fence failure Problems even when they expand a tiny success outcome.
        await using var counter = new Counter((512 << 10) - 1 - (reserveTargetFailures ? 18 * 512 : 0));
        await JsonSerializer.SerializeAsync(counter, new { protocolVersion = 1, id = new string('\u0800', 128), result }, Json, token);
    }
    private sealed class Counter(int maximum) : Stream
    {
        private long written;
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => written; public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Add(count);
        public override void Write(ReadOnlySpan<byte> buffer) => Add(buffer.Length);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); Add(buffer.Length); return ValueTask.CompletedTask; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Add(count); return Task.CompletedTask; }
        private void Add(int count)
        {
            if (count < 0 || count > maximum - written)
                throw new DiscoveryException("ITEM_EFFECTS_RESULT_LIMIT", "The effects result exceeds the complete response byte limit.");
            written += count;
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
