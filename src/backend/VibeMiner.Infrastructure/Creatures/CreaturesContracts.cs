using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

internal sealed record CreatureProfileField(string Name, int ColumnIndex, string Family, int? Bits, bool? Unsigned, int Slot = 0);
internal sealed record CreaturesBinding(CreaturesContext Context, string BuildVersion, OpeningSourceReference SourceReference,
    IReadOnlyList<ConfigurationReceipt> Configurations, TableDataSnapshot Source, TableRowsArtifact Rows,
    IReadOnlyList<CreatureProfileField> Fields);
internal sealed record CreaturesArtifact(string ContentKey, string Sha256, long ByteLength, CreaturesBinding Binding,
    CreatureCounts Counts, string FeatureRevision = CreaturesPolicy.FeatureRevision);
internal sealed record StoredCreaturesAdmission(CreaturesArtifact Artifact, string PreparedAt);
internal sealed record CreaturesLimits(int MaxRows = 1_000_000, long MaxArtifactBytes = 128L << 20, long MaxNameBytes = 64L << 20,
    int MaxMetadataBytes = 128 << 10, int MaxSummaryBytes = 2048);

internal static class CreaturesPolicy
{
    internal const string FeatureRevision = "creatures-core-v1", QueryPolicy = "primary-name-ordinal-ignorecase-contains-v1",
        SemanticProfile = "creatures-direct-display-v1", Table = "c9d6b6b3", Layout = "6E14C900", FileDataId = "841631";
    internal const int MaxDescriptorBytes = 128 << 10, MaxResponseBytes = (512 << 10) - 4096;
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { MaxDepth = 32, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    internal static readonly string[] FactNames = ["Classification", "CreatureType", "CreatureFamily", "StartAnimState"];
    internal static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    internal static DiscoveryException Invalid() => new("CREATURES_PROJECTION_INVALID", "The Creature projection or its exact source binding is invalid.");
    internal static DiscoveryException ReceiptMismatch(MetadataReceipt admitted) => MetadataReceiptIdentity.Mismatch("CREATURES_SOURCE_RECEIPT_MISMATCH", "The Creature table", admitted);
    internal static DiscoveryException Unsupported() => new("CREATURES_SCHEMA_UNSUPPORTED", "The exact selected Creature profile does not qualify names, numeric facts and four declared slots.");
    internal static DiscoveryException Limit() => new("CREATURES_PROJECTION_LIMIT", "Creature projection bytes, metadata, names or rows exceed the bounded feature budget.");
    internal static DiscoveryException Stale() => new("CREATURES_STALE", "The current Creature source or exact opening changed. Refresh the Creature browser.", true);
    internal static bool Hash(string? value, int width = 64) => value is not null && value.Length == width && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool Number(string? value, out long number) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0 && N(number) == value;
    internal static CreaturesBinding Normalize(CreaturesBinding b) => b with
    {
        Configurations = b.Configurations.Select(c => c with { RetrievedAt = "", VerifiedAt = "" }).ToArray(),
        Source = b.Source with { PreparedAt = "", Source = b.Source.Source with { RetrievedAt = "", VerifiedAt = "" },
            Association = b.Source.Association with {
                EncodingSource = b.Source.Association.EncodingSource with { RetrievedAt = "", VerifiedAt = "" },
                RootSource = b.Source.Association.RootSource with { RetrievedAt = "", VerifiedAt = "" } } }
    };
    internal static byte[] Bytes<T>(T value)
    {
        var output = new DescriptorWriter();
        using var writer = new Utf8JsonWriter(output); JsonSerializer.Serialize(writer, value, Json); writer.Flush(); return output.Written();
    }
    private sealed class DescriptorWriter : IBufferWriter<byte>
    {
        private readonly byte[] bytes = new byte[MaxDescriptorBytes]; private int count;
        public void Advance(int written) { if (written < 0 || written > bytes.Length - count) throw Limit(); count += written; }
        public Memory<byte> GetMemory(int sizeHint = 0) { if (sizeHint < 0 || sizeHint > bytes.Length - count) throw Limit(); return bytes.AsMemory(count); }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        internal byte[] Written() => bytes.AsSpan(0, count).ToArray();
    }
    internal static string ContentKey(CreaturesBinding b) => Convert.ToHexStringLower(SHA256.HashData(Bytes(new
        { FeatureRevision, QueryPolicy, SemanticProfile, binding = Normalize(b) })));
    internal static void Validate(CreaturesArtifact artifact)
    {
        if (artifact is null) throw Invalid(); ValidateBinding(artifact.Binding);
        if (artifact.FeatureRevision != FeatureRevision || artifact.ContentKey != ContentKey(artifact.Binding) || !Hash(artifact.Sha256) ||
            artifact.ByteLength is < 64 or > (128L << 20) || artifact.Counts is null) throw Invalid();
        var c = artifact.Counts;
        if (!Number(c.Rows, out var rows) || !Number(c.ValidNames, out var valid) || !Number(c.EmptyNames, out var empty) || !Number(c.InvalidNames, out var invalid) ||
            !Number(c.PositiveDisplaySlots, out var positive) || !Number(c.ZeroDisplaySlots, out var zero) || !Number(c.InvalidDisplaySlots, out var negative) ||
            rows != Total(artifact.Binding) || valid > rows || empty > rows || invalid > rows || valid + empty + invalid != rows ||
            positive > rows * 4 || zero > rows * 4 || negative > rows * 4 || positive + zero + negative != rows * 4) throw Invalid();
        _ = Bytes(artifact);
    }
    internal static long Total(CreaturesBinding b) => checked(b.Rows.Completion.PhysicalRows + b.Rows.Completion.CopyRows);
    internal static void ValidateBinding(CreaturesBinding b)
    {
        if (b is null || b.Context is null || b.SourceReference is null || b.Configurations is null || b.Configurations.Count != 2 || b.Configurations.Any(c => c is null) ||
            b.Source is null || b.Source.Source is null || b.Source.Association is null || b.Source.Association.EncodingSource is null || b.Source.Association.RootSource is null ||
            b.Source.Association.Memberships is null || b.Source.Association.Memberships.Count is < 1 or > 256 || b.Source.Association.Memberships.Any(m => m is null) ||
            b.Source.Columns is null || b.Source.Columns.Count is < 1 or > 512 || b.Source.Columns.Any(c => c is null) || b.Source.Coverage is null ||
            b.Rows is null || b.Rows.Binding is null || b.Rows.Header is null || b.Rows.Header.Columns is null || b.Rows.Header.Columns.Any(c => c is null) || b.Rows.Completion is null ||
            b.Fields is null || b.Fields.Count != 16 || b.Fields.Any(f => f is null)) throw Invalid();
        CreaturesService.Validate(b.Context); var s = b.Source; var r = b.Rows; var c = r.Completion; var coverage = s.Coverage;
        if (s.LayoutHash != Layout || r.Header.LayoutHash != Layout) throw Unsupported();
        if (c.PhysicalRows > 1_000_000 || c.CopyRows > 1_000_000 || c.PhysicalRows >= 0 && c.CopyRows >= 0 && c.PhysicalRows > 1_000_000 - c.CopyRows) throw Limit();
        if (s.ProductId != b.Context.ProductId || s.BuildIdentity != b.Context.BuildIdentity || s.SourceRegion != b.Context.SourceRegion || s.ResearchLocale != "enUS" ||
            s.TableId != Table || !string.Equals(r.Header.TableHash, Table, StringComparison.OrdinalIgnoreCase) || 
            s.Association.FileDataId != FileDataId || s.ArtifactId != r.ContentKey || r.ContentKey != TableRowArtifacts.ContentKey(r.Binding) ||
            s.Source.Sha256 != r.Binding.SourceSha256 || s.DefinitionSha256 != r.Binding.DefinitionSha256 || s.ProviderRevision != r.Binding.ProviderRevision || s.DecoderRevision != r.Binding.DecoderRevision ||
            r.Header.Columns.Count != s.Columns.Count || c.PhysicalRows < 0 || c.CopyRows < 0 || c.PhysicalRows > 1_000_000 - c.CopyRows ||
            c.ExcludedPhysicalRecords < 0 || c.ExcludedCopyRecords < 0 || c.ExcludedEncryptedSections < 0 || c.TextIssueCount < 0 || c.TextIssueCount > Total(b) * s.Columns.Count || c.DeclaredRecords != c.PhysicalRows + c.ExcludedPhysicalRecords ||
            r.Header.DeclaredCopyRecords != c.CopyRows + c.ExcludedCopyRecords || c.Scope is not ("complete" or "unencryptedSections") ||
            coverage.Scope != c.Scope || coverage.DeclaredRecords != N(c.DeclaredRecords) || coverage.PhysicalRows != N(c.PhysicalRows) || coverage.CopyRows != N(c.CopyRows) ||
            coverage.DeclaredCopyRecords != N(r.Header.DeclaredCopyRecords) || coverage.ExcludedKeyedSections != N(c.ExcludedEncryptedSections) ||
            coverage.ExcludedPhysicalRecords != N(c.ExcludedPhysicalRecords) || coverage.ExcludedCopyRecords != N(c.ExcludedCopyRecords) ||
            coverage.ExcludedDeclaredRecords != N(c.ExcludedPhysicalRecords + c.ExcludedCopyRecords) || s.RowCount != N(Total(b)) || s.TextIssueCount != N(c.TextIssueCount) ||
            c.Scope == "complete" && (c.ExcludedPhysicalRecords != 0 || c.ExcludedCopyRecords != 0 || c.ExcludedEncryptedSections != 0) ||
            c.Scope == "unencryptedSections" && c.ExcludedEncryptedSections == 0 ||
            b.Configurations.Count(v => v.Role == "buildConfig") != 1 || b.Configurations.Count(v => v.Role == "cdnConfig") != 1) throw Invalid();
        var expected = new[] { "Name_lang", "NameAlt_lang", "Title_lang", "TitleAlt_lang" }.Concat(FactNames).Concat(Enumerable.Repeat("DisplayID", 4)).Concat(Enumerable.Repeat("DisplayProbability", 4)).ToArray();
        for (var i = 0; i < 16; i++)
        {
            var field = b.Fields[i]; var column = field.ColumnIndex;
            var family = i < 4 ? "string" : i < 12 ? "integer" : "float";
            var expectedName = i < 8 ? expected[i] : expected[i] + "[" + (i < 12 ? i - 8 : i - 12) + "]";
            if (field.Name != expected[i] || field.Family != family || column < 0 || column >= s.Columns.Count ||
                s.Columns[column].Name != expectedName || s.Columns[column].Family != family || s.Columns[column].Interpretation is not null ||
                r.Header.Columns[column].Name != expectedName || r.Header.Columns[column].Kind != family || r.Header.Columns[column].Interpretation is not null ||
                field.Slot != (i < 8 ? 0 : i < 12 ? i - 8 : i - 12) ||
                field.Bits != (i is 4 or 5 or 7 ? 8 : i == 6 ? 16 : i >= 8 ? 32 : null) || field.Unsigned != (i is 5 or 6 ? true : i >= 4 && i < 12 ? false : null)) throw Unsupported();
        }
        if (b.Fields.Select(f => f.ColumnIndex).Distinct().Count() != 16) throw Unsupported();
        _ = Bytes(b); _ = Bytes(Normalize(b));
    }
    internal static CreatureFieldMap FieldMap(CreaturesBinding b)
    {
        CreatureFieldReference F(int i) => new(Table, b.Fields[i].ColumnIndex);
        return new(F(0), F(1), F(2), F(3), Enumerable.Range(4, 4).Select(F).ToArray(), Enumerable.Range(8, 4).Select(F).ToArray(), Enumerable.Range(12, 4).Select(F).ToArray());
    }
    internal static IReadOnlyList<CreatureProfileField> SelectFields(DbdDocument definition, string version, string layout, IReadOnlyList<TableDataColumn> columns)
    {
        var selected = definition.SelectBuild(version, layout); if (selected.Blocks.Count != 1) throw Unsupported();
        var fields = selected.Blocks[0].Fields; var bound = new List<(DbdField Field, int Index)>(); var ordinal = 0;
        foreach (var field in fields) { bound.Add((field, ordinal)); ordinal = checked(ordinal + field.ArrayLength); }
        if (ordinal != columns.Count) throw Unsupported();
        var output = new List<CreatureProfileField>();
        foreach (var name in new[] { "Name_lang", "NameAlt_lang", "Title_lang", "TitleAlt_lang" }.Concat(FactNames).Concat(new[] { "DisplayID", "DisplayProbability" }))
        {
            var matches = bound.Where(f => f.Field.Column.Name == name).ToArray(); if (matches.Length != 1) throw Unsupported();
            var (field, start) = matches[0]; var array = name is "DisplayID" or "DisplayProbability" ? 4 : 1;
            if (!field.Column.VerifiedName || field.ArrayLength != array) throw Unsupported();
            var family = field.Column.Family switch { "int" => "integer", "string" or "locstring" => "string", "float" => "float", _ => "unsupported" };
            for (var slot = 0; slot < array; slot++) output.Add(new(name, start + slot, family, family == "float" ? 32 : field.IntegerBits, field.Unsigned, slot));
        }
        return output;
    }
}
