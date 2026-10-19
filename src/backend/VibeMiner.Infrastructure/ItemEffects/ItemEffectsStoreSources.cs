using System.Globalization;
using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

public sealed partial class ItemEffectsStore
{
    private sealed record SourceProbe(ItemEffectsSourceRole Role, StoredTableAdmission? Admission,
        TableDataSnapshot? CapturedSnapshot, ItemEffectsSource Public, bool CurrentProfile);
    private static readonly string[] EffectColumns = ["ID", "LegacySlotIndex", "TriggerType", "Charges", "CoolDownMSec", "CategoryCoolDownMSec",
        "SpellCategoryID", "SpellID", "ChrSpecializationID", "PlayerConditionID"];
    private static (string Table, string Fdid, string Layout, string Definition) Role(ItemEffectsSourceRole role) => role switch
    {
        ItemEffectsSourceRole.Bridge => ("00cb674f", "3177687", "96F083AD", "3c513c171fc9d4f6aab09147c86b8c5c2da1a573adf4b04904b20067d5e7d0eb"),
        ItemEffectsSourceRole.Effect => ("4002a5b1", "969941", "4CA77678", "7276994228541b94e821a3b7834497c7aa0c8e8f3d1cea83ac17a40f13c4148c"),
        ItemEffectsSourceRole.Name => ("46c66698", "1990283", "782EE721", "d48ae2eb0455a30451cadda1c63627b9ad4ad4e9c4e473cd3444def1981fa667"),
        _ => throw new DiscoveryException("INVALID_REQUEST", "Choose one exact effects source role.")
    };
    private static SourceProbe Unchecked(ItemEffectsSourceRole role) => new(role, null, null, new(role, ItemEffectsSourceState.NotChecked), false);
    private static ItemEffectsProblem Problem(ItemEffectsSourceState state) => state switch
    {
        ItemEffectsSourceState.Unavailable => new("ITEM_EFFECTS_SOURCE_UNAVAILABLE", "The selected source is unavailable.", true),
        ItemEffectsSourceState.Protected => new("ITEM_EFFECTS_SOURCE_PROTECTED", "The selected source is protected.", false),
        ItemEffectsSourceState.Unsupported => new("ITEM_EFFECTS_SOURCE_UNSUPPORTED", "The selected source profile is unsupported.", false),
        ItemEffectsSourceState.Corrupt => new("ITEM_EFFECTS_SOURCE_CORRUPT", "The selected source failed integrity validation.", false),
        ItemEffectsSourceState.ResourceLimit => new("ITEM_EFFECTS_LIMIT", "The effects resource limit was exceeded.", false),
        ItemEffectsSourceState.Stale => new("ITEM_EFFECTS_TARGET_STALE", "The selected target source changed.", true),
        _ => throw new DiscoveryException("ITEM_EFFECTS_RESULT_INVALID", "The effects result did not satisfy its bounded source and state contract.")
    };
    private static SourceProbe Failed(SourceProbe probe, ItemEffectsSourceState state) => probe with
    { Public = new(probe.Role, state, Problem: Problem(state)), CurrentProfile = false };
    private static SourceProbe Failed(SourceProbe probe, ItemEffectsProblem problem) => Failed(probe, problem.Code switch
    { "ITEM_EFFECTS_TARGET_STALE" => ItemEffectsSourceState.Stale, "ITEM_EFFECTS_LIMIT" => ItemEffectsSourceState.ResourceLimit, _ => ItemEffectsSourceState.Corrupt });

