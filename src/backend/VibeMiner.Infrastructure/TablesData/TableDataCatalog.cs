using VibeMiner.Application;
using System.Globalization;
using System.Text.RegularExpressions;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

internal sealed record StoredTableAdmission(TableRowsArtifact Artifact, ContentReceipt Source,
    SelectedFileAssociation Association, string PreparedAt);

public sealed partial class DiscoveryCatalog
{
    private void UpgradeTablesData()
    {
        using var transaction = Db.BeginTransaction();
        Execute("""
            CREATE TABLE content_locators(ekey TEXT NOT NULL,cdn_configuration_sha256 TEXT NOT NULL,descriptor TEXT NOT NULL,
                PRIMARY KEY(ekey,cdn_configuration_sha256));
            CREATE TABLE content_objects(ekey TEXT PRIMARY KEY,ckey TEXT NOT NULL,sha256 TEXT NOT NULL,
                encoded_bytes INTEGER NOT NULL,decoded_bytes INTEGER NOT NULL,receipt TEXT NOT NULL);
            CREATE TABLE table_artifacts(content_key TEXT PRIMARY KEY,descriptor TEXT NOT NULL);
            CREATE TABLE table_admissions(build_id TEXT NOT NULL,region TEXT NOT NULL,source_reference TEXT NOT NULL,
                table_id TEXT NOT NULL,locale TEXT NOT NULL,content_key TEXT NOT NULL,source TEXT NOT NULL,
                association TEXT NOT NULL,prepared_at TEXT NOT NULL,
                PRIMARY KEY(build_id,region,source_reference,table_id,locale),
                FOREIGN KEY(build_id) REFERENCES builds(id),FOREIGN KEY(content_key) REFERENCES table_artifacts(content_key));
            PRAGMA user_version=7;
            """, transaction);
        transaction.Commit();
    }

