using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

/// <summary>Bounded streaming VMCRTR01 writer. The source reader belongs to the reserved core slot.</summary>
internal sealed partial class CreaturesProjection(VerifiedObjectStore objects, AcquisitionResources resources, CreaturesLimits? configuredLimits = null,
    Func<CancellationToken, Task<FileStream>>? createStaging = null)
{
    private const int HeaderBytes = 64, DirectoryBytes = 32;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly SemaphoreSlim Builder = new(1, 1);
    private readonly CreaturesLimits limits = configuredLimits ?? new();
    private readonly Func<CancellationToken, Task<FileStream>> stagingFile = createStaging ?? objects.CreateStagingFileAsync;
    private sealed record Metadata(CreaturesBinding Binding, CreatureCounts Counts, string FeatureRevision, string QueryPolicy, string SemanticProfile);
    private sealed record Payload(IReadOnlyList<string> Facts, IReadOnlyList<string> DisplayIds, IReadOnlyList<string> Probabilities, string? NameIssue);
    private readonly record struct Entry(uint Id, CreatureNameState State, long NameOffset, int NameLength, long SummaryOffset, int SummaryLength)
    {
        internal byte[] Bytes()
        {
            var b = new byte[DirectoryBytes]; BinaryPrimitives.WriteUInt32LittleEndian(b, Id); b[4] = (byte)State;
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), NameOffset); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), NameLength);
            BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(20), SummaryOffset); BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28), SummaryLength); return b;
        }
        internal static Entry Read(ReadOnlySpan<byte> b)
        {
            if (b.Length != DirectoryBytes || b[4] > 2 || b[5] != 0 || b[6] != 0 || b[7] != 0) throw CreaturesPolicy.Invalid();
            return new(BinaryPrimitives.ReadUInt32LittleEndian(b), (CreatureNameState)b[4], BinaryPrimitives.ReadInt64LittleEndian(b[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(b[16..]), BinaryPrimitives.ReadInt64LittleEndian(b[20..]), BinaryPrimitives.ReadInt32LittleEndian(b[28..]));
        }
    }
    private static Payload PayloadFrom(CreaturesBinding b, Db2Row row)
    {
        if (row.Values.Count != b.Source.Columns.Count || row.IsCopy != (row.CopySourceId is not null) || row.TextIssues?.Any(i => i.ColumnIndex < 0 || i.ColumnIndex >= row.Values.Count) == true) throw CreaturesPolicy.Invalid();
        string V(int i) => row.Values[b.Fields[i].ColumnIndex];
        var facts = Enumerable.Range(4, 4).Select(V).ToArray(); var displays = Enumerable.Range(8, 4).Select(V).ToArray(); var probabilities = Enumerable.Range(12, 4).Select(V).ToArray();
        for (var i = 0; i < 4; i++)
        {
            var f = b.Fields[4 + i];
            if (!long.TryParse(facts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) || value.ToString(CultureInfo.InvariantCulture) != facts[i] ||
                value < (f.Unsigned == true ? 0 : -(1L << (f.Bits!.Value - 1))) || value > (f.Unsigned == true ? (1L << f.Bits!.Value) - 1 : (1L << (f.Bits!.Value - 1)) - 1) ||
                !int.TryParse(displays[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var display) || display.ToString(CultureInfo.InvariantCulture) != displays[i] ||
                !float.TryParse(probabilities[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var probability) || !float.IsFinite(probability) || probability.ToString("R", CultureInfo.InvariantCulture) != probabilities[i]) throw CreaturesPolicy.Invalid();
        }
        var issues = row.TextIssues?.Where(i => i.ColumnIndex == b.Fields[0].ColumnIndex).ToArray() ?? [];
        if (issues.Length > 1 || issues.Any(i => i.Code != "DB2_TEXT_ENCODING_INVALID")) throw CreaturesPolicy.Invalid();
        return new(facts, displays, probabilities, issues.SingleOrDefault()?.Code);
    }
    internal async Task<CreaturesArtifact> BuildAsync(CreaturesBinding binding, TableRowArtifacts reader,
        Func<CreaturesPreparationProgress, Task> progress, CancellationToken token)
    {
        CreaturesPolicy.ValidateBinding(binding);
        if (limits.MaxRows is < 0 or > 1_000_000 || limits.MaxArtifactBytes is < 64 or > (128L << 20) || limits.MaxNameBytes is < 0 or > (64L << 20) ||
            limits.MaxMetadataBytes is < 1 or > (128 << 10) || limits.MaxSummaryBytes is < 1 or > 2048) throw new ArgumentOutOfRangeException(nameof(configuredLimits));
        if (CreaturesPolicy.Total(binding) > limits.MaxRows) throw CreaturesPolicy.Limit();
        if (!await Builder.WaitAsync(0, token)) throw new DiscoveryException("CREATURES_BUSY", "Another Creature projection is being built.", true);
        var paths = new List<string>(3); IDisposable? reservation = null;
        try
        {
            reservation = await resources.ReserveStagingAsync(256L << 20, null, token);
            await using var spool = await stagingFile(token); paths.Add(spool.Name);
            await using var names = await stagingFile(token); paths.Add(names.Name);
            long rows = 0, nameBytes = 0, summaryBytes = 0, valid = 0, empty = 0, invalid = 0, positive = 0, zero = 0, negative = 0;
            uint? previous = null; long after = 0;
            var sliceBytes = 0; var sliceChars = 0; var sliceRows = 0;
            if (CreaturesPolicy.Total(binding) == 0)
            {
                var emptyPage = await reader.ReadPageAsync(binding.Rows, 0, 1, null, token);
                if (emptyPage.Rows.Count != 0 || emptyPage.TotalCount != 0) throw CreaturesPolicy.Invalid();
            }
            while (rows < CreaturesPolicy.Total(binding))
            {
                var page = await reader.ReadPageAsync(binding.Rows, after, 50, null, token);
                if (page.Rows.Count == 0 || page.TotalCount != CreaturesPolicy.Total(binding)) throw CreaturesPolicy.Invalid();
                foreach (var row in page.Rows)
                {
                    token.ThrowIfCancellationRequested();
                    var text = row.Values[binding.Fields[0].ColumnIndex];
                    if (text.Length > 65536) throw CreaturesPolicy.Limit();
                    if (sliceRows >= 4096 || sliceBytes + 4L * text.Length + limits.MaxSummaryBytes > 1 << 20 || sliceChars + text.Length > 65536)
                    { await Task.Yield(); token.ThrowIfCancellationRequested(); sliceRows = sliceBytes = sliceChars = 0; }
                    byte[] name, summary, entry;
                    using (await resources.EnterCpuAsync(null, token))
                    {
                        token.ThrowIfCancellationRequested();
                        if (previous is { } id && row.Id <= id) throw new DiscoveryException("CREATURES_SOURCE_DUPLICATE", "Creature emitted IDs must be unique and strictly increasing.");
                        previous = row.Id;
                        var payload = PayloadFrom(binding, row);
                        var state = payload.NameIssue is not null ? CreatureNameState.Invalid : text.Length == 0 ? CreatureNameState.Empty : CreatureNameState.Valid;
                        name = state == CreatureNameState.Valid ? Utf8.GetBytes(text) : [];
                        summary = JsonSerializer.SerializeToUtf8Bytes(payload, CreaturesPolicy.Json);
                        if (name.Length > 262144 || name.Length > limits.MaxNameBytes - nameBytes || summary.Length > limits.MaxSummaryBytes ||
                            rows >= limits.MaxRows || checked(HeaderBytes + rows * DirectoryBytes + DirectoryBytes + summaryBytes + summary.Length + nameBytes + name.Length + limits.MaxMetadataBytes) > limits.MaxArtifactBytes) throw CreaturesPolicy.Limit();
                        entry = new Entry(row.Id, state, nameBytes, name.Length, summaryBytes, summary.Length).Bytes();
                        if (state == CreatureNameState.Valid) valid++; else if (state == CreatureNameState.Empty) empty++; else invalid++;
                        foreach (var display in payload.DisplayIds) { var value = int.Parse(display, CultureInfo.InvariantCulture); if (value > 0) positive++; else if (value == 0) zero++; else negative++; }
                    }
                    await spool.WriteAsync(entry, token);
                    await spool.WriteAsync(summary, token); await names.WriteAsync(name, token);
                    rows++; nameBytes += name.Length; summaryBytes += summary.Length;
                    sliceBytes += summary.Length + name.Length; sliceChars += text.Length; sliceRows++;
                    if (sliceRows >= 4096 || sliceBytes >= 1 << 20 || sliceChars >= 65536)
                    { await Task.Yield(); token.ThrowIfCancellationRequested(); sliceRows = sliceBytes = sliceChars = 0; }
                }
                after += page.Rows.Count;
                await progress(new("Projecting admitted Creature rows", "rows", rows, CreaturesPolicy.Total(binding), CreaturesPolicy.Table));
            }
            var counts = new CreatureCounts(CreaturesPolicy.N(rows), CreaturesPolicy.N(valid), CreaturesPolicy.N(empty), CreaturesPolicy.N(invalid), CreaturesPolicy.N(positive), CreaturesPolicy.N(zero), CreaturesPolicy.N(negative));
            byte[] metadata;
            using (await resources.EnterCpuAsync(null, token))
                metadata = CreaturesPolicy.Bytes(new Metadata(CreaturesPolicy.Normalize(binding), counts, CreaturesPolicy.FeatureRevision, CreaturesPolicy.QueryPolicy, CreaturesPolicy.SemanticProfile));
            var length = checked(HeaderBytes + metadata.Length + rows * DirectoryBytes + summaryBytes + nameBytes);
            if (metadata.Length > limits.MaxMetadataBytes || length > limits.MaxArtifactBytes) throw CreaturesPolicy.Limit();
            await using var final = await stagingFile(token); paths.Add(final.Name);
            var header = new byte[HeaderBytes]; "VMCRTR01"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), checked((uint)rows)); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), checked((uint)metadata.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), DirectoryBytes); BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), summaryBytes); BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32), nameBytes);
            await final.WriteAsync(header, token); await final.WriteAsync(metadata, token); await spool.FlushAsync(token); await names.FlushAsync(token);
            var encoded = new byte[DirectoryBytes];
            spool.Position = 0;
            for (long ordinal = 0; ordinal < rows; ordinal++)
            {
                await spool.ReadExactlyAsync(encoded, token);
                var entry = Entry.Read(encoded);
                await final.WriteAsync(encoded, token); spool.Position += entry.SummaryLength;
                if ((ordinal & 4095) == 4095) { await Task.Yield(); token.ThrowIfCancellationRequested(); }
            }
            spool.Position = 0; var buffer = new byte[64 << 10]; var copiedSummaryBytes = 0;
            for (long ordinal = 0; ordinal < rows; ordinal++)
            {
                Entry entry;
                await spool.ReadExactlyAsync(encoded, token); entry = Entry.Read(encoded);
                await CopyAsync(spool, final, entry.SummaryLength, buffer, token); copiedSummaryBytes += entry.SummaryLength;
                if ((ordinal & 4095) == 4095 || copiedSummaryBytes >= 1 << 20) { await Task.Yield(); token.ThrowIfCancellationRequested(); copiedSummaryBytes = 0; }
            }
            names.Position = 0; await CopyAsync(names, final, nameBytes, buffer, token); await final.FlushAsync(token);
            if (final.Length != length) throw CreaturesPolicy.Invalid(); var path = final.Name; await final.DisposeAsync();
            var sha256 = await objects.PublishFileAsync(path, length, resources, null, token);
            var result = new CreaturesArtifact(CreaturesPolicy.ContentKey(binding), sha256, length, binding, counts); CreaturesPolicy.Validate(result); return result;
        }
        catch (EncoderFallbackException) { throw CreaturesPolicy.Invalid(); }
        finally
        {
            Exception? cleanup = null;
            foreach (var path in paths) { try { File.Delete(path); } catch (Exception error) { cleanup ??= error; } }
            // A failed deletion stays charged rather than allowing unbounded cleanup debt.
            if (cleanup is null) reservation?.Dispose(); Builder.Release();
            if (cleanup is not null) throw new DiscoveryException("CREATURES_CLEANUP_FAILED", "Creature staging cleanup failed; its storage reservation remains charged.");
        }
    }
    private async Task CopyAsync(Stream source, Stream destination, long length, byte[] buffer, CancellationToken token)
    {
        var slice = 0;
        while (length > 0)
        {
            var take = (int)Math.Min(length, buffer.Length);
            await source.ReadExactlyAsync(buffer.AsMemory(0, take), token);
            await destination.WriteAsync(buffer.AsMemory(0, take), token);
            length -= take; slice += take;
            if (slice >= 1 << 20) { await Task.Yield(); token.ThrowIfCancellationRequested(); slice = 0; }
        }
    }
}
