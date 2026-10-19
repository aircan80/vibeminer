using System.Buffers.Binary;
using VibeMiner.Domain;

namespace VibeMiner.Formats.Wow;

// Buffered caller-owned async input. The extra byte at the bound is solely an EOF/limit probe.
internal sealed class RootStreamReader(Stream source, long byteLimit, CancellationToken cancellationToken)
{
    private readonly byte[] buffer = new byte[65_536];
    private int offset, available;
    private long received;
    public long Position { get; private set; }
    public string Context { get; set; } = "";

    public ValueTask<byte?> ReadOptionalByteAsync()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offset < available)
        {
            Position++;
            return ValueTask.FromResult<byte?>(buffer[offset++]);
        }
        return RefillByteAsync();
    }

    private async ValueTask<byte?> RefillByteAsync()
    {
        var remaining = byteLimit - received;
        var requested = remaining >= buffer.Length ? buffer.Length : (int)remaining + 1;
        cancellationToken.ThrowIfCancellationRequested();
        available = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        offset = 0;
        if (available > remaining) throw Fail("ROOT_LIMIT", "ROOT decoded byte limit exceeded.");
        received += available;
        if (available == 0) return null;
        Position++;
        return buffer[offset++];
    }

    public async ValueTask<byte> ReadByteAsync() =>
        await ReadOptionalByteAsync() ?? throw Fail("ROOT_INVALID", "ROOT stream is truncated.");

    public ValueTask<uint> ReadUInt32Async()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (available - offset >= 4)
        {
            var result = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
            offset += 4;
            Position += 4;
            return ValueTask.FromResult(result);
        }
        return ReadSplitUInt32Async();
    }

    private async ValueTask<uint> ReadSplitUInt32Async()
    {
        uint result = 0;
        for (var shift = 0; shift < 32; shift += 8)
            result |= (uint)await ReadByteAsync() << shift;
        return result;
    }

    public async ValueTask<ulong> ReadUInt64Async()
    {
        var low = await ReadUInt32Async();
        return low | ((ulong)await ReadUInt32Async() << 32);
    }

    public async ValueTask<uint?> ReadOptionalUInt32Async()
    {
        var first = await ReadOptionalByteAsync();
        if (first is null) return null;
        uint result = first.Value;
        for (var shift = 8; shift < 32; shift += 8)
            result |= (uint)await ReadByteAsync() << shift;
        return result;
    }

    public async ValueTask ReadExactlyAsync(Memory<byte> target)
    {
        while (!target.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset == available)
            {
                var first = await ReadOptionalByteAsync();
                if (first is null) throw Fail("ROOT_INVALID", "ROOT stream is truncated.");
                target.Span[0] = first.Value;
                target = target[1..];
                continue;
            }
            var count = Math.Min(target.Length, available - offset);
            buffer.AsMemory(offset, count).CopyTo(target);
            target = target[count..];
            offset += count;
            Position += count;
        }
    }

    public void RequireBudget(long bytes)
    {
        if (bytes < 0 || bytes > byteLimit - Position)
            throw Fail("ROOT_LIMIT", "ROOT declared framing exceeds the decoded byte limit.");
    }

    public DiscoveryException Fail(string code, string message) =>
        new(code, $"{message} [decodedOffset={Position}{(Context.Length == 0 ? "" : ", " + Context)}]");
}
