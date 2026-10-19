using System.Text;
using VibeMiner.Domain;

namespace VibeMiner.Protocols.Tact;

/// <summary>Bounded caller-owned decoded input with asynchronous buffered refills.</summary>
internal sealed class AsyncInventoryReader(Stream input, long maxDecodedBytes, string formatPrefix,
    CancellationToken cancellationToken)
{
    private readonly byte[] buffer = new byte[65_536];
    private int offset, end;
    private long received;
    public long ConsumedBytes { get; private set; }

    public void CheckCancellation() => cancellationToken.ThrowIfCancellationRequested();

    public async ValueTask<byte> ByteAsync()
    {
        var value = await TryByteAsync();
        return value ?? throw Invalid("Decoded inventory is truncated.");
    }

    public async ValueTask<byte?> TryByteAsync()
    {
        CheckCancellation();
        if (offset == end && !await RefillAsync()) return null;
        ConsumedBytes++;
        return buffer[offset++];
    }

    public async ValueTask<ushort> UInt16BigEndianAsync() => (ushort)((await ByteAsync() << 8) | await ByteAsync());
    public async ValueTask<uint> UInt32BigEndianAsync() => ((uint)await UInt16BigEndianAsync() << 16) | await UInt16BigEndianAsync();
    public async ValueTask<long> UInt40BigEndianAsync() => ((long)await ByteAsync() << 32) | await UInt32BigEndianAsync();

    public async ValueTask<byte[]> BytesAsync(int count)
    {
        RequireBudget(count);
        var bytes = new byte[count];
        await ReadExactlyAsync(bytes);
        return bytes;
    }

    public async ValueTask ReadExactlyAsync(Memory<byte> target)
    {
        RequireBudget(target.Length);
        while (!target.IsEmpty)
        {
            CheckCancellation();
            if (offset == end && !await RefillAsync()) throw Invalid("Decoded inventory is truncated.");
            var count = Math.Min(target.Length, end - offset);
            buffer.AsMemory(offset, count).CopyTo(target);
            offset += count;
            ConsumedBytes += count;
            target = target[count..];
        }
    }

    public async ValueTask<string> CStringAsync(int maxNameBytes, bool allowEmpty = false)
    {
        if (maxNameBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxNameBytes));
        var bytes = new byte[Math.Min(maxNameBytes, 256)];
        var count = 0;
        while (true)
        {
            var next = await ByteAsync();
            if (next == 0) break;
            if (count == maxNameBytes) throw Limit("Inventory name exceeds the byte limit.");
            if (count == bytes.Length)
                Array.Resize(ref bytes, (int)Math.Min(maxNameBytes, Math.Max(1L, bytes.Length * 2L)));
            bytes[count++] = next;
        }
        if (count == 0 && !allowEmpty) throw Invalid("Inventory name is empty.");
        try { return new UTF8Encoding(false, true).GetString(bytes, 0, count); }
        catch (DecoderFallbackException) { throw Invalid("Inventory name is not valid UTF-8."); }
    }

    public async ValueTask ExpectEofAsync()
    {
        CheckCancellation();
        if (offset != end || await RefillAsync()) throw Invalid("Inventory has trailing decoded bytes.");
        CheckCancellation();
    }

    private void RequireBudget(long count)
    {
        CheckCancellation();
        if (count < 0 || count > maxDecodedBytes - ConsumedBytes)
            throw Limit("Inventory read exceeds the decoded-byte limit.");
    }

    private async ValueTask<bool> RefillAsync()
    {
        CheckCancellation();
        var remaining = maxDecodedBytes - received;
        // One excess-byte probe distinguishes exact EOF from an exceeded finite body ceiling.
        var request = remaining >= buffer.Length ? buffer.Length : (int)remaining + 1;
        var count = await input.ReadAsync(buffer.AsMemory(0, request), cancellationToken);
        CheckCancellation();
        if (count == 0) return false;
        if (count > remaining) throw Limit("Inventory exceeds the decoded-byte limit.");
        received += count;
        offset = 0;
        end = count;
        return true;
    }

    private DiscoveryException Invalid(string message) => new(formatPrefix + "_INVALID", message);
    private DiscoveryException Limit(string message) => new(formatPrefix + "_LIMIT", message);
}
