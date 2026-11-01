using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace VibeMiner.Objects;

internal enum WalkStatus { Entry, End, Torn, Newer }

/// <summary>Reads entry headers one after another from a pack. Never trusts a length that runs past the file's end.</summary>
internal sealed class PackWalker(SafeFileHandle handle, int pack, long start, long end, ObjectStoreKind kind)
{
    private readonly byte[] header = new byte[PackFormat.EntryHeaderBytes];
    public int Pack { get; } = pack;
    public long Offset { get; private set; } = start;
    public EntryHeader Current { get; private set; }
    public long CurrentOffset { get; private set; }

    public async ValueTask<WalkStatus> NextAsync(CancellationToken ct)
    {
        if (Offset == end) return WalkStatus.End;
        if (end - Offset < PackFormat.EntryHeaderBytes) return WalkStatus.Torn;
        if (await PackFiles.ReadFullyAsync(handle, header, Offset, ct) != header.Length) return WalkStatus.Torn;
        switch (PackFormat.ReadEntryHeader(header, kind, out var e))
        {
            case PackFormat.HeaderCheck.Torn: return WalkStatus.Torn;
            case PackFormat.HeaderCheck.Newer: return WalkStatus.Newer;
        }
        if (end - Offset < e.EntryBytes) return WalkStatus.Torn;
        Current = e; CurrentOffset = Offset; Offset += e.EntryBytes;
        return WalkStatus.Entry;
    }
}

internal enum PayloadCheck { Valid, Corrupt, TypeFailed }

internal static class EntryIO
{
    private const int Slice = 1 << 20;

    /// <summary>
    /// Streams an entry's payload once: SHA-256 against the header, every block against its CRC-32C, and optionally the key type's
    /// own check. <paramref name="sink"/> sees each verified-so-far slice (compaction copies through it).
    /// </summary>
    public static async ValueTask<(PayloadCheck check, string? reason)> CheckPayloadAsync(SafeFileHandle h, long entryOffset, EntryHeader e,
        bool keyType, Func<ReadOnlyMemory<byte>, long, ValueTask>? sink, CancellationToken ct, Action<ObjectKey>? recordDocument = null)
    {
        var payloadAt = entryOffset + PackFormat.EntryHeaderBytes;
        var table = new byte[PackFormat.TableBytes(e.Length)];
        if (await PackFiles.ReadFullyAsync(h, table, payloadAt + e.Length, ct) != table.Length) return (PayloadCheck.Corrupt, "truncated block table");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ObjectVerifier? verifier = keyType && !e.Tombstone ? ObjectVerifier.For(e.Key, e.Length) : null;  // a tombstone has no bytes to prove
        var buffer = ArrayPool<byte>.Shared.Rent(Slice);
        try
        {
            for (long at = 0; at < e.Length;)
            {
                ct.ThrowIfCancellationRequested();
                var n = (int)Math.Min(Slice, e.Length - at);
                if (await PackFiles.ReadFullyAsync(h, buffer.AsMemory(0, n), payloadAt + at, ct) != n) return (PayloadCheck.Corrupt, "truncated payload");
                var span = buffer.AsSpan(0, n);
                for (var b = 0; b < n; b += PackFormat.BlockBytes)
                {
                    var block = span.Slice(b, Math.Min(PackFormat.BlockBytes, n - b));
                    var index = (int)((at + b) >> PackFormat.BlockShift);
                    if (PackFormat.Crc32C(block) != BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(index * 4))) return (PayloadCheck.Corrupt, $"block {index} checksum mismatch");
                }
                sha.AppendData(span);
                if (verifier is not null)
                {
                    try { verifier.Append(span); }
                    catch (VerificationFailure f) { return (PayloadCheck.TypeFailed, f.Code); }
                }
                if (sink is not null) await sink(buffer.AsMemory(0, n), at);
                at += n;
            }
            if (!CryptographicOperations.FixedTimeEquals(sha.GetHashAndReset(), e.Sha256)) return (PayloadCheck.Corrupt, "SHA-256 mismatch");
            if (verifier is not null)
            {
                try { verifier.Complete(); }
                catch (VerificationFailure f) { return (PayloadCheck.TypeFailed, f.Code); }
                if (verifier is HistoryRecordVerifier { Document: { } d }) recordDocument?.Invoke(d);
            }
            return (PayloadCheck.Valid, null);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); verifier?.Dispose(); }
    }

    /// <summary>Reads a history record's payload (at most 2 KiB) for the history table.</summary>
    public static async ValueTask<HistoryRecordFormat.Parsed?> ReadHistoryAsync(SafeFileHandle h, long entryOffset, EntryHeader e, CancellationToken ct)
    {
        if (e.Length > HistoryRecordFormat.MaxBytes) return null;
        var b = new byte[e.Length];
        if (await PackFiles.ReadFullyAsync(h, b, entryOffset + PackFormat.EntryHeaderBytes, ct) != b.Length) return null;
        return HistoryRecordFormat.TryParse(b, out var p) ? p : null;
    }
}