    private async Task<SourceProbe> ProbeAsync(ItemEffectsItemsProof original, ItemEffectsSourceRole role, CancellationToken token)
    {
        SourceProbe? result = null;
        using (EnterQuery(token))
        {
        // This is the bounded original-authority read, never generic Tables readiness.
        var admission = await catalog.ReadItemEffectsSourceAsync(original.Opening, role, token);
        if (admission is null) return new(role, null, null, new(role, ItemEffectsSourceState.NotPrepared), false);
        var provider = await TablesCatalog.Provider.Value.WaitAsync(token);
        await CpuAsync(() =>
        {
            var info = Role(role); provider.Definitions.TryGetValue(info.Table, out var definition);
            var snapshot = Snapshot(original.Snapshot.Context, admission, role,
                admission.Artifact.Binding.DefinitionSha256 == definition?.Sha256 ? definition : null);
            var state = RevisionState(role, admission.Artifact.Binding.DecoderRevision, admission.Source.Projection is not null);
            var probe = new SourceProbe(role, admission, snapshot, new(role, state,
                HistoricalArtifactId: state == ItemEffectsSourceState.NotPrepared ? snapshot.ArtifactId : null), false);
            // Recognized history publishes no current semantic or physical facts. Let shared
            // Tables upgrade it under today's pinned definition before current eligibility.
            if (state == ItemEffectsSourceState.NotPrepared) { result = probe; return; }
            if (definition is null || definition.Sha256 != info.Definition || admission.Artifact.Binding.DefinitionSha256 != info.Definition ||
                admission.Artifact.Binding.ProviderRevision != BundledTableDefinitions.Revision)
            { result = Failed(probe, ItemEffectsSourceState.Unsupported); return; }
            if (state != ItemEffectsSourceState.Ready) { result = Failed(probe, ItemEffectsSourceState.Unsupported); return; }
            if (!Qualified(original.Opening.Build.Version, role, admission, definition, token))
            { result = Failed(probe, ItemEffectsSourceState.Unsupported); return; }
            result = probe with { Public = new(role, ItemEffectsSourceState.Ready, snapshot), CurrentProfile = true };
        }, token);
        await ValidateConfigurationsAsync(original, [result!], token);
        }
        // An unsupported interpretation is still a claim about an authenticated current
        // physical source. Historical NotPrepared is different: Tables may replace an old
        // derived format without this feature opening that incompatible row artifact.
        if (result!.Public.State == ItemEffectsSourceState.Unsupported)
            return await VerifyUnsupportedAsync(original, result, token);
        return result;
    }
    private async Task<SourceProbe> VerifyUnsupportedAsync(ItemEffectsItemsProof original, SourceProbe probe, CancellationToken token)
    {
        BridgeBorrow? bridge = null; TargetBorrow? target = null;
        try
        {
            try
            {
                if (probe.Role == ItemEffectsSourceRole.Bridge)
                    bridge = await BorrowBridgeAsync(original, new(probe, null), false, true, token);
                else target = await BorrowTargetAsync(original, probe, token);
            }
            catch (Exception error) when (PhysicalFailure(error, out _))
            {
                _ = PhysicalFailure(error, out var state); Invalidate(probe, probe.Role == ItemEffectsSourceRole.Bridge);
                return Failed(probe, state);
            }
            return probe;
        }
        finally
        {
            if (bridge is not null) await bridge.DisposeAsync();
            if (target is not null) await target.DisposeAsync();
        }
    }
    private static TableDataSnapshot Snapshot(ItemsContext context, StoredTableAdmission admission,
        ItemEffectsSourceRole role, BundledTableDefinition? definition)
    {
        var artifact = admission.Artifact; var c = artifact.Completion;
        var columns = artifact.Header.Columns.Select(column => definition is not null ? TableDataStore.ProjectColumn(column, definition.Document) :
            new TableDataColumn(column.Name, column.Kind, column.IsId, null, column.Interpretation is { } p ?
                new(p.Policy, p.PhysicalBits, p.PhysicalSigned, p.CandidateBits, p.CandidateUnsigned, p.Warning) : null)).ToArray();
        return new(context.ProductId, context.BuildIdentity, context.SourceRegion, Role(role).Table, context.ResearchLocale,
            artifact.ContentKey, admission.PreparedAt, artifact.Header.LayoutHash.ToUpperInvariant(), artifact.Binding.DefinitionSha256,
            artifact.Binding.ProviderRevision, artifact.Binding.DecoderRevision, admission.Source, admission.Association, Array.AsReadOnly(columns),
            new(c.Scope, N(c.DeclaredRecords), N(c.PhysicalRows), N(c.CopyRows), N(c.ExcludedEncryptedSections),
                N(checked(c.ExcludedPhysicalRecords + c.ExcludedCopyRecords)), N(artifact.Header.DeclaredCopyRecords),
                N(c.ExcludedPhysicalRecords), N(c.ExcludedCopyRecords)), N(checked(c.PhysicalRows + c.CopyRows)), N(c.TextIssueCount));
    }
    private static bool Qualified(string version, ItemEffectsSourceRole role, StoredTableAdmission admission,
        BundledTableDefinition definition, CancellationToken token)
    {
        var info = Role(role); var artifact = admission.Artifact;
        if (artifact.Header.SchemaVersion != 5 || !string.Equals(artifact.Header.TableHash, info.Table, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(artifact.Header.LayoutHash, info.Layout, StringComparison.OrdinalIgnoreCase) || admission.Association.FileDataId != info.Fdid ||
            artifact.Binding.DecoderRevision != TableDataStore.AdmissionRevision(info.Table, definition.Sha256, BundledTableDefinitions.Revision, admission.Source.Projection is not null) ||
            artifact.Binding.SchemaSelectionSha256 != TableRowArtifacts.SchemaSelectionKey(definition.Document, version, token)) return false;
        var selected = definition.Document.SelectBuild(version, info.Layout, token);
        var names = role == ItemEffectsSourceRole.Bridge ? new[] { "ID", "ItemEffectID", "ItemID" } :
            role == ItemEffectsSourceRole.Effect ? EffectColumns : ["ID", "Name_lang"];
        if (selected.Blocks.Count != 1 || selected.Blocks[0].Fields.Count != names.Length || artifact.Header.Columns.Count != names.Length) return false;
        for (var i = 0; i < names.Length; i++)
        {
            token.ThrowIfCancellationRequested(); var field = selected.Blocks[0].Fields[i]; var actual = artifact.Header.Columns[i];
            var text = role == ItemEffectsSourceRole.Name && i == 1;
            var relation = role == ItemEffectsSourceRole.Bridge && i == 2;
            var noninline = i == 0 || relation;
            var bits = role == ItemEffectsSourceRole.Effect ? i is 1 or 2 ? 8 : i is 3 or 6 or 8 ? 16 : 32 : 32;
            var unsigned = role == ItemEffectsSourceRole.Effect && i is 1 or 2 or 6 or 8;
            if (field.Column.VerifiedName != !(role == ItemEffectsSourceRole.Effect && i == 9) || field.Column.Name != names[i] || field.Column.Family != (text ? "locstring" : "int") ||
                field.ArrayLength != 1 || field.IsId != (i == 0) || field.NonInline != noninline || field.RelationStorage != relation ||
                !text && (field.IntegerBits != bits || field.Unsigned != unsigned) || actual.Name != names[i] ||
                actual.Kind != (text ? "string" : "integer") || actual.IsId != (i == 0)) return false;
            if (role == ItemEffectsSourceRole.Effect && i is 3 or 6 or 8)
            {
                if (actual.Interpretation is not { Policy: "raw-physical-integer-v1", PhysicalBits: 32, PhysicalSigned: false, CandidateBits: 16 } p ||
                    p.CandidateUnsigned != (i != 3) || p.Warning.Length is < 1 or > 512 || p.Warning.Any(char.IsControl)) return false;
            }
            else if (actual.Interpretation is not null) return false;
        }
        return true;
    }
    private async Task ValidateConfigurationsAsync(ItemEffectsItemsProof original, IReadOnlyList<SourceProbe> probes, CancellationToken token)
    {
        var build = original.Configurations.Single(receipt => receipt.Role == "buildConfig");
        if (build.ByteLength is < 1 or > ItemEffectsConfigurationBudget.MaxBuildBytes) throw ItemEffectsBridgePolicy.Limit();
        var bytes = await objects.ReadAsync(build.Sha256, build.ByteLength, token);
        await CpuAsync(() => ValidateBuild(bytes, build, probes, token), token);
        // The nonasync build helper's parsed graph has retired before this next raw object is read.
        var cdn = original.Configurations.Single(receipt => receipt.Role == "cdnConfig");
        if (cdn.ByteLength is < 1 or > 1 << 20) throw ItemEffectsBridgePolicy.Limit();
        var cdnBytes = await objects.ReadAsync(cdn.Sha256, cdn.ByteLength, token);
        await CpuAsync(() =>
        {
            if (Convert.ToHexStringLower(MD5.HashData(cdnBytes)) != cdn.Key)
                throw new DiscoveryException("CONFIG_INTEGRITY", "The captured configuration content failed verification.");
        }, token);
    }
    private static void ValidateBuild(byte[] bytes, ConfigurationReceipt receipt, IReadOnlyList<SourceProbe> probes, CancellationToken token)
    {
        if (Convert.ToHexStringLower(MD5.HashData(bytes)) != receipt.Key)
            throw new DiscoveryException("CONFIG_INTEGRITY", "The captured configuration content failed verification.");
        var parsed = ItemEffectsConfigurationBudget.ParseBuild(bytes, token);
        for (var i = 0; i < probes.Count; i++) if (probes[i].Admission is { } source)
            TableDataStore.ValidateSourceAssociation(parsed, source.Association);
    }
    private async Task CheckOriginalAsync(ItemEffectsItemsProof original, BridgeResolution? bridge, CancellationToken token)
    {
        using var query = EnterQuery(token);
        var probes = bridge is null ? Array.Empty<SourceProbe>() : [bridge.Source];
        await ValidateConfigurationsAsync(original, probes, token);
        await CatalogCopyAsync(() => catalog.ReauthorizeItemEffectsAsync(original, BridgeProof(bridge), [], token), token);
    }
    private async Task<SourceProbe> EnsureAsync(ItemEffectsItemsProof original, SourceProbe initial,
        BridgeResolution? bridge, Operation operation)
    {
        var token = operation.Token;
        if (initial.Public.State != ItemEffectsSourceState.NotPrepared) return initial;
        var info = Role(initial.Role);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            // No query or physical borrow exists while waiting for the shared producer.
            await CheckOriginalAsync(original, bridge, token);
            _ = await catalog.ReadItemEffectsSourceAsync(original.Opening, initial.Role, token);
            try
            {
                _ = await tables.PrepareAsync(new(original.Snapshot.Context.ProductId, original.Snapshot.Context.BuildIdentity,
                    original.Snapshot.Context.SourceRegion, info.Table, original.Snapshot.Context.ResearchLocale),
                    stage => operation.ReportAsync(new(stage, "stage", TableId: info.Table)), token);
            }
            catch (DiscoveryException error) when (error.Code == "TABLE_DATA_BUSY")
            {
                await operation.ReportAsync(new("Waiting for shared table preparation", "stage", TableId: info.Table));
                await tables.WaitForAvailabilityAsync(token);
                continue;
            }
            catch (Exception error) when (AcquisitionFailure(error, out _))
            { _ = AcquisitionFailure(error, out var state); return Failed(initial, state); }
            // Required persisted authority preflight is outside the optional acquisition catch.
            var current = await ProbeAsync(original, initial.Role, token);
            await CheckOriginalAsync(original, bridge, token);
            if (initial.Admission is { } prior && current.Admission is { } next)
            {
                var same = false; await CpuAsync(() => same = ItemEffectsBridgePolicy.SameAuthority(prior.Source, next.Source) &&
                    ItemEffectsBridgePolicy.SameAuthority(prior.Association, next.Association), token);
                if (!same)
                {
                    if (initial.Role == ItemEffectsSourceRole.Bridge) throw Stale();
                    return Failed(current, ItemEffectsSourceState.Stale);
                }
            }
            return current;
        }
        throw Busy();
    }
    private static bool AcquisitionFailure(Exception error, out ItemEffectsSourceState state)
    {
        state = ItemEffectsSourceState.Corrupt;
        if (error is OperationCanceledException) return false;
        if (error is IOException or UnauthorizedAccessException) { state = ItemEffectsSourceState.Unavailable; return true; }
        if (error is not DiscoveryException failure) return false;
        switch (failure.Code)
        {
            case "BLTE_ENCRYPTED": case "BLTE_KEY_UNAVAILABLE": case "BLTE_RANGE_UNAVAILABLE":
                state = ItemEffectsSourceState.Protected; return true;
            case "CONTENT_UNAVAILABLE": case "CONTENT_VARIANT_MISSING": case "CONTENT_UNRESOLVED": case "CONTENT_TIMEOUT":
            case "METADATA_UNAVAILABLE": case "LOCAL_OBJECT_MISSING": case "HTTP_ERROR": case "HTTP_TIMEOUT":
                state = ItemEffectsSourceState.Unavailable; return true;
            case "DB2_SCHEMA_MISSING": case "DB2_SCHEMA_AMBIGUOUS": case "DB2_SCHEMA_MISMATCH": case "BLTE_UNSUPPORTED":
            case "DB2_UNSUPPORTED": case "DEFINITION_NOT_BUNDLED": case "SCHEMA_BUILD_NOT_LISTED": case "CONTENT_ROOT_UNSUPPORTED":
                state = ItemEffectsSourceState.Unsupported; return true;
            case "ITEM_EFFECTS_LIMIT": case "CONTENT_LIMIT": case "BLTE_LIMIT": case "DB2_LIMIT": case "CONTENT_VARIANT_LIMIT":
            case "ACQUISITION_CAPACITY":
                state = ItemEffectsSourceState.ResourceLimit; return true;
            case "ITEM_EFFECTS_TARGET_STALE": state = ItemEffectsSourceState.Stale; return true;
            case "LOCAL_OBJECT_CORRUPT": case "BLTE_INTEGRITY": case "BLTE_INVALID": case "DB2_INTEGRITY": case "DB2_INVALID":
            case "CONTENT_PROJECTION_INVALID": case "CONTENT_PROJECTION_INTEGRITY":
                state = ItemEffectsSourceState.Corrupt; return true;
            default: return false;
        }
    }
    private Task<SourceProbe> PrepareOptionalAsync(ItemEffectsItemsProof original, SourceProbe initial,
        BridgeResolution bridge, Operation operation) => EnsureAsync(original, initial, bridge, operation);
    private async Task<SourceProbe[]> FinishAsync(ItemEffectsItemsProof original, BridgeResolution? bridge,
        SourceProbe[] consulted, CancellationToken token)
    {
        using var query = EnterQuery(token);
        var sources = bridge is null ? consulted : new[] { bridge.Source }.Concat(consulted).ToArray();
        await ValidateConfigurationsAsync(original, sources, token);
        var provider = await TablesCatalog.Provider.Value.WaitAsync(token);
        // Interpretation/profile checks precede the one SQL linearization point. Unsupported/historical
        // proof remains an authority proof without being required to satisfy the profile it failed.
        foreach (var source in sources)
        {
            if (!source.CurrentProfile || source.Admission is null) continue;
            var valid = false; await CpuAsync(() => valid = provider.Definitions.TryGetValue(Role(source.Role).Table, out var definition) &&
                Qualified(original.Opening.Build.Version, source.Role, source.Admission, definition, token), token);
            if (!valid) throw source.Role == ItemEffectsSourceRole.Bridge ? Stale() : new DiscoveryException("ITEM_EFFECTS_TARGET_STALE", "The selected target source changed.", true);
        }
        var targets = consulted.Where(probe => probe.Admission is not null).Select(probe =>
            new ItemEffectsTargetProof(probe.Role, probe.Admission!, probe.CapturedSnapshot!)).ToArray();
        IReadOnlyList<ItemEffectsTargetFenceResult> fences;
        try { fences = await CatalogCopyAsync(() => catalog.ReauthorizeItemEffectsAsync(original, BridgeProof(bridge), targets, token), token); }
        catch (DiscoveryException error) when (error.Code == "ITEM_EFFECTS_BRIDGE_STALE")
        { if (bridge is not null) Invalidate(bridge.Source, true); throw; }
        var result = (SourceProbe[])consulted.Clone();
        foreach (var fence in fences)
        {
            if (fence.Problem is null) continue;
            var index = Array.FindIndex(result, probe => probe.Role == fence.Role);
            Invalidate(result[index], false); result[index] = Failed(result[index], fence.Problem);
        }
        return result;
    }
    private void Invalidate(SourceProbe source, bool bridge)
    {
        if (source.Admission is null) return;
        lock (gate)
        {
            IEnumerable<PhysicalSlot> slots = bridge ? bridges : targets;
            foreach (var slot in slots) if (slot.Admission.Artifact.ContentKey == source.Admission.Artifact.ContentKey)
                slot.Retiring = true;
        }
    }

