using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Creature projection builder")]
public sealed class CreatureGeometrySourcesTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    [Fact]
    public async Task Actual_C2_shared_B_two_full_originals_produce_private_closure_and_budgeted_VMG()
    {
        await using var fixture = await CreatureGeometrySourcesFixture.CreateAsync();
        var prepared = await fixture.PrepareAsync().WaitAsync(Deadline); var closure = prepared.Closure;
        Assert.Equal(2, prepared.Sources.Count); Assert.Equal([CreatureGeometrySourceRole.Model, CreatureGeometrySourceRole.Skin], prepared.Sources.Select(s => s.Role));
        Assert.Equal(["777", "778"], prepared.Sources.Select(s => s.FileDataId)); Assert.All(prepared.Sources, s => Assert.Equal("content-blte-v1", s.FullVerifierRevision));
        Assert.Equal(new GeometryNativeCounts(3, 3, 3, 3, 1), closure.Capsule.Counts);
        Assert.Equal(CreatureGeometrySourcesFixture.SkinId, closure.Capsule.MainSkinFileDataId); Assert.Equal(0, closure.Capsule.MainSkinOrdinal);
        Assert.Equal(fixture.Selection.Core.Opening.Reference, closure.Capsule.SourceContext.Opening);
        Assert.Equal(fixture.Selection.Core.Artifact.Binding.Configurations[0].Sha256, closure.Capsule.SourceContext.BuildConfigurationSha256);
        Assert.Equal(GeometryPayloadBudget.ProducerBytes, fixture.Payload.ReservedBytes);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Projection.DirectoryPath, "staging-v1")));
        var budget = new GeometryWorkBudget(fixture.Resources); using var output = new MemoryStream();
        var artifact = await ModelGeometryArtifacts.BuildAsync(prepared.Skin, prepared.DataBinding, output, budget, default);
        Assert.Equal(closure.DataBindingSha256, artifact.BindingSha256); Assert.Equal("notEvaluated", artifact.Manifest.Evaluations.Appearance);
        output.Position = 0; await ModelGeometryArtifacts.VerifyAsync(output, artifact, budget, default);
        Assert.True(fixture.Fence.Checks > 5);
        foreach (var source in prepared.Sources)
        {
            var original = await fixture.Catalog.FindContentAsync(source.EncodingKey, default); Assert.NotNull(original); Assert.Null(original.Projection);
            Assert.Equal(source.EncodedSha256, original.Sha256);
        }
    }
    [Fact]
    public async Task Parsed_Q0_keeps_full_pair_proof_and_global_unsectioned_triangles_but_no_artifact()
    {
        await using var fixture = await CreatureGeometrySourcesFixture.CreateAsync(empty: true);
        var prepared = await fixture.PrepareAsync().WaitAsync(Deadline);
        Assert.Equal(new GeometryNativeCounts(3, 3, 3, 0, 1), prepared.Closure.Capsule.Counts);
        Assert.Equal(2, prepared.Sources.Count); Assert.All(prepared.Sources, s => Assert.Equal("content-blte-v1", s.FullVerifierRevision));
        using var output = new MemoryStream();
        Assert.Equal("GEOMETRY_ARTIFACT_UNSUPPORTED", (await Assert.ThrowsAsync<DiscoveryException>(() => ModelGeometryArtifacts.BuildAsync(prepared.Skin, prepared.DataBinding, output))).Code);
        Assert.Equal(0, output.Length);
    }
    [Fact]
    public async Task Encrypted_skin_is_protected_with_only_reached_full_model_evidence_and_no_leaked_grants()
    {
        await using var fixture = await CreatureGeometrySourcesFixture.CreateAsync(encryptedSkin: true);
        var error = await Assert.ThrowsAsync<GeometrySourceFailureException>(() => fixture.PrepareAsync().WaitAsync(Deadline));
        Assert.Equal(CreatureGeometryState.Protected, error.State); Assert.Equal(CreatureGeometryDependency.Skin, error.Dependency);
        Assert.Equal("BLTE_ENCRYPTED", error.Problem.Code); Assert.Equal("777", Assert.Single(error.Sources).FileDataId);
        Assert.Equal(0, fixture.Payload.ReservedBytes); Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Projection.DirectoryPath, "staging-v1")));
    }
    [Fact]
    public async Task Offline_repeat_reverifies_original_sources_without_restamping_receipt_or_data_binding()
    {
        await using var fixture = await CreatureGeometrySourcesFixture.CreateAsync();
        var first = await fixture.PrepareAsync().WaitAsync(Deadline); var model = first.Closure.Capsule.Model.Receipt; var binding = first.DataBindingSha256;
        var requests = fixture.Http.Requests.Count; fixture.Http.Offline = true;
        var second = await fixture.PrepareAsync().WaitAsync(Deadline);
        Assert.Equal(model, second.Closure.Capsule.Model.Receipt); Assert.Equal(binding, second.DataBindingSha256);
        Assert.Equal(requests, fixture.Http.Requests.Count); Assert.Equal(2, second.Sources.Count);
    }
    [Theory]
    [InlineData("modelVerification")]
    [InlineData("modelParsing")]
    [InlineData("skinVerification")]
    [InlineData("skinParsing")]
    public async Task Progress_wait_is_CPU_free_and_cancel_retires_real_files_and_child_payload(string stage)
    {
        await using var fixture = await CreatureGeometrySourcesFixture.CreateAsync();
        using var stop = new CancellationTokenSource(); var entered = Signal(); var release = Signal();
        var preparing = fixture.PrepareAsync(async p => { if (p.Stage == stage) { entered.TrySetResult(); await release.Task.WaitAsync(stop.Token); } }, stop.Token);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            using (await fixture.Resources.EnterCpuAsync(null, default).WaitAsync(Deadline)) Assert.False(preparing.IsCompleted);
            stop.Cancel();
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
        Assert.Equal(0, fixture.Payload.ReservedBytes); Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Projection.DirectoryPath, "staging-v1")));
        await fixture.PrepareAsync().WaitAsync(Deadline);
    }
    [Fact]
    public async Task Stale_selection_during_model_acquisition_never_escapes_as_a_source_result_or_closure()
    {
        await using var fixture = await CreatureGeometrySourcesFixture.CreateAsync(); var stale = false;
        fixture.Http.Before = (request, _) =>
        { if (request.RequestUri!.AbsoluteUri.EndsWith(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(fixture.ModelEncoded)), StringComparison.Ordinal)) stale = true; return Task.CompletedTask; };
        fixture.Fence.Before = (_, _) => stale ? Task.FromException(new DiscoveryException("CREATURE_GEOMETRY_STALE", "Authored source replaced.")) : Task.CompletedTask;
        Assert.Equal("CREATURE_GEOMETRY_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.PrepareAsync().WaitAsync(Deadline))).Code);
        Assert.Equal(0, fixture.Payload.ReservedBytes); Assert.Null(fixture.Prepared);
    }
    [Fact]
    public async Task Canonical_codec_roundtrip_preserves_full_originals_and_projects_compact_evidence_without_fresh_authority()
    {
        await using var fixture = await CreatureGeometrySourcesFixture.CreateAsync(); var prepared = await fixture.PrepareAsync().WaitAsync(Deadline);
        var budget = new GeometryWorkBudget(fixture.Resources); var closure = prepared.Closure;
        var parsed = await GeometrySourceCapsuleCodec.ParseAsync(closure.CanonicalUtf8, budget, default);
        Assert.Equal(closure.AuthoritySha256, parsed.AuthoritySha256); Assert.Equal(closure.CanonicalUtf8.ToArray(), parsed.CanonicalUtf8.ToArray());
        Assert.Equal(closure.Capsule.Model.Receipt, parsed.Capsule.Model.Receipt); Assert.Equal(closure.Capsule.Skin.Receipt, parsed.Capsule.Skin.Receipt);
        Assert.Equal(prepared.Sources, await GeometrySourceCapsuleCodec.ProjectSourcesAsync(parsed.Capsule, budget, default));
        Assert.All(typeof(GeometrySourceClosure).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic), c => Assert.True(c.IsPrivate));
    }
    [Theory]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("trailing")]
    [InlineData("whitespace")]
    [InlineData("truncated")]
    [InlineData("memberUnknown")]
    public async Task Codec_rejects_noncanonical_framing_without_tolerant_reinterpretation(string kind)
    {
        await using var fixture = await CreatureGeometrySourcesFixture.CreateAsync(); var prepared = await fixture.PrepareAsync().WaitAsync(Deadline);
        var json = Encoding.UTF8.GetString(prepared.Closure.CanonicalUtf8.Span);
        json = kind switch
        {
            "duplicate" => "{\"revision\":\"geometry-source-capsule-v1\"," + json[1..],
            "unknown" => "{\"unknown\":0," + json[1..], "trailing" => json + "{}", "whitespace" => " " + json,
            "memberUnknown" => json.Replace("\"blockOrdinal\":", "\"unknown\":0,\"blockOrdinal\":", StringComparison.Ordinal), _ => json[..^1]
        };
        await Assert.ThrowsAsync<DiscoveryException>(() => GeometrySourceCapsuleCodec.ParseAsync(Encoding.UTF8.GetBytes(json), new GeometryWorkBudget(fixture.Resources), default));
    }
    [Theory]
    [InlineData("ck")]
    [InlineData("projection")]
    [InlineData("fdid")]
    [InlineData("context")]
    [InlineData("resolver")]
    [InlineData("count")]
    public async Task Codec_refuses_inconsistent_full_proof_and_native_identity(string kind)
    {
        await using var fixture = await CreatureGeometrySourcesFixture.CreateAsync(); var prepared = await fixture.PrepareAsync().WaitAsync(Deadline); var pair = prepared.Closure.Capsule;
        pair = kind switch
        {
            "ck" => pair with { Model = pair.Model with { Receipt = pair.Model.Receipt with { ContentKey = new string('f', 32) } } },
            "projection" => pair with { Model = pair.Model with { Receipt = pair.Model.Receipt with { VerifierRevision = "content-blte-projection-v1" } } },
            "fdid" => pair with { MainSkinFileDataId = 779 },
            "context" => pair with { SourceContext = pair.SourceContext with { BuildConfigurationSha256 = new string('f', 64) } },
            "resolver" => pair with { Resolvers = [pair.Resolvers[1], pair.Resolvers[0]] },
            _ => pair with { Counts = pair.Counts with { SectionTriangleIndices = 131073 } }
        };
        await Assert.ThrowsAsync<DiscoveryException>(() => GeometrySourceCapsuleCodec.EncodeAsync(pair, new GeometryWorkBudget(fixture.Resources), default));
    }
    // D095 is a texture-only rule: a model or SKIN association naming it is refused by the capsule, the association
    // snapshot and the catalog source shape; the same association is accepted only on the texture path.
    [Theory]
    [InlineData("model")]
    [InlineData("skin")]
    public async Task A_model_or_SKIN_source_naming_the_D095_texture_policy_is_refused(string role)
    {
        await using var fixture = await CreatureGeometrySourcesFixture.CreateAsync(); var prepared = await fixture.PrepareAsync().WaitAsync(Deadline);
        var pair = prepared.Closure.Capsule; var source = role == "model" ? pair.Model : pair.Skin; var budget = new GeometryWorkBudget(fixture.Resources);
        var d095 = source.Association with { VariantPolicy = RootTextureVariants.Policy };
        pair = role == "model" ? pair with { Model = pair.Model with { Association = d095 } } : pair with { Skin = pair.Skin with { Association = d095 } };
        Assert.Equal("CREATURE_GEOMETRY_SOURCE_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => GeometrySourceCapsuleCodec.EncodeAsync(pair, budget, default))).Code);
        Assert.Equal("CREATURE_GEOMETRY_SOURCE_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => GeometrySourceCapsuleCodec.SnapshotAssociationAsync(d095, budget, default))).Code);
        Assert.Equal(RootTextureVariants.Policy, (await GeometrySourceCapsuleCodec.SnapshotAssociationAsync(d095, budget, default, texture: true)).VariantPolicy);
        Assert.Equal("CREATURE_GEOMETRY_SOURCE_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            fixture.Catalog.ReadGeometryOriginalSourcesAsync(d095, source.Expectation, source.Receipt, default))).Code);
        Assert.Equal(source.Receipt.EncodingKey, (await fixture.Catalog.ReadGeometryOriginalSourcesAsync(d095, source.Expectation, source.Receipt, default, texture: true)).Receipt.EncodingKey);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Complete_B_ambiguity_and_encoded_limit_stop_before_any_model_payload_demand(bool ambiguous)
    {
        await using var fixture = await CreatureGeometrySourcesFixture.CreateAsync(oversizedModel: !ambiguous, ambiguousModel: ambiguous);
        var requests = fixture.Http.Requests.Count;
        var failure = await Assert.ThrowsAsync<GeometrySourceFailureException>(() => fixture.PrepareAsync().WaitAsync(Deadline));
        Assert.Equal(ambiguous ? CreatureGeometryState.AmbiguousVariants : CreatureGeometryState.ResourceLimit, failure.State);
        // #98: the refusal names the size that tripped and the allowed one, and says it is not corruption.
        if (!ambiguous)
        {
            Assert.Equal("CREATURE_GEOMETRY_RESOURCE_LIMIT", failure.Problem.Code);
            // #111: the message opens with the sizes and caps, not with engine wording.
            Assert.StartsWith("The model file is ", failure.Problem.Message);
            Assert.Contains("4.1 MiB encoded; geometry reads at most 8 MiB decoded and 4 MiB encoded.", failure.Problem.Message);
            Assert.EndsWith("encoded. This is a size limit, not a corrupt file.", failure.Problem.Message);
            Assert.DoesNotContain("admitted", failure.Problem.Message); Assert.DoesNotContain("This is a limit", failure.Problem.Message);
            Assert.InRange(failure.Problem.Message.Length, 1, 512);
        }
        Assert.Empty(failure.Sources); Assert.Equal(requests, fixture.Http.Requests.Count); Assert.Equal(0, fixture.Payload.ReservedBytes);
    }
    // #111: a limit opens with what tripped it. Only the source-size refusal (tested above) says "size limit"; the same
    // codes also carry count caps (memberships, vertices), so every other limit says "a limit". A retryable capacity
    // refusal claims neither, and other states keep their named prefix.
    [Theory]
    [InlineData("CREATURE_GEOMETRY_RESOURCE_LIMIT", "The geometry proof exceeds its bounded metadata allowance.", false,
        "The geometry proof exceeds its bounded metadata allowance. This is a limit, not a corrupt file.")]
    [InlineData("MODEL_LIMIT", "The M2 vertices count exceeds its bound.", false, "The M2 vertices count exceeds its bound. This is a limit, not a corrupt file.")]
    [InlineData("BLTE_FRAME_LIMIT", "Reader detail.", false, "The geometry source is over one of its limits. This is a limit, not a corrupt file.")]
    [InlineData("CREATURE_GEOMETRY_RESOURCE_LIMIT", "Geometry payload reservations are at their bounded capacity.", true, "Geometry payload reservations are at their bounded capacity.")]
    [InlineData("MODEL_INVALID", "The M2 header is short.", false, "The original geometry source could not be admitted (Corrupt): The M2 header is short.")]
    public void Failure_message_leads_with_the_limit_and_names_only_the_source_size_as_a_size_limit(string code, string detail, bool retryable, string expected)
    {
        var failure = GeometrySourceClosure.Failure(new DiscoveryException(code, detail, retryable), CreatureGeometryDependency.Model, []);
        Assert.Equal(expected, failure.Problem.Message); Assert.Equal(code, failure.Problem.Code);
        Assert.Equal(code == "MODEL_INVALID" ? CreatureGeometryState.Corrupt : CreatureGeometryState.ResourceLimit, failure.State);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_membership_and_external_skeleton_do_not_guess_a_skin_or_appearance(bool external)
    {
        await using var fixture = await CreatureGeometrySourcesFixture.CreateAsync(missingModel: !external, externalSkeleton: external);
        var requests = fixture.Http.Requests.Count;
        var failure = await Assert.ThrowsAsync<GeometrySourceFailureException>(() => fixture.PrepareAsync().WaitAsync(Deadline));
        Assert.Equal(external ? CreatureGeometryState.Unsupported : CreatureGeometryState.MissingMembership, failure.State);
        if (external) Assert.Contains("external skeleton file (SKID 900)", failure.Problem.Message);
        Assert.Equal(external ? 1 : 0, failure.Sources.Count);
        Assert.Equal(external ? requests + 1 : requests, fixture.Http.Requests.Count);
        Assert.Equal(0, fixture.Payload.ReservedBytes);
    }
    [Theory]
    [InlineData(272u, null)]
    [InlineData(273u, "M2 version 273 in a chunked MD21 model is outside the admitted profiles")]
    public async Task Chunked_272_models_prepare_and_other_versions_name_their_version(uint version, string? named)
    {
        await using var fixture = await CreatureGeometrySourcesFixture.CreateAsync(modelVersion: version);
        if (named is null)
        {
            var prepared = await fixture.PrepareAsync().WaitAsync(Deadline);
            Assert.Equal(272u, prepared.Skin.Model.Version); Assert.Equal(new GeometryNativeCounts(3, 3, 3, 3, 1), prepared.Closure.Capsule.Counts);
            var budget = new GeometryWorkBudget(fixture.Resources); using var output = new MemoryStream();
            var artifact = await ModelGeometryArtifacts.BuildAsync(prepared.Skin, prepared.DataBinding, output, budget, default);
            output.Position = 0; await ModelGeometryArtifacts.VerifyAsync(output, artifact, budget, default);
            return;
        }
        var failure = await Assert.ThrowsAsync<GeometrySourceFailureException>(() => fixture.PrepareAsync().WaitAsync(Deadline));
        Assert.Equal(CreatureGeometryState.Unsupported, failure.State); Assert.Equal("MODEL_UNSUPPORTED", failure.Problem.Code);
        Assert.Contains(named, failure.Problem.Message); Assert.Single(failure.Sources); Assert.Equal(0, fixture.Payload.ReservedBytes);
    }
    [Theory]
    [InlineData(256)]
    [InlineData(257)]
    public async Task Full_membership_bound_preserves_order_and_high_unsigned_name_hash_or_refuses_whole(int count)
    {
        await using var fixture = await CreatureGeometrySourcesFixture.CreateAsync(); var prepared = await fixture.PrepareAsync().WaitAsync(Deadline);
        var original = prepared.Closure.Capsule;
        GeometrySourceCapsule Many(GeometrySourceCapsule source) => source with { Association = source.Association with
        { Memberships = Enumerable.Range(0, count).Select(i => new RootTargetMatch(i, i, source.Association.Memberships[0].Entry with { NameHash = ulong.MaxValue })).ToArray() } };
        var pair = original with { Model = Many(original.Model), Skin = Many(original.Skin) }; var budget = new GeometryWorkBudget(fixture.Resources);
        if (count == 257) { Assert.Equal("CREATURE_GEOMETRY_RESOURCE_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => GeometrySourceCapsuleCodec.EncodeAsync(pair, budget, default))).Code); return; }
        var encoded = await GeometrySourceCapsuleCodec.EncodeAsync(pair, budget, default);
        var parsed = await GeometrySourceCapsuleCodec.ParseAsync(encoded.CanonicalUtf8, budget, default);
        Assert.Equal(256, parsed.Capsule.Model.Association.Memberships.Count); Assert.Equal(ulong.MaxValue, parsed.Capsule.Skin.Association.Memberships[255].Entry.NameHash);
        Assert.Equal(Enumerable.Range(0, 256).Select(i => (long)i), parsed.Capsule.Model.Association.Memberships.Select(m => m.BlockOrdinal));
    }
    [Fact]
    public async Task Codec_cancellation_at_grant_disposes_late_scope_and_oversize_input_is_refused_before_budget()
    {
        using var stop = new CancellationTokenSource(); var budget = new CancelBudget(stop);
        await Assert.ThrowsAsync<DiscoveryException>(() => GeometrySourceCapsuleCodec.ParseAsync(new byte[(256 << 10) + 1], budget, default));
        Assert.Equal(0, budget.Enters);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GeometrySourceCapsuleCodec.ParseAsync("{}"u8.ToArray(), budget, stop.Token));
        Assert.Equal(1, budget.Enters); Assert.Equal(1, budget.Releases);
    }
    [Fact]
    public async Task Demand_preflight_reserves_full_source_envelope_and_refuses_without_payload_requests()
    {
        await using var fixture = await CreatureGeometrySourcesFixture.CreateAsync(); var prepared = await fixture.PrepareAsync().WaitAsync(Deadline);
        var source = prepared.Closure.Capsule.Model.Association;
        // Authored bounded metadata with expensive JSON escaping exercises the serialized
        // envelope independently of the usual short official URLs in the real B fixture.
        var url = new string('\u0001', 2048);
        source = source with
        {
            Configurations = source.Configurations.Select(c => c with { SourceUrl = url }).ToArray(),
            EncodingSource = source.EncodingSource with { SourceUrl = url }, RootSource = source.RootSource with { SourceUrl = url },
            Memberships = Enumerable.Range(0, 256).Select(i => new RootTargetMatch(long.MaxValue - i, long.MaxValue - i,
                source.Memberships[0].Entry with { LocaleFlags = uint.MaxValue, ContentFlags = uint.MaxValue,
                    RawContentFlags = new(uint.MaxValue, uint.MaxValue, byte.MaxValue), NameHash = ulong.MaxValue })).ToArray()
        };
        var requests = fixture.Http.Requests.Count; var budget = new GeometryWorkBudget(fixture.Resources);
        Assert.Equal("CREATURE_GEOMETRY_RESOURCE_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            GeometrySourceCapsuleCodec.SourceAssociationSha256Async(source, budget, default))).Code);
        Assert.Equal(requests, fixture.Http.Requests.Count);
    }
    [Fact]
    public void Shared_payload_ledger_and_producer_child_slices_keep_debt_until_actual_retirement()
    {
        var ledger = new GeometryPayloadBudget(); var producer = ledger.ReserveProducer(); var capture = producer.Reserve(16 << 20); var source = producer.Reserve(32 << 20);
        var textures = producer.Reserve(16 << 20);
        using var one = ledger.Reserve(8 << 20); using var two = ledger.Reserve(8 << 20);
        Assert.Equal(80L << 20, ledger.ReservedBytes);
        Assert.Equal("CREATURE_GEOMETRY_RESOURCE_LIMIT", Assert.Throws<DiscoveryException>(() => ledger.Reserve(1)).Code);
        Assert.Equal("CREATURE_GEOMETRY_RESOURCE_LIMIT", Assert.Throws<DiscoveryException>(() => producer.Reserve(1)).Code);
        Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED", Assert.Throws<DiscoveryException>(() => producer.Dispose()).Code);
        Assert.Equal(80L << 20, ledger.ReservedBytes); source.Dispose(); textures.Dispose(); Assert.Equal(80L << 20, ledger.ReservedBytes);
        capture.Dispose(); Assert.Equal(16L << 20, ledger.ReservedBytes); producer.Dispose(); Assert.Equal(16L << 20, ledger.ReservedBytes);
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(1)]
    public void Failure_observation_snapshot_refuses_invalid_or_vanished_bounded_lists(int count)
    {
        var values = new VanishedSources(count);
        Assert.Throws<DiscoveryException>(() => new GeometrySourceFailureException(CreatureGeometryState.Unavailable,
            CreatureGeometryDependency.Skin, new DiscoveryException("AUTHORED_FAILURE", "Authored source failure."), values));
        Assert.Equal(1, values.CountReads); Assert.Equal(count == 1 ? 1 : 0, values.IndexReads);
    }
    [Theory]
    [InlineData("encoded")]
    [InlineData("decoded")]
    [InlineData("staging")]
    public async Task Actual_source_close_or_release_failure_retains_owners_payload_and_staging_without_retry(string fault)
    {
        var resources = new AcquisitionResources(maximumCpu: 1, maximumBackgroundCpu: 1,
            maximumStagingBytes: 8 << 20, maximumBackgroundStagingBytes: 4 << 20);
        var staging = await resources.ReserveStagingAsync(8 << 20, null, default);
        var ledger = new GeometryPayloadBudget(); var producer = ledger.ReserveProducer();
        using var capture = producer.Reserve(16 << 20); var source = producer.Reserve(32 << 20);
        var encodedPath = Path.Combine(Path.GetTempPath(), "vm-geometry-encoded-" + Guid.NewGuid().ToString("N"));
        var decodedPath = Path.Combine(Path.GetTempPath(), "vm-geometry-decoded-" + Guid.NewGuid().ToString("N"));
        var encoded = new FaultingCloseFile(encodedPath, fault == "encoded");
        var decoded = new FaultingCloseFile(decodedPath, fault == "decoded");
        var release = new FaultingRelease(staging, fault == "staging");
        var owner = new GeometrySourceVerification.Owner(release); owner.OwnEncoded(encoded); owner.OwnDecoded(decoded);
        try
        {
            GeometrySourceCleanupException failure;
            if (fault == "encoded") failure = await Assert.ThrowsAsync<GeometrySourceCleanupException>(() => owner.CloseEncodedAsync().AsTask());
            else { await owner.CloseEncodedAsync(); failure = await Assert.ThrowsAsync<GeometrySourceCleanupException>(() => owner.DisposeAsync().AsTask()); }
            failure.RetainPayload(source); // Same attachment performed by actual Prepare's exact-type catch.
            var handle = new FaultingRetirement();
            Assert.Same(failure, await Assert.ThrowsAsync<GeometrySourceCleanupException>(() => GeometrySourceClosure.RetireHandleAsync(handle, failure)));
            Assert.Equal(1, handle.Calls); // A later finally must retain, never replace, the original stream owner.
            Assert.Same(failure, await Assert.ThrowsAsync<GeometrySourceCleanupException>(() => owner.DisposeAsync().AsTask()));
            Assert.Equal(1, encoded.CloseCalls); Assert.Equal(1, decoded.CloseCalls);
            Assert.Equal(fault == "staging" ? 1 : 0, release.Calls);
            Assert.IsType<IOException>(failure.InnerException); Assert.Equal(64L << 20, ledger.ReservedBytes);
            Assert.Equal("CREATURE_GEOMETRY_RESOURCE_LIMIT", Assert.Throws<DiscoveryException>(() => ledger.ReserveProducer()).Code);
            Assert.Equal("CREATURE_GEOMETRY_CLEANUP_FAILED", Assert.Throws<DiscoveryException>(() => producer.Dispose()).Code);
            using var stop = new CancellationTokenSource();
            var peer = resources.ReserveStagingAsync(1, null, stop.Token); Assert.False(peer.IsCompleted);
            using (await resources.EnterCpuAsync(null, default).WaitAsync(Deadline)) Assert.False(peer.IsCompleted);
            stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => peer);
            GC.KeepAlive(failure); // The exact Store-retained exception owns stream/staging/source debt.
        }
        finally { File.Delete(encodedPath); File.Delete(decodedPath); }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class CancelBudget(CancellationTokenSource stop) : IGeometryArtifactWorkBudget
    {
        internal int Enters, Releases;
        public ValueTask<IDisposable> EnterAsync(CancellationToken token) { Enters++; stop.Cancel(); return ValueTask.FromResult<IDisposable>(new Release(this)); }
        private sealed class Release(CancelBudget owner) : IDisposable { public void Dispose() => owner.Releases++; }
    }
    private sealed class VanishedSources(int count) : IReadOnlyList<CreatureGeometrySource>
    {
        internal int CountReads, IndexReads;
        public int Count { get { CountReads++; return count; } }
        public CreatureGeometrySource this[int index] { get { IndexReads++; throw new ArgumentOutOfRangeException(nameof(index)); } }
        public IEnumerator<CreatureGeometrySource> GetEnumerator() => throw new NotSupportedException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class FaultingCloseFile(string path, bool fail) : FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous)
    {
        internal int CloseCalls;
        public override async ValueTask DisposeAsync()
        { CloseCalls++; await base.DisposeAsync(); if (fail) throw new IOException("Authored actual file close uncertainty."); }
    }
    private sealed class FaultingRelease(IDisposable inner, bool fail) : IDisposable
    {
        internal int Calls;
        public void Dispose() { Calls++; if (fail) throw new IOException("Authored staging release uncertainty."); inner.Dispose(); }
    }
    private sealed class FaultingRetirement : IAsyncDisposable
    {
        internal int Calls;
        public ValueTask DisposeAsync() { Calls++; return ValueTask.FromException(new IOException("Authored feature retirement uncertainty.")); }
    }
}
