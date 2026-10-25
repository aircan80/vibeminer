using System.Globalization;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    private const int GeometrySourceBytes = 128 << 10, GeometryPairBytes = 256 << 10, GeometryDescriptorBytes = 64 << 10;
    private static readonly JsonSerializerOptions GeometryJson = new(JsonSerializerDefaults.Web)
    { MaxDepth = 32, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) } };
    private static readonly ConcurrentDictionary<Type, IReadOnlyDictionary<string, Type>> GeometryProperties = new();
    private static DiscoveryException GeometryInvalid() => new("CREATURE_GEOMETRY_SOURCE_INVALID", "The geometry source has invalid original authority.");
    private static DiscoveryException GeometryStale() => new("CREATURE_GEOMETRY_STALE", "The selected geometry source changed. Retry the current selection.", true);
    private static DiscoveryException GeometryLimit() => new("CREATURE_GEOMETRY_RESOURCE_LIMIT", "The geometry proof exceeds its bounded metadata allowance.");
    private static DiscoveryException GeometryCorrupt() => new("CREATURE_GEOMETRY_CORRUPT", "A geometry source or artifact has known corruption.");

    internal Task<GeometryCanonicalSources> ReadGeometryOriginalSourcesAsync(AssetSourceAssociation association,
        MetadataExpectation expectation, ContentReceipt verifiedCandidate, CancellationToken token, bool texture = false)
    {
        token.ThrowIfCancellationRequested();
        association = GeometryCopyAssociation(association);
        GeometryAssociationShape(association, expectation, verifiedCandidate, texture);
        // Bound serialization before copying; caller lists cannot change the queued source identity.
        var frozenBytes = GeometryBytes(new GeometrySourceCapsule("source",
            association.Memberships[0].Entry.FileDataId, expectation, verifiedCandidate, association), GeometrySourceBytes);
        var frozen = GeometryDecode<GeometrySourceCapsule>(frozenBytes);
        GeometryAssociationShape(frozen.Association, frozen.Expectation, frozen.Receipt, texture);
        return Schedule(() =>
        {
            using var transaction = Db.BeginTransaction(); var budget = GeometrySourceBytes - frozenBytes.Length;
            var result = GeometryOriginalSources(frozen.Association, frozen.Expectation, frozen.Receipt, transaction, ref budget, insert: true, texture);
            token.ThrowIfCancellationRequested(); transaction.Commit(); return result;
        }, token);
    }

    private static AssetSourceAssociation GeometryCopyAssociation(AssetSourceAssociation source)
    {
        if (source?.Configurations is null || source.Memberships is null) throw GeometryInvalid();
        var configurationCount = source.Configurations.Count; var membershipCount = source.Memberships.Count;
        if (configurationCount != 2 || membershipCount < 1) throw GeometryInvalid();
        if (membershipCount > 256) throw GeometryLimit();
        var configurations = new ConfigurationReceipt[2]; var memberships = new RootTargetMatch[membershipCount];
        try
        {
            for (var i = 0; i < 2; i++) configurations[i] = source.Configurations[i] is { } receipt ? receipt with { } : throw GeometryInvalid();
            for (var i = 0; i < membershipCount; i++)
            {
                var member = source.Memberships[i];
                if (member?.Entry?.RawContentFlags is null) throw GeometryInvalid();
                memberships[i] = member with { Entry = member.Entry with { RawContentFlags = member.Entry.RawContentFlags with { } } };
            }
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or IndexOutOfRangeException) { throw GeometryInvalid(); }
        return source with { Configurations = Array.AsReadOnly(configurations), Memberships = Array.AsReadOnly(memberships) };
    }

    private GeometryCanonicalSources GeometryOriginalSources(AssetSourceAssociation association, MetadataExpectation expected,
        ContentReceipt candidate, SqliteTransaction transaction, ref int budget, bool insert, bool texture = false)
    {
        GeometryAssociationShape(association, expected, candidate, texture);
        GeometryAssociationOpening(association, transaction);
        foreach (var configuration in association.Configurations)
        {
            using var command = Command("""
                SELECT sha256,byte_length,source_url,retrieved_at,verifier_revision,
                    length(CAST(sha256 AS BLOB)),length(CAST(source_url AS BLOB)),
                    length(CAST(retrieved_at AS BLOB)),length(CAST(verifier_revision AS BLOB))
                FROM configuration_objects WHERE key=$k
                """, transaction, ("$k", configuration.Key));
            using var reader = command.ExecuteReader(); if (!reader.Read()) throw GeometryStale();
            GeometrySizes(reader, [5, 6, 7, 8], ref budget);
            if (reader.GetString(0) != configuration.Sha256 || reader.GetInt32(1) != configuration.ByteLength ||
                reader.GetString(2) != configuration.SourceUrl || reader.GetString(3) != configuration.RetrievedAt ||
                reader.GetString(4) != ConfigurationWorkspacePreparer.VerifierRevision || !DownloadUtc(configuration.VerifiedAt) ||
                configuration.ParsedFieldCount is < 1 or > 4096) throw GeometryStale();
        }
        var encoding = GeometryMetadata(association.EncodingSource, transaction, ref budget);
        var root = GeometryMetadata(association.RootSource, transaction, ref budget);
        var encodingLookup = GeometryResolver(encoding, ResolverArtifacts.EncodingRevision, transaction, ref budget);
        var rootLookup = GeometryResolver(root, AssetResolutionSessions.RootArtifactRevision, transaction, ref budget);
        var original = GeometryContent(candidate.EncodingKey, transaction, ref budget);
        if (original is null)
        {
            if (!insert) throw GeometryStale();
            Execute("INSERT INTO content_objects VALUES($e,$c,$h,$n,$d,$r)", transaction,
                ("$e", candidate.EncodingKey), ("$c", candidate.ContentKey), ("$h", candidate.Sha256),
                ("$n", expected.EncodedBytes), ("$d", expected.DecodedBytes), ("$r", Encoding.UTF8.GetString(GeometryBytes(candidate, GeometrySourceBytes))));
            LogStoredChange(candidate.EncodingKey, transaction);
            original = candidate;
        }
        else if (!SameContentBytes(original, candidate) || !insert && !CreatureAuthority(original, candidate)) throw GeometryStale();
        return new(original, encodingLookup, rootLookup);
    }

    private void GeometryAssociationOpening(AssetSourceAssociation source, SqliteTransaction transaction)
    {
        var reference = source.SourceReference;
        using var command = Command("""
            SELECT r.generation,r.ordinal,r.cdns_ordinal,v.sha256,c.sha256,
                length(CAST(v.sha256 AS BLOB)),length(CAST(c.sha256 AS BLOB))
            FROM build_regions r JOIN builds b ON b.id=r.build_id
            JOIN documents v ON v.generation=r.generation AND v.role='versions'
            JOIN documents c ON c.generation=r.generation AND c.role='cdns'
            WHERE r.build_id=$b AND r.region=$r AND b.product=$p
            """, transaction, ("$b", source.Request.BuildIdentity), ("$r", source.Request.SourceRegion), ("$p", source.Request.ProductId));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(2) || reader.GetInt64(5) != 64 || reader.GetInt64(6) != 64 ||
            reader.GetInt64(0).ToString(CultureInfo.InvariantCulture) != reference.Generation || reader.GetInt32(1) != reference.VersionsRowOrdinal ||
            reader.GetInt32(2) != reference.CdnsRowOrdinal || reader.GetString(3) != reference.VersionsSha256 || reader.GetString(4) != reference.CdnsSha256) throw GeometryStale();
        reader.Close();
        var admission = Scalar("SELECT id FROM workspace_admissions WHERE build_id=$b AND product=$p AND region=$r AND source_reference=$ref ORDER BY id DESC LIMIT 1", transaction,
            ("$b", source.Request.BuildIdentity), ("$p", source.Request.ProductId), ("$r", source.Request.SourceRegion), ("$ref", Serialize(reference)));
        if (admission is null) throw GeometryStale();
        foreach (var configuration in source.Configurations)
            if (Scalar("SELECT 1 FROM workspace_configurations WHERE admission_id=$a AND role=$role AND key=$key AND sha256=$sha AND byte_length=$n AND source_url=$url AND retrieved_at=$at AND parsed_field_count=$f", transaction,
                ("$a", admission), ("$role", configuration.Role), ("$key", configuration.Key), ("$sha", configuration.Sha256),
                ("$n", configuration.ByteLength), ("$url", configuration.SourceUrl), ("$at", configuration.RetrievedAt), ("$f", configuration.ParsedFieldCount)) is null) throw GeometryStale();
    }

    private MetadataReceipt GeometryMetadata(MetadataReceipt expected, SqliteTransaction transaction, ref int budget)
    {
        using var command = Command("SELECT receipt,length(CAST(receipt AS BLOB)),ckey,encoded,length(CAST(ckey AS BLOB)) FROM metadata_objects WHERE ekey=$k", transaction, ("$k", expected.EncodingKey));
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw GeometryStale(); GeometrySizes(reader, [1, 4], ref budget);
        if (reader.GetInt64(4) != 32) throw GeometryInvalid();
        var original = GeometryDecode<MetadataReceipt>(Encoding.UTF8.GetBytes(reader.GetString(0)));
        ValidateDownloadReceiptFacts(original);
        if (original.ContentKey != reader.GetString(2) || DownloadNumber(original.EncodedBytes) != reader.GetInt64(3) || !MetadataReceiptIdentity.Same(original, expected)) throw GeometryStale(); return original;
    }

    private ResolverArtifact GeometryResolver(MetadataReceipt source, string revision, SqliteTransaction transaction, ref int budget)
    {
        using var command = Command("""
            SELECT receipt,decoded_sha256,decoded_bytes,length(CAST(receipt AS BLOB)),length(CAST(decoded_sha256 AS BLOB))
            FROM resolver_artifacts WHERE source_sha256=$s AND role=$role AND parser_revision=$r
            """, transaction, ("$s", source.Sha256), ("$role", source.Role), ("$r", revision));
        using var reader = command.ExecuteReader(); if (!reader.Read()) throw GeometryStale(); GeometrySizes(reader, [3, 4], ref budget);
        if (reader.GetInt64(4) != 64) throw GeometryInvalid();
        var result = new ResolverArtifact(GeometryDecode<MetadataReceipt>(Encoding.UTF8.GetBytes(reader.GetString(0))), revision, reader.GetString(1), reader.GetInt64(2));
        ValidateResolverArtifact(result);
        if (!MetadataReceiptIdentity.Same(result.Source, source)) throw GeometryStale(); return result;
    }

    private ContentReceipt? GeometryContent(string key, SqliteTransaction transaction, ref int budget)
    {
        using var command = Command("""
            SELECT receipt,ckey,sha256,encoded_bytes,decoded_bytes,length(CAST(receipt AS BLOB)),
                length(CAST(ckey AS BLOB)),length(CAST(sha256 AS BLOB)) FROM content_objects WHERE ekey=$k
            """, transaction, ("$k", key));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null; GeometrySizes(reader, [5, 6, 7], ref budget);
        if (reader.GetInt64(6) != 32 || reader.GetInt64(7) != 64) throw GeometryInvalid();
        var receipt = GeometryDecode<ContentReceipt>(Encoding.UTF8.GetBytes(reader.GetString(0)));
        if (receipt.Projection is not null || receipt.VerifierRevision != "content-blte-v1") throw GeometryInvalid();
        ValidateContentReceipt(receipt);
        if (receipt.EncodingKey != key || receipt.ContentKey != reader.GetString(1) || receipt.Sha256 != reader.GetString(2) ||
            DownloadNumber(receipt.EncodedBytes) != reader.GetInt64(3) || DownloadNumber(receipt.DecodedBytes) != reader.GetInt64(4)) throw GeometryInvalid();
        return receipt;
    }

    // Texture sources (#63) allow 16 MiB encoded and decoded; model and SKIN keep 4 MiB / 8 MiB.
    private static void GeometryAssociationShape(AssetSourceAssociation source, MetadataExpectation expected, ContentReceipt receipt, bool texture = false)
    {
        if (source?.Request is null || source.SourceReference is null || source.Configurations is null || source.Configurations.Count != 2 ||
            source.Configurations.Any(c => c is null) || source.Configurations.Count(c => c.Role == "buildConfig") != 1 ||
            source.Configurations.Count(c => c.Role == "cdnConfig") != 1 || source.EncodingSource is null || source.RootSource is null ||
            source.Memberships is null || source.Memberships.Count is < 1 or > 256 || source.Memberships.Any(m => m?.Entry is null) ||
            expected is null || receipt is null || receipt.Location is null || receipt.Projection is not null || receipt.VerifierRevision != "content-blte-v1" ||
            source.ResearchLocale != "enUS" || source.SourceReference.Region != source.Request.SourceRegion ||
            !(source.VariantPolicy == AssetResolutionSession.VariantPolicy || texture && source.VariantPolicy == RootTextureVariants.Policy) || source.EncodingLookupRevision != ResolverArtifacts.EncodingRevision ||
            source.RootLookupRevision != AssetResolutionSessions.RootArtifactRevision || source.EncodingSelectionPolicy != AssetResolutionSession.EncodingSelectionPolicy ||
            expected.Role != "asset" || expected.EncodedBytes < 9 || expected.EncodedBytes > (texture ? 16 << 20 : 4 << 20) ||
            expected.DecodedBytes < 1 || expected.DecodedBytes > (texture ? 16 << 20 : 8 << 20) ||
            receipt.ContentKey != expected.ContentKey || receipt.EncodingKey != expected.EncodingKey || DownloadNumber(receipt.EncodedBytes) != expected.EncodedBytes ||
            DownloadNumber(receipt.DecodedBytes) != expected.DecodedBytes || source.Memberships.Any(m => m.BlockOrdinal < 0 || m.EntryOrdinal < 0 ||
                m.Entry.FileDataId == 0 || m.Entry.FileDataId != source.Memberships[0].Entry.FileDataId || m.Entry.ContentKey != expected.ContentKey)) throw GeometryInvalid();
        ValidateContentReceipt(receipt); ValidateDownloadReceiptFacts(source.EncodingSource); ValidateDownloadReceiptFacts(source.RootSource);
        foreach (var configuration in source.Configurations)
            if (!DownloadHash(configuration.Key, 32) || !DownloadHash(configuration.Sha256, 64) || configuration.ByteLength is < 1 or > (1 << 20) ||
                configuration.SourceUrl is null || configuration.SourceUrl.Length is < 1 or > 2048 ||
                !DownloadUtc(configuration.RetrievedAt) || !DownloadUtc(configuration.VerifiedAt)) throw GeometryInvalid();
        if (source.EncodingSource.Role != "encoding" || source.RootSource.Role != "root") throw GeometryInvalid();
    }

    private static void GeometrySizes(SqliteDataReader reader, int[] positions, ref int budget)
    {
        long total = 0;
        foreach (var position in positions)
        { if (reader.IsDBNull(position) || reader.GetInt64(position) is < 1 or > GeometryPairBytes) throw GeometryLimit(); total += reader.GetInt64(position); }
        if (total > budget) throw GeometryLimit(); budget -= checked((int)total);
    }

    private static byte[] GeometryBytes<T>(T value, int limit)
    {
        try
        {
            var bytes = CreaturesPolicy.Bytes(value); if (bytes.Length > limit) throw GeometryLimit(); return bytes;
        }
        catch (DiscoveryException error) when (error.Code == "CREATURES_PROJECTION_LIMIT") { throw GeometryLimit(); }
    }
    private static T GeometryDecode<T>(ReadOnlySpan<byte> bytes)
    {
        try
        {
            GeometryPreflight<T>(bytes);
            return JsonSerializer.Deserialize<T>(bytes, GeometryJson) ?? throw GeometryInvalid();
        }
        catch (JsonException) { throw GeometryInvalid(); }
    }
    private static void GeometryPreflight<T>(ReadOnlySpan<byte> bytes)
    {
        // Keep only the active depth32 schema path. Duplicate/unknown properties and collection
        // geometry are refused before JsonSerializer can construct any receipt/descriptor graph.
        Span<int> counts = stackalloc int[33], limits = stackalloc int[33], properties = stackalloc int[33];
        var types = new Type?[33]; var values = new Type?[33]; var seen = new HashSet<string>?[33];
        var exactMembers = typeof(T) == typeof(GeometryArtifactDescriptor);
        Span<char> name = stackalloc char[97];
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 32 });
        while (reader.Read())
        {
            var depth = reader.CurrentDepth;
            if (depth > 0 && limits[depth - 1] > 0 && reader.TokenType is not (JsonTokenType.PropertyName or JsonTokenType.EndArray or JsonTokenType.EndObject) && ++counts[depth - 1] > limits[depth - 1]) throw GeometryLimit();
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueSpan.Length > 96 || types[depth - 1] is not { } owner) throw GeometryInvalid();
                var text = name[..reader.CopyString(name)]; var property = text.ToString();
                var schema = GeometryProperties.GetOrAdd(owner, static type => type.GetProperties()
                    .Where(p => p.GetMethod?.IsPublic == true).ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p => p.PropertyType, StringComparer.Ordinal));
                if (!schema.TryGetValue(property, out values[depth]) || !seen[depth - 1]!.Add(property)) throw GeometryInvalid();
                properties[depth] = 256;
                properties[depth] = text.SequenceEqual("columns") ? 512 : text.SequenceEqual("fields") ? 16 :
                    text.SequenceEqual("configurations") || text.SequenceEqual("resolvers") ? 2 : text.SequenceEqual("min") || text.SequenceEqual("max") ? 3 : 256;
            }
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
            {
                var expected = depth == 0 ? typeof(T) : limits[depth - 1] > 0 ? types[depth - 1] : values[depth];
                if (expected is null) throw GeometryInvalid(); expected = Nullable.GetUnderlyingType(expected) ?? expected;
                var collection = expected.IsArray ? expected.GetElementType() : expected.IsGenericType && typeof(System.Collections.IEnumerable).IsAssignableFrom(expected) ? expected.GetGenericArguments()[0] : null;
                if ((reader.TokenType == JsonTokenType.StartArray) != (collection is not null) || expected == typeof(string) || expected.IsPrimitive) throw GeometryInvalid();
                types[depth] = collection ?? expected; seen[depth] = collection is null ? new(StringComparer.Ordinal) : null;
                limits[depth] = collection is not null ? (properties[depth] > 0 ? properties[depth] : 256) : 0; counts[depth] = 0;
            }
            if (exactMembers && reader.TokenType == JsonTokenType.EndObject && types[depth] is { } objectType &&
                seen[depth]!.Count != GeometryProperties.GetOrAdd(objectType, static type => type.GetProperties()
                    .Where(p => p.GetMethod?.IsPublic == true).ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p => p.PropertyType, StringComparer.Ordinal)).Count) throw GeometryInvalid();
        }
    }
}
