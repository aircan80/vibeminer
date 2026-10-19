using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

public sealed partial class DiscoveryCatalog
{
    private RetentionPlan ReadRetentionPlan(RetentionTarget target, SqliteTransaction? transaction)
    {
        long admission; OpeningSourceReference reference; MetadataReceipt[] metadata;
        using (var command = Command("""
            SELECT a.id,a.source_reference,d.metadata,length(a.source_reference),length(d.metadata)
            FROM workspace_admissions a JOIN download_admissions d ON d.admission_id=a.id
            WHERE a.product=$p AND a.build_id=$b AND a.region=$r AND a.profile=$profile AND d.artifact_sha256=$l
            ORDER BY a.id DESC LIMIT 1
            """, transaction, ("$p", target.ProductId), ("$b", target.BuildIdentity), ("$r", target.SourceRegion), ("$profile", DownloadProfile), ("$l", target.LedgerId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) throw new DiscoveryException("DOWNLOAD_NOT_READY", "This exact build has no admitted scoped download ledger.");
            if (reader.GetInt64(3) is < 1 or > 2048 || reader.IsDBNull(4) || reader.GetInt64(4) is < 1 or > 32768)
                throw RetentionIntegrity("An admitted retention source exceeds its bounds.");
            admission = reader.GetInt64(0);
            try { reference = Deserialize<OpeningSourceReference>(reader.GetString(1)); metadata = Deserialize<MetadataReceipt[]>(reader.GetString(2)); }
            catch (JsonException) { throw RetentionIntegrity("An admitted retention source is malformed."); }
        }
        if (metadata.Length is < 1 or > 2 || metadata.Count(m => m.Role == "download") != 1)
            throw RetentionIntegrity("A retention admission lacks its exact download source.");
        var configurations = new List<ConfigurationReceipt>();
        using (var command = Command("SELECT role,key,sha256,byte_length,source_url,retrieved_at,verified_at,parsed_field_count FROM workspace_configurations WHERE admission_id=$a", transaction, ("$a", admission)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) configurations.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7)));
        var artifact = ReadDownloadArtifact(target.LedgerId, transaction) ?? throw RetentionIntegrity("The admitted download ledger is missing.");
        var plan = new RetentionPlan(target, reference, configurations.ToArray(), metadata.Single(m => m.Role == "download"),
            new(artifact.Sha256, artifact.ByteLength, artifact.ObjectCount, artifact.TotalEncodedBytes, DownloadLedger.Revision), DownloadScope, RetentionPolicyRevision);
        ValidateRetentionPlan(plan); ValidateRetentionPlanAssociation(plan, transaction);
        return plan;
    }

    private void ValidateRetentionPlanAssociation(RetentionPlan plan, SqliteTransaction? transaction)
    {
        ValidateRetentionPlan(plan);
        ValidateDownloadSourceAssociation(plan.DownloadSource, transaction);
        var artifact = ReadDownloadArtifact(plan.Target.LedgerId, transaction);
        if (artifact != new DownloadLedgerArtifact(plan.Ledger.Sha256, plan.Ledger.ByteLength, plan.Ledger.ObjectCount, plan.Ledger.TotalEncodedBytes))
            throw RetentionIntegrity("A pinned retention ledger has changed geometry.");
        using (var command = Command("SELECT receipt,artifact_sha256 FROM download_artifact_sources WHERE source_sha256=$s AND parser_revision=$p AND ledger_revision=$r", transaction,
            ("$s", plan.DownloadSource.Sha256), ("$p", DownloadParserRevision), ("$r", DownloadLedger.Revision)))
        using (var reader = command.ExecuteReader())
            if (!reader.Read() || reader.GetString(1) != plan.Target.LedgerId || !SameDownloadSource(Deserialize<MetadataReceipt>(reader.GetString(0)), plan.DownloadSource))
                throw RetentionIntegrity("A pinned retention ledger differs from its immutable parsed source.");
        var identity = Scalar("SELECT identity_fields FROM builds WHERE id=$b AND product=$p", transaction,
            ("$b", plan.Target.BuildIdentity), ("$p", plan.Target.ProductId)) as string;
        if (identity is null) throw RetentionIntegrity("A pinned retention build is missing.");
        var fields = Deserialize<Dictionary<string, string>>(identity);
        foreach (var receipt in plan.Configurations)
        {
            var field = receipt.Role == "buildConfig" ? "BuildConfig" : "CDNConfig";
            if (!fields.TryGetValue(field, out var key) || key != receipt.Key)
                throw RetentionIntegrity("Pinned configuration roles differ from the exact retained build.");
            using var command = Command("SELECT sha256,byte_length,source_url,retrieved_at,verifier_revision FROM configuration_objects WHERE key=$k", transaction, ("$k", receipt.Key));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != receipt.Sha256 || reader.GetInt32(1) != receipt.ByteLength || reader.GetString(2) != receipt.SourceUrl ||
                reader.GetString(3) != receipt.RetrievedAt || reader.GetString(4) != ConfigurationWorkspacePreparer.VerifierRevision)
                throw RetentionIntegrity("Pinned configuration evidence differs from its original verified receipt.");
        }
        var source = plan.SourceReference;
        using var binding = Command("""
            SELECT v.sha256,c.sha256 FROM observations o
            JOIN documents v ON v.generation=o.generation AND v.role='versions'
            JOIN documents c ON c.generation=o.generation AND c.role='cdns'
            WHERE o.generation=$g AND o.ordinal=$o AND o.build_id=$b AND o.region=$r AND o.cdns_ordinal=$c
            """, transaction, ("$g", DownloadNumber(source.Generation)), ("$o", source.VersionsRowOrdinal),
            ("$b", plan.Target.BuildIdentity), ("$r", plan.Target.SourceRegion), ("$c", source.CdnsRowOrdinal));
        using var proof = binding.ExecuteReader();
        if (!proof.Read() || proof.GetString(0) != source.VersionsSha256 || proof.GetString(1) != source.CdnsSha256)
            throw RetentionIntegrity("A pinned retention source differs from the retained discovery reference.");
    }

    private RetentionSnapshot RetentionSnapshotOf(RetentionJob job, SqliteTransaction? transaction)
    {
        ValidateRetentionPlanAssociation(job.Plan, transaction);
        var coverage = ReadRetentionCoverage(job.Plan.Ledger, transaction);
        int? position = null;
        if (job.State == RetentionState.Queued)
        {
            var ordered = ReadOccupiedRetentionJobs(transaction).Where(j => j.State == RetentionState.Queued).ToArray();
            var index = Array.FindIndex(ordered, j => j.JobId == job.JobId);
            if (index >= 0) position = index + 1;
        }
        return new(job.Plan.Target, job.JobId.Length == 0 ? null : job.JobId, job.Revision, job.Mode, job.Paused, job.State,
            job.Plan.Scope, job.Plan.Ledger.ObjectCount, job.Plan.Ledger.TotalEncodedBytes,
            new(coverage.Count, coverage.Bytes, coverage.At, job.PresenceState, job.AssessedAt,
                job.Present + job.Missing + job.Corrupt, job.Present, job.PresentBytes, job.Missing, job.Corrupt),
            job.Progress, job.Pending, job.WireBytes, job.Unresolved, job.Failed, position, job.Problem);
    }
}
