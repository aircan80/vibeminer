using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>Finite request snapshots and response invariants; never authorizes source bytes or rows.</summary>
public static partial class ItemEffectsValidation
{
    public const string IndexRevision = "item-effect-bridge-index-v1";
    public const string OrderPolicy = "signed-item-unsigned-bridge-source-ordinal-v1";
    public const string InputPolicy = "item-effect-inputs-v1";
    public const string NamePolicy = "spell-name-by-emitted-id-v1";
    public const string BridgeTable = "00cb674f", EffectTable = "4002a5b1", NameTable = "46c66698";

    public static void Validate(ItemEffectsSelection selection)
    {
        if (selection is null) throw Invalid();
        ItemsService.Validate(selection.Context);
        if (!Hash(selection.ItemsArtifactId) || !Unsigned(selection.ItemId, uint.MaxValue)) throw Invalid();
    }
    public static void Validate(ItemEffectsPageRequest request) => Snapshot(request);
    public static void Validate(ItemEffectDetailsRequest request) => Snapshot(request);
    public static void Validate(ItemEffectNamesRequest request) => Snapshot(request);

    public static ItemEffectsPageRequest Snapshot(ItemEffectsPageRequest request)
    {
        if (request is null) throw Invalid();
        Validate(request.Selection);
        if (!Hash(request.BridgeIndexArtifactId) || request.Limit is < 1 or > 16) throw Invalid();
        if (request.After is not null && (!ReadCursor(request.After, out var binding, out _) || binding != CursorBinding(request)))
            throw new DiscoveryException("ITEM_EFFECTS_CURSOR_INVALID", "Use the continuation for this exact effects selection, index and page limit.");
        return request;
    }
    public static ItemEffectDetailsRequest Snapshot(ItemEffectDetailsRequest request)
    {
        if (request is null) throw Invalid();
        Validate(request.Selection);
        if (!Hash(request.BridgeIndexArtifactId)) throw Invalid();
        return request with { Edges = SnapshotEdges(request.Edges, 16) };
    }
    public static ItemEffectNamesRequest Snapshot(ItemEffectNamesRequest request)
    {
        if (request is null) throw Invalid();
        Validate(request.Selection);
        if (!Hash(request.BridgeIndexArtifactId) || !Hash(request.ItemEffectArtifactId) || request.PolicyRevision != NamePolicy) throw Invalid();
        return request with { Edges = SnapshotEdges(request.Edges, 8) };
    }
    private static IReadOnlyList<ItemEffectsEdgeKey> SnapshotEdges(IReadOnlyList<ItemEffectsEdgeKey>? edges, int maximum)
    {
        try
        {
            if (edges is null) throw Invalid();
            var count = edges.Count;
            if (count < 1 || count > maximum) throw Invalid();
            var owned = new ItemEffectsEdgeKey[count];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var edge = edges[i];
                if (edge is null || !Unsigned(edge.IndexOrdinal, 65535) || !seen.Add(edge.IndexOrdinal)) throw Invalid();
                owned[i] = edge;
            }
            return Array.AsReadOnly(owned);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { throw Invalid(); }
    }
    internal static void ValidateProgress(ItemEffectsProgress value)
    {
        if (value is null || !BoundedText(value.Stage, 128) || !BoundedText(value.Unit, 32) ||
            value.Completed is < 0 or > 9_007_199_254_740_991 || value.Total is < 0 or > 9_007_199_254_740_991 ||
            value.Completed is { } done && value.Total is { } total && done > total ||
            value.TableId is not null && value.TableId is not (BridgeTable or EffectTable or NameTable))
            throw new DiscoveryException("ITEM_EFFECTS_PROGRESS_INVALID", "Effects progress requires bounded stage text and honest current-stage counters.");
    }
    private static string CursorBinding(ItemEffectsPageRequest request) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    { selection = request.Selection, bridgeIndexArtifactId = request.BridgeIndexArtifactId, revision = IndexRevision, orderPolicy = OrderPolicy, limit = request.Limit }, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
    private static bool ReadCursor(string value, out string binding, out ulong afterOrdinal)
    {
        binding = ""; afterOrdinal = 0;
        if (value.Length is < 1 or > 256 || value.Any(c => c is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))) return false;
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var bytes = Convert.FromBase64String(padded);
            if (Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') != value) return false;
            using var json = JsonDocument.Parse(bytes, new() { MaxDepth = 3 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var properties = root.EnumerateObject();
            if (!properties.MoveNext() || properties.Current.Name != "binding" || properties.Current.Value.ValueKind != JsonValueKind.String || !Hash(properties.Current.Value.GetString()) ||
                !properties.MoveNext() || properties.Current.Name != "after" || properties.Current.Value.ValueKind != JsonValueKind.String) return false;
            var after = properties.Current.Value.GetString();
            if (!Unsigned(after, 65535) || after == "0" || properties.MoveNext()) return false;
            binding = root.GetProperty("binding").GetString()!; afterOrdinal = ulong.Parse(after!, CultureInfo.InvariantCulture);
            return bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(new { binding, after }));
        }
        catch (Exception error) when (error is FormatException or JsonException) { return false; }
    }
    private static bool Hash(string? value) => Hex(value, 64);
    private static bool Hex(string? value, int width) => value is not null && value.Length == width && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Unsigned(string? value, ulong maximum) => value is { Length: >= 1 and <= 20 } &&
        (value.Length == 1 || value[0] != '0') && value.All(c => c is >= '0' and <= '9') &&
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= maximum;
    private static bool Signed(string? value) => value is { Length: >= 1 and <= 11 } &&
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) && number.ToString(CultureInfo.InvariantCulture) == value;
    private static bool BoundedText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    private static DiscoveryException Invalid() => new("INVALID_REQUEST", "Choose an exact Item, bridge index and bounded unique edge keys with a supported effects policy.");
}
