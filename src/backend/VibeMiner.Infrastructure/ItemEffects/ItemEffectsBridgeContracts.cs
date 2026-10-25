using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed record ItemEffectsBridgeBinding(ItemsContext Context, string BuildVersion,
    OpeningSourceReference SourceReference, IReadOnlyList<ConfigurationReceipt> Configurations,
    TableDataSnapshot Source, TableRowsArtifact Rows, ItemEffectsBridgeFields Fields);
internal sealed record ItemEffectsBridgeArtifact(string ContentKey, string Sha256, long ByteLength,
    ItemEffectsBridgeBinding Binding, string RowCount, string Revision = ItemEffectsBridgePolicy.Revision);
internal sealed record StoredItemEffectsBridgeAdmission(ItemEffectsBridgeArtifact Artifact, string PreparedAt);

// A written caller-owned stage, not a published/registered artifact or a SHA proof.
internal sealed record ItemEffectsBridgeBuild(string ContentKey, long ByteLength,
    ItemEffectsBridgeBinding Binding, string RowCount);
internal readonly record struct ItemEffectsBridgeTuple(uint IndexOrdinal, int ItemId, uint BridgeId,
    int ItemEffectId, bool IsCopy, uint? CopySourceId, ulong SourceOrdinal);
internal readonly record struct ItemEffectsBridgeRange(uint Start, uint End)
{ internal uint Count => End - Start; }

