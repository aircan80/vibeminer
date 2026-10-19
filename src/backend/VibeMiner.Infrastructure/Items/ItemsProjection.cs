using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

internal sealed record ItemsProjectionBinding(ItemsContext Context, TableDataSnapshot ItemSource,
    TableDataSnapshot SparseSource, TableRowsArtifact ItemRows, TableRowsArtifact SparseRows);
internal sealed record ItemsProjectionArtifact(string ContentKey, string Sha256, long ByteLength,
    ItemsProjectionBinding Binding, ItemsJoinCounts Counts, IReadOnlyList<ItemPropertyGroup> Groups,
    string FeatureRevision = ItemsProjection.LegacyRevision);
internal sealed record ItemsProjectionLimits(long MaxArtifactBytes = 512L << 20, long MaxNameBytes = 256L << 20,
    int MaxRows = 1_000_000);

/// <summary>One compact, Item-anchored immutable projection. Original row artifacts remain evidence authority.</summary>
internal sealed partial class ItemsProjection(VerifiedObjectStore objects, AcquisitionResources resources,
    IAcquisitionWorkContext? context = null, ItemsProjectionLimits? configuredLimits = null, Action<string>? deleteStaging = null)
{
    internal const string LegacyRevision = "items-projection-v1", Revision = "items-projection-v2", QueryPolicy = "ordinal-ignorecase-contains-v1";
    internal const string ItemTable = "50238ec2", SparseTable = "919be54e";
    private const int HeaderBytes = 64, DirectoryBytes = 32, MaxMetadataBytes = 1 << 20;
    // Conservatively reserve 128 Unicode scalars: at most 1536 escaped JSON bytes.
    // The fixed success wrapper fits in the remainder. Keep application payloads below the frame budget.
    internal const int MaxResponseBytes = (512 << 10) - 2048;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { MaxDepth = 32, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private static readonly SemaphoreSlim Building = new(1, 1);
    private readonly ItemsProjectionLimits limits = configuredLimits ?? new();
    private DerivedArtifactStaging? cleanupDebt;
    private void RequireBuilderCleanup()
    {
        if (Volatile.Read(ref cleanupDebt) is { } debt) throw debt.Failure();
    }
    private sealed record Metadata(ItemsProjectionBinding Binding, ItemsJoinCounts Counts,
        IReadOnlyList<ItemPropertyGroup> Groups, string FeatureRevision);
    private static byte[] MetadataBytes(ItemsProjectionBinding binding, ItemsJoinCounts counts,
        IReadOnlyList<ItemPropertyGroup> groups, string revision) => revision == LegacyRevision
        ? JsonSerializer.SerializeToUtf8Bytes(new { Binding = binding, Counts = counts, Groups = groups }, Json)
        : JsonSerializer.SerializeToUtf8Bytes(new Metadata(binding, counts, groups, revision), Json);
    private sealed record SummaryPayload(IReadOnlyList<ItemSourceValue> BaseValues, string? IconFileDataId);
    private readonly record struct DirectoryEntry(uint Id, ItemNameState NameState, long NameOffset, int NameLength,
        int SummaryLength, long SummaryOffset)
    {
        internal void Write(Span<byte> bytes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Id); BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], (int)NameState);
            BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], NameOffset); BinaryPrimitives.WriteInt32LittleEndian(bytes[16..], NameLength);
            BinaryPrimitives.WriteInt32LittleEndian(bytes[20..], SummaryLength); BinaryPrimitives.WriteInt64LittleEndian(bytes[24..], SummaryOffset);
        }
        internal static DirectoryEntry Read(ReadOnlySpan<byte> b) => new(BinaryPrimitives.ReadUInt32LittleEndian(b),
            (ItemNameState)BinaryPrimitives.ReadInt32LittleEndian(b[4..]), BinaryPrimitives.ReadInt64LittleEndian(b[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(b[16..]), BinaryPrimitives.ReadInt32LittleEndian(b[20..]), BinaryPrimitives.ReadInt64LittleEndian(b[24..]));
    }

    internal static string ContentKey(ItemsProjectionBinding binding, string revision = Revision)
    {
        ValidateBinding(binding); ValidateRevision(revision);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            revision, queryPolicy = QueryPolicy, binding.Context,
            item = binding.ItemSource with { PreparedAt = "" }, sparse = binding.SparseSource with { PreparedAt = "" },
            binding.ItemRows, binding.SparseRows
        }, Json)));
    }

    internal async Task<ItemsProjectionArtifact> BuildAsync(ItemsProjectionBinding binding,
        Func<ItemsPreparationProgress, Task> progress, CancellationToken token)
    {
        RequireBuilderCleanup();
        ValidateBinding(binding);
        if (limits.MaxArtifactBytes is < 64 or > VerifiedObjectStore.MaxObjectBytes || limits.MaxNameBytes is < 0 or > 268435456 ||
            limits.MaxRows is < 0 or > TableRowArtifacts.MaxRows) throw new ArgumentOutOfRangeException(nameof(configuredLimits));
        if (!await Building.WaitAsync(0, token)) throw Error("ITEMS_BUSY", "Another compact Items projection is being prepared.");
        DerivedArtifactStaging? staging = null;
        try
        {
            RequireBuilderCleanup();
            staging = new(await resources.ReserveStagingAsync(checked(limits.MaxArtifactBytes + limits.MaxNameBytes + (long)limits.MaxRows * DirectoryBytes + MaxMetadataBytes), context, token),
                deleteStaging ?? File.Delete, "ITEMS_CLEANUP_FAILED");
            await using var itemReader = new TableRowArtifacts(objects, resources, context); await using var sparseReader = new TableRowArtifacts(objects, resources, context);
            var item = new SourceCursor(itemReader, binding.ItemRows);
            var sparse = new SourceCursor(sparseReader, binding.SparseRows);
            var hasItem = await item.NextAsync(token); var hasSparse = await sparse.NextAsync(token);
            var nameColumn = Column(binding.SparseSource, "Display_lang", "string");
            if (nameColumn < 0) throw Error("ITEMS_NAME_UNSUPPORTED", "The admitted Sparse schema has no verified Display_lang text field.");
            var groups = Groups(binding);
            var itemFields = SummaryColumns(binding.ItemSource, ["ClassID", "SubclassID", "InventoryType"]);
            var sparseFields = SummaryColumns(binding.SparseSource, ["OverallQualityID", "ItemLevel", "InventoryType"]);
            var iconColumn = Column(binding.ItemSource, "IconFileDataID", "integer");
            long itemCount = 0, sparseCount = 0, matched = 0, without = 0, sparseOnly = 0, valid = 0, empty = 0, invalid = 0;
            long nameBytes = 0; ItemsJoinCounts counts; long metadataOffset, namesOffset, directoryOffset; int metadataLength;
            string mainPath;
            {
                var main = await staging.CreateAsync(objects.CreateStagingFileAsync, token);
                var names = await staging.CreateAsync(objects.CreateStagingFileAsync, token);
                var directory = await staging.CreateAsync(objects.CreateStagingFileAsync, token);
                mainPath = main.Name;
                await main.WriteAsync(new byte[HeaderBytes], token);
                var operations = 0; long encodedSinceTurn = 0;
                async Task Slice(long anticipatedBytes = 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (++operations < 4096 && (encodedSinceTurn == 0 || anticipatedBytes <= (1 << 20) - encodedSinceTurn)) return;
                    operations = 0; encodedSinceTurn = 0;
                    await Task.Yield();
                    await progress(new("Joining admitted Item and ItemSparse", "rows", itemCount + sparseCount,
                        Total(binding.ItemRows) + Total(binding.SparseRows)));
                }
                while (hasItem)
                {
                    while (hasSparse && sparse.Current.Id < item.Current.Id)
                    { sparseOnly++; sparseCount++; hasSparse = await sparse.NextAsync(token); await Slice(); }
                    var row = item.Current; Db2Row? counterpart = hasSparse && sparse.Current.Id == row.Id ? sparse.Current : null;
                    // Strict UTF-8 needs at most three bytes per UTF-16 code unit; summary/directory have fixed caps.
                    await Slice((counterpart?.Values[nameColumn].Length ?? 0) * 3L + 8192 + DirectoryBytes);
                    byte[] payload, nameData, encodedEntry;
                    // Readers may cold-open CAS. Interpret one admitted row pair only after both advances finish.
                    using (await resources.EnterCpuAsync(context, token))
                    {
                        var name = counterpart is null ? new ItemName(ItemNameState.MissingSparse, null) : Name(counterpart, nameColumn);
                        if (counterpart is null) without++;
                        else
                        {
                            matched++; sparseCount++;
                            if (name.State == ItemNameState.Valid) valid++; else if (name.State == ItemNameState.Empty) empty++; else invalid++;
                        }
                        var fields = Values(binding.ItemSource, row, itemFields).Concat(counterpart is null ? [] : Values(binding.SparseSource, counterpart, sparseFields)).ToArray();
                        var icon = iconColumn < 0 ? null : Unsigned(row.Values[iconColumn]);
                        payload = JsonSerializer.SerializeToUtf8Bytes(new SummaryPayload(fields, icon), Json);
                        if (payload.Length > 8192) throw Error("ITEMS_PROJECTION_LIMIT", "An Items summary exceeds its compact payload bound.");
                        nameData = name.State == ItemNameState.Valid ? Utf8.GetBytes(name.Value!) : [];
                        if (nameData.Length > 262144 || nameData.Length > limits.MaxNameBytes - nameBytes || itemCount >= limits.MaxRows)
                            throw Error("ITEMS_PROJECTION_LIMIT", "Item rows or encoded names exceed their admitted projection budget.");
                        var entry = new DirectoryEntry(row.Id, name.State, nameBytes, nameData.Length, payload.Length, main.Position);
                        encodedEntry = new byte[DirectoryBytes]; entry.Write(encodedEntry);
                    }
                    await directory.WriteAsync(encodedEntry, token); await names.WriteAsync(nameData, token); await main.WriteAsync(payload, token);
                    nameBytes += nameData.Length; itemCount++;
                    encodedSinceTurn += nameData.Length + payload.Length + DirectoryBytes;
                    if (main.Position + nameBytes + itemCount * DirectoryBytes + MaxMetadataBytes > limits.MaxArtifactBytes)
                        throw Error("ITEMS_PROJECTION_LIMIT", "The compact Items artifact exceeds its complete publication budget.");
                    if (counterpart is not null) hasSparse = await sparse.NextAsync(token);
                    hasItem = await item.NextAsync(token);
                }
                while (hasSparse) { sparseOnly++; sparseCount++; hasSparse = await sparse.NextAsync(token); await Slice(); }
                if (itemCount != Total(binding.ItemRows) || sparseCount != Total(binding.SparseRows))
                    throw Error("ITEMS_SOURCE_INVALID", "The complete joined source traversal differs from its admission counts.");
                counts = new(N(itemCount), N(sparseCount), N(matched), N(without), N(sparseOnly), N(valid), N(empty), N(invalid));
                var metadata = MetadataBytes(binding, counts, groups, Revision);
                if (metadata.Length > MaxMetadataBytes) throw Error("ITEMS_PROJECTION_LIMIT", "Items source metadata exceeds its artifact bound.");
                metadataOffset = main.Position; metadataLength = metadata.Length; await main.WriteAsync(metadata, token);
                namesOffset = main.Position; await names.FlushAsync(token); names.Position = 0; await names.CopyToAsync(main, 65536, token);
                directoryOffset = main.Position; await directory.FlushAsync(token); directory.Position = 0; await directory.CopyToAsync(main, 65536, token);
                if (main.Position > limits.MaxArtifactBytes) throw Error("ITEMS_PROJECTION_LIMIT", "The complete Items projection exceeds its artifact bound.");
                var header = new byte[HeaderBytes]; "VMITEM01"u8.CopyTo(header);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 1); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), DirectoryBytes);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), itemCount); BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), metadataOffset);
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), metadataLength); BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(36), namesOffset);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(44), nameBytes); BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(52), directoryOffset);
                main.Position = 0; await main.WriteAsync(header, token); await main.FlushAsync(token);
            }
            await staging.CloseAsync();
            await progress(new("Publishing admitted Items projection", "rows", itemCount, itemCount));
            var length = checked(directoryOffset + itemCount * DirectoryBytes);
            var sha = await objects.PublishFileAsync(mainPath, length, resources, context, token);
            return new(ContentKey(binding), sha, length, binding, counts, groups, Revision);
        }
        finally
        {
            try
            {
                if (staging is not null && !await staging.RetireAsync())
                {
                    Volatile.Write(ref cleanupDebt, staging);
                    throw staging.Failure();
                }
            }
            finally { Building.Release(); }
        }
    }

    private sealed class SourceCursor(TableRowArtifacts reader, TableRowsArtifact artifact)
    {
        private IReadOnlyList<Db2Row> page = []; private int at; private long after; private uint? previous;
        internal Db2Row Current { get; private set; } = null!;
        internal async ValueTask<bool> NextAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (at == page.Count)
            {
                if (after == Total(artifact)) return false;
                var result = await reader.ReadPageAsync(artifact, after, 50, null, token); page = result.Rows; at = 0;
                if (result.TotalCount != Total(artifact) || page.Count == 0) throw Error("ITEMS_SOURCE_INVALID", "A source row page cannot advance to its complete admitted count.");
            }
            var row = page[at++]; after++;
            if (previous is { } prior && row.Id <= prior) throw Error("ITEMS_SOURCE_DUPLICATE", "Items sources require unique strictly increasing emitted target IDs.");
            previous = row.Id; Current = row; return true;
        }
    }
    private static ItemName Name(Db2Row row, int column) => row.TextIssues?.Any(issue => issue.ColumnIndex == column) == true
        ? new(ItemNameState.Invalid, null) : row.Values[column].Length == 0 ? new(ItemNameState.Empty, "") : new(ItemNameState.Valid, row.Values[column]);
    private static int Column(TableDataSnapshot source, string name, string family)
    {
        var columns = source.Columns.Select((column, index) => (column, index)).Where(pair => pair.column.Name == name &&
            pair.column.Family == family && pair.column.Interpretation is null).Select(pair => pair.index).ToArray();
        if (columns.Length > 1) throw Error("ITEMS_SCHEMA_AMBIGUOUS", "A named Items field has multiple verified source columns.");
        return columns.Length == 0 ? -1 : columns[0];
    }
    private static int[] SummaryColumns(TableDataSnapshot source, string[] names) => names.Select(name => Column(source, name, "integer")).Where(index => index >= 0).ToArray();
    private static IEnumerable<ItemSourceValue> Values(TableDataSnapshot source, Db2Row row, int[] columns)
    {
        foreach (var column in columns)
        {
            var value = row.Values[column];
            if (!(long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var signed) && N(signed) == value) &&
                !(ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var unsigned) && unsigned.ToString(CultureInfo.InvariantCulture) == value))
                throw Error("ITEMS_SOURCE_INVALID", "A source-backed integer summary field is not a canonical decoded integer.");
            yield return new(source.TableId, column, value);
        }
    }
    private static string? Unsigned(string value)
    {
        if (uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && N(id) == value) return value;
        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var signed) && N(signed) == value ||
            ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var unsigned) && unsigned.ToString(CultureInfo.InvariantCulture) == value) return null;
        throw Error("ITEMS_SOURCE_INVALID", "The admitted icon source is not a canonical decoded integer.");
    }
    private static long Total(TableRowsArtifact artifact) => checked(artifact.Completion.PhysicalRows + artifact.Completion.CopyRows);
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static DiscoveryException Error(string code, string message) => new(code, message);
    internal static void ValidateBinding(ItemsProjectionBinding b)
    {
        if (b is null || b.ItemSource.TableId != ItemTable || b.SparseSource.TableId != SparseTable) throw Error("ITEMS_SOURCE_INVALID", "Exact Item and ItemSparse admissions are required.");
        foreach (var pair in new[] { (b.ItemSource, b.ItemRows), (b.SparseSource, b.SparseRows) })
        {
            var (s, r) = pair;
            if (s.ProductId != b.Context.ProductId || s.BuildIdentity != b.Context.BuildIdentity || s.SourceRegion != b.Context.SourceRegion ||
                s.ResearchLocale != b.Context.ResearchLocale || s.ArtifactId != r.ContentKey || r.ContentKey != TableRowArtifacts.ContentKey(r.Binding) ||
                s.Source.Sha256 != r.Binding.SourceSha256 || s.DefinitionSha256 != r.Binding.DefinitionSha256 || s.ProviderRevision != r.Binding.ProviderRevision ||
                s.DecoderRevision != r.Binding.DecoderRevision || s.ResearchLocale != r.Binding.Locale || !string.Equals(s.LayoutHash, r.Header.LayoutHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(s.TableId, r.Header.TableHash, StringComparison.OrdinalIgnoreCase) || r.Completion.PhysicalRows < 0 || r.Completion.CopyRows < 0 ||
                Total(r) > TableRowArtifacts.MaxRows || s.RowCount != N(Total(r)) || s.TextIssueCount != N(r.Completion.TextIssueCount) ||
                s.Coverage != new TableDataCoverage(r.Completion.Scope, N(r.Completion.DeclaredRecords), N(r.Completion.PhysicalRows), N(r.Completion.CopyRows),
                    N(r.Completion.ExcludedEncryptedSections), N(r.Completion.ExcludedPhysicalRecords + r.Completion.ExcludedCopyRecords), N(r.Header.DeclaredCopyRecords),
                    N(r.Completion.ExcludedPhysicalRecords), N(r.Completion.ExcludedCopyRecords)) || s.Columns.Count != r.Header.Columns.Count ||
                s.Columns.Where((c, i) => c.Name != r.Header.Columns[i].Name || c.Family != r.Header.Columns[i].Kind || c.IsId != r.Header.Columns[i].IsId ||
                    c.Interpretation != (r.Header.Columns[i].Interpretation is { } p ? new TableColumnInterpretation(p.Policy, p.PhysicalBits,
                        p.PhysicalSigned, p.CandidateBits, p.CandidateUnsigned, p.Warning) : null)).Any())
                throw Error("ITEMS_SOURCE_CONTEXT", "The feature source differs from its exact context, schema or immutable row artifact.");
        }
    }
    private static void ValidateRevision(string revision)
    {
        if (revision is not (LegacyRevision or Revision))
            throw Error("ITEMS_PROJECTION_INVALID", "The Items artifact uses an unsupported feature revision.");
    }
    private static IReadOnlyList<ItemPropertyGroup> Groups(ItemsProjectionBinding binding, string revision = Revision)
    {
        var result = new Dictionary<string, List<ItemFieldReference>>(StringComparer.Ordinal);
        foreach (var source in new[] { binding.ItemSource, binding.SparseSource })
        for (var index = 0; index < source.Columns.Count; index++)
        {
            var column = source.Columns[index]; if (column.Interpretation is not null) continue;
            var name = column.Name.Split('[')[0];
            var group = name switch
            {
                "ID" or "Display_lang" or "Display1_lang" or "Display2_lang" or "Display3_lang" or "Description_lang" or "IconFileDataID" => "identity",
                "ClassID" or "SubclassID" or "InventoryType" or "OverallQualityID" or "ItemLevel" => "classification",
                "RequiredLevel" or "RequiredSkill" or "RequiredSkillRank" or "RequiredAbility" or "MinFactionID" or "MinReputation" => "requirements",
                "Stackable" or "MaxCount" or "ContainerSlots" or "BuyPrice" or "SellPrice" or "VendorStackCount" or "PriceVariance" or "PriceRandomValue" or "DurationInInventory" or "Bonding" => "inventoryEconomy",
                "SocketType" or "StatModifier_bonusStat" or "StatPercentEditor" or "StatPercentageOfSocket" => "socketsStats",
                "Socket_match_enchantment_id" or "GemProperties" when revision == LegacyRevision => "socketsStats",
                "Socket_match_enchantment_ID" or "Gem_properties" when revision == Revision => "socketsStats",
                "Flags" or "AllowableRace" or "AllowableRaces" or "AllowableClass" or "ZoneBound" or "RequiredHoliday" or "InstanceBound" => "restrictions",
                "DamageType" or "ItemDelay" or "DmgVariance" or "ItemRange" or "ContentTuningID" or "ItemBonusListID" => "otherInputs",
                _ => null
            };
            if (group is not null) { if (!result.TryGetValue(group, out var fields)) result.Add(group, fields = []); fields.Add(new(source.TableId, index)); }
        }
        return result.Select(pair => new ItemPropertyGroup(pair.Key, pair.Value.AsReadOnly())).ToArray();
    }
}
