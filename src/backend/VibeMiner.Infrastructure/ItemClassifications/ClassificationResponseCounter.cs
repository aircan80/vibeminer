using System.Buffers;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>Counts the complete UTF8 envelope with one reusable64KiB buffer; rejected JSON is never retained.</summary>
internal sealed class ClassificationResponseCounter(int maximum) : IBufferWriter<byte>, IDisposable
{
    private readonly byte[] buffer = new byte[64 << 10];
    private int count;
    public void Advance(int written)
    {
        if (written < 0 || written > buffer.Length) throw new ArgumentOutOfRangeException(nameof(written));
        if (written > maximum - count) throw Limit();
        count += written;
    }
    public Memory<byte> GetMemory(int sizeHint = 0)
    { if (sizeHint < 0 || sizeHint > buffer.Length) throw Limit(); return buffer; }
    public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    public void Dispose() { }
    private static DiscoveryException Limit() => new("ITEM_CLASSIFICATION_RESPONSE_LIMIT",
        "The complete classification response exceeds its frame allowance; no labels or outcomes were truncated.");
}
