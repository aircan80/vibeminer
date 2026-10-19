using System.Globalization;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed record RetainedEncodedObject(string Key, string ContentKey, string Sha256, long EncodedBytes, long DecodedBytes, ContentProjection? Projection = null);

internal sealed record StoredDownloadAdmission(DownloadLedgerArtifact Artifact,
    IReadOnlyList<string> VerifiedKeys, string AssessedAt, IReadOnlyDictionary<string, long> VerifiedLengths);

public sealed partial class DiscoveryCatalog
{
    internal const string DownloadProfile = "home-download-v1";
    internal const string DownloadScope = "downloadManifestAllVariants";
    private const string DownloadParserRevision = "download-v1";
    private const int MaxDownloadAssessmentKeys = 10_000;

    private void UpgradeDownloads()
    {
        using var transaction = Db.BeginTransaction();
        Execute("""
            CREATE TABLE download_artifacts(sha256 TEXT PRIMARY KEY,byte_length INTEGER NOT NULL,
                object_count INTEGER NOT NULL,total_encoded_bytes INTEGER NOT NULL,ledger_revision TEXT NOT NULL);
            CREATE TABLE download_artifact_sources(source_sha256 TEXT NOT NULL,parser_revision TEXT NOT NULL,
                ledger_revision TEXT NOT NULL,artifact_sha256 TEXT NOT NULL,receipt TEXT NOT NULL,
                PRIMARY KEY(source_sha256,parser_revision,ledger_revision),
                FOREIGN KEY(artifact_sha256) REFERENCES download_artifacts(sha256));
            CREATE TABLE download_admissions(admission_id INTEGER PRIMARY KEY,artifact_sha256 TEXT,
                verified_keys TEXT NOT NULL,assessed_at TEXT NOT NULL,metadata TEXT,summary TEXT,problem TEXT,
                FOREIGN KEY(admission_id) REFERENCES workspace_admissions(id) ON DELETE CASCADE,
                FOREIGN KEY(artifact_sha256) REFERENCES download_artifacts(sha256));
            CREATE INDEX download_admission_artifact ON download_admissions(artifact_sha256,admission_id);
            CREATE TABLE resolver_artifacts(source_sha256 TEXT NOT NULL,role TEXT NOT NULL,parser_revision TEXT NOT NULL,
                receipt TEXT NOT NULL,decoded_sha256 TEXT NOT NULL,decoded_bytes INTEGER NOT NULL,
                PRIMARY KEY(source_sha256,role,parser_revision));
            PRAGMA user_version=6;
            """, transaction);
        transaction.Commit();
    }

