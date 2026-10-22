using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    /// <summary>Resolve only the retained observation originally admitted; never fall back to latest routes.</summary>
    internal Task<CapturedOpeningContext> ResolveOpeningAsync(WorkspaceRequest request,
        OpeningSourceReference sourceReference, CancellationToken token) => Schedule(() =>
    {
        if (request is null || request.ProductId is null || request.BuildIdentity is null || request.SourceRegion is null)
            throw PinnedError("SOURCE_REFERENCE_INVALID", "An exact retained build and source reference are required.");
        WorkspaceService.Validate(request);
        if (sourceReference is null || sourceReference.Region != request.SourceRegion ||
            !long.TryParse(sourceReference.Generation, NumberStyles.None, CultureInfo.InvariantCulture, out var generation) ||
            generation < 1 || generation.ToString(CultureInfo.InvariantCulture) != sourceReference.Generation ||
            !PinnedHash(sourceReference.VersionsSha256) || !PinnedHash(sourceReference.CdnsSha256) ||
            sourceReference.VersionsRowOrdinal is < 0 or >= MetadataParser.MaxRows ||
            sourceReference.CdnsRowOrdinal is < 0 or >= MetadataParser.MaxRows)
            throw PinnedError("SOURCE_REFERENCE_INVALID", "The pinned source identity, region or row geometry is invalid.");

        string originalFields, originalRoute, observedCdnConfig;
        using (var command = Command("""
            SELECT o.region,o.build_id,o.cdns_ordinal,o.original_fields,o.route_fields,e.product,
                CASE WHEN typeof(o.region)='text' AND length(CAST(o.region AS BLOB)) BETWEEN 2 AND 8
                    AND typeof(o.build_id)='text' AND length(CAST(o.build_id AS BLOB))=64
                    AND typeof(e.product)='text' AND length(CAST(e.product AS BLOB)) BETWEEN 1 AND $maxProduct
                    AND typeof(o.original_fields)='text' AND length(CAST(o.original_fields AS BLOB))<=131072
                    AND (o.route_fields IS NULL OR typeof(o.route_fields)='text' AND length(CAST(o.route_fields AS BLOB))<=131072)
                    AND typeof(o.cdn_config)='text' AND length(o.cdn_config)=32 AND o.cdn_config NOT GLOB '*[^0-9a-f]*'
                    THEN 1 ELSE 0 END,o.cdn_config
            FROM observations o JOIN events e ON e.generation=o.generation
            WHERE o.generation=$generation AND o.ordinal=$ordinal
            """, null, ("$generation", generation), ("$ordinal", sourceReference.VersionsRowOrdinal), ("$maxProduct", ProductRegistry.MaxIdBytes)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw PinnedError("SOURCE_REFERENCE_UNAVAILABLE", "The exact retained observation is unavailable.");
            if (reader.GetInt32(6) != 1)
                throw PinnedError("CATALOG_INTEGRITY", "The retained observation exceeds its bounded field representation.");
            if (reader.GetString(0) != request.SourceRegion || reader.GetString(1) != request.BuildIdentity ||
                reader.IsDBNull(2) || reader.GetInt32(2) != sourceReference.CdnsRowOrdinal ||
                reader.IsDBNull(4) || reader.GetString(5) != request.ProductId)
                throw PinnedError("SOURCE_REFERENCE_UNAVAILABLE", "The exact retained observation does not match this pinned build, region and route.");
            originalFields = reader.GetString(3); originalRoute = reader.GetString(4); observedCdnConfig = reader.GetString(7);
            if (originalFields.Length > 131072 || originalRoute.Length > 131072)
                throw PinnedError("CATALOG_INTEGRITY", "A retained observation exceeds its bounded field representation.");
        }

        var documents = ReadOpeningSourceDocuments(generation, token);
        var versions = documents["versions"]; var cdns = documents["cdns"];
        if (versions.Sha256 != sourceReference.VersionsSha256 || cdns.Sha256 != sourceReference.CdnsSha256)
            throw PinnedError("CATALOG_INTEGRITY", "A pinned source digest differs from its retained document.");
        foreach (var document in documents.Values)
            if (document.Url != $"https://us.version.battle.net/v2/products/{request.ProductId}/{document.Role}" || document.ParserRevision != "text-metadata-v1")
                throw PinnedError("SOURCE_UNSUPPORTED", "The pinned source lacks its original supported official discovery receipt.");
        var parsedVersions = MetadataParser.Parse(versions.Bytes, token);
        var parsedCdns = MetadataParser.Parse(cdns.Bytes, token);
        if (sourceReference.VersionsRowOrdinal >= parsedVersions.Rows.Count || sourceReference.CdnsRowOrdinal >= parsedCdns.Rows.Count)
            throw PinnedError("CATALOG_INTEGRITY", "A pinned row lies outside its authenticated source document.");
        var versionFields = parsedVersions.Rows[sourceReference.VersionsRowOrdinal].Fields;
        var routeFields = parsedCdns.Rows[sourceReference.CdnsRowOrdinal].Fields;
        var identity = BuildIdentity.Create(request.ProductId, versionFields);
        // The pinned row's own CDN configuration, never the build's newest one.
        var cdnConfig = BuildIdentity.ObservedCdnConfig(versionFields);
        if (identity.Id != request.BuildIdentity || versionFields.GetValueOrDefault("Region") != request.SourceRegion ||
            cdnConfig != observedCdnConfig ||
            routeFields.GetValueOrDefault("Name") != request.SourceRegion ||
            parsedCdns.Rows.Count(row => row.Fields.GetValueOrDefault("Name") == request.SourceRegion) != 1 ||
            !PinnedFieldsEqual(originalFields, versionFields) || !PinnedFieldsEqual(originalRoute, routeFields))
            throw PinnedError("CATALOG_INTEGRITY", "The pinned raw rows, saved observation and exact build identity do not agree.");

        string first, last;
        using (var command = Command("SELECT canonical,identity_fields,first_seen,last_seen FROM builds WHERE id=$id AND product=$product",
            null, ("$id", request.BuildIdentity), ("$product", request.ProductId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw PinnedError("SOURCE_REFERENCE_UNAVAILABLE", "The pinned saved build is unavailable.");
            if (reader.GetString(0) != identity.Canonical || !PinnedFieldsEqual(reader.GetString(1), identity.Fields))
                throw PinnedError("CATALOG_INTEGRITY", "The pinned source differs from the saved complete build identity.");
            first = reader.GetString(2); last = reader.GetString(3);
        }
        string configFirst, configLast;
        using (var command = Command("SELECT first_seen,last_seen FROM build_cdn_configs WHERE build_id=$id AND cdn_config=$c",
            null, ("$id", request.BuildIdentity), ("$c", cdnConfig)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw PinnedError("CATALOG_INTEGRITY", "The pinned CDN configuration is missing from the build's history.");
            configFirst = reader.GetString(0); configLast = reader.GetString(1);
        }
        // This internal projection is source-bound. Reading latest discovery receipts here would
        // let a later refresh alter or break an otherwise valid historical retention plan.
        var receipts = new SourceReceipt[]
        {
            new(request.SourceRegion, "versions", versions.Url, versions.RetrievedAt, versions.Sha256,
                versions.Bytes.Length, sourceReference.VersionsRowOrdinal),
            new(request.SourceRegion, "cdns", cdns.Url, cdns.RetrievedAt, cdns.Sha256,
                cdns.Bytes.Length, sourceReference.CdnsRowOrdinal)
        };
        var fields = identity.Fields;
        var build = new Build(identity.Id, request.ProductId, fields["VersionsName"], fields.GetValueOrDefault("BuildId", ""),
            fields["BuildConfig"], cdnConfig, fields.GetValueOrDefault("ProductConfig") is { Length: > 0 } product ? product : null,
            fields, [request.SourceRegion], [], [request.SourceRegion], first, last, false, false, false, "unknown", receipts,
            [new CdnConfigObservation(cdnConfig, configFirst, configLast, [request.SourceRegion], [], [receipts[0]])], 1);
        var result = new CapturedOpeningContext(build, sourceReference, versions, cdns);
        _ = ConfigurationWorkspacePreparer.ResolveRoute(result);
        token.ThrowIfCancellationRequested(); return result;
    }, token);

    private static bool PinnedHash(string? value) => value is { Length: 64 } &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool PinnedFieldsEqual(string json, IReadOnlyDictionary<string, string> fields)
    {
        if (json.Length > 131072) throw PinnedError("CATALOG_INTEGRITY", "A pinned identity field set exceeds its bounded representation.");
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json);
            return saved is not null && saved.Count == fields.Count && fields.All(pair => saved.TryGetValue(pair.Key, out var value) && value == pair.Value);
        }
        catch (JsonException) { throw PinnedError("CATALOG_INTEGRITY", "A pinned saved field set is malformed."); }
    }
    private static DiscoveryException PinnedError(string code, string message) => new(code, message);
}
