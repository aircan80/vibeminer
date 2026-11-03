using System.Buffers.Binary;
using System.Security.Cryptography;

namespace VibeMiner.Objects;

/// <summary>Per key type limits and the namespaces format 1 accepts. A Cdn store knows types 1..6, a generated store only type 7.</summary>
internal static class ObjectFormats
{
    public const long MaxInMemoryBytes = 64L << 20;
    public static bool Known(ObjectStoreKind kind, ObjectKeyType t) => kind switch
    {
        ObjectStoreKind.Cdn => t is >= ObjectKeyType.Config and <= ObjectKeyType.HistoryRecord,
        ObjectStoreKind.Generated => t == ObjectKeyType.Generated,
        ObjectStoreKind.Library => t is >= ObjectKeyType.Config and <= ObjectKeyType.Generated,
        _ => false,
    };
    public static long MaxBytes(ObjectKeyType t) => t switch
    {
        ObjectKeyType.Config => 16L << 20,
        ObjectKeyType.ArchiveIndex or ObjectKeyType.FileIndex => MaxInMemoryBytes,
        ObjectKeyType.Encoded => PackFormat.MaxPayloadBytes,
        ObjectKeyType.HistoryDocument => 16L << 20,
        ObjectKeyType.HistoryRecord => HistoryRecordFormat.MaxBytes,
        ObjectKeyType.Generated => MaxInMemoryBytes,
        _ => 0,
    };
    public static string? Refusal(ObjectKey key, long declaredLength, ObjectStoreKind kind = ObjectStoreKind.Cdn)
    {
        if (!Known(kind, key.Type)) return ObjectRefusalCodes.KeyTypeUnsupported;
        if (key.Namespace != 0) return ObjectRefusalCodes.NamespaceUnsupported;
        if (declaredLength <= 0 || declaredLength > MaxBytes(key.Type)) return ObjectRefusalCodes.TooLarge;
        return null;
    }
}

internal sealed class VerificationFailure(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Checks bytes against their own key before they reach a pack (D122). Fed in order, any slice sizes; throws
/// <see cref="VerificationFailure"/> as soon as the bytes can't match. The local SHA-256 is separate and proves nothing about the key.
/// </summary>
internal abstract class ObjectVerifier : IDisposable
{
    protected ObjectVerifier(ObjectKey key, long declared) { Key = key; Declared = declared; }
    protected ObjectKey Key { get; }
    protected long Declared { get; }
    protected long Position { get; private set; }

    /// <summary>Bytes from the start already proven by a chunk check, for staging resume. 0 when no partial proof exists.</summary>
    public virtual long VerifiedPrefix => 0;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (Position + data.Length > Declared) Fail(ObjectRefusalCodes.SizeMismatch, "More bytes than the declared length.");
        Consume(data); Position += data.Length;
    }
    public void Complete()
    {
        if (Position != Declared) Fail(ObjectRefusalCodes.SizeMismatch, "Fewer bytes than the declared length.");
        Finish();
    }
    protected abstract void Consume(ReadOnlySpan<byte> data);
    protected abstract void Finish();
    public virtual void Dispose() { }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    protected static void Fail(string code, string message) => throw new VerificationFailure(code, message);

    public static ObjectVerifier For(ObjectKey key, long declared) => key.Type switch
    {
        ObjectKeyType.Config or ObjectKeyType.HistoryDocument => new WholeMd5Verifier(key, declared),
        ObjectKeyType.ArchiveIndex => new IndexVerifier(key, declared, 4),
        ObjectKeyType.FileIndex => new IndexVerifier(key, declared, 0),
        ObjectKeyType.Encoded => new BlteVerifier(key, declared),
        ObjectKeyType.HistoryRecord => new HistoryRecordVerifier(key, declared),
        ObjectKeyType.Generated => new LengthOnlyVerifier(key, declared),
        _ => throw new VerificationFailure(ObjectRefusalCodes.KeyTypeUnsupported, "Unknown key type."),
    };

    /// <summary>Runs the whole check over bytes already in memory.</summary>
    public static void VerifyWhole(ObjectKey key, ReadOnlySpan<byte> bytes, out ObjectKey? requiredDocument)
    {
        using var v = For(key, bytes.Length);
        v.Append(bytes); v.Complete();
        requiredDocument = (v as HistoryRecordVerifier)?.Document;
    }

    protected bool KeyEquals(ReadOnlySpan<byte> md5)
    {
        Span<byte> k = stackalloc byte[16]; Key.WriteKey(k);
        return CryptographicOperations.FixedTimeEquals(md5, k);
    }
}

