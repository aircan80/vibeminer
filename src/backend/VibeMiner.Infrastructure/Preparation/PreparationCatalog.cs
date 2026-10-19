using System.Globalization;
using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    private void UpgradeWorkspace()
    {
        using var transaction = Db.BeginTransaction();
        Execute("""
            CREATE TABLE configuration_objects(key TEXT PRIMARY KEY,sha256 TEXT NOT NULL,byte_length INTEGER NOT NULL,
                source_url TEXT NOT NULL,retrieved_at TEXT NOT NULL,verifier_revision TEXT NOT NULL);
            CREATE TABLE workspace_admissions(id INTEGER PRIMARY KEY AUTOINCREMENT,product TEXT NOT NULL,
                build_id TEXT NOT NULL,region TEXT NOT NULL,profile TEXT NOT NULL,source_reference TEXT NOT NULL,
                opened_at TEXT NOT NULL,FOREIGN KEY(build_id) REFERENCES builds(id));
            CREATE INDEX admissions_context ON workspace_admissions(build_id,region,profile,id DESC);
            CREATE TABLE workspace_configurations(admission_id INTEGER NOT NULL,role TEXT NOT NULL,key TEXT NOT NULL,
                sha256 TEXT NOT NULL,byte_length INTEGER NOT NULL,source_url TEXT NOT NULL,retrieved_at TEXT NOT NULL,
                verified_at TEXT NOT NULL,parsed_field_count INTEGER NOT NULL,
                PRIMARY KEY(admission_id,role),FOREIGN KEY(admission_id) REFERENCES workspace_admissions(id),
                FOREIGN KEY(key) REFERENCES configuration_objects(key));
            CREATE TABLE open_acknowledgements(build_id TEXT PRIMARY KEY,opened_at TEXT NOT NULL,admission_id INTEGER NOT NULL,
                FOREIGN KEY(build_id) REFERENCES builds(id),FOREIGN KEY(admission_id) REFERENCES workspace_admissions(id));
            PRAGMA user_version=3;
            """, transaction);
        transaction.Commit();
    }
    /// <summary>
    /// The context of the build as it is open now: the CDN configuration of the latest workspace admission for
    /// this build and region. A newer advertised CDN configuration only applies after an explicit new open.
    /// Without any admission this is the newest CDN configuration the region advertised.
    /// </summary>
    public Task<CapturedOpeningContext> ResolveOpeningAsync(WorkspaceRequest request, CancellationToken token) =>
        Schedule(() => ResolveOpeningCore(request, latest: false, token), token);
    /// <summary>The context for a new open: the newest CDN configuration the selected region advertised.</summary>
    public Task<CapturedOpeningContext> ResolveLatestOpeningAsync(WorkspaceRequest request, CancellationToken token) =>
        Schedule(() => ResolveOpeningCore(request, latest: true, token), token);
    private CapturedOpeningContext ResolveOpeningCore(WorkspaceRequest request, bool latest, CancellationToken token)
    {
        WorkspaceService.Validate(request);
        var build = ReadCore(request.ProductId, "all", 1, null, token, request.BuildIdentity).Builds.SingleOrDefault()
            ?? throw new DiscoveryException("BUILD_NOT_FOUND", "This exact saved build is unavailable. Refresh discovery and select it again.", true);
        var admitted = latest ? null : Scalar("""
            SELECT c.key FROM workspace_admissions a
            JOIN workspace_configurations c ON c.admission_id=a.id AND c.role='cdnConfig'
            WHERE a.product=$p AND a.build_id=$id AND a.region=$region ORDER BY a.id DESC LIMIT 1
            """, null, ("$p", request.ProductId), ("$id", request.BuildIdentity), ("$region", request.SourceRegion)) as string;
        long generation; int ordinal; int cdnsOrdinal; string cdnConfig;
        using (var command = Command("""
            SELECT r.generation,r.ordinal,o.cdns_ordinal,r.cdn_config FROM build_cdn_config_regions r
                JOIN observations o ON o.generation=r.generation AND o.ordinal=r.ordinal
            WHERE r.build_id=$id AND r.region=$region AND o.build_id=$id AND o.region=$region AND o.cdn_config=r.cdn_config
                AND ($cdn IS NULL OR r.cdn_config=$cdn)
            ORDER BY r.generation DESC,r.ordinal LIMIT 1
            """, null, ("$id", request.BuildIdentity), ("$region", request.SourceRegion), ("$cdn", admitted)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.IsDBNull(2))
                throw new DiscoveryException("SOURCE_UNAVAILABLE", "The selected saved region has no unambiguous CDN route.", true);
            generation = reader.GetInt64(0); ordinal = reader.GetInt32(1); cdnsOrdinal = reader.GetInt32(2); cdnConfig = reader.GetString(3);
        }
        var documents = ReadOpeningSourceDocuments(generation, token);
        var versions = documents["versions"]; var cdns = documents["cdns"];
        // The context is bound to the CDN configuration of its own versions row, not the build's newest one.
        return new CapturedOpeningContext(build with { CdnConfig = cdnConfig }, new(generation.ToString(CultureInfo.InvariantCulture), request.SourceRegion,
            versions.Sha256, ordinal, cdns.Sha256, cdnsOrdinal), versions, cdns);
    }
    // Both declared size and the actual SQLite value are checked before any body/text getter.
    // This is a narrow source-document bound, not a whole ReadCore/identity graph bound.
    private const string OpeningSourceRowValid = """
        typeof(d.role)='text' AND d.role IN ('versions','cdns')
        AND typeof(d.url)='text' AND length(CAST(d.url AS BLOB))<=2048
        AND typeof(d.retrieved_at)='text' AND length(CAST(d.retrieved_at AS BLOB))<=64
        AND typeof(d.parser_revision)='text' AND length(CAST(d.parser_revision AS BLOB))<=128
        AND typeof(d.sha256)='text' AND length(CAST(d.sha256 AS BLOB))=64 AND d.sha256 NOT GLOB '*[^0-9a-f]*'
        AND typeof(b.byte_length)='integer' AND b.byte_length BETWEEN 1 AND 1048576
        AND typeof(b.body)='blob' AND length(b.body) BETWEEN 1 AND 1048576 AND length(b.body)=b.byte_length
        """;
    internal static readonly string OpeningSourceDocumentsSql = $$"""
        SELECT CASE WHEN {{OpeningSourceRowValid}} THEN d.role END,
            CASE WHEN {{OpeningSourceRowValid}} THEN d.url END,
            CASE WHEN {{OpeningSourceRowValid}} THEN d.retrieved_at END,
            CASE WHEN {{OpeningSourceRowValid}} THEN d.sha256 END,
            CASE WHEN {{OpeningSourceRowValid}} THEN b.byte_length END,
            CASE WHEN {{OpeningSourceRowValid}} THEN b.body END,
            CASE WHEN {{OpeningSourceRowValid}} THEN d.parser_revision END,
            CASE WHEN {{OpeningSourceRowValid}} THEN 1 ELSE 0 END
        FROM documents d LEFT JOIN raw_documents b ON b.sha256=d.sha256
        WHERE d.generation=$generation AND d.role IN ('versions','cdns') ORDER BY d.role LIMIT 3
        """;
    private void ValidateOpeningSourceDocuments(long generation, CancellationToken token, bool requireCdns = true)
    {
        token.ThrowIfCancellationRequested();
        // The third row is an overflow sentinel. Never COUNT every corrupt foreign role.
        using var command = Command($$"""
            WITH bounded AS (
                SELECT CASE d.role WHEN 'versions' THEN 1 WHEN 'cdns' THEN 2 ELSE 0 END AS role,
                    CASE WHEN d.role='cdns' AND $requireCdns=0 THEN 1 WHEN {{OpeningSourceRowValid}} THEN 1 ELSE 0 END AS valid
                FROM documents d LEFT JOIN raw_documents b ON b.sha256=d.sha256
                WHERE d.generation=$generation LIMIT 3)
            SELECT COUNT(*),COALESCE(SUM(role=1),0),COALESCE(SUM(role=2),0),COALESCE(MIN(valid),0) FROM bounded
            """, null, ("$generation", generation), ("$requireCdns", requireCdns ? 1 : 0));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) is < 1 or > 2 || reader.GetInt32(1) != 1 ||
            reader.GetInt32(2) != (requireCdns ? 1 : reader.GetInt32(0) - 1) || reader.GetInt32(3) != 1)
            throw new DiscoveryException("CATALOG_INTEGRITY", "The saved source document pair exceeds its bounds or differs from its exact supported receipts.");
    }
    private Dictionary<string, SourceDocument> ReadOpeningSourceDocuments(long generation, CancellationToken token)
    {
        ValidateOpeningSourceDocuments(generation, token);
        var documents = new Dictionary<string, SourceDocument>(StringComparer.Ordinal);
        using var command = Command(OpeningSourceDocumentsSql, null, ("$generation", generation));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (documents.Count >= 2 || reader.GetInt32(7) != 1)
                throw new DiscoveryException("CATALOG_INTEGRITY", "The saved source document pair changed or exceeds its bounds.");
            var role = reader.GetString(0); var hash = reader.GetString(3); var bytes = (byte[])reader[5];
            if (bytes.Length != reader.GetInt32(4) || Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash || documents.ContainsKey(role))
                throw new DiscoveryException("CATALOG_INTEGRITY", "The exact saved source document failed integrity verification.");
            documents.Add(role, new(role, reader.GetString(1), reader.GetString(2), hash, bytes, reader.GetString(6)));
        }
        if (documents.Count != 2 || !documents.ContainsKey("versions") || !documents.ContainsKey("cdns"))
            throw new DiscoveryException("CATALOG_INTEGRITY", "The saved versions/CDN document pair is incomplete.");
        return documents;
    }
    public Task<bool> HasAdmissionAsync(CapturedOpeningContext context, CancellationToken token) => Schedule(() =>
    {
        // Each admission keeps the CDN configuration it was made with. One made with another CDN configuration
        // of the same build does not admit this context; it is not a broken proof either.
        var value = Scalar("""
            SELECT a.id FROM workspace_admissions a
            JOIN workspace_configurations c ON c.admission_id=a.id AND c.role='cdnConfig' AND c.key=$cdn
            WHERE a.build_id=$id AND a.region=$region AND a.profile IN ($profile,'home-inventory-v1','home-download-v1')
            ORDER BY a.id DESC LIMIT 1
            """, null, ("$id", context.Build.Id), ("$region", context.Reference.Region),
            ("$profile", ConfigurationWorkspacePreparer.Profile), ("$cdn", context.Build.CdnConfig));
        if (value is null) return false;
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = Command("SELECT role,key FROM workspace_configurations WHERE admission_id=$id", null, ("$id", value));
        using var reader = command.ExecuteReader();
        while (reader.Read()) roles.Add(reader.GetString(0), reader.GetString(1));
        if (roles.Count != 2 || roles.GetValueOrDefault("buildConfig") != context.Build.BuildConfig || roles.GetValueOrDefault("cdnConfig") != context.Build.CdnConfig)
            throw new DiscoveryException("CATALOG_INTEGRITY", "The saved workspace admission has incomplete configuration proofs.");
        return true;
    }, token);
    public Task<StoredConfiguration?> FindConfigurationAsync(string key, CancellationToken token) => Schedule(() =>
    {
        using var command = Command("SELECT sha256,byte_length,source_url,retrieved_at,verifier_revision FROM configuration_objects WHERE key=$key", null, ("$key", key));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetString(4) != ConfigurationWorkspacePreparer.VerifierRevision || reader.GetInt32(1) is < 1 or > 1024 * 1024)
            throw new DiscoveryException("CATALOG_INTEGRITY", "The saved configuration verifier is unsupported.");
        return new StoredConfiguration(key, reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3));
    }, token);
    public Task RecordConfigurationAsync(StoredConfiguration configuration, CancellationToken token) => Schedule(() =>
    {
        using var transaction = Db.BeginTransaction();
        using (var command = Command("SELECT sha256,byte_length FROM configuration_objects WHERE key=$key", transaction, ("$key", configuration.Key)))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read() && (reader.GetString(0) != configuration.Sha256 || reader.GetInt32(1) != configuration.ByteLength))
                throw new DiscoveryException("CONFIG_IDENTITY_CONFLICT", "Saved bytes conflict with the exact configuration identity.");
        }
        Execute("INSERT OR IGNORE INTO configuration_objects VALUES($key,$sha,$length,$url,$time,$revision)", transaction,
            ("$key", configuration.Key), ("$sha", configuration.Sha256), ("$length", configuration.ByteLength),
            ("$url", configuration.SourceUrl), ("$time", configuration.RetrievedAt), ("$revision", ConfigurationWorkspacePreparer.VerifierRevision));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);
    public Task<WorkspaceSnapshot> CommitWorkspaceAsync(CapturedOpeningContext context,
        IReadOnlyList<ConfigurationReceipt> configurations, string openedAt, CancellationToken token) => Schedule(() =>
    {
        if (configurations.Count != 2 || configurations.Count(value => value.Role == "buildConfig" && value.Key == context.Build.BuildConfig) != 1 ||
            configurations.Count(value => value.Role == "cdnConfig" && value.Key == context.Build.CdnConfig) != 1)
            throw new DiscoveryException("CONFIGURATION_INCOMPLETE", "Both exact verified configurations are required before Home.");
        using var transaction = Db.BeginTransaction();
        foreach (var configuration in configurations)
        {
            using var command = Command("SELECT sha256,byte_length,source_url,retrieved_at FROM configuration_objects WHERE key=$key", transaction, ("$key", configuration.Key));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != configuration.Sha256 || reader.GetInt32(1) != configuration.ByteLength ||
                reader.GetString(2) != configuration.SourceUrl || reader.GetString(3) != configuration.RetrievedAt || configuration.ParsedFieldCount is < 0 or > 4096)
                throw new DiscoveryException("CATALOG_INTEGRITY", "A configuration proof changed before workspace admission.");
        }
        Execute("INSERT INTO workspace_admissions(product,build_id,region,profile,source_reference,opened_at) VALUES($p,$build,$region,$profile,$source,$time)", transaction,
            ("$p", context.Build.ProductId), ("$build", context.Build.Id), ("$region", context.Reference.Region),
            ("$profile", ConfigurationWorkspacePreparer.Profile), ("$source", Serialize(context.Reference)), ("$time", openedAt));
        var admission = Convert.ToInt64(Scalar("SELECT last_insert_rowid()", transaction));
        foreach (var configuration in configurations)
            Execute("INSERT INTO workspace_configurations VALUES($id,$role,$key,$sha,$length,$url,$retrieved,$verified,$count)", transaction,
                ("$id", admission), ("$role", configuration.Role), ("$key", configuration.Key), ("$sha", configuration.Sha256),
                ("$length", configuration.ByteLength), ("$url", configuration.SourceUrl), ("$retrieved", configuration.RetrievedAt),
                ("$verified", configuration.VerifiedAt), ("$count", configuration.ParsedFieldCount));
        Execute("INSERT INTO open_acknowledgements VALUES($build,$time,$id) ON CONFLICT(build_id) DO UPDATE SET opened_at=$time,admission_id=$id", transaction,
            ("$build", context.Build.Id), ("$time", openedAt), ("$id", admission));
        token.ThrowIfCancellationRequested(); transaction.Commit();
        var build = ReadCore(context.Build.ProductId, "all", 1, null, CancellationToken.None, context.Build.Id).Builds.Single();
        // A concurrent discovery refresh may have newer projection receipts or a newer CDN configuration.
        // Opening keeps the pair and the CDN configuration it actually used.
        var reference = context.Reference;
        build = build with { CdnConfig = context.Build.CdnConfig, SourceReceipts = build.SourceReceipts.Where(receipt => receipt.Region != reference.Region).Concat(new[]
        {
            new SourceReceipt(reference.Region, "versions", context.Versions.Url, context.Versions.RetrievedAt,
                context.Versions.Sha256, context.Versions.Bytes.Length, reference.VersionsRowOrdinal),
            new SourceReceipt(reference.Region, "cdns", context.Cdns.Url, context.Cdns.RetrievedAt,
                context.Cdns.Sha256, context.Cdns.Bytes.Length, reference.CdnsRowOrdinal)
        }).ToArray() };
        return new WorkspaceSnapshot(build, openedAt, ConfigurationWorkspacePreparer.Profile, "configurationReady", "configurationsVerified",
            "notPrepared", "unknown", "onDemand", null, null, context.Reference, configurations);
    }, token);
}
