using VibeMiner.Builds.Formats;
using VibeMiner.Cdn;

namespace VibeMiner.Builds;

internal sealed class OpenedEncodingRows(IEncodingRowSource source, EncodingTable table) : IEncodingRowLease
{
    private bool closed;
    public BuildRef Build => source.Build;
    public CdnKey EncodingCKey => source.EncodingCKey;
    public long EncodedCount => table.EncodedCount;
    public ValueTask<EncodingRowPage> ReadPageAsync(CdnKey? after, int limit = 1024, CancellationToken ct = default)
    {
        if (closed) throw new EncodingInventoryException(EncodingInventoryCodes.Closed, "The ENCODING lease is closed.");
        return ValueTask.FromResult(table.ReadEncodedPage(after, limit, ct));
    }
    public ValueTask DisposeAsync() { closed = true; return ValueTask.CompletedTask; }
}