/// <summary>Config and history document: MD5 of the whole file is the key.</summary>
internal sealed class WholeMd5Verifier(ObjectKey key, long declared) : ObjectVerifier(key, declared)
{
    private readonly IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    protected override void Consume(ReadOnlySpan<byte> data) => md5.AppendData(data);
    protected override void Finish() { if (!KeyEquals(md5.GetHashAndReset())) Fail(ObjectRefusalCodes.KeyMismatch, "The MD5 of the file is not its key."); }
    public override void Dispose() => md5.Dispose();
}

/// <summary>
/// Archive index (offset width 4) and loose file-index (offset width 0): footer MD5 is the key, footer checksum, widths,
/// exact geometry, TOC hash and every page hash. Entry ordering is the reader's business; the hashes prove these are the served bytes.
/// </summary>
internal sealed class IndexVerifier(ObjectKey key, long declared, int offsetWidth) : ObjectVerifier(key, declared)
{
    private const int Footer = 28, Page = 4096;
    private readonly byte[] bytes = new byte[declared];
    protected override void Consume(ReadOnlySpan<byte> data) => data.CopyTo(bytes.AsSpan((int)Position));
    protected override void Finish()
    {
        var all = bytes.AsSpan();
        if (all.Length < Footer) Fail(ObjectRefusalCodes.IndexInvalid, "An index is at least its 28-byte footer.");
        var footer = all[^Footer..];
        if (!KeyEquals(MD5.HashData(footer))) Fail(ObjectRefusalCodes.KeyMismatch, "The MD5 of the index footer is not its key.");
        Span<byte> inner = stackalloc byte[20]; footer[8..].CopyTo(inner); inner[12..].Clear();
        if (!MD5.HashData(inner).AsSpan(0, 8).SequenceEqual(footer[20..])) Fail(ObjectRefusalCodes.IndexInvalid, "Index footer checksum mismatch.");
        if (footer[8] != 1 || footer[9] != 0 || footer[10] != 0 || footer[11] != 4 || footer[12] != offsetWidth ||
            footer[13] != 4 || footer[14] != 16 || footer[15] != 8)
            Fail(ObjectRefusalCodes.IndexUnsupported, "Unsupported index version, page size or field widths for this key type.");
        var entries = (long)BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]);
        var perPage = Page / (16 + 4 + offsetWidth);
        var pages = (entries + perPage - 1) / perPage;
        if (pages * Page + pages * 24 + Footer != all.Length) Fail(ObjectRefusalCodes.IndexInvalid, "Index length differs from its footer's geometry.");
        var toc = all.Slice((int)(pages * Page), (int)(pages * 24));
        if (!MD5.HashData(toc).AsSpan(0, 8).SequenceEqual(footer[..8])) Fail(ObjectRefusalCodes.IndexInvalid, "Index TOC hash mismatch.");
        for (var p = 0; p < pages; p++)
            if (!MD5.HashData(all.Slice(p * Page, Page)).AsSpan(0, 8).SequenceEqual(toc.Slice((int)(pages * 16) + p * 8, 8)))
                Fail(ObjectRefusalCodes.IndexInvalid, $"Index page {p} hash mismatch.");
    }
}

