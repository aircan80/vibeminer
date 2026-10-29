using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>Only immutable index bytes. Caller retains staging reservation/cleanup and the shared CAS's
/// single hash/publication pass; this class never acquires, publishes, registers or authorizes a source.</summary>
internal sealed partial class ItemEffectsBridgeArtifacts(Func<Action, CancellationToken, Task> runCpuSlice,
    int maximumPageBytes = (512 << 10) - 1, TableDefinitionSources? definitionSources = null)
{
    private readonly TableDefinitionSources definitions = definitionSources ?? new();
    private readonly int maximumPageBytes = maximumPageBytes is >= 1 and <= (512 << 10) - 1 ? maximumPageBytes : throw new ArgumentOutOfRangeException(nameof(maximumPageBytes));
    private async Task CpuAsync(Action owned, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var calls = 0; var finished = false; var returned = false; Exception? actionFailure = null;
        try
        {
            await runCpuSlice(() =>
            {
                if (returned || Interlocked.Increment(ref calls) != 1) throw ItemEffectsBridgePolicy.Invalid();
                try { token.ThrowIfCancellationRequested(); owned(); }
                catch (Exception error) { actionFailure = error; throw; }
                finally { finished = true; }
            }, token);
            if (calls != 1 || !finished) throw ItemEffectsBridgePolicy.Invalid();
            if (actionFailure is not null) ExceptionDispatchInfo.Capture(actionFailure).Throw();
        }
        finally
        {
            returned = true;
            // The executor contract requires its permit to be released before its Task completes.
            // Yield after that retirement on success, cancellation and failure alike.
            await Task.Yield();
        }
    }
    internal async Task<ItemEffectsBridgeBuild> BuildAsync(ItemEffectsBridgeBinding binding, FileStream callerOwnedStaging,
        Func<long, int, CancellationToken, Task<TableArtifactPage>> readPage, CancellationToken token)
    {
        var owned = ItemEffectsBridgePolicy.FreezeBinding(binding); // Before the first await.
        token.ThrowIfCancellationRequested();
        if (callerOwnedStaging is null || !callerOwnedStaging.CanRead || !callerOwnedStaging.CanWrite || !callerOwnedStaging.CanSeek ||
            callerOwnedStaging.Length != 0 || callerOwnedStaging.Position != 0 || readPage is null) throw ItemEffectsBridgePolicy.Invalid();
        await ItemEffectsBridgePolicy.ValidateProfileAsync(owned, token, CpuAsync, definitions);
        var count = checked((int)ItemEffectsBridgePolicy.Total(owned)); var rowCount = ItemEffectsBridgePolicy.N(count);
        // Columns come from the binding's link: ItemXItemEffect.ItemID/ItemEffectID, or ItemEffect.ParentItemID/ID (#86).
        var columns = owned.Source.Columns.Count; var itemColumn = owned.Fields.ItemId.ColumnIndex; var effectColumn = owned.Fields.ItemEffectId.ColumnIndex;
        byte[]? metadata = null; string? key = null;
        await CpuAsync(() =>
        {
            metadata = ItemEffectsBridgePolicy.MetadataBytes(owned, rowCount);
            key = Convert.ToHexStringLower(SHA256.HashData(metadata));
            // The complete original registration descriptor must fit too, before arrays or stage writes.
            _ = ItemEffectsBridgePolicy.Bytes(new ItemEffectsBridgeArtifact(key, new string('0', 64),
                checked(64L + metadata.Length + (long)count * 32), owned, rowCount));
        }, token);
        var bytes = checked(count * ItemEffectsBridgePolicy.TupleBytes);
        var first = new byte[bytes]; var second = new byte[bytes]; var seen = new byte[(count + 7) / 8];
        long after = 0; uint? previousId = null; var copies = 0;
        do
        {
            token.ThrowIfCancellationRequested();
            // This callback is an existing bounded Tables page unit, never under our CPU permit.
            var page = await readPage(after, 50, token);
            if (page is null || page.Rows is null) throw ItemEffectsBridgePolicy.Invalid();
            var pageCount = page.Rows.Count;
            if (pageCount is < 0 or > 50 || page.TotalCount != count ||
                pageCount > count - after || pageCount == 0 && (count != 0 || after != 0) ||
                page.NextCursor != (after + pageCount < count ? after + pageCount : null)) throw ItemEffectsBridgePolicy.Invalid();
            await CpuAsync(() =>
            {
                for (var i = 0; i < pageCount; i++)
                {
                    token.ThrowIfCancellationRequested(); var row = page.Rows[i];
                    if (row is null || row.Values is null || row.Values.Count != columns || row.IsCopy != row.CopySourceId.HasValue ||
                        row.TextIssues is { Count: > 0 } || previousId is { } previous && row.Id < previous ||
                        row.Values[0] != row.Id.ToString(CultureInfo.InvariantCulture)) throw ItemEffectsBridgePolicy.Invalid();
                    var parent = Signed(row.Values[itemColumn]); var effect = Signed(row.Values[effectColumn]);
                    var ordinal = checked((uint)(after + i));
                    Write(first.AsSpan(checked((int)ordinal * ItemEffectsBridgePolicy.TupleBytes), ItemEffectsBridgePolicy.TupleBytes),
                        new(ordinal, parent, row.Id, effect, row.IsCopy, row.CopySourceId, ordinal));
                    previousId = row.Id; if (row.IsCopy) copies++;
                }
            }, token);
            after += pageCount;
            if (page.NextCursor is null) break;
        } while (true);
        if (after != count || copies != owned.Rows.Completion.CopyRows) throw ItemEffectsBridgePolicy.Invalid();
        // Stable bottom-up merge over two packed arrays; no object graph or uninterruptible sort.
        var from = first; var to = second;
        for (var width = 1; width < count; width = checked(width * 2))
        {
            var left = 0; var middle = Math.Min(width, count); var end = Math.Min(width * 2, count);
            var a = left; var b = middle; var output = left;
            while (left < count)
            {
                await CpuAsync(() =>
                {
                    var entries = 0; var comparisons = 0;
                    while (left < count && entries < ItemEffectsBridgePolicy.CpuEntries && comparisons < ItemEffectsBridgePolicy.CpuEntries)
                    {
                        token.ThrowIfCancellationRequested();
                        if (output == end)
                        {
                            left = end;
                            if (left == count) break;
                            middle = Math.Min(left + width, count); end = Math.Min(left + width * 2, count); a = left; b = middle; output = left;
                        }
                        var takeLeft = b == end;
                        if (a < middle && b < end) { comparisons++; takeLeft = Compare(from.AsSpan(a * 32, 32), from.AsSpan(b * 32, 32)) <= 0; }
                        var index = takeLeft && a < middle ? a++ : b++;
                        from.AsSpan(index * 32, 32).CopyTo(to.AsSpan(output++ * 32, 32)); entries++;
                    }
                }, token);
            }
            (from, to) = (to, from);
        }
        await ValidateTuplesAsync(from, count, seen, token);
        var length = checked(ItemEffectsBridgePolicy.HeaderBytes + metadata!.Length + bytes);
        if (length > ItemEffectsBridgePolicy.MaxArtifactBytes) throw ItemEffectsBridgePolicy.Limit();
        var header = new byte[64]; "VMIEFX01"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)metadata.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 32);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), (ulong)bytes);
        await WriteSlicesAsync(callerOwnedStaging, header, token);
        await WriteSlicesAsync(callerOwnedStaging, metadata, token);
        await WriteSlicesAsync(callerOwnedStaging, from, token);
        await callerOwnedStaging.FlushAsync(token);
        if (callerOwnedStaging.Length != length || callerOwnedStaging.Position != length) throw ItemEffectsBridgePolicy.Invalid();
        return new(key!, length, owned, rowCount);
    }
    private async Task WriteSlicesAsync(FileStream output, byte[] bytes, CancellationToken token)
    {
        for (var at = 0; at < bytes.Length; at += ItemEffectsBridgePolicy.IoBytes)
        {
            token.ThrowIfCancellationRequested();
            await output.WriteAsync(bytes.AsMemory(at, Math.Min(bytes.Length - at, ItemEffectsBridgePolicy.IoBytes)), token);
            await Task.Yield();
        }
    }
    private async Task ValidateTuplesAsync(byte[] tuples, int count, byte[] seen, CancellationToken token)
    {
        for (var begin = 0; begin < count; begin += ItemEffectsBridgePolicy.CpuEntries)
        {
            var start = begin;
            await CpuAsync(() =>
            {
                var end = Math.Min(start + ItemEffectsBridgePolicy.CpuEntries, count);
                for (var i = start; i < end; i++)
                {
                    token.ThrowIfCancellationRequested(); var span = tuples.AsSpan(i * 32, 32); var tuple = Read(span, (uint)i, count);
                    if (i != 0 && Compare(tuples.AsSpan((i - 1) * 32, 32), span) >= 0 ||
                        (seen[(int)tuple.SourceOrdinal / 8] & (1 << ((int)tuple.SourceOrdinal % 8))) != 0) throw ItemEffectsBridgePolicy.Invalid();
                    seen[(int)tuple.SourceOrdinal / 8] |= (byte)(1 << ((int)tuple.SourceOrdinal % 8));
                }
            }, token);
        }
    }
    private static int Signed(string? value)
    {
        if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) || ItemEffectsBridgePolicy.N(number) != value) throw ItemEffectsBridgePolicy.Invalid();
        return number;
    }
    private static int Compare(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var result = BinaryPrimitives.ReadInt32LittleEndian(a).CompareTo(BinaryPrimitives.ReadInt32LittleEndian(b));
        if (result != 0) return result;
        result = BinaryPrimitives.ReadUInt32LittleEndian(a[4..]).CompareTo(BinaryPrimitives.ReadUInt32LittleEndian(b[4..]));
        return result != 0 ? result : BinaryPrimitives.ReadUInt64LittleEndian(a[20..]).CompareTo(BinaryPrimitives.ReadUInt64LittleEndian(b[20..]));
    }
    private static void Write(Span<byte> bytes, ItemEffectsBridgeTuple tuple)
    {
        bytes.Clear(); BinaryPrimitives.WriteInt32LittleEndian(bytes, tuple.ItemId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], tuple.BridgeId); BinaryPrimitives.WriteInt32LittleEndian(bytes[8..], tuple.ItemEffectId);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], tuple.IsCopy ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[16..], tuple.CopySourceId ?? 0); BinaryPrimitives.WriteUInt64LittleEndian(bytes[20..], tuple.SourceOrdinal);
    }
    private static ItemEffectsBridgeTuple Read(ReadOnlySpan<byte> bytes, uint ordinal, int count)
    {
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]); var source = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        var sourceOrdinal = BinaryPrimitives.ReadUInt64LittleEndian(bytes[20..]);
        if (flags > 1 || flags == 0 && source != 0 || sourceOrdinal >= (ulong)count || BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]) != 0) throw ItemEffectsBridgePolicy.Invalid();
        return new(ordinal, BinaryPrimitives.ReadInt32LittleEndian(bytes), BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]), flags == 1, flags == 1 ? source : null, sourceOrdinal);
    }
}
