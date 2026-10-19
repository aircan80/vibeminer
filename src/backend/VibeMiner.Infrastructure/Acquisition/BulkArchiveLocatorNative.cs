using System.Buffers.Binary;
using VibeMiner.Application;
using VibeMiner.Protocols.Tact.Archives;

namespace VibeMiner.Infrastructure;

internal sealed partial class BulkArchiveLocator
{
    private readonly record struct Location(ulong High, ulong Low, int Archive, long Offset, uint Size)
    {
        internal int CompareKey(Location other) => High != other.High ? High.CompareTo(other.High) : Low.CompareTo(other.Low);
        internal void Write(Span<byte> bytes)
        {
            BinaryPrimitives.WriteUInt64BigEndian(bytes, High); BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], Low);
            BinaryPrimitives.WriteInt32BigEndian(bytes[16..], Archive); BinaryPrimitives.WriteInt64BigEndian(bytes[20..], Offset);
            BinaryPrimitives.WriteUInt32BigEndian(bytes[28..], Size);
        }
        internal static Location Read(ReadOnlySpan<byte> bytes) => new(BinaryPrimitives.ReadUInt64BigEndian(bytes),
            BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]), BinaryPrimitives.ReadInt32BigEndian(bytes[16..]),
            BinaryPrimitives.ReadInt64BigEndian(bytes[20..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[28..]));
    }
    private sealed class LocationComparer : IComparer<Location>
    {
        internal static readonly LocationComparer Instance = new();
        public int Compare(Location left, Location right)
        { var key = left.CompareKey(right); return key != 0 ? key : left.Archive.CompareTo(right.Archive); }
    }
    private sealed class MapWriter(FileStream output)
    {
        private readonly byte[] buffer = new byte[RecordBytes * 2048]; private int used;
        internal async ValueTask WriteAsync(Location value, CancellationToken token)
        {
            value.Write(buffer.AsSpan(used, RecordBytes)); used += RecordBytes;
            if (used == buffer.Length) await FlushAsync(token);
        }
        internal async ValueTask FlushAsync(CancellationToken token)
        { if (used == 0) return; await output.WriteAsync(buffer.AsMemory(0, used), token); used = 0; }
    }

    /// <summary>
    /// At most one compact native page per archive, all authenticated TOCs, and a bounded file LRU.
    /// Every LRU page read is authenticated again; a path alone is never accepted as a proof.
    /// </summary>
    private sealed class NativeCursors : IAsyncDisposable
    {
        private readonly AcquisitionResources resources;
        private readonly IAcquisitionWorkContext? context;
        private readonly int maximumOpen;
        private readonly Dictionary<int, (FileStream Stream, LinkedListNode<int> Node)> open = [];
        private readonly LinkedList<int> lru = [];
        internal List<NativeCursor> Items { get; } = [];
        private NativeCursors(AcquisitionResources resources, IAcquisitionWorkContext? context, int maximumOpen)
        { this.resources = resources; this.context = context; this.maximumOpen = maximumOpen; }

        internal static async Task<NativeCursors> OpenAsync(VerifiedObjectStore objects, IReadOnlyList<BulkArchiveIndexSource> sources,
            AcquisitionResources resources, IAcquisitionWorkContext? context, BulkArchiveLocatorLimits limits, CancellationToken token)
        {
            var result = new NativeCursors(resources, context, limits.MaximumOpenFiles);
            try
            {
                // Charge managed compact page arrays (24 bytes/entry), TOC ownership, and fixed
                // file/merge/read buffers. No per-entry strings are retained across page boundaries.
                long working = (long)limits.MaximumOpenFiles * 4096 + (1 << 20);
                for (var ordinal = 0; ordinal < sources.Count; ordinal++)
                {
                    var source = sources[ordinal];
                    await using var input = await objects.OpenVerifiedAsync(source.Sha256, source.ByteLength, resources, context, token);
                    input.Position = input.Length - ArchiveIndexParser.FooterBytes;
                    var footerBytes = new byte[ArchiveIndexParser.FooterBytes]; await input.ReadExactlyAsync(footerBytes, token);
                    ArchiveIndexFooter footer;
                    using (await resources.EnterCpuAsync(context, token))
                        footer = ArchiveIndexParser.AuthenticateFooter(footerBytes, source.ArchiveKey, source.ByteLength, new(), token);
                    working = checked(working + footer.TocBytes + Math.Min(170L, footer.EntryCount) * 24 + 1024);
                    if (working > limits.MaxWorkingBytes)
                        throw Error("BULK_LOCATOR_LIMIT", "Authenticated archive cursor pages and TOCs exceed the bounded working-set allowance.");
                    input.Position = footer.TocOffset;
                    var tocBytes = new byte[footer.TocBytes]; await input.ReadExactlyAsync(tocBytes, token);
                    ArchiveIndexToc toc;
                    using (await resources.EnterCpuAsync(context, token)) toc = ArchiveIndexParser.AuthenticateToc(footer, tocBytes, token);
                    result.Items.Add(new(result, ordinal, input.Name, footer, toc));
                }
                return result;
            }
            catch { await result.DisposeAsync(); throw; }
        }

        private async Task<FileStream> GetAsync(NativeCursor cursor)
        {
            if (open.TryGetValue(cursor.Ordinal, out var saved))
            { lru.Remove(saved.Node); lru.AddLast(saved.Node); return saved.Stream; }
            if (open.Count == maximumOpen)
            {
                var oldest = lru.First!; var retired = open[oldest.Value];
                open.Remove(oldest.Value); lru.RemoveFirst(); await retired.Stream.DisposeAsync();
            }
            var input = OpenNative(cursor.Path);
            if (input.Length != cursor.Footer.IndexBytes)
            { await input.DisposeAsync(); throw Error("BULK_LOCATOR_INTEGRITY", "A native index changed length after its complete CAS proof."); }
            var node = lru.AddLast(cursor.Ordinal); open.Add(cursor.Ordinal, (input, node)); return input;
        }

        internal async Task<IReadOnlyList<ArchiveIndexEntry>> ReadPageAsync(NativeCursor cursor, int ordinal, CancellationToken token, bool cpuOwned)
        {
            var input = await GetAsync(cursor); var page = ArchiveIndexParser.GetPage(cursor.Toc, ordinal, token);
            input.Position = page.Offset; var bytes = new byte[ArchiveIndexParser.PageBytes]; await input.ReadExactlyAsync(bytes, token);
            using var cpu = cpuOwned ? null : await resources.EnterCpuAsync(context, token);
            return ArchiveIndexParser.EnumeratePage(cursor.Toc, page, bytes, token);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var value in open.Values) await value.Stream.DisposeAsync();
            open.Clear(); lru.Clear(); Items.Clear();
        }
    }

    private sealed class NativeCursor(NativeCursors owner, int ordinal, string path, ArchiveIndexFooter footer, ArchiveIndexToc toc)
    {
        internal int Ordinal { get; } = ordinal;
        internal string Path { get; } = path;
        internal ArchiveIndexFooter Footer { get; } = footer;
        internal ArchiveIndexToc Toc { get; } = toc;
        private int nextPage, at;
        private IReadOnlyList<ArchiveIndexEntry> entries = [];
        internal Location Current { get; private set; }
        internal async ValueTask<bool> NextAsync(CancellationToken token, bool cpuOwned = false)
        {
            token.ThrowIfCancellationRequested();
            if (at == entries.Count)
            {
                if (nextPage == Footer.PageCount) return false;
                entries = await owner.ReadPageAsync(this, nextPage++, token, cpuOwned); at = 0;
            }
            var entry = entries[at++]; Current = new(entry.KeyHigh, entry.KeyLow, Ordinal, entry.ArchiveOffset, entry.EncodedSize); return true;
        }
    }
}