/// <summary>
/// BLTE: chunked objects prove the header MD5 (= EKey) and every chunk MD5 as each chunk completes, and the chunk sizes
/// must add up to the declared length exactly. Headerless objects prove the whole-object MD5 at the end. Chunk modes are
/// opaque: encrypted chunks are stored as served.
/// </summary>
internal sealed class BlteVerifier(ObjectKey key, long declared) : ObjectVerifier(key, declared)
{
    private readonly IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    private byte[] header = new byte[BlteChunkTable.PrefixBytes];
    private int headerFill;
    private long headerLength = -1;  // -1 until the 8-byte prefix is in; 0 headerless
    private BlteChunkTable? table;
    private int chunk;
    private long chunkRemaining, verified;
    private readonly byte[] digest = new byte[16];

    public override long VerifiedPrefix => verified;

    protected override void Consume(ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (headerLength < 0 || headerFill < header.Length)
            {
                var take = Math.Min(header.Length - headerFill, data.Length);
                data[..take].CopyTo(header.AsSpan(headerFill)); headerFill += take; data = data[take..];
                if (headerFill < header.Length) return;
                if (headerLength < 0) Prefix();
                else HeaderComplete();
                continue;
            }
            if (headerLength == 0) { md5.AppendData(data); return; }
            if (chunk >= table!.ChunkCount) Fail(ObjectRefusalCodes.BlteInvalid, "Bytes after the last BLTE chunk.");
            var n = (int)Math.Min(chunkRemaining, data.Length);
            md5.AppendData(data[..n]); data = data[n..]; chunkRemaining -= n;
            if (chunkRemaining == 0)
            {
                md5.GetHashAndReset(digest);
                if (!table.ChunkMatches(chunk, digest)) Fail(ObjectRefusalCodes.BlteInvalid, $"BLTE chunk {chunk} MD5 mismatch.");
                verified += table.ChunkLength(chunk);
                chunk++;
                if (chunk < table.ChunkCount) chunkRemaining = table.ChunkLength(chunk);
            }
        }
    }

    private void Prefix()
    {
        headerLength = BlteChunkTable.HeaderLengthOf(header, Declared);
        if (headerLength == 0) { md5.AppendData(header); return; }
        var full = new byte[headerLength]; header.CopyTo(full, 0); header = full;
    }

    private void HeaderComplete()
    {
        table = BlteChunkTable.Parse(Key, Declared, header);
        verified = headerLength;
        chunkRemaining = table.ChunkLength(0);
    }

    protected override void Finish()
    {
        if (headerLength < 0 || headerLength > 0 && headerFill < header.Length) Fail(ObjectRefusalCodes.BlteInvalid, "BLTE is truncated.");
        if (headerLength == 0) { if (!KeyEquals(md5.GetHashAndReset())) Fail(ObjectRefusalCodes.KeyMismatch, "The MD5 of the headerless BLTE object is not the key."); return; }
        if (chunk != table!.ChunkCount) Fail(ObjectRefusalCodes.BlteInvalid, "BLTE is truncated.");
    }
    public override void Dispose() => md5.Dispose();
}

/// <summary>Generated data: the key is the writer's choice, so only the declared length is checked.</summary>
internal sealed class LengthOnlyVerifier(ObjectKey key, long declared) : ObjectVerifier(key, declared)
{
    protected override void Consume(ReadOnlySpan<byte> data) { }
    protected override void Finish() { }
}

internal sealed class HistoryRecordVerifier(ObjectKey key, long declared) : ObjectVerifier(key, declared)
{
    private readonly byte[] bytes = new byte[declared];
    public ObjectKey? Document { get; private set; }
    protected override void Consume(ReadOnlySpan<byte> data) => data.CopyTo(bytes.AsSpan((int)Position));
    protected override void Finish()
    {
        if (!KeyEquals(MD5.HashData(bytes))) Fail(ObjectRefusalCodes.KeyMismatch, "The MD5 of the history record is not its key.");
        if (!HistoryRecordFormat.TryParse(bytes, out var record)) Fail(ObjectRefusalCodes.HistoryInvalid, "The history record doesn't follow format 1.");
        Document = record.Document;
    }
}