internal static class ItemEffectsBridgePolicy
{
    internal const string Revision = "item-effect-bridge-index-v1", OrderPolicy = "signed-item-unsigned-bridge-source-ordinal-v1";
    internal const string Table = "00cb674f", Layout = "96F083AD", FileDataId = "3177687",
        Definition = "3c513c171fc9d4f6aab09147c86b8c5c2da1a573adf4b04904b20067d5e7d0eb";
    internal const int MaxRows = 65_536, MaxMetadataBytes = 128 << 10, MaxArtifactBytes = 4 << 20,
        HeaderBytes = 64, TupleBytes = 32, IoBytes = 64 << 10, CpuEntries = 4096;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
    internal static DiscoveryException Invalid() => new("ITEM_EFFECTS_INDEX_INVALID", "The effects bridge index or its source binding is invalid.");
    internal static DiscoveryException Limit() => new("ITEM_EFFECTS_LIMIT", "The effects resource limit was exceeded.");
    internal static DiscoveryException Cleanup() => new("ITEM_EFFECTS_CLEANUP_FAILED", "The effects resource cleanup could not retire.");
    internal static DiscoveryException CursorInvalid() => new("ITEM_EFFECTS_CURSOR_INVALID", "The effects page cursor is invalid for this selected range.");
    internal static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    internal static bool Hash(string? value, int width = 64) => value is not null && value.Length == width && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static long Number(string? value)
    {
        if (value is null || value.Length is < 1 or > 19 || value.Length > 1 && value[0] == '0' ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < 0 || N(result) != value) throw Invalid();
        return result;
    }
    internal static byte[] Bytes<T>(T value)
    {
        try
        {
            using var output = new BoundedBytes(); JsonSerializer.Serialize(output, value, Json);
            var bytes = output.ToArray(); Preflight(bytes); return bytes;
        }
        catch (JsonException) { throw Invalid(); }
    }
    private sealed class BoundedBytes : MemoryStream
    {
        internal BoundedBytes() : base(MaxMetadataBytes) { }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        private void Check(int count) { if (count < 0 || count > MaxMetadataBytes - Length) throw Limit(); }
    }
    private static void Preflight(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
        Span<int> limits = stackalloc int[32], counts = stackalloc int[32]; Span<bool> arrays = stackalloc bool[32];
        var depth = 0; var next = 512;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                next = reader.ValueTextEquals("configurations"u8) ? 2 : reader.ValueTextEquals("memberships"u8) ? 256 :
                    reader.ValueTextEquals("readableRanges"u8) || reader.ValueTextEquals("unavailableRanges"u8) ? ContentProjectionIdentity.MaxRanges : 512;
                continue;
            }
            if (reader.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject) { depth--; continue; }
            if (depth > 0 && arrays[depth - 1] && ++counts[depth - 1] > limits[depth - 1]) throw Limit();
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                if (depth == arrays.Length) throw Limit();
                arrays[depth] = reader.TokenType == JsonTokenType.StartArray; counts[depth] = 0; limits[depth] = next; depth++;
            }
            next = 512;
        }
    }
    internal static ItemEffectsBridgeBinding FreezeBinding(ItemEffectsBridgeBinding binding)
    {
        // This synchronous bounded capture is deliberately before the first await of Build/Open.
        ShallowBounds(binding); var bytes = Bytes(binding);
        try { var owned = JsonSerializer.Deserialize<ItemEffectsBridgeBinding>(bytes, Json) ?? throw Invalid(); ValidateBinding(owned); return owned; }
        catch (JsonException) { throw Invalid(); }
    }
    private static void ShallowBounds(ItemEffectsBridgeBinding b)
    {
        if (b is null) throw Invalid();
        if (b.Configurations?.Count > 2 || b.Source?.Columns?.Count > 512 || b.Source?.Association?.Memberships?.Count > 256 ||
            b.Rows?.Header?.Columns?.Count > 512) throw Limit();
        if (b is null || b.Context is null || b.SourceReference is null || b.Configurations is null || b.Configurations.Count != 2 ||
            b.Source is null || b.Source.Source is null || b.Source.Association is null || b.Source.Association.EncodingSource is null || b.Source.Association.RootSource is null ||
            b.Source.Columns is null || b.Source.Columns.Count is < 1 or > 512 || b.Source.Association.Memberships is null || b.Source.Association.Memberships.Count is < 1 or > 256 ||
            b.Rows is null || b.Rows.Binding is null || b.Rows.Header is null || b.Rows.Header.Columns is null || b.Rows.Header.Columns.Count is < 1 or > 512 || b.Rows.Completion is null || b.Fields is null)
            throw Invalid();
        if (b.Source.Source.Projection is { } projection && (projection.ReadableRanges is null || projection.UnavailableRanges is null ||
            projection.ReadableRanges.Count > 128 || projection.UnavailableRanges.Count > 128 || (long)projection.ReadableRanges.Count + projection.UnavailableRanges.Count > 128)) throw Limit();
    }
    internal static ItemEffectsBridgeBinding NormalizeBinding(ItemEffectsBridgeBinding b)
    {
        _ = Bytes(b);
        return b with
    {
        Configurations = b.Configurations.Select(c => c with { RetrievedAt = "", VerifiedAt = "" }).ToArray(),
        Source = b.Source with { PreparedAt = "", Source = b.Source.Source with { RetrievedAt = "", VerifiedAt = "" },
            Association = b.Source.Association with
            {
                EncodingSource = b.Source.Association.EncodingSource with { RetrievedAt = "", VerifiedAt = "" },
                RootSource = b.Source.Association.RootSource with { RetrievedAt = "", VerifiedAt = "" }
            } }
    };
    }
    internal static byte[] MetadataBytes(ItemEffectsBridgeBinding binding, string rowCount)
    {
        _ = Bytes(binding); ValidateBinding(binding);
        if (Number(rowCount) != Total(binding)) throw Invalid();
        return Bytes(new { revision = Revision, orderPolicy = OrderPolicy, binding = NormalizeBinding(binding), rowCount });
    }
    internal static string ContentKey(ItemEffectsBridgeBinding binding, string rowCount) =>
        Convert.ToHexStringLower(SHA256.HashData(MetadataBytes(binding, rowCount)));
    internal static long Total(ItemEffectsBridgeBinding b)
    {
        var physical = b.Rows.Completion.PhysicalRows; var copies = b.Rows.Completion.CopyRows;
        if (physical < 0 || copies < 0) throw Invalid();
        if (physical > MaxRows || copies > MaxRows || physical > MaxRows - copies) throw Limit();
        return physical + copies;
    }
    internal static void ValidateBinding(ItemEffectsBridgeBinding b)
    {
        try
        {
            ShallowBounds(b); ItemsService.Validate(b.Context); var s = b.Source; var r = b.Rows; var c = r.Completion; var coverage = s.Coverage;
            var total = Total(b); var names = new[] { "ID", "ItemEffectID", "ItemID" };
            if (b.BuildVersion is null || b.BuildVersion.Length is < 7 or > 43 || s.Coverage is null ||
                s.ProductId != b.Context.ProductId || s.BuildIdentity != b.Context.BuildIdentity || s.SourceRegion != b.Context.SourceRegion || s.ResearchLocale != "enUS" || s.TableId != Table ||
                r.Header.SchemaVersion != 5 || s.LayoutHash != Layout || !string.Equals(r.Header.TableHash, Table, StringComparison.OrdinalIgnoreCase) || r.Header.LayoutHash.ToUpperInvariant() != Layout ||
                !Hash(s.DefinitionSha256) || !Hash(s.ProviderRevision, 40) || s.DecoderRevision !=
                    (s.Source.Projection is null ? TableDataStore.DecoderRevision : TableDataStore.ProjectionDecoderRevision) ||
                s.ArtifactId != r.ContentKey || r.ContentKey != TableRowArtifacts.ContentKey(r.Binding) || !Hash(r.Sha256) || r.ByteLength is < 64 or > VerifiedObjectStore.MaxObjectBytes ||
                r.Binding.SourceSha256 != s.Source.Sha256 || r.Binding.DefinitionSha256 != s.DefinitionSha256 || r.Binding.ProviderRevision != s.ProviderRevision || r.Binding.Locale != "enUS" || r.Binding.DecoderRevision != s.DecoderRevision ||
                !Hash(r.Binding.DecodedSha256) || !Hash(r.Binding.SchemaSelectionSha256) || s.Source.Projection is { } projection && r.Binding.DecodedSha256 != projection.IdentitySha256 ||
                c.ConsumedBytes != Number(s.Source.DecodedBytes) ||
                s.Source.VerifierRevision != (s.Source.Projection is null ? "content-blte-v1" : ContentProjectionIdentity.VerifierRevision) ||
                s.Source.Projection is { } admittedProjection && admittedProjection.IdentitySha256 != ContentProjectionIdentity.Compute(s.Source.ContentKey, s.Source.EncodingKey, s.Source.DecodedBytes, admittedProjection) ||
                s.Association.FileDataId != FileDataId || s.Association.ResearchLocale != "enUS" || s.Association.EncodingSource.Role != "encoding" || s.Association.RootSource.Role != "root" ||
                !Hash(s.Source.ContentKey, 32) || !Hash(s.Source.EncodingKey, 32) || !Hash(s.Source.Sha256) || Number(s.Source.EncodedBytes) is < 1 or > VerifiedObjectStore.MaxObjectBytes || Number(s.Source.DecodedBytes) is < 1 or > VerifiedObjectStore.MaxObjectBytes ||
                s.Columns.Count != 3 || r.Header.Columns.Count != 3 || b.Configurations.Any(v => v is null) || b.Configurations.Count(v => v.Role == "buildConfig") != 1 || b.Configurations.Count(v => v.Role == "cdnConfig") != 1 ||
                b.Fields.RowId != new ItemEffectsFieldReference(Table, 0) || b.Fields.ItemEffectId != new ItemEffectsFieldReference(Table, 1) || b.Fields.ItemId != new ItemEffectsFieldReference(Table, 2) ||
                c.DeclaredRecords < 0 || c.ExcludedPhysicalRecords < 0 || c.ExcludedCopyRecords < 0 || c.ExcludedEncryptedSections < 0 || c.TextIssueCount != 0 ||
                c.DeclaredRecords != checked(c.PhysicalRows + c.ExcludedPhysicalRecords) || r.Header.DeclaredRecords != c.DeclaredRecords || r.Header.DeclaredCopyRecords != checked(c.CopyRows + c.ExcludedCopyRecords) ||
                c.Scope is not ("complete" or "unencryptedSections") || c.Scope == "complete" && (c.ExcludedPhysicalRecords != 0 || c.ExcludedCopyRecords != 0 || c.ExcludedEncryptedSections != 0 || s.Source.Projection is not null) ||
                c.Scope == "unencryptedSections" && c.ExcludedEncryptedSections < 1 ||
                coverage.Scope != c.Scope || coverage.DeclaredRecords != N(c.DeclaredRecords) || coverage.PhysicalRows != N(c.PhysicalRows) || coverage.CopyRows != N(c.CopyRows) ||
                coverage.DeclaredCopyRecords != N(r.Header.DeclaredCopyRecords) || coverage.ExcludedKeyedSections != N(c.ExcludedEncryptedSections) ||
                coverage.ExcludedPhysicalRecords != N(c.ExcludedPhysicalRecords) || coverage.ExcludedCopyRecords != N(c.ExcludedCopyRecords) ||
                coverage.ExcludedDeclaredRecords != N(checked(c.ExcludedPhysicalRecords + c.ExcludedCopyRecords)) || s.RowCount != N(total) || s.TextIssueCount != "0") throw Invalid();
            for (var i = 0; i < 3; i++)
                if (s.Columns[i] is not { } column || r.Header.Columns[i] is not { } raw || column.Name != names[i] || raw.Name != names[i] ||
                    column.Family != "integer" || raw.Kind != "integer" || column.Interpretation is not null || raw.Interpretation is not null || column.IsId != (i == 0) || raw.IsId != (i == 0)) throw Invalid();
            _ = Bytes(b);
        }
        catch (Exception error) when (error is OverflowException or ArgumentException or NullReferenceException) { throw Invalid(); }
        catch (DiscoveryException error) when (error.Code is not ("ITEM_EFFECTS_INDEX_INVALID" or "ITEM_EFFECTS_LIMIT")) { throw Invalid(); }
    }
    internal static void ValidateArtifact(ItemEffectsBridgeArtifact artifact)
    {
        if (artifact is null) throw Invalid(); _ = Bytes(artifact); ValidateBinding(artifact.Binding);
        var metadata = MetadataBytes(artifact.Binding, artifact.RowCount);
        if (artifact.Revision != Revision || !Hash(artifact.Sha256) || artifact.ContentKey != Convert.ToHexStringLower(SHA256.HashData(metadata)) ||
            artifact.ByteLength != checked(HeaderBytes + metadata.Length + Total(artifact.Binding) * TupleBytes) || artifact.ByteLength > MaxArtifactBytes) throw Invalid();
    }
    // D101: the bridge is checked against the definition Tables chooses now, bundled or fetched; the reviewed layout and
    // field profile below are what qualify it. Rows read with another definition are stale, not invalid.
    internal static async Task ValidateProfileAsync(ItemEffectsBridgeBinding binding, CancellationToken token,
        Func<Action, CancellationToken, Task> cpuSlice, TableDefinitionSources definitions)
    {
        token.ThrowIfCancellationRequested();
        await cpuSlice(() => ValidateBinding(binding), token);
        var definition = await definitions.ForFeatureRowsAsync(Table, binding.BuildVersion, binding.Rows, token);
        if (binding.Source.DefinitionSha256 != definition.Sha256 || binding.Source.ProviderRevision != definition.Revision)
            throw new DiscoveryException("ITEM_EFFECTS_BRIDGE_STALE", "The selected effects bridge source or index changed.", true);
        await cpuSlice(() => ValidateProfile(binding, definition, token), token);
    }
    private static void ValidateProfile(ItemEffectsBridgeBinding binding, TableDefinition definition, CancellationToken token)
    {
        ValidateBinding(binding); token.ThrowIfCancellationRequested();
        if (binding.Rows.Binding.SchemaSelectionSha256 != TableRowArtifacts.SchemaSelectionKey(definition.Document, binding.BuildVersion, token)) throw Invalid();
        var selected = definition.Document.SelectFile(binding.BuildVersion, Layout, token);
        if (selected.Blocks.Count != 1 || selected.Blocks[0].Fields.Count != 3) throw Invalid();
        var fields = selected.Blocks[0].Fields; var names = new[] { "ID", "ItemEffectID", "ItemID" };
        for (var i = 0; i < 3; i++)
        {
            var field = fields[i];
            if (!field.Column.VerifiedName || field.Column.Name != names[i] || field.Column.Family != "int" || field.ArrayLength != 1 || field.IntegerBits != 32 ||
                field.Unsigned != false || field.IsId != (i == 0) || field.NonInline != (i != 1) || field.RelationStorage != (i == 2) ||
                binding.Source.Columns[i] != TableDataStore.ProjectColumn(binding.Rows.Header.Columns[i], definition.Document)) throw Invalid();
        }
    }
    internal static bool SameAuthority<T>(T first, T current)
    {
        var a = JsonNode.Parse(Bytes(first)); var b = JsonNode.Parse(Bytes(current));
        ClearVerified(a); ClearVerified(b); return JsonNode.DeepEquals(a, b);
    }
    internal static bool SameCurrentSource(TableDataSnapshot first, TableDataSnapshot current)
    {
        _ = Bytes(first); _ = Bytes(current);
        return SameAuthority(first with { PreparedAt = "" }, current with { PreparedAt = "" });
    }
    private static void ClearVerified(JsonNode? value)
    {
        if (value is JsonObject obj) foreach (var name in obj.Select(pair => pair.Key).ToArray())
        { if (name == "verifiedAt") obj[name] = ""; else ClearVerified(obj[name]); }
        else if (value is JsonArray array) foreach (var child in array) ClearVerified(child);
    }
}
