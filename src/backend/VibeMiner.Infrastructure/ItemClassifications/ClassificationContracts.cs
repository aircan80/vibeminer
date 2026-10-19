using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed record ClassificationField(string Name, int ColumnIndex, string Family,
    int? IntegerBits, bool? Unsigned, bool VerifiedName);
internal sealed record ClassificationIndexBinding(ItemsContext Context, string BuildVersion,
    OpeningSourceReference SourceReference, IReadOnlyList<ConfigurationReceipt> Configurations,
    ItemClassificationKind Kind, TableDataSnapshot Source, TableRowsArtifact Rows,
    IReadOnlyList<ClassificationField> KeyFields, ClassificationField TextField);
internal sealed record ClassificationIndexArtifact(string ContentKey, string Sha256, long ByteLength,
    ClassificationIndexBinding Binding, string RowCount, string KeyCount,
    string Revision = ClassificationPolicy.Revision);
internal sealed record StoredClassificationAdmission(ClassificationIndexArtifact Artifact, string PreparedAt);

internal static class ClassificationPolicy
{
    internal const string CompositeRevision = "classification-label-index-v2", SubclassTable = "95d8638f", SubclassFile = "1261604";
    internal static string Table(ItemClassificationKind kind) => kind == ItemClassificationKind.Class ? ClassTable : SubclassTable;
    internal static string Format(ItemClassificationKind kind) => kind == ItemClassificationKind.Class ? Revision : CompositeRevision;
    internal const string Revision = "classification-label-index-v1", ClassTable = "b977271e", ClassFile = "1140189";
    internal const int MaxDescriptorBytes = 1 << 20, MaxLiveDescriptorBytes = 128 << 10, MaxScratchBytes = 16 << 20, MaxArtifactBytes = 16 << 20, MaxRows = 65_536, MaxTextUnits = 4096;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { MaxDepth = 32, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    // Data identity and original receipt authority deliberately use different time policies.
    internal static ClassificationIndexBinding NormalizeBinding(ClassificationIndexBinding binding) => binding with
    {
        Configurations = binding.Configurations.Select(c => c with { RetrievedAt = "", VerifiedAt = "" }).ToArray(),
        Source = binding.Source with
        {
            PreparedAt = "",
            Source = binding.Source.Source with { RetrievedAt = "", VerifiedAt = "" },
            Association = binding.Source.Association with
            {
                EncodingSource = binding.Source.Association.EncodingSource with { RetrievedAt = "", VerifiedAt = "" },
                RootSource = binding.Source.Association.RootSource with { RetrievedAt = "", VerifiedAt = "" }
            }
        }
    };
    internal static byte[] NormalizedBytes(ClassificationIndexBinding binding)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(NormalizeBinding(binding), Json);
        if (bytes.Length > MaxLiveDescriptorBytes) throw Limit("The classification metadata exceeds its128KiB live-memory allowance.");
        return bytes;
    }
    internal static bool SameAuthority<T>(T left, T right)
    {
        var a = JsonSerializer.SerializeToNode(left, Json)!;
        var b = JsonSerializer.SerializeToNode(right, Json)!;
        Normalize(a, false); Normalize(b, false);
        return JsonNode.DeepEquals(a, b);
    }
    private static void Normalize(JsonNode? node, bool identity)
    {
        if (node is JsonObject obj)
        {
            foreach (var name in obj.Select(pair => pair.Key).ToArray())
            {
                if (name == "verifiedAt" || identity && name is "retrievedAt" or "preparedAt") obj[name] = "";
                else Normalize(obj[name], identity);
            }
        }
        else if (node is JsonArray array) foreach (var child in array) Normalize(child, identity);
    }
    internal static string ContentKey(ClassificationIndexBinding binding) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { revision = Format(binding.Kind), binding = NormalizeBinding(binding) }, Json)));
    internal static void Validate(ClassificationIndexArtifact artifact)
    {
        if (artifact is null) throw Invalid();
        ValidateBinding(artifact.Binding);
        if (artifact.Revision != Format(artifact.Binding.Kind) || artifact.ContentKey != ContentKey(artifact.Binding) ||
            artifact.Sha256 is not { Length: 64 } || artifact.Sha256.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            artifact.ByteLength is < 64 or > MaxArtifactBytes || !int.TryParse(artifact.RowCount, out var rows) || rows is < 0 or > MaxRows ||
            rows.ToString(System.Globalization.CultureInfo.InvariantCulture) != artifact.RowCount ||
            !int.TryParse(artifact.KeyCount, out var groups) || groups < 0 || groups > (artifact.Binding.Kind == ItemClassificationKind.Class ? 256 : 65_536) || groups > rows ||
            groups.ToString(System.Globalization.CultureInfo.InvariantCulture) != artifact.KeyCount ||
            rows != artifact.Binding.Rows.Completion.PhysicalRows + artifact.Binding.Rows.Completion.CopyRows)
            throw Invalid();
        if (JsonSerializer.SerializeToUtf8Bytes(artifact, Json).Length > MaxLiveDescriptorBytes) throw Limit("The classification descriptor exceeds its128KiB live-memory allowance.");
    }
    internal static void ValidateBinding(ClassificationIndexBinding b)
    {
        if (b is null || b.Context is null || b.Source is null || b.Rows is null || b.SourceReference is null ||
            b.Configurations is null || b.Configurations.Any(v => v is null) || b.KeyFields is null || b.KeyFields.Any(v => v is null) || b.TextField is null ||
            b.Source.Source is null || b.Source.Association is null || b.Source.Columns is null || b.Source.Columns.Count is < 1 or > 512 || b.Source.Columns.Any(v => v is null) ||
            b.Source.Coverage is null || b.Rows.Binding is null || b.Rows.Header is null || b.Rows.Completion is null ||
            b.Rows.Header.Columns is null || b.Rows.Header.Columns.Count is < 1 or > 512 || b.Rows.Header.Columns.Any(v => v is null) ||
            b.Source.Association.EncodingSource is null || b.Source.Association.RootSource is null || b.Source.Association.Memberships is null ||
            b.Source.Association.Memberships.Count > 256 || b.Source.Association.Memberships.Any(v => v is null)) throw Invalid();
        var s = b.Source; var r = b.Rows;
        if (b.Kind is not (ItemClassificationKind.Class or ItemClassificationKind.Subclass) || s.TableId != Table(b.Kind) || s.Association.FileDataId != (b.Kind == ItemClassificationKind.Class ? ClassFile : SubclassFile) ||
            b.Context.ResearchLocale != "enUS" || s.ResearchLocale != b.Context.ResearchLocale || s.ProductId != b.Context.ProductId ||
            s.BuildIdentity != b.Context.BuildIdentity || s.SourceRegion != b.Context.SourceRegion ||
            s.ArtifactId != r.ContentKey || r.ContentKey != TableRowArtifacts.ContentKey(r.Binding) ||
            s.Source.Sha256 != r.Binding.SourceSha256 || s.DefinitionSha256 != r.Binding.DefinitionSha256 ||
            s.ProviderRevision != r.Binding.ProviderRevision || s.DecoderRevision != r.Binding.DecoderRevision ||
            !string.Equals(s.LayoutHash, r.Header.LayoutHash, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(s.TableId, r.Header.TableHash, StringComparison.OrdinalIgnoreCase) ||
            b.Configurations.Count != 2 || b.Configurations.Count(c => c.Role == "buildConfig") != 1 ||
            b.Configurations.Count(c => c.Role == "cdnConfig") != 1 || b.KeyFields.Count != (b.Kind == ItemClassificationKind.Class ? 1 : 2) ||
            r.Completion.Scope is not ("complete" or "unencryptedSections") || s.Coverage.Scope != r.Completion.Scope ||
            r.Completion.PhysicalRows + r.Completion.CopyRows < 0)
            throw Invalid();
        if (r.Completion.PhysicalRows + r.Completion.CopyRows > MaxRows) throw Limit("The admitted classification target exceeds65,536 emitted rows.");
        var completion = r.Completion; var coverage = s.Coverage;
        string N(long number) => number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (completion.PhysicalRows < 0 || completion.CopyRows < 0 || completion.ExcludedPhysicalRecords < 0 || completion.ExcludedCopyRecords < 0 ||
            completion.ExcludedEncryptedSections < 0 || completion.DeclaredRecords != completion.PhysicalRows + completion.ExcludedPhysicalRecords ||
            r.Header.DeclaredCopyRecords != completion.CopyRows + completion.ExcludedCopyRecords ||
            coverage.DeclaredRecords != N(completion.DeclaredRecords) || coverage.PhysicalRows != N(completion.PhysicalRows) ||
            coverage.CopyRows != N(completion.CopyRows) || coverage.DeclaredCopyRecords != N(r.Header.DeclaredCopyRecords) ||
            coverage.ExcludedPhysicalRecords != N(completion.ExcludedPhysicalRecords) || coverage.ExcludedCopyRecords != N(completion.ExcludedCopyRecords) ||
            coverage.ExcludedDeclaredRecords != N(completion.ExcludedPhysicalRecords + completion.ExcludedCopyRecords) ||
            s.TextIssueCount != N(completion.TextIssueCount) || coverage.ExcludedKeyedSections != N(completion.ExcludedEncryptedSections) || s.RowCount != N(completion.PhysicalRows + completion.CopyRows) ||
            completion.Scope == "complete" && (completion.ExcludedEncryptedSections != 0 || completion.ExcludedPhysicalRecords != 0 || completion.ExcludedCopyRecords != 0) ||
            completion.Scope == "unencryptedSections" && completion.ExcludedEncryptedSections == 0) throw Invalid();
        var text = b.TextField;
        if (b.KeyFields.Where((key, index) => key.Name != (index == 0 ? "ClassID" : "SubClassID") || key.Family != "integer" ||
                key.IntegerBits != 8 || key.Unsigned != false || !key.VerifiedName).Any() ||
            text.Name != (b.Kind == ItemClassificationKind.Class ? "ClassName_lang" : "DisplayName_lang") || text.Family != "string" || !text.VerifiedName)
            throw new DiscoveryException("ITEM_CLASSIFICATION_PROFILE_UNSUPPORTED", "Labels require the selected verified signed8 key profile and exact selected label field.");
        foreach (var field in b.KeyFields.Append(text))
        {
            if (field.ColumnIndex < 0 || field.ColumnIndex >= s.Columns.Count || field.ColumnIndex >= r.Header.Columns.Count ||
                s.Columns[field.ColumnIndex].Name != field.Name || s.Columns[field.ColumnIndex].Family != field.Family ||
                s.Columns[field.ColumnIndex].Interpretation is not null || r.Header.Columns[field.ColumnIndex].Interpretation is not null ||
                r.Header.Columns[field.ColumnIndex].Name != field.Name || r.Header.Columns[field.ColumnIndex].Kind != field.Family)
                throw Invalid();
        }
    }
    internal static DiscoveryException Invalid() => new("ITEM_CLASSIFICATION_INDEX_INVALID", "The exact classification index or source binding is invalid.");
    internal static DiscoveryException Stale() => new("ITEM_CLASSIFICATION_STALE", "The exact classification target source or Items admission changed. Retry the visible batch.", true);
    internal static DiscoveryException Limit(string message) => new("ITEM_CLASSIFICATION_LIMIT", message);
}
