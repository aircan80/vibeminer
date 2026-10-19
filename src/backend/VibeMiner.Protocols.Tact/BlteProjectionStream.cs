using VibeMiner.Domain;

namespace VibeMiner.Protocols.Tact;

public interface IBlteProjectionSource
{
    IReadOnlyList<BlteUnavailableRange> UnavailableRanges { get; }
    IReadOnlyList<BlteReadableRange>? ReadableRanges { get; }
}

/// <summary>Read-only view of authenticated available plaintext. Seeking over holes is allowed;
/// any read intersecting a hole fails before accessing backing bytes. The caller owns source proof.</summary>
public sealed class BlteProjectionStream : Stream, IBlteProjectionSource
{
    private readonly Stream backing;
    private readonly bool leaveOpen;
    private readonly long length;
    private bool disposed;
    public IReadOnlyList<BlteUnavailableRange> UnavailableRanges { get; }
    public IReadOnlyList<BlteReadableRange>? ReadableRanges { get; }

    public BlteProjectionStream(Stream backing, IReadOnlyList<BlteUnavailableRange> unavailableRanges,
        bool leaveOpen = true, IReadOnlyList<BlteReadableRange>? readableRanges = null)
    {
        ArgumentNullException.ThrowIfNull(backing); ArgumentNullException.ThrowIfNull(unavailableRanges);
        if (!backing.CanSeek || !backing.CanRead) throw new ArgumentException("Projection backing must be readable and seekable.", nameof(backing));
        this.backing = backing; this.leaveOpen = leaveOpen; length = backing.Length;
        if (length < 0 || length > BlteDecoder.MaxDecodedBytes || unavailableRanges.Count > BlteDecoder.MaxProjectionRanges)
            throw Invalid("Projection source/ranges exceed their bounds.");
        var holes = unavailableRanges.ToArray(); if (holes.Any(hole => hole is null)) throw Invalid("Projection contains a null hole."); Array.Sort(holes, (left, right) => left.Offset.CompareTo(right.Offset));
        long after = 0; var ordinals = new HashSet<int>();
        foreach (var hole in holes)
        {
            if (hole is null || hole.Offset < after || hole.ByteLength <= 0 || hole.Offset > length || hole.ByteLength > length - hole.Offset
                || hole.ChunkOrdinal is < 0 or >= BlteDecoder.MaxProjectionChunks || !ordinals.Add(hole.ChunkOrdinal)
                || hole.KeyName is null || hole.KeyName.Length != 16 || hole.KeyName.Any(ch => !char.IsAsciiHexDigit(ch) || char.IsAsciiLetterLower(ch)))
                throw Invalid("Projection hole geometry or key identity is invalid.");
            after = hole.Offset + hole.ByteLength;
        }
        UnavailableRanges = Array.AsReadOnly(holes);
        if (readableRanges is not null)
        {
            if (readableRanges.Count + holes.Length > BlteDecoder.MaxProjectionRanges) throw Invalid("Projection range count exceeds its bound.");
            var clear = readableRanges.ToArray(); if (clear.Any(span => span is null)) throw Invalid("Projection contains a null readable range."); Array.Sort(clear, (left, right) => left.Offset.CompareTo(right.Offset));
            after = 0;
            foreach (var span in clear)
            {
                if (span is null || span.Offset < after || span.ByteLength <= 0 || span.Offset > length || span.ByteLength > length - span.Offset
                    || span.Sha256 is null || span.Sha256.Length != 64 || span.Sha256.Any(ch => !char.IsAsciiHexDigit(ch)))
                    throw Invalid("Projection readable range geometry or hash is invalid.");
                after = span.Offset + span.ByteLength;
            }
            var partition = clear.Select(span => (span.Offset, span.ByteLength)).Concat(holes.Select(hole => (hole.Offset, hole.ByteLength))).OrderBy(span => span.Offset);
            after = 0;
            foreach (var span in partition)
            { if (span.Offset != after) throw Invalid("Projection ranges do not exactly partition the declared source."); after += span.ByteLength; }
            if (after != length) throw Invalid("Projection ranges do not cover the declared source.");
            ReadableRanges = Array.AsReadOnly(clear);
        }
    }

    private static DiscoveryException Invalid(string message) => new("BLTE_INVALID", message);
    private void CheckOpen()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (backing.Length != length) throw Invalid("Projection backing length changed.");
    }
    private void CheckRead(int count)
    {
        CheckOpen(); var first = backing.Position;
        if (count == 0 || first >= length) return;
        var last = first + Math.Min((long)count, length - first);
        foreach (var hole in UnavailableRanges)
        {
            if (hole.Offset >= last) break;
            if (hole.Offset + hole.ByteLength > first)
                throw new DiscoveryException("BLTE_RANGE_UNAVAILABLE", "The requested plaintext range intersects an unavailable encrypted chunk.");
        }
    }
    public override int Read(byte[] buffer, int offset, int count)
    { ArgumentNullException.ThrowIfNull(buffer); ArgumentOutOfRangeException.ThrowIfNegative(offset); ArgumentOutOfRangeException.ThrowIfNegative(count); if (offset > buffer.Length - count) throw new ArgumentException("Invalid read buffer range."); return Read(buffer.AsSpan(offset, count)); }
    public override int Read(Span<byte> buffer) { CheckRead(buffer.Length); return backing.Read(buffer); }
    public override int ReadByte() { CheckRead(1); return backing.ReadByte(); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { ArgumentNullException.ThrowIfNull(buffer); ArgumentOutOfRangeException.ThrowIfNegative(offset); ArgumentOutOfRangeException.ThrowIfNegative(count); if (offset > buffer.Length - count) throw new ArgumentException("Invalid read buffer range."); return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask(); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); CheckRead(buffer.Length); return backing.ReadAsync(buffer, cancellationToken); }
    public override bool CanRead => !disposed && backing.CanRead;
    public override bool CanSeek => !disposed && backing.CanSeek;
    public override bool CanWrite => false;
    public override long Length { get { CheckOpen(); return length; } }
    public override long Position { get { CheckOpen(); return backing.Position; } set { CheckOpen(); backing.Position = value; } }
    public override long Seek(long offset, SeekOrigin origin) { CheckOpen(); return backing.Seek(offset, origin); }
    public override void Flush() { CheckOpen(); }
    public override Task FlushAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); CheckOpen(); return Task.CompletedTask; }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    { if (!disposed) { disposed = true; if (disposing && !leaveOpen) backing.Dispose(); } base.Dispose(disposing); }
    public override async ValueTask DisposeAsync()
    { if (!disposed) { disposed = true; if (!leaveOpen) await backing.DisposeAsync(); } GC.SuppressFinalize(this); }
}
