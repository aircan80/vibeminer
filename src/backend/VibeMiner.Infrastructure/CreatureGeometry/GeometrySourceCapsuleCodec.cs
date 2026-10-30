using System.Buffers;
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

/// <summary>Bounded canonical proof encoding, never a shape-to-fresh-authority factory.</summary>
internal static class GeometrySourceCapsuleCodec
{
    internal const string Revision = "geometry-source-capsule-v1";
    internal const int MaximumSourceBytes = 128 << 10, MaximumPairBytes = 256 << 10, MaximumMemberships = 256;
    private static readonly JsonSerializerOptions Json = Options();
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, PropertyNameCaseInsensitive = false };
        options.Converters.Add(new UInt64Text()); return options;
    }
    internal static async Task<GeometryCapsuleEncoding> EncodeAsync(GeometrySourceCapsulePair value,
        IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(budget); token.ThrowIfCancellationRequested();
        var pair = await SnapshotAsync(value, budget, token);
        var buffer = new CappedBuffer(MaximumPairBytes);
        using var writer = new Utf8JsonWriter(buffer);
        using (await Work(budget, token))
        {
            writer.WriteStartObject(); writer.WriteString("revision", pair.Revision); writer.WriteString("interpretationPolicy", pair.InterpretationPolicy);
            writer.WriteString("variantPolicy", pair.VariantPolicy); writer.WriteString("encodingSelectionPolicy", pair.EncodingSelectionPolicy);
            writer.WritePropertyName("sourceContext"); JsonSerializer.Serialize(writer, pair.SourceContext, Json);
        }
        await Task.Yield();
        await WriteSourceAsync(writer, "model", pair.Model, budget, token);
        await WriteSourceAsync(writer, "skin", pair.Skin, budget, token);
        using (await Work(budget, token))
        {
            writer.WritePropertyName("resolvers"); writer.WriteStartArray();
            foreach (var resolver in pair.Resolvers) JsonSerializer.Serialize(writer, resolver, Json);
            writer.WriteEndArray(); writer.WriteNumber("mainSkinOrdinal", pair.MainSkinOrdinal); writer.WriteNumber("mainSkinFileDataId", pair.MainSkinFileDataId);
            writer.WritePropertyName("counts"); JsonSerializer.Serialize(writer, pair.Counts, Json); writer.WriteEndObject(); writer.Flush();
        }
        await Task.Yield(); token.ThrowIfCancellationRequested();
        if (buffer.Count > MaximumPairBytes) throw Limit();
        var bytes = await CopyAsync(buffer.Written, budget, token);
        var sha = await HashAsync("vibeminer-geometry-source-authority-v1\0", bytes, budget, token);
        return new(pair, bytes, sha);
    }
    internal static async Task<GeometryCapsuleEncoding> ParseAsync(ReadOnlyMemory<byte> input,
        IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(budget); token.ThrowIfCancellationRequested();
        if (input.Length is < 2 or > MaximumPairBytes) throw Limit();
        // Own the bounded input before interpretation. The catalog supplies its private row
        // snapshot; no caller buffer is retained by the parsed result.
        var owned = await CopyAsync(input, budget, token); var reader = new Cursor(owned);
        string revision, interpretation, variants, encoding; GeometrySourceContextIdentity context;
        using (await Work(budget, token))
        {
            reader.BeginObject(); revision = reader.String("revision"); interpretation = reader.String("interpretationPolicy");
            variants = reader.String("variantPolicy"); encoding = reader.String("encodingSelectionPolicy");
            context = reader.Object<GeometrySourceContextIdentity>("sourceContext");
        }
        await Task.Yield();
        var model = await ReadSourceAsync(reader, "model", budget, token);
        var skin = await ReadSourceAsync(reader, "skin", budget, token);
        using (await Work(budget, token)) { reader.Property("resolvers"); reader.BeginArray(); }
        await Task.Yield();
        var resolvers = new[] { await ReadLeafAsync<ResolverArtifact>(reader, null, budget, token), await ReadLeafAsync<ResolverArtifact>(reader, null, budget, token) };
        int ordinal; uint fileId;
        using (await Work(budget, token))
        { reader.EndArray(); ordinal = reader.Int32("mainSkinOrdinal"); fileId = reader.UInt32("mainSkinFileDataId"); }
        await Task.Yield();
        var counts = await ReadLeafAsync<GeometryNativeCounts>(reader, "counts", budget, token);
        using (await Work(budget, token)) { reader.EndObject(); reader.Eof(); }
        await Task.Yield();
        var result = await EncodeAsync(new(revision, interpretation, variants, encoding, context, model, skin, Array.AsReadOnly(resolvers), ordinal, fileId, counts), budget, token);
        // Canonical framing/order prevents duplicates, aliases or whitespace from being sealed differently.
        for (var at = 0; at < owned.Length; at += 32768)
        {
            var take = Math.Min(32768, owned.Length - at);
            using (await Work(budget, token))
                if (result.CanonicalUtf8.Length != owned.Length || !owned.AsSpan(at, take).SequenceEqual(result.CanonicalUtf8.Span.Slice(at, take))) throw Invalid();
            await Task.Yield();
        }
        return result;
    }
    internal static async Task<string> AssociationSha256Async(AssetSourceAssociation association, IGeometryArtifactWorkBudget budget, CancellationToken token)
        => await AssociationSha256Async(association, MaximumSourceBytes, budget, token);
    // Reserve8KiB for the complete source's bounded receipt/expectation/role/FDID
    // BEFORE physical demand. Exact complete-source128KiB admission still follows
    // canonical capture; this conservative envelope never truncates memberships.
    internal static async Task<string> SourceAssociationSha256Async(AssetSourceAssociation association, IGeometryArtifactWorkBudget budget, CancellationToken token)
        => await AssociationSha256Async(association, MaximumSourceBytes - 8192, budget, token);
    private static async Task<string> AssociationSha256Async(AssetSourceAssociation association, int maximum,
        IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        var snapshot = await SnapshotAssociationAsync(association, budget, token);
        var bytes = new CappedBuffer(maximum); using var writer = new Utf8JsonWriter(bytes);
        await WriteAssociationAsync(writer, snapshot, budget, token);
        using (await Work(budget, token)) writer.Flush();
        if (bytes.Count > maximum) throw Limit();
        return await HashAsync("vibeminer-geometry-association-v1\0", bytes.Written, budget, token);
    }
    internal static async Task<IReadOnlyList<CreatureGeometrySource>> ProjectSourcesAsync(GeometrySourceCapsulePair capsule,
        IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        var checkedPair = await SnapshotAsync(capsule, budget, token);
        var result = new CreatureGeometrySource[2]; var values = new[] { checkedPair.Model, checkedPair.Skin };
        for (var i = 0; i < values.Length; i++)
        {
            var source = values[i]; var sha = await AssociationSha256Async(source.Association, budget, token);
            result[i] = new(i == 0 ? CreatureGeometrySourceRole.Model : CreatureGeometrySourceRole.Skin, source.FileDataId.ToString(CultureInfo.InvariantCulture),
                source.Receipt.ContentKey, source.Receipt.EncodingKey, source.Receipt.Sha256, source.Expectation.EncodedBytes, source.Expectation.DecodedBytes,
                source.Receipt.VerifierRevision, sha, source.Association.VariantPolicy, source.Association.EncodingLookupRevision, source.Association.RootLookupRevision,
                source.Association.EncodingSelectionPolicy);
        }
        return Array.AsReadOnly(result);
    }
    private static async Task<GeometrySourceCapsulePair> SnapshotAsync(GeometrySourceCapsulePair pair, IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        if (pair is null || pair.SourceContext is null || pair.Counts is null || pair.Resolvers is null) throw Invalid();
        GeometrySourceContextIdentity context; ResolverArtifact[] resolvers; GeometryNativeCounts counts;
        using (await Work(budget, token))
        {
            if (pair.Revision != Revision || pair.InterpretationPolicy != M2GeometrySkin.InterpretationPolicy || pair.VariantPolicy != AssetResolutionSession.VariantPolicy ||
                pair.EncodingSelectionPolicy != AssetResolutionSession.EncodingSelectionPolicy || pair.MainSkinOrdinal != 0 || pair.MainSkinFileDataId == 0) throw Invalid();
            var c = pair.SourceContext;
            if (c.Request is null || c.Opening is null || c.ResearchLocale != "enUS") throw Invalid();
            WorkspaceService.Validate(c.Request); Hash(c.BuildConfigurationKey, 32); Hash(c.CdnConfigurationKey, 32); Hash(c.BuildConfigurationSha256, 64); Hash(c.CdnConfigurationSha256, 64);
            if (c.Opening.Region != c.Request.SourceRegion || c.Opening.VersionsRowOrdinal < 0 || c.Opening.CdnsRowOrdinal < 0) throw Invalid();
            Number(c.Opening.Generation, 1, long.MaxValue); Hash(c.Opening.VersionsSha256, 64); Hash(c.Opening.CdnsSha256, 64);
            context = c with { Request = c.Request with { }, Opening = c.Opening with { } };
            counts = pair.Counts with { };
            if (counts.Vertices is < 0 or > 65536 || counts.VertexLookup is < 0 or > 65536 || counts.GlobalTriangleIndices is < 0 or > 131070 ||
                counts.SectionTriangleIndices is < 0 or > 131070 || counts.Sections is < 0 or > 256 || counts.GlobalTriangleIndices % 3 != 0 || counts.SectionTriangleIndices % 3 != 0 ||
                counts.SectionTriangleIndices > 0 && (counts.Vertices < 1 || counts.VertexLookup < 1 || counts.Sections < 1)) throw Invalid();
            var count = pair.Resolvers.Count; if (count != 2) throw Invalid();
            resolvers = new ResolverArtifact[2];
            for (var i = 0; i < 2; i++) { var r = pair.Resolvers[i]; if (r is null) throw Invalid(); resolvers[i] = r with { Source = Metadata(r.Source) }; }
        }
        await Task.Yield();
        var model = await SnapshotSourceAsync(pair.Model, "model", context, budget, token);
        var skin = await SnapshotSourceAsync(pair.Skin, "skin", context, budget, token);
        using (await Work(budget, token))
        {
            if (skin.FileDataId != pair.MainSkinFileDataId || !SameMetadata(model.Association.EncodingSource, skin.Association.EncodingSource) || !SameMetadata(model.Association.RootSource, skin.Association.RootSource)) throw Invalid();
            for (var i = 0; i < 2; i++)
            {
                var r = resolvers[i]; var metadata = i == 0 ? model.Association.EncodingSource : model.Association.RootSource;
                if (r.ParserRevision != (i == 0 ? ResolverArtifacts.EncodingRevision : AssetResolutionSessions.RootArtifactRevision) || r.DecodedBytes is < 1 or > VerifiedObjectStore.MaxObjectBytes ||
                    r.DecodedBytes.ToString(CultureInfo.InvariantCulture) != metadata.DecodedBytes || !SameMetadata(r.Source, metadata)) throw Invalid();
                Hash(r.DecodedSha256, 64);
            }
        }
        await Task.Yield();
        return pair with { SourceContext = context, Model = model, Skin = skin, Counts = counts, Resolvers = Array.AsReadOnly(resolvers) };
    }
    private static async Task<GeometrySourceCapsule> SnapshotSourceAsync(GeometrySourceCapsule value, string role, GeometrySourceContextIdentity context,
        IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        if (value is null || value.Receipt is null || value.Expectation is null || value.Role != role || value.FileDataId == 0) throw Invalid();
        ContentReceipt receipt; MetadataExpectation expectation;
        using (await Work(budget, token))
        {
            expectation = value.Expectation with { }; receipt = Receipt(value.Receipt);
            if (expectation.Role != "asset" || expectation.EncodedBytes is < 9 or > (4 << 20) || expectation.DecodedBytes is < 1 or > (8 << 20) ||
                receipt.ContentKey != expectation.ContentKey || receipt.EncodingKey != expectation.EncodingKey || receipt.EncodedBytes != expectation.EncodedBytes.ToString(CultureInfo.InvariantCulture) ||
                receipt.DecodedBytes != expectation.DecodedBytes.ToString(CultureInfo.InvariantCulture)) throw Invalid();
        }
        var association = await SnapshotAssociationAsync(value.Association, budget, token);
        using (await Work(budget, token))
        {
            if (association.Request != context.Request || association.SourceReference != context.Opening || association.ResearchLocale != context.ResearchLocale ||
                association.Configurations[0].Key != context.BuildConfigurationKey || association.Configurations[0].Sha256 != context.BuildConfigurationSha256 ||
                association.Configurations[1].Key != context.CdnConfigurationKey || association.Configurations[1].Sha256 != context.CdnConfigurationSha256) throw Invalid();
            foreach (var match in association.Memberships)
                if (match.Entry.FileDataId != value.FileDataId || match.Entry.ContentKey != expectation.ContentKey) throw Invalid();
        }
        await Task.Yield();
        return value with { Expectation = expectation, Receipt = receipt, Association = association };
    }
    /// <summary>A texture association may also name the D095 standard-resolution policy; model and SKIN keep the exact one.</summary>
    internal static async Task<AssetSourceAssociation> SnapshotAssociationAsync(AssetSourceAssociation value, IGeometryArtifactWorkBudget budget, CancellationToken token,
        bool texture = false)
    {
        if (value is null || value.Request is null || value.SourceReference is null || value.Configurations is null || value.Memberships is null) throw Invalid();
        RootTargetMatch[] members; ConfigurationReceipt[] configurations; MetadataReceipt encoding, root;
        using (await Work(budget, token))
        {
            WorkspaceService.Validate(value.Request);
            if (value.ResearchLocale != "enUS" || !(value.VariantPolicy == AssetResolutionSession.VariantPolicy || texture && value.VariantPolicy == RootTextureVariants.Policy) ||
                value.EncodingSelectionPolicy != AssetResolutionSession.EncodingSelectionPolicy ||
                value.EncodingLookupRevision != ResolverArtifacts.EncodingRevision || value.RootLookupRevision != AssetResolutionSessions.RootArtifactRevision) throw Invalid();
            var count = value.Memberships.Count; if (count < 1) throw Limit();
            if (count > MaximumMemberships) throw new DiscoveryException("CREATURE_GEOMETRY_RESOURCE_LIMIT", string.Create(CultureInfo.InvariantCulture,
                $"The file has {count:N0} ROOT memberships; geometry reads at most {MaximumMemberships:N0}."));
            members = new RootTargetMatch[count];
            if (value.Configurations.Count != 2) throw Invalid();
            var first = value.Configurations[0]; var second = value.Configurations[1];
            if (first is null || second is null || first.Role == second.Role) throw Invalid();
            configurations = [Configuration(first.Role == "buildConfig" ? first : second, "buildConfig"), Configuration(first.Role == "cdnConfig" ? first : second, "cdnConfig")];
            encoding = Metadata(value.EncodingSource); root = Metadata(value.RootSource);
            if (encoding.Role != "encoding" || root.Role != "root") throw Invalid();
        }
        for (var at = 0; at < members.Length; at += 16)
        {
            using (await Work(budget, token))
                for (var i = at; i < Math.Min(at + 16, members.Length); i++)
                {
                    RootTargetMatch m;
                    try { m = value.Memberships[i]; } catch (ArgumentOutOfRangeException) { throw Invalid(); } catch (IndexOutOfRangeException) { throw Invalid(); }
                    if (m is null || m.Entry is null || m.Entry.RawContentFlags is null || m.BlockOrdinal < 0 || m.EntryOrdinal < 0 || m.Entry.FileDataId == 0) throw Invalid();
                    Hash(m.Entry.ContentKey, 32); members[i] = m with { Entry = m.Entry with { RawContentFlags = m.Entry.RawContentFlags with { } } };
                }
            await Task.Yield();
        }
        return value with { Request = value.Request with { }, SourceReference = value.SourceReference with { }, Configurations = Array.AsReadOnly(configurations), EncodingSource = encoding,
            RootSource = root, Memberships = Array.AsReadOnly(members) };
    }
    private static async Task WriteSourceAsync(Utf8JsonWriter writer, string name, GeometrySourceCapsule source, IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        long start;
        using (await Work(budget, token))
        {
            writer.WritePropertyName(name); writer.WriteStartObject(); start = writer.BytesCommitted + writer.BytesPending;
            writer.WriteString("role", source.Role); writer.WriteNumber("fileDataId", source.FileDataId);
            writer.WritePropertyName("expectation"); JsonSerializer.Serialize(writer, source.Expectation, Json);
            writer.WritePropertyName("receipt"); JsonSerializer.Serialize(writer, source.Receipt, Json);
            writer.WritePropertyName("association");
        }
        await WriteAssociationAsync(writer, source.Association, budget, token);
        using (await Work(budget, token))
        { writer.WriteEndObject(); if (writer.BytesCommitted + writer.BytesPending - start + 1 > MaximumSourceBytes) throw Limit(); }
        await Task.Yield();
    }
    private static async Task WriteAssociationAsync(Utf8JsonWriter writer, AssetSourceAssociation value, IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        using (await Work(budget, token))
        {
            writer.WriteStartObject(); writer.WritePropertyName("request"); JsonSerializer.Serialize(writer, value.Request, Json);
            writer.WriteString("researchLocale", value.ResearchLocale); writer.WritePropertyName("sourceReference"); JsonSerializer.Serialize(writer, value.SourceReference, Json);
            writer.WritePropertyName("configurations"); writer.WriteStartArray();
        }
        await Task.Yield();
        foreach (var configuration in value.Configurations)
        { using (await Work(budget, token)) JsonSerializer.Serialize(writer, configuration, Json); await Task.Yield(); }
        using (await Work(budget, token))
        { writer.WriteEndArray(); writer.WritePropertyName("encodingSource"); JsonSerializer.Serialize(writer, value.EncodingSource, Json); }
        await Task.Yield();
        using (await Work(budget, token))
        {
            writer.WritePropertyName("rootSource"); JsonSerializer.Serialize(writer, value.RootSource, Json);
            writer.WriteString("variantPolicy", value.VariantPolicy); writer.WritePropertyName("memberships"); writer.WriteStartArray();
        }
        await Task.Yield();
        for (var at = 0; at < value.Memberships.Count; at += 16)
        {
            using (await Work(budget, token))
                for (var i = at; i < Math.Min(at + 16, value.Memberships.Count); i++) JsonSerializer.Serialize(writer, value.Memberships[i], Json);
            await Task.Yield();
        }
        using (await Work(budget, token))
        {
            writer.WriteEndArray(); writer.WriteString("encodingLookupRevision", value.EncodingLookupRevision); writer.WriteString("rootLookupRevision", value.RootLookupRevision);
            writer.WriteString("encodingSelectionPolicy", value.EncodingSelectionPolicy); writer.WriteEndObject();
        }
        await Task.Yield();
    }
    private static async Task<T> ReadLeafAsync<T>(Cursor reader, string? name, IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        T value;
        // One <=8KiB leaf per scope: scanner+deserializer+text/object writes have a
        // conservative64KiB allowance. Never group16 unvalidated leaves into a slice.
        using (await Work(budget, token)) value = reader.Object<T>(name);
        await Task.Yield(); return value;
    }
    private static async Task<GeometrySourceCapsule> ReadSourceAsync(Cursor reader, string role, IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        var start = reader.Offset; string parsedRole; uint id;
        using (await Work(budget, token))
        { reader.Property(role); reader.BeginObject(); parsedRole = reader.String("role"); id = reader.UInt32("fileDataId"); }
        await Task.Yield();
        var expected = await ReadLeafAsync<MetadataExpectation>(reader, "expectation", budget, token);
        var receipt = await ReadLeafAsync<ContentReceipt>(reader, "receipt", budget, token);
        using (await Work(budget, token)) { reader.Property("association"); reader.BeginObject(); }
        await Task.Yield();
        var request = await ReadLeafAsync<WorkspaceRequest>(reader, "request", budget, token);
        string locale;
        using (await Work(budget, token)) locale = reader.String("researchLocale");
        await Task.Yield();
        var reference = await ReadLeafAsync<OpeningSourceReference>(reader, "sourceReference", budget, token);
        using (await Work(budget, token)) { reader.Property("configurations"); reader.BeginArray(); }
        await Task.Yield();
        var configurations = new[] { await ReadLeafAsync<ConfigurationReceipt>(reader, null, budget, token), await ReadLeafAsync<ConfigurationReceipt>(reader, null, budget, token) };
        using (await Work(budget, token)) reader.EndArray();
        await Task.Yield();
        var encoding = await ReadLeafAsync<MetadataReceipt>(reader, "encodingSource", budget, token);
        var root = await ReadLeafAsync<MetadataReceipt>(reader, "rootSource", budget, token);
        string variants;
        using (await Work(budget, token))
        { variants = reader.String("variantPolicy"); reader.Property("memberships"); reader.BeginArray(); }
        await Task.Yield(); var members = new List<RootTargetMatch>(MaximumMemberships);
        while (!reader.IsEndArray)
        {
            if (members.Count == MaximumMemberships) throw Limit();
            members.Add(await ReadLeafAsync<RootTargetMatch>(reader, null, budget, token));
        }
        string encodingRevision, rootRevision, selection;
        using (await Work(budget, token))
        {
            reader.EndArray(); encodingRevision = reader.String("encodingLookupRevision"); rootRevision = reader.String("rootLookupRevision"); selection = reader.String("encodingSelectionPolicy");
            reader.EndObject(); reader.EndObject(); if (reader.Offset - start > MaximumSourceBytes + 16) throw Limit();
        }
        await Task.Yield();
        return new(parsedRole, id, expected, receipt, new(request, locale, reference, Array.AsReadOnly(configurations), encoding, root, variants, Array.AsReadOnly(members.ToArray()), encodingRevision, rootRevision, selection));
    }
    private static ConfigurationReceipt Configuration(ConfigurationReceipt c, string role)
    {
        if (c.Role != role || c.ByteLength is < 1 or > (1 << 20) || c.ParsedFieldCount < 1) throw Invalid();
        Hash(c.Key, 32); Hash(c.Sha256, 64); Text(c.SourceUrl, 2048); Text(c.RetrievedAt, 64); Text(c.VerifiedAt, 64); return c with { };
    }
    private static MetadataReceipt Metadata(MetadataReceipt r)
    {
        if (r is null) throw Invalid(); Hash(r.ContentKey, 32); Hash(r.EncodingKey, 32); Hash(r.Sha256, 64);
        Number(r.EncodedBytes, 9, VerifiedObjectStore.MaxObjectBytes); Number(r.DecodedBytes, 1, VerifiedObjectStore.MaxObjectBytes);
        Text(r.SourceUrl, 2048); Text(r.RetrievedAt, 64); Text(r.VerifiedAt, 64); Text(r.ParserRevision, 128); Text(r.Role, 64);
        if (r.Location is not null) Location(r.Location);
        return r with { Location = r.Location is null ? null : r.Location with { } };
    }
    private static ContentReceipt Receipt(ContentReceipt r)
    {
        Hash(r.ContentKey, 32); Hash(r.EncodingKey, 32); Hash(r.Sha256, 64);
        Number(r.EncodedBytes, 9, 4 << 20); Number(r.DecodedBytes, 1, 8 << 20);
        if (r.Projection is not null || r.VerifierRevision != SelectedFileAcquirer.VerifierRevision || r.Location is null) throw Invalid();
        Text(r.SourceUrl, 2048); Text(r.RetrievedAt, 64); Text(r.VerifiedAt, 64); Location(r.Location);
        return r with { Location = r.Location with { } };
    }
    private static void Location(ContentLocation l)
    {
        if (l.Kind is not ("loose" or "archive")) throw Invalid(); Number(l.Offset, 0, long.MaxValue); Number(l.SourceBytes, 9, long.MaxValue);
        if (l.ArchiveKey is not null) Hash(l.ArchiveKey, 32);
    }
    private static bool SameMetadata(MetadataReceipt a, MetadataReceipt b) => MetadataReceiptIdentity.Same(a, b);
    internal static void Hash(string? value, int length)
    { if (value is null || value.Length != length || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || value.All(c => c == '0')) throw Invalid(); }
    private static void Text(string? value, int max) { if (value is null || value.Length is < 1 || value.Length > max) throw Invalid(); }
    private static void Number(string? value, long min, long max)
    { if (value is null || value.Length > 19 || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min || n > max || n.ToString(CultureInfo.InvariantCulture) != value) throw Invalid(); }
    private static async Task<byte[]> CopyAsync(ReadOnlyMemory<byte> input, IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        var result = new byte[input.Length];
        for (var at = 0; at < result.Length; at += 32768)
        {
            // Charge source reads AND destination writes, not just emitted bytes.
            using (await Work(budget, token)) input.Span.Slice(at, Math.Min(32768, result.Length - at)).CopyTo(result.AsSpan(at));
            await Task.Yield();
        }
        return result;
    }
    private static async Task<string> HashAsync(string separator, ReadOnlyMemory<byte> bytes, IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (await Work(budget, token))
        { hash.AppendData(Encoding.UTF8.GetBytes(separator)); var length = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)bytes.Length); hash.AppendData(length); }
        await Task.Yield();
        for (var at = 0; at < bytes.Length; at += 65536)
        { using (await Work(budget, token)) hash.AppendData(bytes.Span.Slice(at, Math.Min(65536, bytes.Length - at))); await Task.Yield(); }
        using (await Work(budget, token)) return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    private static async ValueTask<IDisposable> Work(IGeometryArtifactWorkBudget budget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var scope = await budget.EnterAsync(token);
        try { token.ThrowIfCancellationRequested(); return scope ?? throw Invalid(); } catch { scope?.Dispose(); throw; }
    }
    internal static DiscoveryException Invalid() => new("CREATURE_GEOMETRY_SOURCE_INVALID", "The geometry source proof differs from its bounded exact original identities.");
    internal static DiscoveryException Limit() => new("CREATURE_GEOMETRY_RESOURCE_LIMIT", "The complete geometry source proof exceeds its bounded allowance.");
    private sealed class UInt64Text : JsonConverter<ulong>
    {
        public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException(); var s = reader.GetString();
            if (!ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n.ToString(CultureInfo.InvariantCulture) != s) throw new JsonException(); return n;
        }
        public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
    private sealed class CappedBuffer(int maximum) : IBufferWriter<byte>
    {
        private readonly byte[] buffer = new byte[maximum + 16384];
        internal int Count { get; private set; }
        public void Advance(int count) { if (count < 0 || count > buffer.Length - Count) throw Limit(); Count += count; if (Count > maximum) throw Limit(); }
        public Memory<byte> GetMemory(int sizeHint = 0) { if (sizeHint < 0 || sizeHint > buffer.Length - Count) throw Limit(); return buffer.AsMemory(Count); }
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
        internal ReadOnlyMemory<byte> Written => buffer.AsMemory(0, Count);
    }
    // Fixed outer grammar plus small bounded leaf objects. No whole-pair JSON graph parse.
    private sealed class Cursor(byte[] bytes)
    {
        private JsonReaderState state = new(new JsonReaderOptions { MaxDepth = 16 });
        internal int Offset { get; private set; }
        // No single tokenizer call can scan an attacker-sized scalar/whitespace suffix.
        private Utf8JsonReader Reader() => new(bytes.AsSpan(Offset, Math.Min(8193, bytes.Length - Offset)), true, state);
        private void Save(ref Utf8JsonReader r) { Offset += checked((int)r.BytesConsumed); state = r.CurrentState; }
        private void Token(JsonTokenType expected)
        { try { var r = Reader(); if (!r.Read() || r.TokenType != expected) throw Invalid(); Save(ref r); } catch (JsonException) { throw Invalid(); } }
        internal void BeginObject() => Token(JsonTokenType.StartObject);
        internal void EndObject() => Token(JsonTokenType.EndObject);
        internal void BeginArray() => Token(JsonTokenType.StartArray);
        internal void EndArray() => Token(JsonTokenType.EndArray);
        internal bool IsEndArray
        {
            get
            {
                if (Offset >= bytes.Length) throw Invalid();
                var next = bytes[Offset];
                if (next != (byte)']' && next != (byte)',' && next != (byte)'{') throw Invalid();
                return next == (byte)']';
            }
        }
        internal void Eof() { try { var r = Reader(); if (r.Read()) throw Invalid(); } catch (JsonException) { throw Invalid(); } }
        internal void Property(string name)
        { try { var r = Reader(); if (!r.Read() || r.TokenType != JsonTokenType.PropertyName || !r.ValueTextEquals(name)) throw Invalid(); Save(ref r); } catch (JsonException) { throw Invalid(); } }
        internal string String(string name)
        { Property(name); try { var r = Reader(); if (!r.Read() || r.TokenType != JsonTokenType.String || r.ValueSpan.Length > 256) throw Invalid(); var s = r.GetString()!; Save(ref r); return s; } catch (JsonException) { throw Invalid(); } }
        internal int Int32(string name) { Property(name); try { var r = Reader(); if (!r.Read() || r.TokenType != JsonTokenType.Number || !r.TryGetInt32(out var n)) throw Invalid(); Save(ref r); return n; } catch (JsonException) { throw Invalid(); } }
        internal uint UInt32(string name) { Property(name); try { var r = Reader(); if (!r.Read() || r.TokenType != JsonTokenType.Number || !r.TryGetUInt32(out var n)) throw Invalid(); Save(ref r); return n; } catch (JsonException) { throw Invalid(); } }
        internal T Object<T>(string? name = null)
        {
            if (name is not null) Property(name);
            try
            {
                var r = Reader(); if (!r.Read() || r.TokenType != JsonTokenType.StartObject) throw Invalid();
                var tokenStart = checked((int)r.TokenStartIndex);
                var stack = new Stack<HashSet<string>>(); stack.Push(new(StringComparer.Ordinal)); var depth = r.CurrentDepth;
                while (r.Read())
                {
                    if (r.BytesConsumed > 8192 || r.TokenType == JsonTokenType.StartArray) throw Limit();
                    if (r.TokenType == JsonTokenType.StartObject) stack.Push(new(StringComparer.Ordinal));
                    else if (r.TokenType == JsonTokenType.PropertyName)
                    { if (r.ValueSpan.Length > 128 || stack.Peek().Count == 32 || !stack.Peek().Add(r.GetString()!)) throw Invalid(); }
                    else if (r.TokenType == JsonTokenType.String && r.ValueSpan.Length > 4096) throw Limit();
                    else if (r.TokenType == JsonTokenType.EndObject) { stack.Pop(); if (r.CurrentDepth == depth) break; }
                }
                if (r.TokenType != JsonTokenType.EndObject || r.BytesConsumed > 8192) throw Invalid();
                var value = JsonSerializer.Deserialize<T>(bytes.AsSpan(Offset + tokenStart, (int)r.BytesConsumed - tokenStart), Json); if (value is null) throw Invalid();
                Save(ref r); return value;
            }
            catch (JsonException) { throw Invalid(); }
        }
    }
}
