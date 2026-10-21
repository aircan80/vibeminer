using System.Buffers.Binary;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed partial class DownloadLedger
{
    /// <summary>One complete local CAS/header/row proof, then one ordered read lease for a bounded merge.</summary>
    internal async Task<DownloadLedgerOrderedCursor> OpenOrderedAsync(DownloadLedgerArtifact artifact, CancellationToken token,
        AcquisitionResources? resources = null, IAcquisitionWorkContext? context = null)
        => new(new LedgerRunReader(await OpenValidatedLedgerAsync(artifact, token, resources, context), artifact.ByteLength - HeaderBytes), artifact);

    internal async Task<DownloadLedgerReadSession> OpenReadSessionAsync(DownloadLedgerArtifact artifact, CancellationToken token,
        AcquisitionResources? resources = null, IAcquisitionWorkContext? context = null)
        => new(await OpenValidatedLedgerAsync(artifact, token, resources, context), artifact);

    /// <summary>
    /// A read session over a ledger file this process already proved complete (SHA-256 and order) and that is unchanged
    /// since; only its header is checked again. Returns null when the header differs.
    /// </summary>
    internal static async Task<DownloadLedgerReadSession?> OpenProvenReadSessionAsync(FileStream input, DownloadLedgerArtifact artifact, CancellationToken token)
    {
        var header = new byte[HeaderBytes];
        if (input.Length != artifact.ByteLength || await RandomAccess.ReadAsync(input.SafeFileHandle, header, 0, token) != HeaderBytes ||
            !header.AsSpan(0, 8).SequenceEqual(Magic) || BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8)) != 1 ||
            BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(12)) != RecordBytes || BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(16)) != artifact.ObjectCount ||
            BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(24)) != artifact.TotalEncodedBytes)
            return null;
        input.Position = HeaderBytes;
        return new(input, artifact);
    }

    private async Task<FileStream> OpenValidatedLedgerAsync(DownloadLedgerArtifact artifact, CancellationToken token,
        AcquisitionResources? resources, IAcquisitionWorkContext? context)
    {
        // Explicit method scheduling is one pair. Retention/bulk callers supply their live
        // context here; ordinary callers inherit construction rather than mixing defaults.
        if (resources is null) { resources = this.resources; context = workContext; }
        var input = await OpenLedgerAsync(artifact, resources, context, token);
        try
        {
            if (resources is null) await ValidateRowsAsync(input, artifact, token, null);
            else
            {
                input.Position = HeaderBytes;
                var proof = new LedgerRunReader(input, artifact.ByteLength - HeaderBytes);
                LedgerValue? previous = null; long count = 0, total = 0; var ended = false;
                while (!ended)
                {
                    using var cpu = await resources.EnterCpuAsync(context, token);
                    for (var batch = 0; batch < 4096; batch++)
                    {
                        if (!await proof.NextAsync(token)) { ended = true; break; }
                        var row = proof.Current;
                        if (previous is { } prior && prior.CompareKey(row) >= 0 || row.Bytes > long.MaxValue - total)
                            throw new DiscoveryException("DOWNLOAD_LEDGER_INTEGRITY", "Download ledger identities or exact accounting are invalid.");
                        previous = row; total += row.Bytes; count++;
                    }
                }
                if (count != artifact.ObjectCount || total != artifact.TotalEncodedBytes)
                    throw new DiscoveryException("DOWNLOAD_LEDGER_INTEGRITY", "Download ledger counts differ from its complete ordered records.");
            }
            Remember(artifact);
            input.Position = HeaderBytes;
            return input;
        }
        catch { await input.DisposeAsync(); throw; }
    }
}

