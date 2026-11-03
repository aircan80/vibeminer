using VibeMiner.Cdn;
using VibeMiner.ObjectAccess;

namespace VibeMiner.Builds.Testing;

/// <summary>Immutable authored rows with bounded leases and optional source faults; no acquisition or object ledger.</summary>
public sealed class InMemoryEncodingRows : IEncodingRowSource
{
    private readonly EncodingRow[] rows;
    private int opened, disposed, reads, maxPage;
    public InMemoryEncodingRows(BuildRef build, CdnKey encodingCKey, IEnumerable<EncodingRow> rows)
    {
        Build = build; EncodingCKey = encodingCKey; this.rows = rows.ToArray();
        for (var i = 0; i < this.rows.Length; i++)
            if (this.rows[i] is null || this.rows[i].EKey == default || this.rows[i].EncodedSize < 0 || i > 0 && this.rows[i].EKey <= this.rows[i - 1].EKey)
                throw new ArgumentException("Authored encoded rows must be unique and sorted.", nameof(rows));
    }
    public BuildRef Build { get; }
    public CdnKey EncodingCKey { get; }
    public long EncodedCount => ReportedCount ?? rows.LongLength;
    public long? ReportedCount { get; set; }
    public int OpenedLeases => Volatile.Read(ref opened);
    public int DisposedLeases => Volatile.Read(ref disposed);
    public int ActiveLeases => OpenedLeases - DisposedLeases;
    public int ReadCalls => Volatile.Read(ref reads);
    public int MaxRequestedPage => Volatile.Read(ref maxPage);
    public Func<CdnKey?, int, CancellationToken, ValueTask>? BeforeRead { get; set; }
    public Func<CdnKey?, int, CancellationToken, ValueTask<EncodingRowPage>>? ReadOverride { get; set; }
    public IEncodingRowLease OpenEncodingRows() { Interlocked.Increment(ref opened); return new Lease(this, EncodedCount); }

    private sealed class Lease(InMemoryEncodingRows owner, long count) : IEncodingRowLease
    {
        private bool closed;
        public BuildRef Build => owner.Build;
        public CdnKey EncodingCKey => owner.EncodingCKey;
        public long EncodedCount => count;
        public async ValueTask<EncodingRowPage> ReadPageAsync(CdnKey? after, int limit = 1024, CancellationToken ct = default)
        {
            if (closed) throw new EncodingInventoryException(EncodingInventoryCodes.Closed, "The encoded-row lease is closed.");
            ct.ThrowIfCancellationRequested();
            if (limit is < 1 or > 1024) throw new EncodingInventoryException(EncodingInventoryCodes.Validation, "A row page is 1 to 1024 rows.");
            Interlocked.Increment(ref owner.reads); InterlockedExtensions.Max(ref owner.maxPage, limit);
            if (owner.BeforeRead is { } before) await before(after, limit, ct);
            ct.ThrowIfCancellationRequested();
            if (owner.ReadOverride is { } scripted) return await scripted(after, limit, ct);
            var low = 0; var high = owner.rows.Length;
            while (low < high) { var middle = low + (high - low) / 2; if (after is { } key && owner.rows[middle].EKey <= key) low = middle + 1; else high = middle; }
            var length = Math.Min(limit, owner.rows.Length - low);
            return new(Array.AsReadOnly(owner.rows.AsSpan(low, length).ToArray()), low + length < owner.rows.Length);
        }
        public ValueTask DisposeAsync() { if (!closed) { closed = true; Interlocked.Increment(ref owner.disposed); } return ValueTask.CompletedTask; }
    }
    private static class InterlockedExtensions
    {
        public static void Max(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (current < value) { var observed = Interlocked.CompareExchange(ref target, value, current); if (observed == current) break; current = observed; }
        }
    }
}

/// <summary>An opened-build test source that throws if inventory publication touches files, the CDN or the materialized ledger.</summary>
public sealed class EncodingSourceBuild(IEncodingRowSource source) : IBuild, IEncodingRowSource, ILedgerSource
{
    public BuildRef Build => source.Build;
    public CdnKey EncodingCKey => source.EncodingCKey;
    public long EncodedCount => source.EncodedCount;
    public BuildMetadata Metadata { get; set; } = new("authored encoding source", default, source.EncodingCKey, default, 0, 0, ["enUS"]);
    public CdnContext? Cdn => throw new InvalidOperationException("Inventory publication must not acquire objects.");
    public IEncodingRowLease OpenEncodingRows() => source.OpenEncodingRows();
    ObjectLedger ILedgerSource.Ledger => throw new InvalidOperationException("Inventory publication must not materialize the ledger.");
    public FileResolution Resolve(uint fileDataId) => throw new InvalidOperationException("Inventory publication must not resolve files.");
    public FileResolution Resolve(string path) => throw new InvalidOperationException("Inventory publication must not resolve files.");
    public IEnumerable<uint> FileDataIds(string locale, uint fromId = 0) => throw new InvalidOperationException("Inventory publication must not enumerate files.");
}