    // These are exact previously shipped interpretations, never a version-prefix/suffix grant.
    internal static ItemEffectsSourceState RevisionState(ItemEffectsSourceRole role, string revision, bool projected)
    {
        if (!Enum.IsDefined(role)) throw new DiscoveryException("INVALID_REQUEST", "Choose one exact effects source role.");
        var current = "wdc5-values-v7+table-admission" + (projected ? "-projection" : "") +
            (role == ItemEffectsSourceRole.Effect ? "-v4" : "-v3");
        if (revision == current) return ItemEffectsSourceState.Ready;
        var historical = projected ? revision is
            "wdc5-fixed-values-v3+table-admission-projection-v1" or
            "wdc5-values-v4+table-admission-projection-v1" or
            "wdc5-values-v5+table-admission-projection-v1" or
            "wdc5-values-v6+table-admission-projection-v1" or
            "wdc5-values-v7+table-admission-projection-v2" or
            "wdc5-values-v7+table-admission-projection-v3" : revision is
            "wdc5-fixed-values-v1+table-admission-v1" or
            "wdc5-fixed-values-v3+table-admission-v1" or
            "wdc5-values-v5+table-admission-v1" or
            "wdc5-values-v6+table-admission-v1" or
            "wdc5-values-v7+table-admission-v2" or
            "wdc5-values-v7+table-admission-v3";
        return historical ? ItemEffectsSourceState.NotPrepared : ItemEffectsSourceState.Unsupported;
    }
}