internal sealed class DownloadLedgerOrderedCursor : IAsyncDisposable
{
    private readonly LedgerRunReader? reader;
    private readonly DownloadLedgerReadSession? session;
    // Values, not rows: a full traversal allocates no per-member objects or key strings.
    private LedgerValue[]? page;
    private int position, filled;
    private long after;
    internal DownloadLedgerArtifact Artifact { get; }
    internal DownloadLedgerOrderedCursor(LedgerRunReader reader, DownloadLedgerArtifact artifact) { this.reader = reader; Artifact = artifact; }
    internal DownloadLedgerOrderedCursor(DownloadLedgerReadSession session, DownloadLedgerArtifact artifact) { this.session = session; Artifact = artifact; }
    internal LedgerValue Current { get; private set; }
    internal async ValueTask<bool> NextAsync(CancellationToken token)
    {
        if (reader is not null)
        {
            if (!await reader.NextAsync(token)) return false;
            Current = reader.Current; return true;
        }
        if (position == filled)
        {
            page ??= new LedgerValue[DownloadLedgerReadSession.OrderedPageRows];
            filled = await session!.ReadOrderedValuesAsync(after, page, token); position = 0;
            if (filled == 0) return false;
            after += filled;
        }
        Current = page![position++]; return true;
    }
    public ValueTask DisposeAsync() => reader?.DisposeAsync() ?? ValueTask.CompletedTask;
}

