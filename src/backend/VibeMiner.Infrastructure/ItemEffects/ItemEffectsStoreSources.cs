using System.Globalization;
using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

public sealed partial class ItemEffectsStore
{
    // Link is the build's item-to-effect link (#86): it decides which table and profile each role reads.
    private sealed record SourceProbe(ItemEffectsSourceRole Role, StoredTableAdmission? Admission,
        TableDataSnapshot? CapturedSnapshot, ItemEffectsSource Public, bool CurrentProfile, ItemEffectsLink Link, bool Provisional = false);
    private static readonly string[] EffectColumns = [.. ItemEffectsLinks.ClassicItemEffect.Columns];
    private static ItemEffectsTableProfile Role(ItemEffectsLink link, ItemEffectsSourceRole role) => ItemEffectsLinks.Profile(link, role);
    private static ItemEffectsTableProfile Role(SourceProbe probe) => ItemEffectsLinks.Profile(probe.Link, probe.Role);
    private static SourceProbe Unchecked(ItemEffectsSourceRole role, ItemEffectsLink link) => new(role, null, null, new(role, ItemEffectsSourceState.NotChecked), false, link);
    private static ItemEffectsProblem Problem(ItemEffectsSourceState state) => state switch
    {
        ItemEffectsSourceState.Unavailable => new("ITEM_EFFECTS_SOURCE_UNAVAILABLE", "The selected source is unavailable.", true),
        ItemEffectsSourceState.Protected => new("ITEM_EFFECTS_SOURCE_PROTECTED", "The selected source is protected.", false),
        ItemEffectsSourceState.Unsupported => new("ITEM_EFFECTS_SOURCE_UNSUPPORTED", "The selected source profile is unsupported.", false),
        ItemEffectsSourceState.Corrupt => new("ITEM_EFFECTS_SOURCE_CORRUPT", "The selected source failed integrity validation.", false),
        ItemEffectsSourceState.ResourceLimit => new("ITEM_EFFECTS_LIMIT", "The effects resource limit was exceeded.", false),
        ItemEffectsSourceState.Stale => new("ITEM_EFFECTS_TARGET_STALE", "The selected target source changed.", true),
        ItemEffectsSourceState.NotInBuild => new("ITEM_EFFECTS_SOURCE_NOT_IN_BUILD", "This build does not include the selected source.", false),
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
        var chosen = await LinkAsync(original, token); var link = chosen ?? ItemEffectsLinks.Parent;
        var info = Role(link, role);
        var admission = await catalog.ReadItemEffectsSourceAsync(original.Opening, role, token, info.TableId);
        if (admission is null) return new(role, null, null, new(role, ItemEffectsSourceState.NotPrepared), false, link, chosen is null);
        var version = original.Opening.Build.Version;
        var (_, bundled) = await TableDefinitionSources.FeatureTableAsync(info.TableId, token);
        // D101: the definition Tables chooses now for these rows, bundled or fetched, under the Settings switch.
        var definition = await definitions.ForFeatureRowsAsync(info.TableId, version, admission.Artifact, token);
        await CpuAsync(() =>
        {
            var chosen = admission.Artifact.Binding.DefinitionSha256 == definition.Sha256 && admission.Artifact.Binding.ProviderRevision == definition.Revision;
            var snapshot = Snapshot(original.Snapshot.Context, admission, info, chosen ? definition : null, version, bundled);
            var state = CurrentState(role, info, admission.Artifact.Binding.DecoderRevision, admission.Artifact.Header.LayoutHash,
                admission.Source.Projection is not null, definition, bundled, version, chosen);
            var probe = new SourceProbe(role, admission, snapshot, new(role, state,
                HistoricalArtifactId: state == ItemEffectsSourceState.NotPrepared ? snapshot.ArtifactId : null), false, link);
            // Recognized history publishes no current semantic or physical facts. Let shared
            // Tables upgrade it under today's chosen definition before current eligibility.
            if (state == ItemEffectsSourceState.NotPrepared) { result = probe; return; }
            if (state != ItemEffectsSourceState.Ready) { result = Failed(probe, ItemEffectsSourceState.Unsupported); return; }
            if (!Qualified(version, info, admission, definition, bundled, token))
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
        ItemEffectsTableProfile profile, TableDefinition? definition, string version, BundledTableDefinition bundled)
    {
        var artifact = admission.Artifact; var c = artifact.Completion;
        var columns = artifact.Header.Columns.Select(column => definition is not null ? TableDataStore.ProjectColumn(column, definition.Document) :
            new TableDataColumn(column.Name, column.Kind, column.IsId, null, column.Interpretation is { } p ?
                new(p.Policy, p.PhysicalBits, p.PhysicalSigned, p.CandidateBits, p.CandidateUnsigned, p.Warning) : null)).ToArray();
        TableDataSnapshot snapshot = new(context.ProductId, context.BuildIdentity, context.SourceRegion, profile.TableId, context.ResearchLocale,
            artifact.ContentKey, admission.PreparedAt, artifact.Header.LayoutHash.ToUpperInvariant(), artifact.Binding.DefinitionSha256,
            artifact.Binding.ProviderRevision, artifact.Binding.DecoderRevision, admission.Source, admission.Association, Array.AsReadOnly(columns),
            new(c.Scope, N(c.DeclaredRecords), N(c.PhysicalRows), N(c.CopyRows), N(c.ExcludedEncryptedSections),
                N(checked(c.ExcludedPhysicalRecords + c.ExcludedCopyRecords)), N(artifact.Header.DeclaredCopyRecords),
                N(c.ExcludedPhysicalRecords), N(c.ExcludedCopyRecords)), N(checked(c.PhysicalRows + c.CopyRows)), N(c.TextIssueCount));
        // Which definition read the rows, as Tables says it (D101).
        return definition is null ? snapshot : TableDataStore.WithDefinitionFacts(snapshot, definition, version, bundled);
    }
    // The reviewed profile per role: the exact layout, file and every field's name, width and flags. Any definition, bundled
    // or fetched (D101), qualifies when its block for that layout says the same; the Effect raw columns also need the
    // reviewed raw rules, which a fetched definition gets only for an identical block.
    private static bool Qualified(string version, ItemEffectsTableProfile info, StoredTableAdmission admission,
        TableDefinition definition, BundledTableDefinition bundled, CancellationToken token)
    {
        var artifact = admission.Artifact;
        if (artifact.Header.SchemaVersion != 5 || !string.Equals(artifact.Header.TableHash, info.TableId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(artifact.Header.LayoutHash, info.Layout, StringComparison.OrdinalIgnoreCase) || admission.Association.FileDataId != info.FileDataId ||
            artifact.Binding.DefinitionSha256 != definition.Sha256 || artifact.Binding.ProviderRevision != definition.Revision ||
            artifact.Binding.DecoderRevision != TableDataStore.AdmissionRevision(info.TableId, definition, bundled, version, info.Layout, admission.Source.Projection is not null, token) ||

            artifact.Binding.SchemaSelectionSha256 != TableRowArtifacts.SchemaSelectionKey(definition.Document, version, token)) return false;
        var selected = definition.Document.SelectFile(version, info.Layout, token);
        var names = info.Columns;
        if (selected.Blocks.Count != 1 || selected.Blocks[0].Fields.Count != names.Count || artifact.Header.Columns.Count != names.Count) return false;
        for (var i = 0; i < names.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var field = selected.Blocks[0].Fields[i]; var actual = artifact.Header.Columns[i];
            var text = info.TableId == ItemEffectsLinks.SpellName.TableId && i == 1;
            if (!ItemEffectsLinkChoice.FieldMatches(info, i, field) || actual.Name != names[i] ||
                actual.Kind != (text ? "string" : "integer") || actual.IsId != (i == 0)) return false;
            if (info.RawColumns.Contains(i))
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
        var info = Role(initial);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            info = Role(initial);
            // No query or physical borrow exists while waiting for the shared producer.
            await CheckOriginalAsync(original, bridge, token);
            _ = await catalog.ReadItemEffectsSourceAsync(original.Opening, initial.Role, token, info.TableId);
            try
            {
                _ = await tables.PrepareAsync(new(original.Snapshot.Context.ProductId, original.Snapshot.Context.BuildIdentity,
                    original.Snapshot.Context.SourceRegion, info.TableId, original.Snapshot.Context.ResearchLocale, FetchedDefinitions: true, Feature: "Item effects"),
                    stage => operation.ReportAsync(new(stage, "stage", TableId: info.TableId)), token);
            }
            catch (DiscoveryException error) when (error.Code == "TABLE_DATA_BUSY")
            {
                await operation.ReportAsync(new("Waiting for shared table preparation", "stage", TableId: info.TableId));
                await tables.WaitForAvailabilityAsync(token);
                continue;
            }
            // An undecided build needs ItemEffect whichever way its layout decides, so a failure there names ItemEffect's link (#86).
            catch (Exception error) when (AcquisitionFailure(error, out _))
            { _ = AcquisitionFailure(error, out var state); return Failed(initial with { Provisional = false }, state); }
            // Required persisted authority preflight is outside the optional acquisition catch.
            var current = await ProbeAsync(original, initial.Role, token);
            await CheckOriginalAsync(original, bridge, token);
            // An undecided build's link reads ItemEffect first; once its layout chose ItemXItemEffect, prepare that (#86).
            if (current.Public.State == ItemEffectsSourceState.NotPrepared && Role(current).TableId != info.TableId)
            { initial = current; continue; }
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
    // The build's link (#86, D104). The bundled ItemEffect definition decides whenever its blocks for the build agree, so no
    // ItemEffect authority is read early. When they disagree, the ItemEffect definition Tables chooses for the admitted
    // ItemEffect rows (D101, bundled or fetched) decides by that file's layout; until the file is admitted the link reads as
    // ParentItemID, which prepares ItemEffect first. Null means that provisional, undecided state.
    private async Task<ItemEffectsLink?> LinkAsync(ItemEffectsItemsProof original, CancellationToken token)
    {
        var version = original.Opening.Build.Version; var table = ItemEffectsLinks.ClassicItemEffect.TableId;
        var (_, bundled) = await TableDefinitionSources.FeatureTableAsync(table, token);
        if (ItemEffectsLinkChoice.For(bundled.Document, version, null, token) is { } decided) return decided;
        var effect = await catalog.ReadItemEffectsSourceAsync(original.Opening, ItemEffectsSourceRole.Effect, token);
        if (effect is null) return null;
        var definition = await definitions.ForFeatureRowsAsync(table, version, effect.Artifact, token);
        return ItemEffectsLinkChoice.For(definition.Document, version, effect.Artifact.Header.LayoutHash, token) ?? ItemEffectsLinks.Parent;
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
            // The exact build's ROOT lists no enUS file for this FileDataID: the build doesn't ship the table, and asking
            // again can't change that (#85). Classic Era 1.15 ships no ItemXItemEffect, for one.
            case "CONTENT_VARIANT_MISSING": state = ItemEffectsSourceState.NotInBuild; return true;
            case "CONTENT_UNAVAILABLE": case "CONTENT_UNRESOLVED": case "CONTENT_TIMEOUT":
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
        // Interpretation/profile checks precede the one SQL linearization point. Unsupported/historical
        // proof remains an authority proof without being required to satisfy the profile it failed.
        foreach (var source in sources)
        {
            if (!source.CurrentProfile || source.Admission is null) continue;
            var table = Role(source).TableId; var version = original.Opening.Build.Version;
            var (_, bundled) = await TableDefinitionSources.FeatureTableAsync(table, token);
            var definition = await definitions.ForFeatureRowsAsync(table, version, source.Admission.Artifact, token);
            var valid = false; await CpuAsync(() => valid = Qualified(version, Role(source), source.Admission, definition, bundled, token), token);
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

    /// <summary>
    /// The state of admitted rows before the field profile is checked. Rows read with another definition than the one
    /// chosen now (the switch changed, or WoWDBDefs has a newer one) are stale like history: shared Tables reads them
    /// again. Rows that are already current for the chosen definition but carry an older-looking revision are not
    /// stale: preparing again would give the same rows, so the profile is unsupported (D101). That is a fetched
    /// ItemEffect block that differs from the reviewed one, so it gets no raw rules and keeps v3.
    /// </summary>
    internal static ItemEffectsSourceState CurrentState(ItemEffectsSourceRole role, string decoderRevision, string layout, bool projected,
        TableDefinition definition, BundledTableDefinition bundled, string version, bool chosen) =>
        CurrentState(role, ItemEffectsLinks.Profile(ItemEffectsLinks.Bridge, role), decoderRevision, layout, projected, definition, bundled, version, chosen);
    private static ItemEffectsSourceState CurrentState(ItemEffectsSourceRole role, ItemEffectsTableProfile profile, string decoderRevision, string layout, bool projected,
        TableDefinition definition, BundledTableDefinition bundled, string version, bool chosen)
    {
        var state = RevisionState(role, decoderRevision, projected, profile);
        if (!chosen) return state == ItemEffectsSourceState.Ready ? ItemEffectsSourceState.NotPrepared : state;
        if (state == ItemEffectsSourceState.NotPrepared &&
            decoderRevision == TableDataStore.AdmissionRevision(profile.TableId, definition, bundled, version, layout, projected))
            return ItemEffectsSourceState.Unsupported;
        return state;
    }
    // The revisions a profile's current rows carry. Retail ItemEffect 4CA77678 needs v4, the reviewed raw rules. Classic
    // ItemEffect 1BF9CF3A is v4 read with the bundled definition (its rules exist but match no column) and v3 with a fetched
    // one (D101); Qualified then checks the exact revision for the chosen definition. Other tables keep v3.
    private static bool CurrentRevision(ItemEffectsTableProfile profile, string revision, bool projected)
    {
        var stem = "wdc5-values-v7+table-admission" + (projected ? "-projection" : "");
        return profile.RawColumns.Count > 0 ? revision == stem + "-v4" :
            revision == stem + "-v3" || profile.TableId == ItemEffectsLinks.ClassicItemEffect.TableId && revision == stem + "-v4";
    }
    // These are exact previously shipped interpretations, never a version-prefix/suffix grant.
    internal static ItemEffectsSourceState RevisionState(ItemEffectsSourceRole role, string revision, bool projected, ItemEffectsTableProfile? profile = null)
    {
        if (!Enum.IsDefined(role)) throw new DiscoveryException("INVALID_REQUEST", "Choose one exact effects source role.");
        if (CurrentRevision(profile ?? ItemEffectsLinks.Profile(ItemEffectsLinks.Bridge, role), revision, projected)) return ItemEffectsSourceState.Ready;
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