    internal Task<ContentLocatorArtifact?> FindContentLocatorAsync(string encodingKey, string cdnConfigurationSha256, CancellationToken token) => Schedule(() =>
    {
        if (!DownloadHash(encodingKey, 32) || !DownloadHash(cdnConfigurationSha256, 64))
            throw new DiscoveryException("INVALID_REQUEST", "Choose one exact encoded object and CDN configuration identity.");
        var saved = Scalar("SELECT descriptor FROM content_locators WHERE ekey=$e AND cdn_configuration_sha256=$c", null,
            ("$e", encodingKey), ("$c", cdnConfigurationSha256)) as string;
        if (saved is null) return null;
        var artifact = Deserialize<ContentLocatorArtifact>(saved); ValidateContentLocator(artifact);
        if (artifact.EncodingKey != encodingKey || artifact.CdnConfigurationSha256 != cdnConfigurationSha256)
            throw TableIntegrity("A saved archive locator has a conflicting exact source identity.");
        return artifact;
    }, token);
    internal Task RecordContentLocatorAsync(ContentLocatorArtifact artifact, CancellationToken token) => Schedule(() =>
    {
        ValidateContentLocator(artifact); using var transaction = Db.BeginTransaction(); var descriptor = Serialize(artifact);
        var saved = Scalar("SELECT descriptor FROM content_locators WHERE ekey=$e AND cdn_configuration_sha256=$c", transaction,
            ("$e", artifact.EncodingKey), ("$c", artifact.CdnConfigurationSha256)) as string;
        if (saved is not null && saved != descriptor) throw TableIntegrity("An exact source locator cannot be reassociated with different authenticated proof bytes.");
        Execute("INSERT OR IGNORE INTO content_locators VALUES($e,$c,$d)", transaction,
            ("$e", artifact.EncodingKey), ("$c", artifact.CdnConfigurationSha256), ("$d", descriptor));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);
    private static void ValidateContentLocator(ContentLocatorArtifact artifact)
    {
        if (!DownloadHash(artifact.EncodingKey, 32) || !DownloadHash(artifact.CdnConfigurationSha256, 64) ||
            !DownloadHash(artifact.ArchiveKey, 32) || !DownloadHash(artifact.Sha256, 64) || artifact.ByteLength is < 1 or > (8L << 20))
            throw TableIntegrity("An archive locator has invalid immutable source proof geometry.");
    }

    internal Task<ContentReceipt?> FindContentAsync(string encodingKey, CancellationToken token) => Schedule(() =>
    {
        if (!DownloadHash(encodingKey, 32)) throw new DiscoveryException("INVALID_REQUEST", "Choose one exact encoded content identity.");
        using var command = Command("SELECT ckey,sha256,encoded_bytes,decoded_bytes,receipt FROM content_objects WHERE ekey=$k", null, ("$k", encodingKey));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        var receipt = Deserialize<ContentReceipt>(reader.GetString(4)); ValidateContentReceipt(receipt);
        if (receipt.EncodingKey != encodingKey || receipt.ContentKey != reader.GetString(0) || receipt.Sha256 != reader.GetString(1) ||
            DownloadNumber(receipt.EncodedBytes) != reader.GetInt64(2) || DownloadNumber(receipt.DecodedBytes) != reader.GetInt64(3))
            throw TableIntegrity("The saved encoded content alias differs from its source receipt.");
        return receipt;
    }, token);

    internal Task RecordContentAsync(ContentReceipt receipt, CancellationToken token) => Schedule(() =>
    {
        ValidateContentReceipt(receipt);
        using var transaction = Db.BeginTransaction();
        using (var command = Command("SELECT receipt FROM content_objects WHERE ekey=$k", transaction, ("$k", receipt.EncodingKey)))
        using (var reader = command.ExecuteReader())
            if (reader.Read())
            {
                var original = Deserialize<ContentReceipt>(reader.GetString(0)); ValidateContentReceipt(original);
                if (!SameContentBytes(original, receipt)) throw TableIntegrity("One exact encoded identity cannot be associated with conflicting verified bytes.");
            }
        Execute("INSERT OR IGNORE INTO content_objects VALUES($e,$c,$h,$n,$d,$r)", transaction,
            ("$e", receipt.EncodingKey), ("$c", receipt.ContentKey), ("$h", receipt.Sha256),
            ("$n", DownloadNumber(receipt.EncodedBytes)), ("$d", DownloadNumber(receipt.DecodedBytes)), ("$r", Serialize(receipt)));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    internal Task<TableRowsArtifact?> FindTableArtifactAsync(string contentKey, CancellationToken token) => Schedule(() =>
    {
        if (!DownloadHash(contentKey, 64)) throw new DiscoveryException("INVALID_REQUEST", "Choose one exact parsed table artifact identity.");
        var value = Scalar("SELECT descriptor FROM table_artifacts WHERE content_key=$k", null, ("$k", contentKey)) as string;
        if (value is null) return null;
        var result = Deserialize<TableRowsArtifact>(value); ValidateTableArtifact(result);
        if (result.ContentKey != contentKey) throw TableIntegrity("A parsed table artifact has a conflicting interpretation key.");
        return result;
    }, token);

    internal Task RecordTableArtifactAsync(TableRowsArtifact artifact, CancellationToken token) => Schedule(() =>
    {
        ValidateTableArtifact(artifact); using var transaction = Db.BeginTransaction(); var encoded = Serialize(artifact);
        var previous = Scalar("SELECT descriptor FROM table_artifacts WHERE content_key=$k", transaction, ("$k", artifact.ContentKey)) as string;
        if (previous is not null && previous != encoded) throw TableIntegrity("A parsed table interpretation cannot be reassociated with a different immutable row artifact.");
        Execute("INSERT OR IGNORE INTO table_artifacts VALUES($k,$d)", transaction, ("$k", artifact.ContentKey), ("$d", encoded));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    internal Task CommitTableAdmissionAsync(CapturedOpeningContext context, string tableId, string locale,
        ContentReceipt source, SelectedFileAssociation association, TableRowsArtifact artifact, string preparedAt,
        CancellationToken token) => Schedule(() =>
    {
        ValidateTableAdmission(tableId, locale, source, association, artifact, preparedAt);
        using var transaction = Db.BeginTransaction();
        RequireTableWorkspace(context, transaction);
        ValidateTableSourceAssociation(source, association, transaction);
        var descriptor = Scalar("SELECT descriptor FROM table_artifacts WHERE content_key=$k", transaction, ("$k", artifact.ContentKey)) as string;
        if (descriptor != Serialize(artifact)) throw TableIntegrity("Complete parsed table rows must be registered before admission.");
        Execute("""
            INSERT INTO table_admissions VALUES($b,$r,$ref,$t,$l,$k,$s,$a,$at)
            ON CONFLICT(build_id,region,source_reference,table_id,locale) DO UPDATE SET
                content_key=excluded.content_key,source=excluded.source,association=excluded.association,prepared_at=excluded.prepared_at
            """, transaction, ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$ref", Serialize(context.Reference)),
            ("$t", tableId), ("$l", locale), ("$k", artifact.ContentKey), ("$s", Serialize(source)), ("$a", Serialize(association)), ("$at", preparedAt));
        token.ThrowIfCancellationRequested(); transaction.Commit(); return true;
    }, token);

    internal Task<StoredTableAdmission?> ReadTableAdmissionAsync(CapturedOpeningContext context, string tableId,
        string locale, CancellationToken token) => Schedule(() =>
    {
        RequireTableWorkspace(context, null);
        using var command = Command("""
            SELECT a.source,a.association,a.prepared_at,r.descriptor FROM table_admissions a
            JOIN table_artifacts r ON r.content_key=a.content_key
            WHERE a.build_id=$b AND a.region=$r AND a.source_reference=$ref AND a.table_id=$t AND a.locale=$l
            """, null, ("$b", context.Build.Id), ("$r", context.Reference.Region), ("$ref", Serialize(context.Reference)), ("$t", tableId), ("$l", locale));
        using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
        var result = new StoredTableAdmission(Deserialize<TableRowsArtifact>(reader.GetString(3)), Deserialize<ContentReceipt>(reader.GetString(0)),
            Deserialize<SelectedFileAssociation>(reader.GetString(1)), reader.GetString(2));
        reader.Close();
        ValidateTableAdmission(tableId, locale, result.Source, result.Association, result.Artifact, result.PreparedAt, requireCurrentRevision: false);
        ValidateTableSourceAssociation(result.Source, result.Association, null);
        return result;
    }, token);

    private void RequireTableWorkspace(CapturedOpeningContext context, Microsoft.Data.Sqlite.SqliteTransaction? transaction)
    {
        var admitted = Scalar("""
            SELECT 1 FROM workspace_admissions a
            JOIN workspace_configurations b ON b.admission_id=a.id AND b.role='buildConfig' AND b.key=$bc
            JOIN workspace_configurations c ON c.admission_id=a.id AND c.role='cdnConfig' AND c.key=$cc
            WHERE a.build_id=$b AND a.product=$p AND a.region=$r LIMIT 1
            """, transaction, ("$bc", context.Build.BuildConfig), ("$cc", context.Build.CdnConfig),
            ("$b", context.Build.Id), ("$p", context.Build.ProductId), ("$r", context.Reference.Region));
        if (admitted is null) throw new DiscoveryException("WORKSPACE_NOT_OPEN", "Open this exact build before loading its table records.");
    }
    private static void ValidateTableAdmission(string tableId, string locale, ContentReceipt source,
        SelectedFileAssociation association, TableRowsArtifact artifact, string preparedAt, bool requireCurrentRevision = true)
    {
        ValidateContentReceipt(source); ValidateTableArtifact(artifact);
        if (!DownloadHash(tableId, 8) || !string.Equals(artifact.Header.TableHash, tableId, StringComparison.OrdinalIgnoreCase) ||
            locale != "enUS" || locale != artifact.Binding.Locale || locale != association.ResearchLocale || artifact.Binding.SourceSha256 != source.Sha256 ||
            artifact.Completion.ConsumedBytes != DownloadNumber(source.DecodedBytes) ||
            !DownloadUtc(preparedAt) || !uint.TryParse(association.FileDataId, NumberStyles.None, CultureInfo.InvariantCulture, out var fdid) ||
            fdid == 0 || fdid.ToString(CultureInfo.InvariantCulture) != association.FileDataId ||
            association.VariantPolicy != "enus-consistent-root-variants-v1" ||
            association.EncodingSource.Role != "encoding" || association.RootSource.Role != "root" || association.Memberships.Count is < 1 or > 256)
            throw TableIntegrity("A table admission differs from its exact table, locale or verified source binding.");
        if (requireCurrentRevision && TableDataStore.IsItemEffectAdmission(tableId, artifact.Binding.DefinitionSha256, artifact.Binding.ProviderRevision) &&
            artifact.Binding.DecoderRevision != TableDataStore.AdmissionRevision(tableId, artifact.Binding.DefinitionSha256,
                artifact.Binding.ProviderRevision, source.Projection is not null))
            throw TableIntegrity("A new ItemEffect admission requires its current scoped interpretation revision.");
        if (source.Projection is { } projection && (artifact.Completion.Scope != "unencryptedSections" ||
            artifact.Binding.DecodedSha256 != projection.IdentitySha256 || !SupportedPartialAdmissionRevision(artifact, requireCurrentRevision) || artifact.Completion.ExcludedEncryptedSections < 1))
            throw TableIntegrity("Partial source rows require an explicit coverage-bound partial table interpretation.");
        if (source.Projection is null && (artifact.Binding.DecoderRevision.EndsWith("+table-admission-projection-v1", StringComparison.Ordinal) ||
            artifact.Binding.DecoderRevision.EndsWith("+table-admission-projection-v2", StringComparison.Ordinal) ||
            artifact.Binding.DecoderRevision.EndsWith("+table-admission-projection-v3", StringComparison.Ordinal) ||
            artifact.Binding.DecoderRevision.EndsWith("+table-admission-projection-v4", StringComparison.Ordinal)))
            throw TableIntegrity("A fully decoded source cannot be admitted using a partial-projection table revision.");
        var seen = new HashSet<(string, string)>();
        foreach (var m in association.Memberships)
            if (m.ContentKey != source.ContentKey || !seen.Add((m.BlockOrdinal, m.EntryOrdinal)) ||
                DownloadNumber(m.BlockOrdinal) < 0 || DownloadNumber(m.EntryOrdinal) < 0 ||
                !CanonicalUInt(m.LocaleFlags) || (uint.Parse(m.LocaleFlags, CultureInfo.InvariantCulture) & 2) == 0 || !CanonicalUInt(m.ContentFlags) || !CanonicalUInt(m.RawFirst) ||
                !CanonicalUInt(m.RawSecond) || !CanonicalUInt(m.RawThird) || uint.Parse(m.RawThird, CultureInfo.InvariantCulture) > 255 || m.NameHash is not null &&
                (!ulong.TryParse(m.NameHash, NumberStyles.None, CultureInfo.InvariantCulture, out var hash) || hash.ToString(CultureInfo.InvariantCulture) != m.NameHash))
                throw TableIntegrity("A table admission has invalid or conflicting selected ROOT memberships.");
    }
    // A historical interpretation may be stale without invalidating its authenticated source proof.
    // Store eligibility checks decide whether to rebuild it; new admissions always use the current reader.
    private static bool SupportedPartialAdmissionRevision(TableRowsArtifact artifact, bool requireCurrentRevision) =>
        artifact.Binding.DecoderRevision == TableDataStore.AdmissionRevision(artifact.Header.TableHash, artifact.Binding.DefinitionSha256,
            artifact.Binding.ProviderRevision, projected: true) || (!requireCurrentRevision && artifact.Binding.DecoderRevision is
            ("wdc5-fixed-values-v3+table-admission-projection-v1" or "wdc5-values-v4+table-admission-projection-v1" or "wdc5-values-v5+table-admission-projection-v1" or "wdc5-values-v6+table-admission-projection-v1" or "wdc5-values-v7+table-admission-projection-v2" or TableDataStore.ProjectionDecoderRevision));

    private void ValidateTableSourceAssociation(ContentReceipt source, SelectedFileAssociation association, Microsoft.Data.Sqlite.SqliteTransaction? transaction)
    {
        var saved = Scalar("SELECT receipt FROM content_objects WHERE ekey=$k", transaction, ("$k", source.EncodingKey)) as string;
        if (saved is null) throw TableIntegrity("A table source has no matching verified encoded object publication.");
        var original = Deserialize<ContentReceipt>(saved); ValidateContentReceipt(original);
        if (!SameContentBytes(original, source))
            throw TableIntegrity("A table source has no matching verified encoded object publication.");
        ValidateDownloadSourceAssociation(association.EncodingSource, transaction);
        ValidateDownloadSourceAssociation(association.RootSource, transaction);
    }
    private static bool CanonicalUInt(string value) => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n.ToString(CultureInfo.InvariantCulture) == value;

    private static void ValidateTableArtifact(TableRowsArtifact artifact)
    {
        if (artifact.Binding.DecoderRevision is TableDataStore.ItemEffectDecoderRevision or TableDataStore.ItemEffectProjectionDecoderRevision &&
            !TableDataStore.IsItemEffectAdmission(artifact.Header.TableHash, artifact.Binding.DefinitionSha256, artifact.Binding.ProviderRevision))
            throw TableIntegrity("The scoped ItemEffect interpretation cannot identify another table or provider definition.");
        if (artifact.ContentKey != TableRowArtifacts.ContentKey(artifact.Binding) || !DownloadHash(artifact.Sha256, 64) ||
            artifact.ByteLength is < 64 or > VerifiedObjectStore.MaxObjectBytes || artifact.Header.Columns.Count is < 1 or > 512 ||
            artifact.Completion.PhysicalRows < 0 || artifact.Completion.CopyRows < 0 ||
            artifact.Completion.PhysicalRows > TableRowArtifacts.MaxRows - artifact.Completion.CopyRows ||
            artifact.Completion.Scope is not ("complete" or "unencryptedSections") || artifact.Completion.TextIssueCount < 0 ||
            artifact.Completion.TextIssueCount > (artifact.Completion.PhysicalRows + artifact.Completion.CopyRows) * artifact.Header.Columns.Count)
            throw TableIntegrity("A parsed table artifact has invalid bounded geometry.");
    }
    private static bool SameContentBytes(ContentReceipt a, ContentReceipt b) => a.ContentKey == b.ContentKey && a.EncodingKey == b.EncodingKey &&
        a.Sha256 == b.Sha256 && a.EncodedBytes == b.EncodedBytes && a.DecodedBytes == b.DecodedBytes && a.VerifierRevision == b.VerifierRevision &&
        a.Projection?.IdentitySha256 == b.Projection?.IdentitySha256;
    private static void ValidateContentReceipt(ContentReceipt receipt)
    {
        if (!DownloadHash(receipt.ContentKey, 32) || !DownloadHash(receipt.EncodingKey, 32) || !DownloadHash(receipt.Sha256, 64) ||
            receipt.ContentKey.All(c => c == '0') || receipt.EncodingKey.All(c => c == '0') ||
            DownloadNumber(receipt.EncodedBytes) is < 9 or > VerifiedObjectStore.MaxObjectBytes || DownloadNumber(receipt.DecodedBytes) is < 1 or > (256L << 20) ||
            receipt.VerifierRevision is not ("content-blte-v1" or "content-blte-projection-v1") ||
            !DownloadUtc(receipt.RetrievedAt) || !DownloadUtc(receipt.VerifiedAt) || receipt.SourceUrl is null || receipt.SourceUrl.Length > 2048 ||
            !Uri.TryCreate(receipt.SourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host is "level3.blizzard.com" or "level3.ssl.blizzard.com" || Regex.IsMatch(uri.Host, "^[a-z]{2,8}\\.cdn\\.blizzard\\.com$", RegexOptions.CultureInvariant)) ||
            receipt.Location is null || receipt.Location.Kind is not ("loose" or "archive") ||
            DownloadNumber(receipt.Location.Offset) < 0 || DownloadNumber(receipt.Location.SourceBytes) < DownloadNumber(receipt.EncodedBytes))
            throw TableIntegrity("A content receipt has incomplete source verification facts.");
        if (receipt.VerifierRevision == "content-blte-v1")
        {
            if (receipt.Projection is not null) throw TableIntegrity("A fully verified content receipt cannot claim a partial decoded projection.");
        }
        else if (receipt.Projection is null ||
            ContentProjectionIdentity.Compute(receipt.ContentKey, receipt.EncodingKey, receipt.DecodedBytes, receipt.Projection) != receipt.Projection.IdentitySha256)
            throw TableIntegrity("A partial source receipt differs from its canonical authenticated projection proof.");
        var key = receipt.Location.Kind == "archive" ? receipt.Location.ArchiveKey : receipt.EncodingKey;
        if (!DownloadHash(key!, 32) || receipt.Location.Kind == "loose" && (receipt.Location.ArchiveKey is not null || receipt.Location.Offset != "0" || receipt.Location.SourceBytes != receipt.EncodedBytes) ||
            !Regex.IsMatch(uri.AbsolutePath, "^/tpr/[A-Za-z0-9_-]{1,64}/data/" + key![..2] + "/" + key[2..4] + "/" + key + "$", RegexOptions.CultureInvariant) ||
            DownloadNumber(receipt.Location.Offset) > DownloadNumber(receipt.Location.SourceBytes) - DownloadNumber(receipt.EncodedBytes))
            throw TableIntegrity("A content receipt has an invalid bounded loose/archive source locator.");
    }
    private static DiscoveryException TableIntegrity(string message) => new("TABLE_STORAGE_INTEGRITY", message);
}