/// <summary>One validated immutable DL handle, shared by ordered traversal and bounded exact lookups.</summary>
internal sealed class DownloadLedgerReadSession(FileStream input, DownloadLedgerArtifact artifact) : IAsyncDisposable
{
    /// <summary>The identity of the proven file behind this session.</summary>
    internal ObjectFileIdentity FileIdentity { get; } = VerifiedObjectStore.IdentityOf(input);
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    internal DownloadLedgerOrderedCursor OpenOrderedCursor() => new(this, artifact);
    internal Task<IReadOnlyList<DownloadLedgerRow>> ReadPageAsync(long after, int limit, CancellationToken token)
    {
        if (limit is < 1 or > 128) throw new DiscoveryException("INVALID_REQUEST", "Read at most 128 ledger members.");
        return ReadAsync(after, limit, token);
    }
    internal const int OrderedPageRows = 4096;
    /// <summary>Fills <paramref name="into"/> with the members after ordinal <paramref name="after"/>; returns how many.</summary>
    internal async Task<int> ReadOrderedValuesAsync(long after, LedgerValue[] into, CancellationToken token)
    {
        if (after < 0 || after > artifact.ObjectCount) throw new DiscoveryException("INVALID_REQUEST", "Choose a valid ledger ordinal.");
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var count = (int)Math.Min(into.Length, artifact.ObjectCount - after);
            if (count == 0) return 0;
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(checked(count * DownloadLedger.RecordBytes));
            try
            {
                input.Position = checked(DownloadLedger.HeaderBytes + after * DownloadLedger.RecordBytes);
                await input.ReadExactlyAsync(buffer.AsMemory(0, count * DownloadLedger.RecordBytes), token);
                for (var i = 0; i < count; i++)
                    into[i] = LedgerValue.Read(buffer.AsSpan(i * DownloadLedger.RecordBytes, DownloadLedger.RecordBytes));
            }
            finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
            return count;
        }
        finally { gate.Release(); }
    }
    private async Task<IReadOnlyList<DownloadLedgerRow>> ReadAsync(long after, int limit, CancellationToken token)
    {
        if (after < 0 || after > artifact.ObjectCount) throw new DiscoveryException("INVALID_REQUEST", "Choose a valid ledger ordinal.");
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var count = (int)Math.Min(limit, artifact.ObjectCount - after);
            if (count == 0) return [];
            var buffer = new byte[checked(count * DownloadLedger.RecordBytes)];
            input.Position = checked(DownloadLedger.HeaderBytes + after * DownloadLedger.RecordBytes);
            await input.ReadExactlyAsync(buffer, token);
            var result = new DownloadLedgerRow[count];
            for (var i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                var value = LedgerValue.Read(buffer.AsSpan(i * DownloadLedger.RecordBytes, DownloadLedger.RecordBytes));
                result[i] = new(after + i + 1, value.Key, value.Bytes);
            }
            return result;
        }
        finally { gate.Release(); }
    }
    internal async Task<DownloadLedgerRow?> FindAsync(string encodingKey, long encodedBytes, CancellationToken token)
    {
        if (encodingKey is null || encodingKey.Length != 32 || encodingKey.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || encodedBytes < 0)
            throw new DiscoveryException("INVALID_REQUEST", "Choose a canonical full encoded identity and exact length.");
        var key = LedgerValue.Parse(encodingKey, encodedBytes);
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var buffer = new byte[DownloadLedger.RecordBytes]; long lower = 0, upper = artifact.ObjectCount;
            async Task<LedgerValue> Read(long ordinal)
            {
                input.Position = checked(DownloadLedger.HeaderBytes + ordinal * DownloadLedger.RecordBytes);
                await input.ReadExactlyAsync(buffer, token); return LedgerValue.Read(buffer);
            }
            while (lower < upper)
            {
                var middle = lower + (upper - lower) / 2;
                if ((await Read(middle)).CompareKey(key) < 0) lower = middle + 1; else upper = middle;
            }
            if (lower == artifact.ObjectCount) return null;
            var row = await Read(lower);
            if (!row.SameKey(key)) return null;
            if (row.Bytes != encodedBytes)
                throw new DiscoveryException("CONTENT_SIZE_CONFLICT", "The exact encoded length differs from its admitted ledger member.");
            return new(lower + 1, row.Key, row.Bytes);
        }
        finally { gate.Release(); }
    }
    /// <summary>
    /// Zero-based index of the first member at or after <paramref name="from"/> whose key is greater than
    /// (<paramref name="after"/>) or at least the given key. Probes read 24 bytes each, outside the stream buffer.
    /// </summary>
    internal async Task<long> SearchAsync(string encodingKey, long from, bool after, CancellationToken token)
    {
        if (encodingKey is null || encodingKey.Length != 32 || encodingKey.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            from < 0 || from > artifact.ObjectCount)
            throw new DiscoveryException("INVALID_REQUEST", "Choose a canonical full encoded identity and a valid ledger ordinal.");
        var key = LedgerValue.Parse(encodingKey, 0);
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var buffer = new byte[DownloadLedger.RecordBytes]; long lower = from, upper = artifact.ObjectCount;
            while (lower < upper)
            {
                token.ThrowIfCancellationRequested();
                var middle = lower + (upper - lower) / 2;
                var offset = checked(DownloadLedger.HeaderBytes + middle * DownloadLedger.RecordBytes);
                if (await RandomAccess.ReadAsync(input.SafeFileHandle, buffer, offset, token) != buffer.Length)
                    throw new DiscoveryException("DOWNLOAD_LEDGER_INTEGRITY", "A download ledger record ended unexpectedly.");
                var compared = LedgerValue.Read(buffer).CompareKey(key);
                if (compared < 0 || after && compared == 0) lower = middle + 1; else upper = middle;
            }
            return lower;
        }
        finally { gate.Release(); }
    }
    /// <summary>The member with this exact key, whatever its length, or null.</summary>
    internal async Task<DownloadLedgerRow?> FindKeyAsync(string encodingKey, long from, CancellationToken token)
    {
        var index = await SearchAsync(encodingKey, from, false, token);
        if (index == artifact.ObjectCount) return null;
        var row = (await ReadAsync(index, 1, token))[0];
        return row.EncodingKey == encodingKey ? row : null;
    }
    /// <summary>Up to 4096 members starting at zero-based <paramref name="index"/>.</summary>
    internal Task<IReadOnlyList<DownloadLedgerRow>> ReadFromAsync(long index, CancellationToken token) => ReadAsync(index, 4096, token);
    internal long Count => artifact.ObjectCount;
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (!disposed) { disposed = true; await input.DisposeAsync(); } }
        finally { gate.Release(); }
    }
}
