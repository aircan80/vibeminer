using System.Collections;
using VibeMiner.Application;
using VibeMiner.Domain;
using Xunit;
using static VibeMiner.Backend.Tests.CreatureGeometryTestFixture;

namespace VibeMiner.Backend.Tests;

public sealed class CreatureGeometryValidationTests
{
    // D095: the chosen variant is the 0x1-clear twin of the left-out one and names the decoded source's content key.
    [Theory]
    [InlineData("valid")][InlineData("samePair")][InlineData("lowViolence")][InlineData("notTwin")][InlineData("policy")]
    [InlineData("sourceKey")][InlineData("sameKeys")][InlineData("nullFileDataId")][InlineData("nullFileDataIdControl")]
    public void A_texture_variant_must_be_the_standard_twin_of_its_decoded_source(string kind)
    {
        var ready = Appearance(); var texture = ready.Textures[0];
        var variant = new CreatureGeometryTextureVariant(CreatureGeometryValidation.TextureVariantPolicy, texture.Source!.ContentKey, 0x2080000, new('d', 32), 0x2080001);
        var unresolved = ready with { State = CreatureGeometryAppearanceState.TextureUnresolved, Manifest = null,
            Problem = new("CREATURE_APPEARANCE_TEXTURE_UNRESOLVED", "Texture ordinal 0 has a zero FileDataID.", false, null, 0) };
        CreatureGeometryAppearance With(CreatureGeometryTextureVariant? v, CreatureGeometryAppearance? on = null, CreatureGeometryTexture? row = null) =>
            (on ?? ready) with { Textures = [(row ?? texture) with { Variant = v }] };
        var unresolvedRow = texture with { FileDataId = null, State = CreatureGeometryTextureState.Unresolved, Image = null, Source = null };
        var value = kind switch
        {
            "valid" => With(variant),
            "samePair" => With(variant with { HighResolutionContentFlags = 0x2080000 }),
            "lowViolence" => With(variant with { ContentFlags = 0x2080080, HighResolutionContentFlags = 0x2080081 }),
            "notTwin" => With(variant with { HighResolutionContentFlags = 0x2080009 }),
            "policy" => With(variant with { Policy = "all-root-variants-consistent-asset-v1" }),
            "sourceKey" => With(variant with { ContentKey = new('e', 32) }),
            "sameKeys" => With(variant with { HighResolutionContentKey = texture.Source.ContentKey }),
            "nullFileDataId" => With(variant, unresolved, unresolvedRow),
            _ => With(null, unresolved, unresolvedRow)
        };
        if (kind is "valid" or "nullFileDataIdControl") { Assert.Equal(value.Textures[0].Variant, CreatureGeometryValidation.Snapshot(value).Textures[0].Variant); return; }
        Assert.Throws<DiscoveryException>(() => CreatureGeometryValidation.Snapshot(value));
    }

