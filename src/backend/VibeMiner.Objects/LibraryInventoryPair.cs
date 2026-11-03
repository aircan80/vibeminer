namespace VibeMiner.Objects;

internal sealed class LibraryInventoryPair(ObjectStore store, LibraryInventorySelection left, LibraryInventorySelection right) : ILibraryInventoryPair
{
    public ValueTask<LibraryInventoryPairMeasurement> MeasureAsync(CancellationToken ct = default) =>
        store.MeasureInventoryPairAsync(left, right, ct);
}

public sealed partial class ObjectStore
{
    /// <summary>Captures exactly two immutable owner/scope/role selections in this library; no cross-library or all-owner inference.</summary>
    public ILibraryInventoryPair ForInventoryPair(LibraryInventorySelection left, LibraryInventorySelection right)
    {
        ThrowIfClosed();
        ArgumentNullException.ThrowIfNull(left); ArgumentNullException.ThrowIfNull(right);
        if (Kind != ObjectStoreKind.Library) throw new InvalidOperationException("Inventory pair views require a mixed library.");
        return new LibraryInventoryPair(this, left, right);
    }

    internal ValueTask<LibraryInventoryPairMeasurement> MeasureInventoryPairAsync(LibraryInventorySelection left, LibraryInventorySelection right, CancellationToken ct)
    {
        ThrowIfClosed();
        return readers.Use((left, right), (c, _, pair) => InventoryRead(c, ct, tx =>
        {
            var l = ResolveInventoryUnion(c, tx, pair.left.Owner, pair.left.Scope, pair.left.Role, ct);
            var r = ResolveInventoryUnion(c, tx, pair.right.Owner, pair.right.Scope, pair.right.Role, ct);
            At(StorePoint.InventoryPairSnapshot);
            ct.ThrowIfCancellationRequested();
            var knownPair = l.Published > 0 && r.Published > 0;
            var leftTotals = new InventoryCoverageCounter(); var rightTotals = new InventoryCoverageCounter();
            var union = new InventoryCoverageCounter(); var intersection = new InventoryCoverageCounter();
            var leftOnly = new InventoryCoverageCounter(); var rightOnly = new InventoryCoverageCounter();
            MergeInventoryStreams(c, tx, l.Generations, r.Generations, StorePoint.InventoryPairRow, (sides, length) =>
            {
                if ((sides & 1) != 0) leftTotals.Add(length);
                if ((sides & 2) != 0) rightTotals.Add(length);
                if (!knownPair) return;
                union.Add(length);
                if (sides == 3) intersection.Add(length);
                else if (sides == 1) leftOnly.Add(length);
                else rightOnly.Add(length);
            }, ct);
            return new LibraryInventoryPairMeasurement(l.Measurement(leftTotals), r.Measurement(rightTotals),
                knownPair ? union.Coverage() : null, knownPair ? intersection.Coverage() : null,
                knownPair ? leftOnly.Coverage() : null, knownPair ? rightOnly.Coverage() : null);
        }), ct);
    }
}
