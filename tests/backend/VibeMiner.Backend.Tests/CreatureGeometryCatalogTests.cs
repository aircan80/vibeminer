using System.Globalization;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Creature projection builder")]
public sealed class CreatureGeometryCatalogTests
{
    [Fact]
    public async Task Local_lookup_of_unprepared_geometry_preserves_current_selected_chain_without_acquisition()
    {
        await using var h = await Harness.Create(); var budget = new Budget();
        Assert.Null(await h.Catalog.FindCreatureGeometryLocatorAsync(h.Selection, null, budget, default));
        Assert.Equal(0, budget.Active); Assert.True(budget.Entries > 3); Assert.Empty(h.Source.Prepared);
        Assert.Equal("CREATURE_GEOMETRY_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.FindCreatureGeometryLocatorAsync(h.Selection, new('a', 64), budget, default))).Code);
    }

    [Fact]
    public async Task A_refresh_while_cpu_preprocessing_waits_cannot_publish_a_local_candidate()
    {
        await using var h = await Harness.Create(); var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var budget = new Budget { Before = async entry => { if (entry == 2) { reached.SetResult(); await release.Task; } } };
        var read = h.Catalog.FindCreatureGeometryLocatorAsync(h.Selection, null, budget, default); await reached.Task;
        await h.Source.Catalog.Refresh(); release.SetResult();
        Assert.Equal("CREATURES_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => read)).Code); Assert.Equal(0, budget.Active);
    }

    [Fact]
    public async Task Cancellation_at_cpu_grant_retires_the_granted_scope_before_return()
    {
        await using var h = await Harness.Create(); using var stop = new CancellationTokenSource();
        var budget = new Budget { Granted = () => stop.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Catalog.FindCreatureGeometryLocatorAsync(h.Selection, null, budget, stop.Token));
        Assert.Equal(0, budget.Active); Assert.Empty(h.Source.Prepared);
    }

    [Theory]
    [InlineData("fields")] [InlineData("slots")] [InlineData("nullSlot")] [InlineData("source")] [InlineData("policy")]
    public async Task Malformed_or_inconsistent_compact_C1_evidence_is_a_typed_refusal(string defect)
    {
        await using var h = await Harness.Create(); var core = h.Selection.Core;
        core = defect switch
        {
            "fields" => core with { Snapshot = core.Snapshot with { Fields = null! } },
            "slots" => core with { Summary = core.Summary! with { DisplaySlots = null! } },
            "nullSlot" => core with { Summary = core.Summary! with { DisplaySlots = [null!, .. core.Summary!.DisplaySlots.Skip(1)] } },
            "source" => core with { Snapshot = core.Snapshot with { CreatureSource = core.Snapshot.CreatureSource with { ArtifactId = new('f', 64) } } },
            _ => core with { Snapshot = core.Snapshot with { SemanticProfile = "different" } }
        };
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.FindCreatureGeometryLocatorAsync(h.Selection with { Core = core }, null, new Budget(), default));
    }

    [Fact]
    public async Task Original_capture_keeps_first_canonical_receipt_and_lookup_sources_without_restamping()
    {
        await using var h = await Harness.Create();
        var candidate = h.Receipt with { RetrievedAt = "2026-10-20T00:00:00Z", VerifiedAt = "2026-10-20T00:00:00Z" };
        var found = await h.Catalog.ReadGeometryOriginalSourcesAsync(h.Association, h.Expectation, candidate, default);
        Assert.Equal(h.Receipt, found.Receipt); Assert.Equal(h.Encoding, found.EncodingLookup); Assert.Equal(h.Root, found.RootLookup);
        var again = await h.Catalog.ReadGeometryOriginalSourcesAsync(h.Association, h.Expectation, candidate, default);
        Assert.Equal(found, again);
    }

    [Fact]
    public async Task Source_membership_snapshot_reads_count_once_and_never_enumerates_caller_collections()
    {
        await using var h = await Harness.Create(); var changing = new ChangingMemberships(h.Association.Memberships[0], false);
        var source = h.Association with { Memberships = changing };
        Assert.Equal(h.Receipt, (await h.Catalog.ReadGeometryOriginalSourcesAsync(source, h.Expectation, h.Receipt, default)).Receipt);
        Assert.Equal(1, changing.CountReads);
        var missing = h.Association with { Memberships = new ChangingMemberships(h.Association.Memberships[0], true) };
        Assert.Equal("CREATURE_GEOMETRY_SOURCE_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadGeometryOriginalSourcesAsync(missing, h.Expectation, h.Receipt, default))).Code);
    }

    [Fact]
    public async Task Source_membership_capacity_refuses_before_copying_an_over_limit_certificate()
    {
        await using var h = await Harness.Create(); var source = h.Association with { Memberships = Enumerable.Repeat(h.Association.Memberships[0], 257).ToArray() };
        Assert.Equal("CREATURE_GEOMETRY_RESOURCE_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadGeometryOriginalSourcesAsync(source, h.Expectation, h.Receipt, default))).Code);
    }

    [Fact]
    public async Task Asset_resolution_root_v1_copy_reads_against_the_root_scopes_saved_copy()
    {
        // #58: asset resolution labels its ROOT copy and resolver source root-v1; metadata_objects keeps the root scope's metadata-blte-source-v1 copy.
        await using var h = await Harness.Create(); var root = h.Association.RootSource with { ParserRevision = "root-v1" };
        Assert.Equal(MetadataAcquirer.SourceRevision, (await h.Catalog.FindMetadataAsync(root.EncodingKey, default))!.ParserRevision);
        await h.Sql("DELETE FROM resolver_artifacts WHERE role='root'"); await h.Catalog.RecordResolverArtifactAsync(h.Root with { Source = root }, default);
        Assert.Equal(h.Receipt, (await h.Catalog.ReadGeometryOriginalSourcesAsync(h.Association with { RootSource = root }, h.Expectation, h.Receipt, default)).Receipt);
    }

    [Fact]
    public async Task Full_candidate_is_inserted_atomically_only_after_all_canonical_proofs_qualify()
    {
        await using var h = await Harness.Create();
        await h.Sql("DELETE FROM content_objects; DELETE FROM resolver_artifacts WHERE role='root';");
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadGeometryOriginalSourcesAsync(h.Association, h.Expectation, h.Receipt, default));
        Assert.Null(await h.Catalog.FindContentAsync(h.Receipt.EncodingKey, default));
        await h.Catalog.RecordResolverArtifactAsync(h.Root, default);
        Assert.Equal(h.Receipt, (await h.Catalog.ReadGeometryOriginalSourcesAsync(h.Association, h.Expectation, h.Receipt, default)).Receipt);
        Assert.Equal(h.Receipt, await h.Catalog.FindContentAsync(h.Receipt.EncodingKey, default));
    }

    [Theory]
    [InlineData("content")] [InlineData("metadata")] [InlineData("resolver")]
    public async Task Oversized_canonical_JSON_is_refused_before_materialization(string table)
    {
        await using var h = await Harness.Create();
        var sql = table switch { "content" => "UPDATE content_objects SET receipt=$v", "metadata" => "UPDATE metadata_objects SET receipt=$v", _ => "UPDATE resolver_artifacts SET receipt=$v" };
        await h.Sql(sql, new string('x', (128 << 10) + 1));
        Assert.Equal("CREATURE_GEOMETRY_RESOURCE_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadGeometryOriginalSourcesAsync(h.Association, h.Expectation, h.Receipt, default))).Code);
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("unknown")] [InlineData("nestedUnknown")] [InlineData("nullLocation")]
    public async Task Poisoned_canonical_receipt_does_not_become_a_full_source_proof(string defect)
    {
        await using var h = await Harness.Create(); var json = JsonSerializer.Serialize(h.Receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json = defect switch
        {
            "duplicate" => json.Replace("\"contentKey\":", "\"contentKey\":\"" + h.Receipt.ContentKey + "\",\"contentKey\":"),
            "unknown" => json[..^1] + ",\"unused\":[]}",
            "nestedUnknown" => json.Replace("\"location\":{", "\"location\":{\"unused\":[{}],"),
            _ => JsonSerializer.Serialize(h.Receipt with { Location = null! }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        await h.Sql("UPDATE content_objects SET receipt=$v", json);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadGeometryOriginalSourcesAsync(h.Association, h.Expectation, h.Receipt, default));
    }

    [Theory]
    [InlineData("contentBytes")] [InlineData("metadataHistory")] [InlineData("configHistory")] [InlineData("resolverSource")]
    public async Task Canonical_alias_or_original_history_mutation_cannot_authorize_sources(string defect)
    {
        await using var h = await Harness.Create();
        switch (defect)
        {
            case "contentBytes": await h.Sql("UPDATE content_objects SET sha256=$v", new('f', 64)); break;
            case "metadataHistory": await h.Sql("UPDATE metadata_objects SET receipt=$v WHERE ekey='" + h.Association.RootSource.EncodingKey + "'", JsonSerializer.Serialize(h.Association.RootSource with { RetrievedAt = "2026-10-20T00:00:00Z" }, new JsonSerializerOptions(JsonSerializerDefaults.Web))); break;
            case "configHistory": await h.Sql("UPDATE configuration_objects SET retrieved_at='2026-10-20T00:00:00Z'"); break;
            default: await h.Sql("UPDATE resolver_artifacts SET decoded_sha256=$v WHERE role='root'", "malformed"); break;
        }
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadGeometryOriginalSourcesAsync(h.Association, h.Expectation, h.Receipt, default));
    }

    [Fact]
    public async Task Cancellation_and_projection_candidates_never_insert_canonical_full_proofs()
    {
        await using var h = await Harness.Create(); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Catalog.ReadGeometryOriginalSourcesAsync(h.Association, h.Expectation, h.Receipt, stop.Token));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadGeometryOriginalSourcesAsync(h.Association, h.Expectation, h.Receipt with { VerifierRevision = "content-blte-projection-v1" }, default));
        Assert.Equal(h.Receipt, await h.Catalog.FindContentAsync(h.Receipt.EncodingKey, default));
    }

    private sealed class Budget : IGeometryArtifactWorkBudget
    {
        internal int Active, Entries; internal Func<int, Task>? Before; internal Action? Granted;
        public async ValueTask<IDisposable> EnterAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); var entry = Interlocked.Increment(ref Entries); if (Before is not null) await Before(entry);
            token.ThrowIfCancellationRequested(); Assert.Equal(1, Interlocked.Increment(ref Active)); Granted?.Invoke(); return new Scope(this);
        }
        private sealed class Scope(Budget owner) : IDisposable { public void Dispose() => Assert.Equal(0, Interlocked.Decrement(ref owner.Active)); }
    }
    private sealed class ChangingMemberships(RootTargetMatch member, bool vanish) : IReadOnlyList<RootTargetMatch>
    {
        internal int CountReads;
        public int Count => ++CountReads == 1 ? 1 : int.MaxValue;
        public RootTargetMatch this[int index] => vanish ? throw new ArgumentOutOfRangeException(nameof(index)) : member;
        public IEnumerator<RootTargetMatch> GetEnumerator() => throw new InvalidOperationException("An external certificate must be copied by a bounded index snapshot.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal CreatureDisplaySourcesTests.Fixture Source = null!;
        private CreatureDisplayMetadataStore store = null!;
        internal GeometryDisplayAuthorization Selection = null!;
        internal DiscoveryCatalog Catalog => Source.Catalog.Catalog;
        internal AssetSourceAssociation Association = null!;
        internal ContentReceipt Receipt = null!;
        internal MetadataExpectation Expectation = null!;
        internal ResolverArtifact Encoding = null!, Root = null!;
        internal static async Task<Harness> Create()
        {
            var h = new Harness { Source = await CreatureDisplaySourcesTests.Fixture.Create() };
            h.store = new(h.Source.Sources()); h.Selection = (await h.store.AuthorizeGeometryAsync(h.Source.Request, default)).Authorization!;
            h.Receipt = h.Source.Core.Artifact.Binding.Source.Source; var original = h.Source.Core.Artifact.Binding.Source.Association;
            h.Association = new(new(h.Source.Core.Snapshot.Context.ProductId, h.Source.Core.Snapshot.Context.BuildIdentity, "us"), "enUS", h.Source.Core.Opening.Reference,
                h.Source.Core.Artifact.Binding.Configurations, original.EncodingSource, original.RootSource, AssetResolutionSession.VariantPolicy,
                [new(0, 0, new(777, h.Receipt.ContentKey, 2, 0, new(0, 0, 0), null))]);
            h.Expectation = new("asset", h.Receipt.ContentKey, h.Receipt.EncodingKey, long.Parse(h.Receipt.EncodedBytes, CultureInfo.InvariantCulture), long.Parse(h.Receipt.DecodedBytes, CultureInfo.InvariantCulture));
            h.Encoding = new(original.EncodingSource, ResolverArtifacts.EncodingRevision, new('1', 64), long.Parse(original.EncodingSource.DecodedBytes, CultureInfo.InvariantCulture));
            h.Root = new(original.RootSource, AssetResolutionSessions.RootArtifactRevision, new('2', 64), long.Parse(original.RootSource.DecodedBytes, CultureInfo.InvariantCulture));
            await h.Catalog.RecordResolverArtifactAsync(h.Encoding, default); await h.Catalog.RecordResolverArtifactAsync(h.Root, default); return h;
        }
        internal Task Sql(string sql, string? value = null) => Source.Catalog.Sql(sql, value);
        public async ValueTask DisposeAsync() { await store.DisposeAsync(); await Source.DisposeAsync(); }
    }
}
