using Microsoft.Data.Sqlite;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Creature projection builder")]
public sealed class CreatureGeometryPublicationTests
{
    [Fact]
    public async Task Actual_two_source_publication_returns_first_owned_locator_and_local_reuse_needs_no_original_files()
    {
        await using var h = await Built.Create();
        var first = Assert.IsType<GeometryLocator>(await h.Publish());
        Assert.Equal(h.Artifact!.Manifest.ArtifactId, first.Descriptor.Manifest.ArtifactId); Assert.Equal(2, first.Sources.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<float>)first.Descriptor.Manifest.Bounds.Min)[0] = 99);
        h.Fixture.Http.Offline = true; var demands = h.Fixture.Http.Requests.Count;
        foreach (var source in new[] { first.Capsule.Model, first.Capsule.Skin })
            File.Delete(Path.Combine(h.Fixture.Projection.DirectoryPath, "objects-v1", source.Receipt.Sha256[..2], source.Receipt.Sha256[2..]));
        var retained = Assert.IsType<GeometryLocator>(await h.Fixture.Catalog.FindCreatureGeometryLocatorAsync(h.Fixture.Selection, first.Descriptor.Manifest.ArtifactId, h.Budget, default));
        await h.Fixture.Catalog.FinalizeCreatureGeometryReuseAsync(h.Fixture.Selection, retained, h.Budget, default);
        Assert.Equal(first.AuthoritySha256, retained.AuthoritySha256); Assert.Equal(first.CanonicalUtf8.ToArray(), retained.CanonicalUtf8.ToArray());
        Assert.Equal(demands, h.Fixture.Http.Requests.Count);
    }

    [Fact]
    public async Task Parsed_zero_declared_triangles_still_fences_sources_without_negative_registration()
    {
        await using var h = await Built.Create(empty: true);
        Assert.Equal(0, h.Prepared.Closure.Capsule.Counts.SectionTriangleIndices);
        Assert.Null(await h.Publish()); Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM geometry_artifacts"));
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM geometry_source_locators"));
        Assert.Null(await h.Fixture.Catalog.FindCreatureGeometryLocatorAsync(h.Fixture.Selection, null, h.Budget, default));
    }

    [Theory]
    [InlineData("display")] [InlineData("model")] [InlineData("creature")] [InlineData("resolver")]
    [InlineData("configuration")] [InlineData("fullSource")] [InlineData("sourceInvalidity")] [InlineData("lookupInvalidity")]
    [InlineData("coreInvalidity")] [InlineData("tableInvalidity")] [InlineData("metadataInvalidity")] [InlineData("configInvalidity")] [InlineData("skinInvalidity")]
    [InlineData("fullSourceHistory")]
    public async Task Fresh_publication_rechecks_all_original_authority_and_invalidity_in_final_transaction(string mutation)
    {
        await using var h = await Built.Create(); await h.Mutate(mutation);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Publish());
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM geometry_artifacts")); Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM geometry_source_locators"));
        await using var orphan = await h.Fixture.Projection.Objects.OpenVerifiedAsync(h.Artifact!.Manifest.Sha256, h.Artifact.Manifest.ByteLength, default);
        Assert.Equal(h.Artifact.Manifest.ByteLength, orphan.Length);
    }

    [Theory]
    [InlineData("display")] [InlineData("model")] [InlineData("resolver")] [InlineData("sourceInvalidity")]
    public async Task Zero_triangles_cannot_bypass_final_currentness_or_corruption(string mutation)
    {
        await using var h = await Built.Create(empty: true); await h.Mutate(mutation);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Publish()); Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM geometry_source_locators"));
    }

    [Theory]
    [InlineData("display")] [InlineData("model")] [InlineData("resolver")] [InlineData("fullSource")]
    [InlineData("sourceInvalidity")] [InlineData("lookupInvalidity")] [InlineData("artifactInvalidity")] [InlineData("bindingColumn")]
    [InlineData("coreInvalidity")] [InlineData("tableInvalidity")] [InlineData("metadataInvalidity")] [InlineData("configInvalidity")] [InlineData("skinInvalidity")]
    [InlineData("fullSourceHistory")]
    public async Task Historical_locator_is_only_a_candidate_until_same_final_fence_passes(string mutation)
    {
        await using var h = await Built.Create(); var retained = Assert.IsType<GeometryLocator>(await h.Publish()); await h.Mutate(mutation);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Fixture.Catalog.FinalizeCreatureGeometryReuseAsync(h.Fixture.Selection, retained, h.Budget, default));
        Assert.Equal(1L, await h.Scalar("SELECT count(*) FROM geometry_artifacts")); Assert.Equal(1L, await h.Scalar("SELECT count(*) FROM geometry_source_locators"));
    }

    [Fact]
    public async Task Locator_ownership_cannot_cross_catalog_instances()
    {
        await using var first = await Built.Create(); var retained = Assert.IsType<GeometryLocator>(await first.Publish());
        await using var second = await Built.Create();
        Assert.Equal("CREATURE_GEOMETRY_SOURCE_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => second.Fixture.Catalog.FinalizeCreatureGeometryReuseAsync(second.Fixture.Selection, retained, second.Budget, default))).Code);
    }

    [Fact]
    public async Task Cancellation_before_commit_leaves_no_ready_registration()
    {
        await using var h = await Built.Create(); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Fixture.Catalog.FinalizeCreatureGeometryAsync(h.Fixture.Selection, h.Prepared.Closure, h.Artifact, GeometryFinalAction.Publish, h.Budget, stop.Token));
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM geometry_source_locators"));
    }

    [Fact]
    public async Task Cold_B_reverification_preserves_first_complete_authority_and_compact_source_hashes()
    {
        await using var h = await Built.Create(); var first = Assert.IsType<GeometryLocator>(await h.Publish());
        var original = first.CanonicalUtf8.ToArray(); h.Prepared = await h.Fixture.PrepareAsync();
        Assert.NotEqual(first.AuthoritySha256, h.Prepared.Closure.AuthoritySha256);
        var repeated = Assert.IsType<GeometryLocator>(await h.Publish());
        Assert.Equal(first.AuthoritySha256, repeated.AuthoritySha256); Assert.Equal(original, repeated.CanonicalUtf8.ToArray());
        Assert.Equal(first.Sources, repeated.Sources); Assert.Equal(1L, await h.Scalar("SELECT count(*) FROM geometry_artifacts"));
        Assert.Equal(1L, await h.Scalar("SELECT count(*) FROM geometry_source_locators"));
    }

    [Fact]
    public async Task One_immutable_data_binding_cannot_be_reassociated_with_another_output()
    {
        await using var h = await Built.Create(); var first = Assert.IsType<GeometryLocator>(await h.Publish());
        var conflict = h.Artifact! with { Manifest = h.Artifact!.Manifest with { ArtifactId = new('f', 64), Sha256 = new('e', 64) } };
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Fixture.Catalog.FinalizeCreatureGeometryAsync(h.Fixture.Selection, h.Prepared.Closure, conflict, GeometryFinalAction.Publish, h.Budget, default));
        var current = Assert.IsType<GeometryLocator>(await h.Fixture.Catalog.FindCreatureGeometryLocatorAsync(h.Fixture.Selection, first.Descriptor.Manifest.ArtifactId, h.Budget, default));
        Assert.Equal(first.AuthoritySha256, current.AuthoritySha256); Assert.Equal(1L, await h.Scalar("SELECT count(*) FROM geometry_artifacts"));
    }

    [Fact]
    public async Task Fresh_nonempty_source_cannot_claim_zero_triangles_or_missing_publish_output()
    {
        await using var h = await Built.Create();
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Fixture.Catalog.FinalizeCreatureGeometryAsync(h.Fixture.Selection, h.Prepared.Closure, null, GeometryFinalAction.ValidateNoDeclaredTriangles, h.Budget, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Fixture.Catalog.FinalizeCreatureGeometryAsync(h.Fixture.Selection, h.Prepared.Closure, null, GeometryFinalAction.Publish, h.Budget, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Fixture.Catalog.FinalizeCreatureGeometryAsync(h.Fixture.Selection, h.Prepared.Closure, h.Artifact, GeometryFinalAction.ValidateNoDeclaredTriangles, h.Budget, default));
        Assert.Equal(0L, await h.Scalar("SELECT count(*) FROM geometry_source_locators"));
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("unknownNested")] [InlineData("nullBounds")] [InlineData("oversized")]
    [InlineData("missingZeroOrdinals")]
    [InlineData("negativeZeroMin")] [InlineData("negativeZeroMax")]
    public async Task Persisted_geometry_descriptor_is_bounded_and_strict_before_materialization(string mutation)
    {
        await using var h = await Built.Create(); var locator = Assert.IsType<GeometryLocator>(await h.Publish());
        var json = System.Text.Json.JsonSerializer.Serialize(h.Artifact, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        json = mutation switch
        {
            "duplicate" => json.Replace("\"artifactId\":", "\"artifactId\":\"" + h.Artifact!.Manifest.ArtifactId + "\",\"artifactId\":"),
            "unknownNested" => json.Replace("\"bounds\":{", "\"bounds\":{\"unrecognized\":[{}],"),
            "nullBounds" => System.Text.Json.JsonSerializer.Serialize(h.Artifact! with { Manifest = h.Artifact.Manifest with { Bounds = null! } }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            "missingZeroOrdinals" => json.Replace(",\"mainSkinOrdinal\":0", ""),
            "negativeZeroMin" => NegativeZero(json, "min"),
            "negativeZeroMax" => NegativeZero(json, "max"),
            _ => new string('x', (64 << 10) + 1)
        };
        await h.Sql("UPDATE geometry_artifacts SET descriptor=$v", json);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Fixture.Catalog.FindCreatureGeometryLocatorAsync(h.Fixture.Selection, locator.Descriptor.Manifest.ArtifactId, h.Budget, default));
    }

    private static string NegativeZero(string json, string bound)
    {
        var document = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var values = document["manifest"]!["bounds"]![bound]!.AsArray();
        var index = Enumerable.Range(0, 3).First(i => values[i]!.GetValue<float>() == 0);
        values[index] = System.Text.Json.Nodes.JsonValue.Create(BitConverter.Int32BitsToSingle(int.MinValue));
        return document.ToJsonString();
    }

    private sealed class Built : IAsyncDisposable
    {
        internal CreatureGeometrySourcesFixture Fixture = null!;
        internal GeometryPreparedSources Prepared = null!;
        internal GeometryArtifactDescriptor? Artifact;
        internal GeometryWorkBudget Budget => new(Fixture.Resources);
        internal static async Task<Built> Create(bool empty = false)
        {
            var h = new Built { Fixture = await CreatureGeometrySourcesFixture.CreateAsync(empty) };
            try
            {
                h.Prepared = await h.Fixture.PrepareAsync();
                if (!empty)
                {
                    using var output = new MemoryStream(); var artifact = await ModelGeometryArtifacts.BuildAsync(h.Prepared.Skin, h.Prepared.DataBinding, output, h.Budget, default);
                    output.Position = 0; await ModelGeometryArtifacts.VerifyAsync(output, artifact, h.Budget, default);
                    h.Artifact = new(artifact.Binding, Map(artifact.Manifest));
                    string path; await using (var staging = await h.Fixture.Projection.Objects.CreateStagingFileAsync(default)) { path = staging.Name; await staging.WriteAsync(output.ToArray()); }
                    var sha = await h.Fixture.Projection.Objects.PublishFileAsync(path, artifact.ByteLength, h.Fixture.Resources, null, default); Assert.Equal(artifact.Sha256, sha);
                }
                return h;
            }
            catch { await h.DisposeAsync(); throw; }
        }
        internal Task<GeometryLocator?> Publish() => Fixture.Catalog.FinalizeCreatureGeometryAsync(Fixture.Selection, Prepared.Closure, Artifact,
            Artifact is null ? GeometryFinalAction.ValidateNoDeclaredTriangles : GeometryFinalAction.Publish, Budget, default);
        internal async Task Mutate(string mutation)
        {
            switch (mutation)
            {
                case "display": await Sql("UPDATE table_admissions SET prepared_at='2026-10-20T00:00:00Z' WHERE table_id='bfdaf9f1'"); break;
                case "model": await Sql("UPDATE table_admissions SET prepared_at='2026-10-20T00:00:00Z' WHERE table_id='7f695fa5'"); break;
                case "creature": await Sql("UPDATE creatures_artifacts SET descriptor=replace(descriptor,'\"featureRevision\":\"creatures-core-v1\"','\"featureRevision\":\"changed\"')"); break;
                case "resolver": await Sql("UPDATE resolver_artifacts SET decoded_sha256='" + new string('f', 64) + "' WHERE role='root'"); break;
                case "configuration": await Sql("UPDATE configuration_objects SET retrieved_at='2026-10-20T00:00:00Z'"); break;
                case "fullSource": await Sql("DELETE FROM content_objects WHERE ekey='" + Prepared.Closure.Capsule.Skin.Receipt.EncodingKey + "'"); break;
                case "fullSourceHistory":
                    var receipt = Prepared.Closure.Capsule.Skin.Receipt with { RetrievedAt = "2026-10-20T00:00:00Z" };
                    await Sql("UPDATE content_objects SET receipt=$v WHERE ekey='" + receipt.EncodingKey + "'", System.Text.Json.JsonSerializer.Serialize(receipt, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))); break;
                case "bindingColumn": await Sql("UPDATE geometry_artifacts SET data_binding_sha256='" + new string('f', 64) + "'"); break;
                default:
                    var source = Prepared.Closure.Capsule;
                    var identity = mutation switch
                    {
                        "lookupInvalidity" => (source.Resolvers[1].DecodedSha256, source.Resolvers[1].DecodedBytes),
                        "artifactInvalidity" => (Artifact!.Manifest.Sha256, Artifact.Manifest.ByteLength),
                        "coreInvalidity" => (Fixture.Selection.Core.Artifact.Sha256, Fixture.Selection.Core.Artifact.ByteLength),
                        "tableInvalidity" => (Fixture.Selection.Display.Binding.Admission.Artifact.Sha256, Fixture.Selection.Display.Binding.Admission.Artifact.ByteLength),
                        "metadataInvalidity" => (source.Model.Association.RootSource.Sha256, long.Parse(source.Model.Association.RootSource.EncodedBytes)),
                        "configInvalidity" => (source.Model.Association.Configurations[0].Sha256, source.Model.Association.Configurations[0].ByteLength),
                        "skinInvalidity" => (source.Skin.Receipt.Sha256, long.Parse(source.Skin.Receipt.EncodedBytes)),
                        _ => (source.Model.Receipt.Sha256, long.Parse(source.Model.Receipt.EncodedBytes))
                    };
                    await Fixture.Catalog.RecordObjectInvalidityAsync(identity.Item1, identity.Item2, "LOCAL_OBJECT_CORRUPT", default); break;
            }
        }
        internal async Task Sql(string sql, string? value = null)
        { await using var db = new SqliteConnection("Data Source=" + Path.Combine(Fixture.Projection.DirectoryPath, "discovery-v1.sqlite3")); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = sql; if (value is not null) command.Parameters.AddWithValue("$v", value); await command.ExecuteNonQueryAsync(); }
        internal async Task<object?> Scalar(string sql)
        { await using var db = new SqliteConnection("Data Source=" + Path.Combine(Fixture.Projection.DirectoryPath, "discovery-v1.sqlite3")); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(); }
        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
        private static CreatureGeometryManifest Map(GeometryManifest m) => new(m.ArtifactId, m.BindingSha256, m.Sha256, m.ByteLength, m.FormatVersion, m.HeaderBytes,
            m.Scope, m.CoordinatePolicy, new(m.Evaluations.Material, m.Evaluations.Pose, m.Evaluations.Visibility, m.Evaluations.Appearance), m.VertexCount, m.VertexLookupCount,
            m.SourceTriangleIndexCount, m.SectionIndexCount, m.LineIndexCount, m.SectionCount, new(m.Positions.Offset, m.Positions.ByteLength), new(m.LineIndices.Offset, m.LineIndices.ByteLength),
            new(m.Sections.Offset, m.Sections.ByteLength), new(m.Bounds.Min, m.Bounds.Max), m.ModelFileDataId, m.SkinFileDataId, m.MainSkinOrdinal, m.ModelReaderRevision, m.SkinReaderRevision,
            m.InterpretationPolicy, m.ArtifactRevision, m.EdgePolicy);
    }
}
