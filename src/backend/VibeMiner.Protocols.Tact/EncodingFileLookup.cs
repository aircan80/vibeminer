using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Domain;

namespace VibeMiner.Protocols.Tact;

/// <summary>
/// Original native EN page lookup over caller-owned, immutable decoded bytes. Open validates
/// complete grammar/EOF, not source provenance: the caller must separately verify the object
/// and retain an exclusive immutable read lease for this lookup's lifetime. Streams stay open.
/// </summary>
public sealed class EncodingFileLookup
{
    private readonly Stream input;
    private readonly long length;
    private readonly Directory content;
    private readonly Directory encoding;
    private int active;

    private EncodingFileLookup(Stream input, long length, Directory content, Directory encoding,
        EncodingCompletion completion)
    {
        this.input = input; this.length = length; this.content = content; this.encoding = encoding;
        Completion = completion;
    }

    public EncodingCompletion Completion { get; }

    public static async Task<EncodingFileLookup> OpenAsync(Stream decoded, EncodingLimits limits,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        if (!decoded.CanRead || !decoded.CanSeek)
            throw new ArgumentException("Native EN lookup needs a readable seekable decoded stream.", nameof(decoded));
        var origin = decoded.Position;
        var sink = new ValidationConsumer();
        var completion = await EncodingInventoryReader.ReadAsync(decoded, limits, sink, cancellationToken);
        var header = sink.Header!;
        var contentIndex = checked(origin + 22 + header.SpecificationBytes);
        var contentStart = checked(contentIndex + header.ContentPageCount * 32);
        var encodingIndex = checked(contentStart + header.ContentPageCount * header.ContentPageBytes);
        var encodingStart = checked(encodingIndex + header.EncodingPageCount * 32);
        var contents = new byte[checked((int)header.ContentPageCount * 32)];
        var encodings = new byte[checked((int)header.EncodingPageCount * 32)];
        decoded.Position = contentIndex;
        await ReadExactlyAsync(decoded, contents, cancellationToken);
        decoded.Position = encodingIndex;
        await ReadExactlyAsync(decoded, encodings, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (decoded.Length != checked(origin + completion.ConsumedBytes))
            throw Error("EN_INTEGRITY", "The decoded encoding source changed during admission.");
        return new(decoded, decoded.Length,
            new(contents, header.ContentPageBytes, contentStart),
            new(encodings, header.EncodingPageBytes, encodingStart), completion);
    }

    /// <summary>Returns every advertised encoding candidate for this exact content key.</summary>
    public async Task<EncodingContentEntry?> FindContentAsync(string contentKey,
        CancellationToken cancellationToken = default)
    {
        var target = ParseKey(contentKey);
        Enter(cancellationToken);
        try
        {
            var page = await ReadPageAsync(content, target, cancellationToken);
            if (page is null) return null;
            for (var at = 0; at < page.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = page[at];
                if (count == 0) return null;
                var comparison = page.AsSpan(at + 6, 16).SequenceCompareTo(target);
                if (comparison > 0) return null;
                if (comparison == 0)
                {
                    var keys = new string[count];
                    for (var ordinal = 0; ordinal < count; ordinal++)
                        keys[ordinal] = Convert.ToHexStringLower(page.AsSpan(at + 22 + ordinal * 16, 16));
                    return new(Convert.ToHexStringLower(target), U40(page, at + 1), Array.AsReadOnly(keys));
                }
                at += 22 + count * 16;
            }
            return null;
        }
        finally { Volatile.Write(ref active, 0); }
    }

    public async Task<EncodingEncodedEntry?> FindEncodingAsync(string encodingKey,
        CancellationToken cancellationToken = default)
    {
        var target = ParseKey(encodingKey);
        Enter(cancellationToken);
        try
        {
            var page = await ReadPageAsync(encoding, target, cancellationToken);
            if (page is null) return null;
            for (var at = 0; page.Length - at >= 25; at += 25)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = page.AsSpan(at, 16);
                if (key.IndexOfAnyExcept((byte)0) < 0) return null;
                var comparison = key.SequenceCompareTo(target);
                if (comparison > 0) return null;
                if (comparison == 0)
                    return new(Convert.ToHexStringLower(target), U40(page, at + 20),
                        BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(at + 16, 4)));
            }
            return null;
        }
        finally { Volatile.Write(ref active, 0); }
    }

    private void Enter(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0)
            throw new InvalidOperationException("Only one native encoding lookup may use this stream at a time.");
    }

    private async Task<byte[]?> ReadPageAsync(Directory directory, byte[] target, CancellationToken token)
    {
        if (input.Length != length) throw Error("EN_INTEGRITY", "The decoded encoding source length changed.");
        var low = 0; var high = directory.Bytes.Length / 32;
        while (low < high)
        {
            token.ThrowIfCancellationRequested();
            var middle = low + (high - low) / 2;
            if (directory.Bytes.AsSpan(middle * 32, 16).SequenceCompareTo(target) <= 0) low = middle + 1;
            else high = middle;
        }
        var pageOrdinal = low - 1;
        if (pageOrdinal < 0) return null;
        var bytes = new byte[directory.PageBytes];
        input.Position = checked(directory.PagesOffset + (long)pageOrdinal * directory.PageBytes);
        await ReadExactlyAsync(input, bytes, token);
        if (!CryptographicOperations.FixedTimeEquals(MD5.HashData(bytes), directory.Bytes.AsSpan(pageOrdinal * 32 + 16, 16)))
            throw Error("EN_INTEGRITY", "The encoding lookup page differs from its admitted directory proof.");
        return bytes;
    }

    private static byte[] ParseKey(string value)
    {
        if (value is null || value.Length != 32 || value.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("A full 16-byte hexadecimal key is required.", nameof(value));
        var bytes = Convert.FromHexString(value);
        if (bytes.AsSpan().IndexOfAnyExcept((byte)0) < 0)
            throw new ArgumentException("A nonzero key is required.", nameof(value));
        return bytes;
    }

    private static long U40(byte[] bytes, int at) => ((long)bytes[at] << 32) |
        BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 1, 4));

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> bytes, CancellationToken token)
    {
        try { await stream.ReadExactlyAsync(bytes, token); }
        catch (EndOfStreamException) { throw Error("EN_INVALID", "The encoding lookup source is truncated."); }
    }

    private static DiscoveryException Error(string code, string message) => new(code, message);
    private sealed record Directory(byte[] Bytes, int PageBytes, long PagesOffset);
    private sealed class ValidationConsumer : IEncodingConsumer
    {
        public EncodingHeader? Header;
        public ValueTask HeaderAsync(EncodingHeader header, CancellationToken token)
        { Header = header; return ValueTask.CompletedTask; }
        public ValueTask ContentAsync(int page, long ordinal, EncodingContentEntry entry, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask EncodingAsync(int page, long ordinal, EncodingEncodedEntry entry, CancellationToken token) => ValueTask.CompletedTask;
    }
}