    [Theory]
    [InlineData(-1, "1")]
    [InlineData(4, "1")]
    [InlineData(0, "01")]
    [InlineData(0, "-1")]
    [InlineData(0, "4294967296")]
    public async Task Invalid_original_selection_never_invokes_store(int slot, string id)
    {
        var store = new Store(); await using var service = new CreatureGeometryService(store);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(new(Selection with { Slot = slot, CreatureId = id })))).Code);
        Assert.Equal(0, store.Prepares);
    }
    [Fact]
    public async Task Exact_expected_artifact_assertion_never_retargets_or_prepares()
    {
        var store = new Store { Read = (r, _) => Task.FromResult(Result(r.Selection, ready: true)) };
        await using var service = new CreatureGeometryService(store);
        Assert.Equal("CREATURE_GEOMETRY_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(new(Selection, new('a', 64))))).Code);
        Assert.Equal(0, store.Prepares); var ready = await service.ReadAsync(new(Selection, Manifest().ArtifactId)); Assert.Equal(CreatureGeometryState.Ready, ready.State);
    }
    [Theory]
    [InlineData("counts")]
    [InlineData("offset")]
    [InlineData("length")]
    [InlineData("evaluation")]
    [InlineData("nan")]
    [InlineData("negativeZero")]
    [InlineData("identity")]
    [InlineData("revision")]
    public void Manifest_mismatch_cannot_become_approved_read_evidence(string kind)
    {
        var m = Manifest(); m = kind switch
        {
            "counts" => m with { LineIndexCount = 6 },
            "offset" => m with { Positions = new(132, 48) },
            "length" => m with { ByteLength = 321 },
            "evaluation" => m with { Evaluations = m.Evaluations with { Appearance = "ready" } },
            "nan" => m with { Bounds = new(new float[] { float.NaN, 0, 0 }, new float[] { 1, 1, 1 }) },
            "negativeZero" => m with { Bounds = new(new float[] { -0f, 0, 0 }, new float[] { 1, 1, 1 }) },
            "identity" => m with { ModelFileDataId = 0 },
            _ => m with { EdgePolicy = "visible-sections" }
        };
        Assert.Equal("CREATURE_GEOMETRY_RESULT_INVALID", Assert.Throws<DiscoveryException>(() => CreatureGeometryValidation.Snapshot(m)).Code);
    }
    [Theory]
    [InlineData("selection")]
    [InlineData("role")]
    [InlineData("copy")]
    [InlineData("edge")]
    [InlineData("row")]
    [InlineData("probability")]
    [InlineData("signed")]
    [InlineData("appearance")]
    [InlineData("missingSources")]
    public void Forged_or_mismatched_ready_chain_is_rejected(string kind)
    {
        var r = Result(ready: true); var targets = r.Metadata.Targets.ToArray();
        r = kind switch
        {
            "selection" => r with { Slot = 1 },
            "role" => r with { Sources = r.Sources.Reverse().ToArray() },
            "probability" => r with { Metadata = r.Metadata with { Selected = r.Metadata.Selected! with { Probability = "NaN" } } },
            "signed" => r with { Metadata = r.Metadata with { Selected = r.Metadata.Selected! with { DisplayId = "-10" } } },
            "appearance" => r with { Manifest = r.Manifest! with { Evaluations = new("evaluated", "notEvaluated", "notEvaluated", "notEvaluated") } },
            "missingSources" => r with { Sources = [] },
            _ => r
        };
        if (kind == "copy") targets[0] = targets[0] with { Row = targets[0].Row! with { CopySourceId = "7" } };
        if (kind == "edge") targets[0] = targets[0] with { Row = targets[0].Row! with { EdgeValue = targets[0].Row!.EdgeValue with { Value = "21" } } };
        if (kind == "row") targets[0] = targets[0] with { Row = targets[0].Row! with { Source = targets[0].Row!.Source with { ArtifactId = new('9', 64) } } };
        if (kind is "copy" or "edge" or "row") r = r with { Metadata = r.Metadata with { Targets = targets } };
        Assert.Equal("CREATURE_GEOMETRY_RESULT_INVALID", Assert.Throws<DiscoveryException>(() => CreatureGeometryValidation.Snapshot(r, Selection)).Code);
    }
    [Fact]
    public void Empty_declared_geometry_retains_two_original_sources_without_manifest_or_appearance_claim()
    {
        var r = Result(ready: true) with { State = CreatureGeometryState.NoDeclaredTriangles, Manifest = null };
        var value = CreatureGeometryValidation.Snapshot(r, Selection); Assert.Equal(2, value.Sources.Count); Assert.Null(value.Manifest);
        Assert.Equal(CreatureDisplayAppearance.NotEvaluated, value.Metadata.Appearance);
        Assert.Throws<DiscoveryException>(() => CreatureGeometryValidation.Snapshot(r with { Manifest = Manifest() }, Selection));
    }
    [Fact]
    public void Scoped_missing_Creature_never_becomes_complete_source_absence()
    {
        var r = Result(); var partial = Coverage with { Scope = "unencryptedSections", ExcludedKeyedSections = "1" };
        r = r with
        {
            State = CreatureGeometryState.MetadataNotResolved,
            Metadata = r.Metadata with
            {
                State = CreatureDisplayMetadataState.CreatureNotFoundInAdmittedScope,
                CreatureCoverage = partial,
                Selected = null,
                RequiredTableId = null,
                Absence = CreatureDisplayAbsence.UnknownOutsideReadableScope
            }
        };
        CreatureGeometryValidation.Snapshot(r, Selection);
        Assert.Throws<DiscoveryException>(() => CreatureGeometryValidation.Snapshot(r with { Metadata = r.Metadata with { Absence = CreatureDisplayAbsence.AbsentFromCompleteSource } }, Selection));
    }
    [Fact]
    public void Returned_graph_owns_its_bounds_targets_sources_and_allows_no_mutation()
    {
        var source = Result(ready: true); var targets = source.Metadata.Targets.ToArray(); var sources = source.Sources.ToArray(); var min = new float[] { -6, -4, 0 };
        source = source with { Metadata = source.Metadata with { Targets = targets }, Sources = sources, Manifest = source.Manifest! with { Bounds = new(min, new float[] { 3, 7, 8 }) } };
        var copy = CreatureGeometryValidation.Snapshot(source, Selection); targets[0] = targets[1]; sources[0] = sources[1]; min[0] = 999;
        Assert.Equal(CreatureDisplayTargetRole.Display, copy.Metadata.Targets[0].Role); Assert.Equal(CreatureGeometrySourceRole.Model, copy.Sources[0].Role); Assert.Equal(-6, copy.Manifest!.Bounds.Min[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<float>)copy.Manifest.Bounds.Min)[0] = 1);
        Assert.Throws<NotSupportedException>(() => ((IList<CreatureGeometryTarget>)copy.Metadata.Targets)[0] = targets[0]);
    }
    [Fact]
    public async Task Oversized_or_changing_output_collection_is_refused_and_admission_retires()
    {
        var list = new HostileSources(); var store = new Store { Read = (r, _) => Task.FromResult(Result(r.Selection, true) with { Sources = list }) };
        await using var service = new CreatureGeometryService(store);
        Assert.Equal("CREATURE_GEOMETRY_RESULT_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(new(Selection)))).Code);
        Assert.Equal(0, list.IndexReads); store.Read = (r, _) => Task.FromResult(Result(r.Selection));
        await service.ReadAsync(new(Selection)); await service.ReadAsync(new(Selection));
    }
    [Theory]
    [InlineData("unknown", "bytes", 0, 1)]
    [InlineData("modelParsing", "percent", 0, 1)]
    [InlineData("modelParsing", "bytes", -1, 1)]
    [InlineData("modelParsing", "bytes", 2, 1)]
    public async Task Dishonest_progress_fails_before_observer(string stage, string unit, long done, long total)
    {
        var store = new Store { Prepare = async (r, report, _) => { await report(new(stage, "Reading", unit, done, total)); return Result(r.Selection); } };
        await using var service = new CreatureGeometryService(store); var called = false;
        Assert.Equal("CREATURE_GEOMETRY_PROGRESS_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(new(Selection), _ => { called = true; return Task.CompletedTask; }))).Code);
        Assert.False(called);
    }
    [Theory]
    [InlineData("copyWithoutRows")]
    [InlineData("physicalWithoutRows")]
    [InlineData("matchOverflow")]
    [InlineData("declaredMismatch")]
    [InlineData("excludedMismatch")]
    [InlineData("falsePartial")]
    [InlineData("falseComplete")]
    [InlineData("rowLimit")]
    public void Incoherent_coverage_or_impossible_match_kind_is_rejected(string kind)
    {
        var r = Result(ready: true); var t = r.Metadata.Targets[0];
        t = kind switch
        {
            "copyWithoutRows" => t with { Row = t.Row! with { IsCopy = true, CopySourceId = "7" } },
            "physicalWithoutRows" => t with { Coverage = Coverage with { DeclaredRecords = "0", PhysicalRows = "0", DeclaredCopyRecords = "1", CopyRows = "1" } },
            "matchOverflow" => t with { MatchCount = "2", Row = null },
            "declaredMismatch" => t with { Coverage = Coverage with { DeclaredRecords = "2" } },
            "excludedMismatch" => t with { Coverage = Coverage with { ExcludedDeclaredRecords = "1" } },
            "falsePartial" => t with { Coverage = Coverage with { Scope = "unencryptedSections" } },
            "falseComplete" => t with { Coverage = Coverage with { ExcludedKeyedSections = "1" } },
            _ => t with { Coverage = Coverage with { DeclaredRecords = "1000001", PhysicalRows = "1000001" } }
        };
        Assert.Throws<DiscoveryException>(() => CreatureGeometryValidation.Snapshot(r with { Metadata = r.Metadata with { Targets = new[] { t, r.Metadata.Targets[1] } } }, Selection));
    }
    [Fact]
    public void Positive_copy_and_partial_coverage_keep_exact_row_evidence()
    {
        var r = Result(ready: true); var t = r.Metadata.Targets[0];
        t = t with { Coverage = new("unencryptedSections", "1", "0", "1", "1", "1", "1", "1", "0"),
            Row = t.Row! with { IsCopy = true, CopySourceId = "7" } };
        var result = CreatureGeometryValidation.Snapshot(r with { Metadata = r.Metadata with { Targets = new[] { t, r.Metadata.Targets[1] } } }, Selection);
        Assert.True(result.Metadata.Targets[0].Row!.IsCopy); Assert.False(result.Metadata.Targets[1].Row!.IsCopy);
    }
    [Theory]
    [InlineData(CreatureDisplayMetadataState.NotPrepared)]
    [InlineData(CreatureDisplayMetadataState.NoDeclaredDisplay)]
    [InlineData(CreatureDisplayMetadataState.InvalidDisplayReference)]
    [InlineData(CreatureDisplayMetadataState.DisplayMissingInAdmittedScope)]
    [InlineData(CreatureDisplayMetadataState.DisplayAmbiguous)]
    [InlineData(CreatureDisplayMetadataState.NoDeclaredModel)]
    [InlineData(CreatureDisplayMetadataState.ModelMissingInAdmittedScope)]
    [InlineData(CreatureDisplayMetadataState.ModelAmbiguous)]
    [InlineData(CreatureDisplayMetadataState.NoDeclaredModelFile)]
    public void Terminal_state_cannot_claim_unreached_or_contradictory_chain(CreatureDisplayMetadataState state)
    {
        var r = Result(); var m = r.Metadata with { State = state, RequiredTableId = state == CreatureDisplayMetadataState.NotPrepared ? "bfdaf9f1" : null };
        if (state == CreatureDisplayMetadataState.NotPrepared) m = m with { ModelId = "20", ModelFileDataId = "800", Absence = CreatureDisplayAbsence.AbsentFromCompleteSource };
        Assert.Throws<DiscoveryException>(() => CreatureGeometryValidation.Snapshot(r with { State = CreatureGeometryState.MetadataNotResolved, Metadata = m }, Selection));
    }
    [Theory]
    [InlineData(CreatureDisplayMetadataState.CreatureNotFoundInAdmittedScope)]
    [InlineData(CreatureDisplayMetadataState.NoDeclaredDisplay)]
    [InlineData(CreatureDisplayMetadataState.InvalidDisplayReference)]
    [InlineData(CreatureDisplayMetadataState.DisplayMissingInAdmittedScope)]
    [InlineData(CreatureDisplayMetadataState.DisplayAmbiguous)]
    [InlineData(CreatureDisplayMetadataState.NoDeclaredModel)]
    [InlineData(CreatureDisplayMetadataState.ModelMissingInAdmittedScope)]
    [InlineData(CreatureDisplayMetadataState.ModelAmbiguous)]
    [InlineData(CreatureDisplayMetadataState.NoDeclaredModelFile)]
    [InlineData(CreatureDisplayMetadataState.NotPrepared)]
    [InlineData(CreatureDisplayMetadataState.Unsupported)]
    public void Reached_terminal_graphs_preserve_their_actual_stage(CreatureDisplayMetadataState state)
    {
        var ready = Result(ready: true); var m = ready.Metadata;
        var targets = m.Targets.ToArray();
        var modelStage = state is CreatureDisplayMetadataState.ModelMissingInAdmittedScope or CreatureDisplayMetadataState.ModelAmbiguous or CreatureDisplayMetadataState.NoDeclaredModelFile;
        m = m with { State = state, Targets = modelStage ? targets : [], ModelId = modelStage ? "20" : null, ModelFileDataId = null };
        if (state == CreatureDisplayMetadataState.CreatureNotFoundInAdmittedScope) m = m with { Selected = null, Absence = CreatureDisplayAbsence.AbsentFromCompleteSource };
        if (state is CreatureDisplayMetadataState.NoDeclaredDisplay or CreatureDisplayMetadataState.InvalidDisplayReference)
            m = m with { Selected = m.Selected! with { DisplayId = state == CreatureDisplayMetadataState.NoDeclaredDisplay ? "0" : "-1",
                State = state == CreatureDisplayMetadataState.NoDeclaredDisplay ? CreatureDisplayState.NoDeclaredDisplay : CreatureDisplayState.InvalidDisplayReference } };
        if (state == CreatureDisplayMetadataState.NotPrepared) m = m with { Targets = [targets[0]], ModelId = "20", RequiredTableId = "7f695fa5" };
        if (state is CreatureDisplayMetadataState.DisplayMissingInAdmittedScope or CreatureDisplayMetadataState.DisplayAmbiguous or CreatureDisplayMetadataState.NoDeclaredModel)
        {
            var t = targets[0];
            t = state == CreatureDisplayMetadataState.NoDeclaredModel ? t with { Row = t.Row! with { EdgeValue = t.Row.EdgeValue with { Value = "0" } } }
                : t with { Row = null, MatchCount = state == CreatureDisplayMetadataState.DisplayAmbiguous ? "2" : "0",
                    Coverage = state == CreatureDisplayMetadataState.DisplayAmbiguous ? Coverage with { DeclaredRecords = "2", PhysicalRows = "2" } : Coverage };
            m = m with { Targets = [t], Absence = state == CreatureDisplayMetadataState.DisplayMissingInAdmittedScope ? CreatureDisplayAbsence.AbsentFromCompleteSource : null };
        }
        if (modelStage)
        {
            var t = targets[1]; t = state == CreatureDisplayMetadataState.NoDeclaredModelFile ? t with { Row = t.Row! with { EdgeValue = t.Row.EdgeValue with { Value = "0" } } }
                : t with { Row = null, MatchCount = state == CreatureDisplayMetadataState.ModelAmbiguous ? "2" : "0",
                    Coverage = state == CreatureDisplayMetadataState.ModelAmbiguous ? Coverage with { DeclaredRecords = "2", PhysicalRows = "2" } : Coverage };
            m = m with { Targets = [targets[0], t], Absence = state == CreatureDisplayMetadataState.ModelMissingInAdmittedScope ? CreatureDisplayAbsence.AbsentFromCompleteSource : null };
        }
        if (state == CreatureDisplayMetadataState.Unsupported) m = m with { Targets = [targets[0]], ModelId = "20" }; // CMD failed before a target was delivered.
        var result = ready with { State = CreatureGeometryState.MetadataNotResolved, Manifest = null, Sources = [], Metadata = m,
            Problem = state == CreatureDisplayMetadataState.Unsupported ? new("CREATURE_DISPLAY_UNSUPPORTED", "Unsupported CMD source.", false, CreatureGeometryDependency.Metadata, CreatureDisplayTargetRole.Model) : null };
        Assert.Equal(state, CreatureGeometryValidation.Snapshot(result, Selection).Metadata.State);
        if (state == CreatureDisplayMetadataState.Unsupported)
        {
            m = m with { Targets = [targets[0], targets[1] with { MatchCount = null, Row = null }] };
            CreatureGeometryValidation.Snapshot(result with { Metadata = m }, Selection);
        }
    }
    [Theory]
    [InlineData(CreatureDisplayMetadataState.Protected)]
    [InlineData(CreatureDisplayMetadataState.Unsupported)]
    [InlineData(CreatureDisplayMetadataState.Unavailable)]
    [InlineData(CreatureDisplayMetadataState.Corrupt)]
    [InlineData(CreatureDisplayMetadataState.ResourceLimit)]
    public void Failed_metadata_requires_its_mapped_typed_problem(CreatureDisplayMetadataState state)
    {
        var r = Result(); r = r with { State = CreatureGeometryState.MetadataNotResolved,
            Metadata = r.Metadata with { State = state, RequiredTableId = null } };
        Assert.Throws<DiscoveryException>(() => CreatureGeometryValidation.Snapshot(r, Selection));
        Assert.Throws<DiscoveryException>(() => CreatureGeometryValidation.Snapshot(r with { Problem = new("BAD", "Failed source.", false, CreatureGeometryDependency.Model) }, Selection));
        Assert.Throws<DiscoveryException>(() => CreatureGeometryValidation.Snapshot(r with { State = CreatureGeometryState.NotPrepared }, Selection));
        Assert.Throws<DiscoveryException>(() => CreatureGeometryValidation.Snapshot(r with { Problem = new("BAD", "Failed source.", false, CreatureGeometryDependency.Metadata) }, Selection));
        Assert.Throws<DiscoveryException>(() => CreatureGeometryValidation.Snapshot(r with { Problem = new("BAD", "Failed source.", false, CreatureGeometryDependency.Metadata, CreatureDisplayTargetRole.Model) }, Selection));
        CreatureGeometryValidation.Snapshot(r with { Problem = new("BAD", "Failed source.", false, CreatureGeometryDependency.Metadata, CreatureDisplayTargetRole.Display) }, Selection);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Failed_metadata_problem_role_matches_the_reached_probe(bool model, bool delivered)
    {
        var r = Result(ready: true); var targets = r.Metadata.Targets.ToArray();
        var failed = targets[model ? 1 : 0] with { MatchCount = null, Row = null };
        var m = r.Metadata with { State = CreatureDisplayMetadataState.Unsupported, ModelId = model ? "20" : null, ModelFileDataId = null,
            Targets = model ? delivered ? [targets[0], failed] : [targets[0]] : delivered ? [failed] : [] };
        var role = model ? CreatureDisplayTargetRole.Model : CreatureDisplayTargetRole.Display;
        r = r with { State = CreatureGeometryState.MetadataNotResolved, Manifest = null, Sources = [], Metadata = m,
            Problem = new("BAD", "Unsupported exact target.", false, CreatureGeometryDependency.Metadata, role) };
        CreatureGeometryValidation.Snapshot(r, Selection);
        Assert.Throws<DiscoveryException>(() => CreatureGeometryValidation.Snapshot(r with { Problem = r.Problem with { Target = null } }, Selection));
        Assert.Throws<DiscoveryException>(() => CreatureGeometryValidation.Snapshot(r with { Problem = r.Problem with { Target = model ? CreatureDisplayTargetRole.Display : CreatureDisplayTargetRole.Model } }, Selection));
    }
    [Theory]
    [InlineData("display")]
    [InlineData("probability")]
    [InlineData("message")]
    public void Pathological_scalar_is_rejected_before_parsing_or_text_scan(string kind)
    {
        var r = Result(); var huge = new string(' ', 1_000_000);
        r = kind == "message" ? r with { Problem = new("BAD", huge, false) } : r with { Metadata = r.Metadata with
        { Selected = kind == "display" ? r.Metadata.Selected! with { DisplayId = huge } : r.Metadata.Selected! with { Probability = huge } } };
        Assert.Throws<DiscoveryException>(() => CreatureGeometryValidation.Snapshot(r, Selection));
    }
    private sealed class HostileSources : IReadOnlyList<CreatureGeometrySource>
    {
        internal int IndexReads; public int Count => int.MaxValue;
        public CreatureGeometrySource this[int index] { get { IndexReads++; throw new InvalidOperationException(); } }
        public IEnumerator<CreatureGeometrySource> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
