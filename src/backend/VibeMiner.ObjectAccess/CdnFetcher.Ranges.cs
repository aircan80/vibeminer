using VibeMiner.Cdn;
using VibeMiner.Objects;

namespace VibeMiner.ObjectAccess;

/// <summary>
/// Range reads of large loose encoded objects (D340): the BLTE header, proven against the EKey, then only the chunks under
/// the range, each proven by its MD5. Every request is one byte range; the whole object is never asked for and nothing is stored.
/// </summary>
internal sealed partial class CdnFetcher
{
    /// <summary>The first header request; a header longer than this is fetched again whole as a second range.</summary>
    internal const int HeaderProbeBytes = 64 << 10;
    private readonly ProvenHeaders headers = new();

    /// <summary>Whether a range read of <paramref name="request"/> may skip the whole object: loose, encoded and at least <see cref="ObjectSourceOptions.RangeFetchMinBytes"/>.</summary>
    internal bool CanFetchRange(ObjectRequest request) =>
        request.Key.Type == ObjectKeyType.Encoded && request.Key.Namespace == 0 && request.Locations is not { Count: > 0 }
        && request.DeclaredLength is { } length && length >= options.RangeFetchMinBytes;

    /// <summary>The largest single range this path asks for; a span of chunks over it is split into several sequential ranges.</summary>
    private long MaxRangeBytes => Math.Min(options.InteractiveInFlightBytes, options.InMemoryObjectBytes);

