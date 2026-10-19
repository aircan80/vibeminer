using System.Buffers.Binary;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

// Binary values retain complete identity. A masked prefix is only an index key, never an EKey.
internal readonly record struct InventoryKey(ulong High, ulong Low) : IComparable<InventoryKey>
{
    internal static InventoryKey Parse(string hex)
    {
        if (hex.Length is not (18 or 32)) throw new ArgumentException("Expected a nine- or sixteen-byte key.", nameof(hex));
        Span<byte> bytes = stackalloc byte[16]; bytes.Clear();
        if (Convert.FromHexString(hex, bytes, out var consumed, out var written) != System.Buffers.OperationStatus.Done || consumed != hex.Length || written != hex.Length / 2)
            throw new ArgumentException("Invalid hexadecimal key.", nameof(hex));
        return new(BinaryPrimitives.ReadUInt64BigEndian(bytes), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
    }
    internal InventoryKey Prefix(int width) => width switch
    {
        9 => new(High, Low & 0xff00000000000000UL),
        16 => this,
        _ => throw new ArgumentOutOfRangeException(nameof(width))
    };
    public int CompareTo(InventoryKey other) => High != other.High ? High.CompareTo(other.High) : Low.CompareTo(other.Low);
    public override string ToString() => $"{High:x16}{Low:x16}";
}
internal readonly record struct CompactEncodedObject(InventoryKey Key, long Bytes);
internal readonly record struct CompactSizeRequest(long Id, InventoryKey Reference, long Bytes);
internal readonly record struct CompactResolution(CompactEncodedObject Object, bool Ambiguous);

/// <summary>Bounded, disposable bulk matching primitives. No acquisition, SQL or publication.</summary>
internal static class CompactInventoryMatching
{
    internal const long MaxIndexBytes = 384L << 20;
    // Dictionary entry + bucket + capacity rounding headroom, excluding transient source records.
    private const int ChargedMapBytesPerObject = 96;
    internal static Dictionary<InventoryKey, CompactResolution> BuildMap(IEnumerable<CompactEncodedObject> objects,
        int capacity, int width, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = default(InventoryKey).Prefix(width);
        if (capacity < 0 || (long)capacity * ChargedMapBytesPerObject > MaxIndexBytes)
            throw new DiscoveryException("INVENTORY_LIMIT", "The compact matching map exceeds its memory budget.");
        var map = new Dictionary<InventoryKey, CompactResolution>(capacity);
        var count = 0;
        foreach (var value in objects)
        {
            token.ThrowIfCancellationRequested();
            if (++count > capacity) throw new DiscoveryException("INVENTORY_LIMIT", "Matching input exceeds its admitted object count.");
            var prefix = value.Key.Prefix(width);
            if (map.TryGetValue(prefix, out var previous))
            {
                if (previous.Object.Key == value.Key && previous.Object.Bytes != value.Bytes) throw Conflict();
                map[prefix] = previous with { Ambiguous = previous.Ambiguous || previous.Object.Key != value.Key };
            }
            else map.Add(prefix, new(value, false));
        }
        return map;
    }
    internal static CompactEncodedObject Resolve(Dictionary<InventoryKey, CompactResolution> map, CompactSizeRequest request, int width)
    {
        if (!map.TryGetValue(request.Reference.Prefix(width), out var value) || value.Ambiguous) throw Unresolved();
        if (value.Object.Bytes != request.Bytes) throw Conflict();
        return value.Object;
    }
    // Both sources must be ordered by the same unsigned binary key/prefix. Readers remain bounded;
    // duplicate requests reuse one resolved prefix group without retaining the whole request set.
    internal static void Merge(IEnumerable<CompactEncodedObject> objects, IEnumerable<CompactSizeRequest> requests,
        int width, Action<CompactSizeRequest, CompactEncodedObject> emit, CancellationToken token)
    {
        _ = default(InventoryKey).Prefix(width);
        using var input = objects.GetEnumerator();
        InventoryKey? previousObject = null;
        CompactEncodedObject current = default;
        bool Advance()
        {
            token.ThrowIfCancellationRequested();
            if (!input.MoveNext()) return false;
            current = input.Current;
            if (previousObject is { } previous && current.Key.CompareTo(previous) <= 0)
                throw new ArgumentException("Matching objects must have unique ascending full keys.", nameof(objects));
            previousObject = current.Key; return true;
        }
        var hasObject = Advance();
        InventoryKey? previousRequest = null;
        var resolution = default(CompactResolution);
        foreach (var request in requests)
        {
            token.ThrowIfCancellationRequested(); var prefix = request.Reference.Prefix(width);
            if (previousRequest is { } previous && prefix.CompareTo(previous) < 0)
                throw new ArgumentException("Matching requests must have ascending prefixes.", nameof(requests));
            if (previousRequest != prefix)
            {
                while (hasObject && current.Key.Prefix(width).CompareTo(prefix) < 0) hasObject = Advance();
                if (!hasObject || current.Key.Prefix(width) != prefix) throw Unresolved();
                resolution = new(current, false); hasObject = Advance();
                while (hasObject && current.Key.Prefix(width) == prefix)
                { resolution = resolution with { Ambiguous = true }; hasObject = Advance(); }
                previousRequest = prefix;
            }
            if (resolution.Ambiguous) throw Unresolved();
            if (resolution.Object.Bytes != request.Bytes) throw Conflict();
            emit(request, resolution.Object);
        }
    }
    private static DiscoveryException Unresolved() => new("INVENTORY_PREFIX_UNRESOLVED", "A size-manifest reference does not identify one complete encoded object.");
    private static DiscoveryException Conflict() => new("INVENTORY_SIZE_CONFLICT", "Published encoded lengths disagree.");
}

internal readonly record struct ResolvedEncodedOrigin(long Id, CompactEncodedObject Object);
internal sealed record OrderedMatchBatch(IReadOnlyList<ResolvedEncodedOrigin> Rows, bool Complete);

/// <summary>Resumable merge; each read bounds both output and source/transition work.</summary>
internal sealed class OrderedInventoryCursor : IDisposable
{
    private enum Stage { Request, Seek, EndGroup, Emit, Complete }
    private readonly IEnumerator<CompactEncodedObject> objects;
    private readonly IEnumerator<CompactSizeRequest> requests;
    private readonly int width;
    private readonly bool allowAbsentExact;
    private Stage stage;
    private bool objectInitialized, hasObject, cached;
    private InventoryKey? previousObject, previousRequest;
    private InventoryKey cachedPrefix;
    private CompactEncodedObject currentObject, selected;
    private CompactSizeRequest request;

    internal OrderedInventoryCursor(IEnumerable<CompactEncodedObject> objects, IEnumerable<CompactSizeRequest> requests,
        int width, bool allowAbsentExact = false)
    {
        _ = default(InventoryKey).Prefix(width);
        if (allowAbsentExact && width != 16) throw new ArgumentException("Only a complete key can identify an absent EN object.");
        this.objects = objects.GetEnumerator(); this.requests = requests.GetEnumerator();
        this.width = width; this.allowAbsentExact = allowAbsentExact;
    }
    internal OrderedMatchBatch Read(int maxWork, int maxOutput, CancellationToken token)
    {
        if (maxWork < 1 || maxOutput is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maxWork));
        token.ThrowIfCancellationRequested();
        var rows = new List<ResolvedEncodedOrigin>(Math.Min(maxOutput,128));
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var work = 0; work < maxWork && rows.Count < maxOutput && stage != Stage.Complete; work++)
        {
            token.ThrowIfCancellationRequested();
            if (work > 0 && (work & 63) == 0 && System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 40) break;
            switch (stage)
            {
                case Stage.Request:
                    if (!requests.MoveNext()) { stage = Stage.Complete; break; }
                    request = requests.Current;
                    var prefix = request.Reference.Prefix(width);
                    if (previousRequest is { } previous && prefix.CompareTo(previous) < 0)
                        throw new ArgumentException("Matching requests must have ascending prefixes.");
                    previousRequest = prefix;
                    stage = cached && cachedPrefix == prefix ? Stage.Emit : Stage.Seek;
                    break;
                case Stage.Seek:
                    if (!objectInitialized) { AdvanceObject(token); break; }
                    var comparison = hasObject ? currentObject.Key.Prefix(width).CompareTo(request.Reference.Prefix(width)) : 1;
                    if (comparison < 0) { AdvanceObject(token); break; }
                    if (comparison > 0)
                    {
                        if (!allowAbsentExact) throw Unresolved();
                        selected = new(request.Reference,request.Bytes); Cache(); break;
                    }
                    selected = currentObject; AdvanceObject(token); stage = Stage.EndGroup; break;
                case Stage.EndGroup:
                    if (hasObject && currentObject.Key.Prefix(width) == request.Reference.Prefix(width)) throw Unresolved();
                    Cache(); break;
                case Stage.Emit:
                    if (selected.Bytes != request.Bytes) throw new DiscoveryException("INVENTORY_SIZE_CONFLICT", "Published encoded lengths disagree.");
                    rows.Add(new(request.Id,selected)); stage = Stage.Request; break;
            }
        }
        return new(rows,stage == Stage.Complete);
    }
    private void Cache() { cached = true; cachedPrefix = request.Reference.Prefix(width); stage = Stage.Emit; }
    private void AdvanceObject(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); objectInitialized = true; hasObject = objects.MoveNext();
        if (!hasObject) return;
        currentObject = objects.Current;
        if (previousObject is { } previous && currentObject.Key.CompareTo(previous) <= 0)
            throw new ArgumentException("Matching objects must have unique ascending full keys.");
        previousObject = currentObject.Key;
    }
    private static DiscoveryException Unresolved() => new("INVENTORY_PREFIX_UNRESOLVED", "A size-manifest reference does not identify one complete encoded object.");
    public void Dispose() { try { requests.Dispose(); } finally { objects.Dispose(); } }
}