    internal Task<DownloadLedgerArtifact?> FindDownloadArtifactAsync(MetadataReceipt source, CancellationToken token) => Schedule(() =>
    {
        ValidateDownloadSource(source, completed: false);
        using var command = Command("""
            SELECT s.receipt,a.sha256,a.byte_length,a.object_count,a.total_encoded_bytes,a.ledger_revision
            FROM download_artifact_sources s JOIN download_artifacts a ON a.sha256=s.artifact_sha256
            WHERE s.source_sha256=$s AND s.parser_revision=$p AND s.ledger_revision=$r
            """, null, ("$s", source.Sha256), ("$p", DownloadParserRevision), ("$r", DownloadLedger.Revision));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var original = Deserialize<MetadataReceipt>(reader.GetString(0));
        ValidateDownloadSource(original, completed: true);
        if (!SameDownloadSource(original, source) || reader.GetString(5) != DownloadLedger.Revision)
            throw DownloadIntegrity("A saved download ledger differs from its exact source binding.");
        var artifact = new DownloadLedgerArtifact(reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
        ValidateDownloadArtifact(artifact);
        return artifact;
    }, token);

    internal Task<ResolverArtifact?> FindResolverArtifactAsync(string sourceSha256, string role, string parserRevision,
        CancellationToken token) => Schedule(() =>
    {
        ValidateResolverIdentity(sourceSha256, role, parserRevision);
        using var command = Command("SELECT receipt,decoded_sha256,decoded_bytes FROM resolver_artifacts WHERE source_sha256=$s AND role=$role AND parser_revision=$r", null,
            ("$s", sourceSha256), ("$role", role), ("$r", parserRevision));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        var result = new ResolverArtifact(Deserialize<MetadataReceipt>(reader.GetString(0)), parserRevision, reader.GetString(1), reader.GetInt64(2));
        reader.Close(); ValidateResolverArtifact(result);
        if (result.Source.Sha256 != sourceSha256 || result.Source.Role != role)
            throw DownloadIntegrity("A resolver artifact differs from its exact source identity.");
        ValidateDownloadSourceAssociation(result.Source, null);
        return result;
    }, token);

    internal Task RecordResolverArtifactAsync(ResolverArtifact artifact, CancellationToken token) => Schedule(() =>
    {
        ValidateResolverArtifact(artifact);
        using var transaction = Db.BeginTransaction();
        ValidateDownloadSourceAssociation(artifact.Source, transaction);
        using (var command = Command("SELECT receipt,decoded_sha256,decoded_bytes FROM resolver_artifacts WHERE source_sha256=$s AND role=$role AND parser_revision=$r", transaction,
            ("$s", artifact.Source.Sha256), ("$role", artifact.Source.Role), ("$r", artifact.ParserRevision)))
        using (var reader = command.ExecuteReader())
            if (reader.Read() && (!SameDownloadSource(Deserialize<MetadataReceipt>(reader.GetString(0)), artifact.Source) ||
                reader.GetString(1) != artifact.DecodedSha256 || reader.GetInt64(2) != artifact.DecodedBytes))
                throw DownloadIntegrity("A source-bound resolver artifact cannot be reassociated with different decoded bytes.");
        Execute("INSERT OR IGNORE INTO resolver_artifacts VALUES($s,$role,$r,$receipt,$d,$n)", transaction,
            ("$s", artifact.Source.Sha256), ("$role", artifact.Source.Role), ("$r", artifact.ParserRevision),
            ("$receipt", Serialize(artifact.Source)), ("$d", artifact.DecodedSha256), ("$n", artifact.DecodedBytes));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    internal Task RecordDownloadArtifactAsync(MetadataReceipt source, DownloadLedgerArtifact artifact, CancellationToken token) => Schedule(() =>
    {
        ValidateDownloadSource(source, completed: true);
        ValidateDownloadArtifact(artifact);
        using var transaction = Db.BeginTransaction();
        ValidateDownloadSourceAssociation(source, transaction);
        var previous = ReadDownloadArtifact(artifact.Sha256, transaction);
        if (previous is not null && previous != artifact)
            throw DownloadIntegrity("An immutable download ledger identity has conflicting geometry.");
        Execute("INSERT OR IGNORE INTO download_artifacts VALUES($h,$b,$n,$t,$r)", transaction,
            ("$h", artifact.Sha256), ("$b", artifact.ByteLength), ("$n", artifact.ObjectCount),
            ("$t", artifact.TotalEncodedBytes), ("$r", DownloadLedger.Revision));
        var existing = Scalar("SELECT receipt FROM download_artifact_sources WHERE source_sha256=$s AND parser_revision=$p AND ledger_revision=$r", transaction,
            ("$s", source.Sha256), ("$p", DownloadParserRevision), ("$r", DownloadLedger.Revision)) as string;
        if (existing is not null)
        {
            var original = Deserialize<MetadataReceipt>(existing);
            var key = Scalar("SELECT artifact_sha256 FROM download_artifact_sources WHERE source_sha256=$s AND parser_revision=$p AND ledger_revision=$r", transaction,
                ("$s", source.Sha256), ("$p", DownloadParserRevision), ("$r", DownloadLedger.Revision)) as string;
            if (!SameDownloadSource(original, source) || key != artifact.Sha256)
                throw DownloadIntegrity("A complete source cannot be reassociated with a different immutable ledger.");
        }
        else Execute("INSERT INTO download_artifact_sources VALUES($s,$p,$r,$a,$receipt)", transaction,
            ("$s", source.Sha256), ("$p", DownloadParserRevision), ("$r", DownloadLedger.Revision),
            ("$a", artifact.Sha256), ("$receipt", Serialize(source)));
        token.ThrowIfCancellationRequested();
        transaction.Commit(); return true;
    }, token);

    // The caller supplies only exact ledger.AssessAsync matches after actual retained-byte
    // verification. SQLite freezes that dated assessment; this method performs no filesystem I/O.
    internal Task<WorkspaceSnapshot> CommitDownloadWorkspaceAsync(CapturedOpeningContext context,
        IReadOnlyList<ConfigurationReceipt> configurations, IReadOnlyList<MetadataReceipt> metadata,
        DownloadLedgerArtifact artifact, IReadOnlyList<string> verifiedKeys, long metadataOverhead,
        CancellationToken token) => Schedule(() =>
    {
        ValidateDownloadArtifact(artifact);
        if (verifiedKeys.Count > MaxDownloadAssessmentKeys || verifiedKeys.Distinct(StringComparer.Ordinal).Count() != verifiedKeys.Count ||
            verifiedKeys.Any(key => !DownloadHash(key, 32)))
            throw DownloadIntegrity("A download coverage assessment exceeds its bound or contains invalid duplicate identities.");
        if (metadata.Count is < 1 or > 2 || metadata.Count(receipt => receipt.Role == "download") != 1 ||
            metadata.Any(receipt => receipt.Role is not ("download" or "encoding")) || metadata.Select(receipt => receipt.Role).Distinct().Count() != metadata.Count)
            throw DownloadIntegrity("A scoped download admission requires its download source and only an optional encoding source.");
        var source = metadata.Single(receipt => receipt.Role == "download");
        ValidateDownloadSource(source, completed: true);
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var transaction = Db.BeginTransaction();
        ValidateDownloadConfigurations(context, configurations, transaction);
        foreach (var receipt in metadata) ValidateDownloadSourceAssociation(receipt, transaction);
        if (ReadDownloadArtifact(artifact.Sha256, transaction) != artifact ||
            Scalar("SELECT artifact_sha256 FROM download_artifact_sources WHERE source_sha256=$s AND parser_revision=$p AND ledger_revision=$r", transaction,
                ("$s", source.Sha256), ("$p", DownloadParserRevision), ("$r", DownloadLedger.Revision)) as string != artifact.Sha256)
            throw DownloadIntegrity("The download ledger was not durably associated with its exact parsed source.");
        var boundSource = Deserialize<MetadataReceipt>((string)Scalar("SELECT receipt FROM download_artifact_sources WHERE source_sha256=$s AND parser_revision=$p AND ledger_revision=$r", transaction,
            ("$s", source.Sha256), ("$p", DownloadParserRevision), ("$r", DownloadLedger.Revision))!);
        if (!SameDownloadSource(boundSource, source)) throw DownloadIntegrity("Download source receipts changed before admission.");
        var verifiedBytes = DownloadAssessment(verifiedKeys, transaction, token).Bytes;
        if (verifiedKeys.Count > artifact.ObjectCount || verifiedBytes > artifact.TotalEncodedBytes)
            throw DownloadIntegrity("A verified retained assessment exceeds the immutable download scope.");
        var overhead = configurations.Select(receipt => (receipt.Sha256, Bytes: (long)receipt.ByteLength))
            .Concat(metadata.Select(receipt => (receipt.Sha256, Bytes: DownloadNumber(receipt.EncodedBytes))))
            .GroupBy(value => value.Sha256, StringComparer.Ordinal).Sum(group =>
            {
                if (group.Select(value => value.Bytes).Distinct().Count() != 1) throw DownloadIntegrity("A physical metadata identity has conflicting lengths.");
                return group.First().Bytes;
            });
        if (metadataOverhead != overhead) throw DownloadIntegrity("The encoded metadata overhead differs from its verified source objects.");
        var summary = new DownloadSummary(artifact.Sha256, DownloadScope, N(artifact.ObjectCount), N(artifact.TotalEncodedBytes),
            N(verifiedBytes), N(checked(artifact.TotalEncodedBytes - verifiedBytes)), now, N(overhead));
        var admission = InsertDownloadWorkspace(context, configurations, now, transaction);
        Execute("INSERT INTO download_admissions VALUES($id,$a,$v,$t,$m,$s,NULL)", transaction,
            ("$id", admission), ("$a", artifact.Sha256), ("$v", Serialize(verifiedKeys.Order(StringComparer.Ordinal).ToArray())),
            ("$t", now), ("$m", Serialize(metadata)), ("$s", Serialize(summary)));
        token.ThrowIfCancellationRequested(); transaction.Commit();
        return new WorkspaceSnapshot(DownloadBuild(context, isLocal: true), now, DownloadProfile, "partial", "verified", "notPrepared", "measured",
            "onDemand", null, null, context.Reference, configurations, null, metadata, summary, null);
    }, token);

    internal Task<WorkspaceSnapshot> CommitUnavailableDownloadWorkspaceAsync(CapturedOpeningContext context,
        IReadOnlyList<ConfigurationReceipt> configurations, DownloadProblem problem, CancellationToken token) => Schedule(() =>
    {
        if (string.IsNullOrWhiteSpace(problem.Code) || problem.Code.Length > 64 || string.IsNullOrWhiteSpace(problem.Message) || problem.Message.Length > 400)
            throw DownloadIntegrity("An unavailable download capability requires a bounded explicit cause.");
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var transaction = Db.BeginTransaction();
        ValidateDownloadConfigurations(context, configurations, transaction);
        var admission = InsertDownloadWorkspace(context, configurations, now, transaction);
        Execute("INSERT INTO download_admissions VALUES($id,NULL,'[]',$t,NULL,NULL,$p)", transaction,
            ("$id", admission), ("$t", now), ("$p", Serialize(problem)));
        token.ThrowIfCancellationRequested(); transaction.Commit();
        return new WorkspaceSnapshot(DownloadBuild(context, isLocal: false), now, DownloadProfile, "partial", "configurationsVerified", "notPrepared", "unknown",
            "onDemand", null, null, context.Reference, configurations, null, null, null, problem);
    }, token);

    internal Task<StoredDownloadAdmission> ReadDownloadAdmissionAsync(DownloadReadRequest request, CancellationToken token) => Schedule(() =>
    {
        WorkspaceService.Validate(new(request.ProductId, request.BuildIdentity, request.SourceRegion));
        if (!DownloadHash(request.LedgerId, 64) || request.Limit is < 1 or > 100 || request.After is not null && !CanonicalOrdinal(request.After))
            throw new DiscoveryException("INVALID_REQUEST", "The download ledger identity or page bounds are invalid.");
        using var command = Command("""
            SELECT d.verified_keys,d.assessed_at,d.summary,length(d.verified_keys),length(d.summary)
            FROM download_admissions d JOIN workspace_admissions a ON a.id=d.admission_id
            WHERE a.product=$p AND a.build_id=$b AND a.region=$r AND a.profile=$profile AND d.artifact_sha256=$h
            ORDER BY a.id DESC LIMIT 1
            """, null, ("$p", request.ProductId), ("$b", request.BuildIdentity), ("$r", request.SourceRegion),
            ("$profile", DownloadProfile), ("$h", request.LedgerId));
        string keysJson, assessedAt, summaryJson;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new DiscoveryException("DOWNLOAD_NOT_READY", "This exact build has no admitted scoped download ledger.");
            if (reader.GetInt64(3) > 400_000 || reader.IsDBNull(4) || reader.GetInt64(4) > 8192)
                throw DownloadIntegrity("A saved download assessment exceeds its bounded projection.");
            keysJson = reader.GetString(0); assessedAt = reader.GetString(1); summaryJson = reader.GetString(2);
        }
        var artifact = ReadDownloadArtifact(request.LedgerId, null) ?? throw DownloadIntegrity("An admitted download ledger descriptor is missing.");
        var keys = Deserialize<string[]>(keysJson); var summary = Deserialize<DownloadSummary>(summaryJson);
        if (keys.Length > MaxDownloadAssessmentKeys || keys.Length > artifact.ObjectCount ||
            keys.Distinct(StringComparer.Ordinal).Count() != keys.Length || keys.Any(key => !DownloadHash(key, 32)) || !DownloadUtc(assessedAt) ||
            summary.LedgerId != artifact.Sha256 || summary.Scope != DownloadScope || summary.AssessedAt != assessedAt ||
            summary.ObjectCount != N(artifact.ObjectCount) || summary.TotalEncodedBytes != N(artifact.TotalEncodedBytes) ||
            checked(DownloadNumber(summary.VerifiedEncodedBytes) + DownloadNumber(summary.RemainingEncodedBytes)) != artifact.TotalEncodedBytes)
            throw DownloadIntegrity("An admitted download assessment is inconsistent.");
        var assessment = DownloadAssessment(keys, null, token);
        if (DownloadNumber(summary.VerifiedEncodedBytes) != assessment.Bytes)
            throw DownloadIntegrity("A download assessment differs from its immutable object receipt lengths.");
        if (request.After is not null && DownloadNumber(request.After) > artifact.ObjectCount)
            throw new DiscoveryException("INVALID_CURSOR", "The download cursor exceeds its immutable ledger.");
        return new StoredDownloadAdmission(artifact, Array.AsReadOnly(keys), assessedAt, assessment.Lengths);
    }, token);

    // Kept as an internal compatibility name; these are physical encoded objects, not invented metadata roles.
    internal Task<IReadOnlyList<RetainedEncodedObject>> ReadRetainedMetadataAsync(CancellationToken token) =>
        Schedule(() => ReadRetainedEncodedObjects(null, null, token), token);

    private IReadOnlyList<RetainedEncodedObject> ReadRetainedEncodedObjects(IReadOnlyList<string>? keys,
        SqliteTransaction? transaction, CancellationToken token)
    {
        var result = new Dictionary<string, RetainedEncodedObject>(StringComparer.Ordinal);
        var filter = keys is null ? "" : "WHERE ekey IN (SELECT value FROM json_each($keys))";
        using var command = Command($"""
            SELECT kind,ekey,ckey,encoded,decoded,sha256,receipt,receipt_bytes FROM (
                SELECT 0 kind,ekey,ckey,encoded,NULL decoded,NULL sha256,receipt,length(receipt) receipt_bytes FROM metadata_objects
                {filter}
                UNION ALL
                SELECT 1 kind,ekey,ckey,encoded_bytes,decoded_bytes,sha256,receipt,length(receipt) receipt_bytes FROM content_objects
                {filter})
            ORDER BY ekey,kind LIMIT 20001
            """, transaction, ("$keys", keys is null ? "[]" : Serialize(keys)));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (reader.GetInt64(7) > (reader.GetInt32(0) == 0 ? 16_384 : 32_768)) throw DownloadIntegrity("A retained receipt exceeds its bounded source projection.");
            RetainedEncodedObject item;
            if (reader.GetInt32(0) == 0)
            {
                var receipt = Deserialize<MetadataReceipt>(reader.GetString(6)); ValidateDownloadReceiptFacts(receipt);
                item = new(receipt.EncodingKey, receipt.ContentKey, receipt.Sha256, DownloadNumber(receipt.EncodedBytes), DownloadNumber(receipt.DecodedBytes));
            }
            else
            {
                var receipt = Deserialize<ContentReceipt>(reader.GetString(6)); ValidateContentReceipt(receipt);
                item = new(receipt.EncodingKey, receipt.ContentKey, receipt.Sha256, DownloadNumber(receipt.EncodedBytes), DownloadNumber(receipt.DecodedBytes), receipt.Projection);
                if (item.DecodedBytes != reader.GetInt64(4) || item.Sha256 != reader.GetString(5))
                    throw DownloadIntegrity("A retained content alias differs from its original source proof.");
            }
            if (item.Key != reader.GetString(1) || item.ContentKey != reader.GetString(2) || item.EncodedBytes != reader.GetInt64(3))
                throw DownloadIntegrity("A retained object alias differs from its original exact-object receipt.");
            if (result.TryGetValue(item.Key, out var previous))
            {
                if (previous with { Projection = null } != item with { Projection = null } ||
                    previous.Projection?.IdentitySha256 != item.Projection?.IdentitySha256) throw DownloadIntegrity("Retained aliases disagree on one exact encoded object's content, digest or lengths.");
            }
            else
            {
                if (result.Count >= MaxDownloadAssessmentKeys)
                    throw new DiscoveryException("DOWNLOAD_ASSESSMENT_LIMIT", "The combined retained-object assessment exceeds its current finite bound.");
                result.Add(item.Key, item);
            }
        }
        return Array.AsReadOnly(result.Values.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray());
    }

    private DownloadLedgerArtifact? ReadDownloadArtifact(string sha256, SqliteTransaction? transaction)
    {
        using var command = Command("SELECT byte_length,object_count,total_encoded_bytes,ledger_revision FROM download_artifacts WHERE sha256=$h", transaction, ("$h", sha256));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        if (reader.GetString(3) != DownloadLedger.Revision) throw DownloadIntegrity("A saved download ledger revision is unsupported.");
        var artifact = new DownloadLedgerArtifact(sha256, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        ValidateDownloadArtifact(artifact); return artifact;
    }
    private static void ValidateDownloadArtifact(DownloadLedgerArtifact artifact)
    {
        if (!DownloadHash(artifact.Sha256, 64) || artifact.ObjectCount is < 0 or > 10_000_000 || artifact.ByteLength is < 32 or > (512L << 20) ||
            artifact.ByteLength != checked(32 + 24 * artifact.ObjectCount) || artifact.TotalEncodedBytes < 0 || artifact.ObjectCount == 0 && artifact.TotalEncodedBytes != 0)
            throw DownloadIntegrity("A download ledger descriptor is malformed or exceeds its admitted bound.");
    }
    private static void ValidateResolverIdentity(string sourceSha256, string role, string parserRevision)
    {
        if (!DownloadHash(sourceSha256, 64) || role is not ("encoding" or "root") || string.IsNullOrEmpty(parserRevision) ||
            parserRevision.Length > 128 || parserRevision.Any(ch => ch is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
            throw DownloadIntegrity("A resolver source identity or interpretation revision is unsupported.");
    }
    private static void ValidateResolverArtifact(ResolverArtifact artifact)
    {
        ValidateDownloadReceiptFacts(artifact.Source);
        ValidateResolverIdentity(artifact.Source.Sha256, artifact.Source.Role, artifact.ParserRevision);
        if (!DownloadHash(artifact.DecodedSha256, 64) || artifact.DecodedBytes is < 1 or > (1L << 30) ||
            artifact.DecodedBytes != DownloadNumber(artifact.Source.DecodedBytes) ||
            artifact.Source.ParserRevision != MetadataAcquirer.SourceRevision && artifact.Source.ParserRevision != artifact.Source.Role + "-v1")
            throw DownloadIntegrity("A resolver artifact has no supported complete decoded-source proof.");
    }
    private static void ValidateDownloadSource(MetadataReceipt source, bool completed)
    {
        ValidateDownloadReceiptFacts(source);
        if (source.Role != "download" || source.ParserRevision != DownloadParserRevision && (completed || source.ParserRevision != MetadataAcquirer.SourceRevision) ||
            !DownloadHash(source.ContentKey, 32) || !DownloadHash(source.EncodingKey, 32) || !DownloadHash(source.Sha256, 64) ||
            DownloadNumber(source.EncodedBytes) is < 9 or > (512L << 20) || DownloadNumber(source.DecodedBytes) is < 1 or > (1L << 30))
            throw DownloadIntegrity("A download source has no supported exact verification and grammar proof.");
    }
    private void ValidateDownloadSourceAssociation(MetadataReceipt receipt, SqliteTransaction? transaction)
    {
        ValidateDownloadReceiptFacts(receipt);
        var value = Scalar("SELECT receipt FROM metadata_objects WHERE ekey=$e", transaction, ("$e", receipt.EncodingKey)) as string;
        if (value is null || !SameDownloadSource(Deserialize<MetadataReceipt>(value), receipt))
            throw DownloadIntegrity("A metadata source differs from its immutable retained object receipt.");
    }
    private (long Bytes, IReadOnlyDictionary<string, long> Lengths) DownloadAssessment(IReadOnlyList<string> keys, SqliteTransaction? transaction, CancellationToken token)
    {
        if (keys.Count > MaxDownloadAssessmentKeys || keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
            throw DownloadIntegrity("A retained assessment has invalid or duplicate encoded identities.");
        var objects = ReadRetainedEncodedObjects(keys, transaction, token);
        if (objects.Count != keys.Count) throw DownloadIntegrity("A verified retained key has no durable exact-object receipt.");
        long bytes = 0; var lengths = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in objects) { token.ThrowIfCancellationRequested(); bytes = checked(bytes + item.EncodedBytes); lengths.Add(item.Key, item.EncodedBytes); }
        return (bytes, new ReadOnlyDictionary<string, long>(lengths));
    }
    private static void ValidateDownloadReceiptFacts(MetadataReceipt receipt)
    {
        if (receipt.Role is not ("encoding" or "root" or "install" or "download" or "size") ||
            !DownloadHash(receipt.ContentKey, 32) || !DownloadHash(receipt.EncodingKey, 32) || !DownloadHash(receipt.Sha256, 64) ||
            receipt.ContentKey.All(ch => ch == '0') || receipt.EncodingKey.All(ch => ch == '0') ||
            DownloadNumber(receipt.EncodedBytes) is < 9 or > (512L << 20) || DownloadNumber(receipt.DecodedBytes) is < 1 or > (1L << 30) ||
            receipt.ParserRevision != MetadataAcquirer.SourceRevision && receipt.ParserRevision != receipt.Role + "-v1" ||
            string.IsNullOrEmpty(receipt.SourceUrl) || receipt.SourceUrl.Length > 2048 || !Uri.TryCreate(receipt.SourceUrl, UriKind.Absolute, out var url) ||
            url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            !(url.Host is "level3.blizzard.com" or "level3.ssl.blizzard.com" || Regex.IsMatch(url.Host, "^[a-z]{2,8}\\.cdn\\.blizzard\\.com$", RegexOptions.CultureInvariant)) ||
            !DownloadUtc(receipt.RetrievedAt) || !DownloadUtc(receipt.VerifiedAt))
            throw DownloadIntegrity("A metadata source has incomplete original verification facts.");
        var location = MetadataSourceLocation(receipt);
        var key = location.Kind == "archive" ? location.ArchiveKey : receipt.EncodingKey;
        var offset = DownloadNumber(location.Offset); var sourceBytes = DownloadNumber(location.SourceBytes);
        var encodedBytes = DownloadNumber(receipt.EncodedBytes);
        if (location.Kind is not ("loose" or "archive") || !DownloadHash(key!, 32) || key!.All(ch => ch == '0') ||
            sourceBytes < encodedBytes || offset > sourceBytes - encodedBytes ||
            location.Kind == "loose" && (location.ArchiveKey is not null || offset != 0 || location.SourceBytes != receipt.EncodedBytes) ||
            !Regex.IsMatch(url.AbsolutePath, "^/tpr/[A-Za-z0-9_-]{1,64}/data/" + key![..2] + "/" + key[2..4] + "/" + key + "$", RegexOptions.CultureInvariant))
            throw DownloadIntegrity("A metadata source has an invalid exact loose/archive source locator.");
    }
    private static ContentLocation MetadataSourceLocation(MetadataReceipt receipt) => receipt.Location ?? new("loose", "0", receipt.EncodedBytes, null);
    private static bool DownloadUtc(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind, out var parsed) && parsed.Offset == TimeSpan.Zero;
    private static bool SameDownloadSource(MetadataReceipt first, MetadataReceipt second) =>
        first.Role == second.Role && first.ContentKey == second.ContentKey && first.EncodingKey == second.EncodingKey && first.Sha256 == second.Sha256 &&
        first.EncodedBytes == second.EncodedBytes && first.DecodedBytes == second.DecodedBytes && first.SourceUrl == second.SourceUrl && first.RetrievedAt == second.RetrievedAt &&
        MetadataSourceLocation(first) == MetadataSourceLocation(second);
    private void ValidateDownloadConfigurations(CapturedOpeningContext context, IReadOnlyList<ConfigurationReceipt> configurations, SqliteTransaction transaction)
    {
        if (configurations.Count != 2 || configurations.Count(value => value.Role == "buildConfig" && value.Key == context.Build.BuildConfig) != 1 ||
            configurations.Count(value => value.Role == "cdnConfig" && value.Key == context.Build.CdnConfig) != 1 || context.Reference.Region.Length == 0 ||
            context.Reference.VersionsSha256 != context.Versions.Sha256 || context.Reference.CdnsSha256 != context.Cdns.Sha256)
            throw DownloadIntegrity("Both exact verified configurations and coherent pinned source receipts are required.");
        foreach (var receipt in configurations)
        {
            using var command = Command("SELECT sha256,byte_length,source_url,retrieved_at FROM configuration_objects WHERE key=$key", transaction, ("$key", receipt.Key));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != receipt.Sha256 || reader.GetInt32(1) != receipt.ByteLength || reader.GetString(2) != receipt.SourceUrl ||
                reader.GetString(3) != receipt.RetrievedAt || receipt.ParsedFieldCount is < 0 or > 4096)
                throw DownloadIntegrity("Configuration proofs changed before scoped Home admission.");
        }
    }
    private long InsertDownloadWorkspace(CapturedOpeningContext context, IReadOnlyList<ConfigurationReceipt> configurations, string now, SqliteTransaction transaction)
    {
        Execute("INSERT INTO workspace_admissions(product,build_id,region,profile,source_reference,opened_at) VALUES($p,$b,$r,$profile,$s,$t)", transaction,
            ("$p", context.Build.ProductId), ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$profile", DownloadProfile), ("$s", Serialize(context.Reference)), ("$t", now));
        var id = Convert.ToInt64(Scalar("SELECT last_insert_rowid()", transaction));
        foreach (var receipt in configurations)
            Execute("INSERT INTO workspace_configurations VALUES($id,$role,$key,$h,$b,$url,$retrieved,$verified,$count)", transaction,
                ("$id", id), ("$role", receipt.Role), ("$key", receipt.Key), ("$h", receipt.Sha256), ("$b", receipt.ByteLength),
                ("$url", receipt.SourceUrl), ("$retrieved", receipt.RetrievedAt), ("$verified", receipt.VerifiedAt), ("$count", receipt.ParsedFieldCount));
        Execute("INSERT INTO open_acknowledgements VALUES($b,$t,$id) ON CONFLICT(build_id) DO UPDATE SET opened_at=$t,admission_id=$id", transaction,
            ("$b", context.Build.Id), ("$t", now), ("$id", id));
        return id;
    }
    private Build DownloadBuild(CapturedOpeningContext context, bool isLocal)
    {
        var build = ReadCore(context.Build.ProductId, "all", 1, null, CancellationToken.None, context.Build.Id).Builds.Single();
        var reference = context.Reference;
        // The admission keeps the CDN configuration it was made with, even if a newer one is advertised by now.
        return build with { CdnConfig = context.Build.CdnConfig, IsLocal = isLocal, SourceReceipts = build.SourceReceipts.Where(receipt => receipt.Region != reference.Region).Concat(new[]
        {
            new SourceReceipt(reference.Region, "versions", context.Versions.Url, context.Versions.RetrievedAt, context.Versions.Sha256, context.Versions.Bytes.Length, reference.VersionsRowOrdinal),
            new SourceReceipt(reference.Region, "cdns", context.Cdns.Url, context.Cdns.RetrievedAt, context.Cdns.Sha256, context.Cdns.Bytes.Length, reference.CdnsRowOrdinal)
        }).ToArray() };
    }
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static bool DownloadHash(string value, int length) => value is not null && value.Length == length && value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool CanonicalOrdinal(string value) => value is not null && value.Length > 0 && (value == "0" || value[0] is >= '1' and <= '9') &&
        value.All(ch => ch is >= '0' and <= '9') && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    private static long DownloadNumber(string value) => CanonicalOrdinal(value) ? long.Parse(value, CultureInfo.InvariantCulture) :
        throw DownloadIntegrity("A scoped download size or count is not a bounded canonical integer.");
    private static DiscoveryException DownloadIntegrity(string message) => new("DOWNLOAD_CATALOG_INTEGRITY", message);
}