    /// <summary>
    /// Fills <paramref name="destination"/> from <paramref name="offset"/> of a loose encoded object that <see cref="CanFetchRange"/>
    /// admits, with proven bytes only. Null when the object can't be proven by range (headerless, or a chunk larger than one
    /// range): the caller takes the whole path. A CDN failure is that object's result, mapped as for any other fetch.
    /// </summary>
    internal async Task<ObjectResult?> FetchRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext context,
        FetchPriority priority, CancellationToken cancellationToken)
    {
        var declared = request.DeclaredLength!.Value;
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (offset > declared - destination.Length) throw new ArgumentOutOfRangeException(nameof(offset), "The range runs past the end of the object.");
        var key = CdnKeyOf(request.Key);
        try
        {
            var table = headers.Get(request.Key, declared);
            if (table is null)
            {
                table = await FetchHeaderAsync(request.Key, declared, key, priority, context, cancellationToken).ConfigureAwait(false);
                if (table is null) return null;
                headers.Add(table);
            }
            var end = offset + destination.Length;
            if (offset < table.HeaderLength)
            {
                var n = (int)(Math.Min(end, table.HeaderLength) - offset);
                table.Header.Slice((int)offset, n).CopyTo(destination);
            }
            if (end > table.HeaderLength && destination.Length > 0)
            {
                var first = table.ChunkAt(Math.Max(offset, table.HeaderLength));
                var last = table.ChunkAt(end - 1);
                for (var i = first; i <= last; i++) if (table.ChunkLength(i) > MaxRangeBytes) return null;
                while (first <= last)
                {
                    // Whole chunks only, as many as fit one range.
                    var to = first;
                    while (to < last && SpanOf(table, first, to + 1) <= MaxRangeBytes) to++;
                    await FetchChunksAsync(table, first, to, offset, destination, key, priority, context, cancellationToken).ConfigureAwait(false);
                    first = to + 1;
                }
            }
            return new(request.Key, ObjectState.Present, ServedFrom.Cdn, ObjectOrigin.Cdn, declared);
        }
        catch (CdnException failure) { return FromCdnFailure(request.Key, failure); }
    }

    private static long SpanOf(BlteChunkTable table, int first, int last) => table.ChunkOffset(last) + table.ChunkLength(last) - table.ChunkOffset(first);

    /// <summary>The proven chunk table, or null for a headerless object. A host whose header fails its proof is passed over.</summary>
    private async Task<BlteChunkTable?> FetchHeaderAsync(ObjectKey objectKey, long declared, CdnKey key, FetchPriority priority, CdnContext context,
        CancellationToken cancellationToken)
    {
        long claimed = -1;
        BlteChunkTable? proven = null;
        await FetchRangeAsync(priority, context, key, 0, Math.Min(HeaderProbeBytes, declared), declared, bytes =>
        {
            if (BlteChunkTable.ReadHeaderLength(bytes, declared, out var length) is { } bad) return bad.Code;
            if (length == 0 || length > bytes.Length) { claimed = length; return null; }
            if (BlteChunkTable.Prove(objectKey, declared, bytes[..(int)length], out var t) is { } refused) return refused.Code;
            (claimed, proven) = (length, t);
            return null;
        }, _ => { }, cancellationToken).ConfigureAwait(false);
        if (proven is not null || claimed == 0) return proven;
        // Longer than the probe: the whole header as one range, proven as a unit so no host's bytes mix with another's.
        await FetchRangeAsync(priority, context, key, 0, claimed, declared, bytes =>
        {
            if (BlteChunkTable.Prove(objectKey, declared, bytes, out var t) is { } refused) return refused.Code;
            proven = t;
            return null;
        }, _ => { }, cancellationToken).ConfigureAwait(false);
        return proven;
    }

    /// <summary>Chunks <paramref name="first"/> to <paramref name="last"/> as one range, each proven, copied where they overlap the read.</summary>
    private Task FetchChunksAsync(BlteChunkTable table, int first, int last, long offset, Memory<byte> destination, CdnKey key, FetchPriority priority,
        CdnContext context, CancellationToken cancellationToken)
    {
        var start = table.ChunkOffset(first);
        return FetchRangeAsync(priority, context, key, start, SpanOf(table, first, last), table.DeclaredLength, bytes =>
        {
            for (var i = first; i <= last; i++)
                if (table.CheckChunk(i, bytes.Slice((int)(table.ChunkOffset(i) - start), (int)table.ChunkLength(i))) is { } refused) return refused.Code;
            return null;
        }, bytes =>
        {
            var from = Math.Max(offset, start);
            var to = Math.Min(offset + destination.Length, start + bytes.Length);
            bytes.Slice((int)(from - start), (int)(to - from)).CopyTo(destination.Span[(int)(from - offset)..]);
        }, cancellationToken);
    }

    /// <summary>
    /// One single-range request on a ticket of its own, under the byte budget and a stream, hosts in ranked order. <paramref name="check"/> runs per
    /// host, so bad bytes move on to the next one; <paramref name="use"/> sees the passing bytes before their budget is released.
    /// </summary>
    private async Task FetchRangeAsync(FetchPriority priority, CdnContext context, CdnKey key, long offset, long length, long total,
        CdnCheck check, Action<ReadOnlySpan<byte>> use, CancellationToken cancellationToken)
    {
        var ticket = Streams.NewTicket(priority);
        var (lease, units) = await AdmitAsync(ticket, length, cancellationToken).ConfigureAwait(false);
        try
        {
            CdnReply answer;
            try
            {
                answer = await Client(ticket).FetchAsync(Hosts.Order(context.Route, ticket.CurrentPriority),
                    CdnRequest.Range(CdnObjectKind.Data, key, offset, length, total, check), cancellationToken).ConfigureAwait(false);
            }
            catch (CdnException failure)
            {
                Hosts.Observe(failure); lease.Complete(failure.Failure); lease = null!;
                throw;
            }
            Hosts.Observe(answer); lease.Complete(null); lease = null!;
            use(answer.Bytes);
        }
        finally
        {
            lease?.Dispose();
            units.Release();
        }
    }

    /// <summary>The last proven chunk tables, so consecutive reads of one object fetch its header once. At most 16 tables and 8 MiB.</summary>
    private sealed class ProvenHeaders
    {
        private const int MaxTables = 16;
        private const long MaxBytes = 8L << 20;
        private readonly Lock gate = new();
        private readonly LinkedList<BlteChunkTable> tables = new();
        private long bytes;

        public BlteChunkTable? Get(ObjectKey key, long declared)
        {
            lock (gate)
                for (var node = tables.First; node is not null; node = node.Next)
                    if (node.Value.Key == key && node.Value.DeclaredLength == declared)
                    {
                        tables.Remove(node); tables.AddFirst(node);
                        return node.Value;
                    }
            return null;
        }

        public void Add(BlteChunkTable table)
        {
            if (table.HeaderLength > MaxBytes) return;
            lock (gate)
            {
                tables.AddFirst(table); bytes += table.HeaderLength;
                while (tables.Count > MaxTables || bytes > MaxBytes) { bytes -= tables.Last!.Value.HeaderLength; tables.RemoveLast(); }
            }
        }
    }
}
