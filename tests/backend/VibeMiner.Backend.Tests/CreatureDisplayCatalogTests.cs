using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Creature projection builder")]
public sealed class CreatureDisplayCatalogTests
{
    private const string Display = "bfdaf9f1", Model = "7f695fa5", Time = CreaturesProjectionTests.Fixture.Time;

    [Fact]
    public async Task CompleteChainAndCoreOnlyFencePreserveEveryOriginalReceiptAcrossRestart()
    {
        await using var h = await Harness.Create(); var original = JsonSerializer.Serialize(h.Targets, CreaturesPolicy.Json);
        await h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, [], default);
        await h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, h.Targets, default);
        await h.Catalog.DisposeAsync(); h.Catalog = await DiscoveryCatalog.OpenAsync(h.Projection.DirectoryPath);
        foreach (var target in h.Targets)
            Assert.Equal(JsonSerializer.Serialize(target.Admission, CreaturesPolicy.Json), JsonSerializer.Serialize(await h.Catalog.ReadCreatureDisplayTargetAsync(h.Opening, target.TableId, default), CreaturesPolicy.Json));
        await h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, h.Targets, default);
        Assert.Equal(original, JsonSerializer.Serialize(h.Targets, CreaturesPolicy.Json));
    }

    [Fact]
    public async Task ModelReplacementAfterDisplayProofRejectsMixedGenerationButDisplayOnlyRemainsValid()
    {
        await using var h = await Harness.Create();
        var display = await h.Catalog.ReadCreatureDisplayTargetAsync(h.Opening, Display, default);
        var oldModel = await h.Catalog.ReadCreatureDisplayTargetAsync(h.Opening, Model, default);
        await h.Sql("UPDATE table_admissions SET prepared_at='2026-10-20T00:00:00Z' WHERE table_id='7f695fa5'");
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, [new(Display, display!), new(Model, oldModel!)], default));
        Assert.Equal("CREATURE_DISPLAY_STALE", error.Code); Assert.True(error.Retryable);
        await h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, [new(Display, display!)], default);
    }

    [Fact]
    public async Task UnusedModelSourceIsNeverReadOrCharged()
    {
        await using var h = await Harness.Create();
        await h.Sql("UPDATE table_artifacts SET descriptor=$v WHERE content_key=(SELECT content_key FROM table_admissions WHERE table_id='7f695fa5')", new string('x', 131073));
        await h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, [], default);
        await h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, [h.Targets[0]], default);
        Assert.Equal("CREATURE_DISPLAY_RESOURCE_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, h.Targets, default))).Code);
    }

    [Theory]
    [InlineData("source")] [InlineData("association")] [InlineData("descriptor")] [InlineData("content")] [InlineData("metadata")]
    public async Task OversizedPersistedJsonFailsLimitBeforeMalformedJsonCanBeMaterialized(string column)
    {
        await using var h = await Harness.Create();
        var sql = column switch
        {
            "descriptor" => "UPDATE table_artifacts SET descriptor=$v WHERE content_key=(SELECT content_key FROM table_admissions WHERE table_id='bfdaf9f1')",
            "content" => "UPDATE content_objects SET receipt=$v",
            "metadata" => "UPDATE metadata_objects SET receipt=$v",
            _ => "UPDATE table_admissions SET " + column + "=$v WHERE table_id='bfdaf9f1'"
        };
        await h.Sql(sql, new string('x', 131073));
        Assert.Equal("CREATURE_DISPLAY_RESOURCE_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadCreatureDisplayTargetAsync(h.Opening, Display, default))).Code);
    }

    [Fact]
    public async Task MultibyteJsonBudgetUsesUtf8BytesRatherThanCharacters()
    {
        await using var h = await Harness.Create();
        await h.Sql("UPDATE table_admissions SET association=$v WHERE table_id='bfdaf9f1'", new string('\u0800', 44000));
        Assert.Equal("CREATURE_DISPLAY_RESOURCE_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadCreatureDisplayTargetAsync(h.Opening, Display, default))).Code);
    }

    [Fact]
    public async Task EntireStoredBundleIsCheckedBeforeFirstMalformedFieldIsDeserialized()
    {
        await using var h = await Harness.Create();
        await h.Sql("UPDATE table_admissions SET source=$v WHERE table_id='bfdaf9f1'", new string('x', 100000));
        await h.Sql("UPDATE table_admissions SET association=$v WHERE table_id='bfdaf9f1'", new string('x', 40000));
        Assert.Equal("CREATURE_DISPLAY_RESOURCE_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadCreatureDisplayTargetAsync(h.Opening, Display, default))).Code);
    }

    [Fact]
    public async Task TableArtifactAliasCannotPointAtDifferentValidInterpretation()
    {
        await using var h = await Harness.Create();
        await h.Sql("UPDATE table_artifacts SET descriptor=$v WHERE content_key=(SELECT content_key FROM table_admissions WHERE table_id='bfdaf9f1')", JsonSerializer.Serialize(h.Targets[1].Admission.Artifact, CreaturesPolicy.Json));
        Assert.Equal("CREATURE_DISPLAY_BINDING_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadCreatureDisplayTargetAsync(h.Opening, Display, default))).Code);
    }

    [Theory]
    [InlineData("columns")] [InlineData("Columns")] [InlineData("\\u0063olumns")]
    public async Task HeaderColumnsAreCountedBeforeDtoArrayConstruction(string property)
    {
        await using var h = await Harness.Create(); var rows = h.Targets[0].Admission.Artifact;
        var json = JsonSerializer.Serialize(rows with { Header = rows.Header with { Columns = Enumerable.Repeat(rows.Header.Columns[0], 513).ToArray() } }, CreaturesPolicy.Json);
        json = json.Replace("\"columns\":", "\"" + property + "\":", StringComparison.Ordinal);
        await h.Sql("UPDATE table_artifacts SET descriptor=$v WHERE content_key=(SELECT content_key FROM table_admissions WHERE table_id='bfdaf9f1')", json);
        Assert.Equal("CREATURE_DISPLAY_RESOURCE_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadCreatureDisplayTargetAsync(h.Opening, Display, default))).Code);
    }

    [Fact]
    public async Task SelectedMembershipsAreCountedBeforeDtoArrayConstruction()
    {
        await using var h = await Harness.Create(); var association = h.Targets[0].Admission.Association;
        await h.Sql("UPDATE table_admissions SET association=$v WHERE table_id='bfdaf9f1'", JsonSerializer.Serialize(association with { Memberships = Enumerable.Repeat(association.Memberships[0], 257).ToArray() }, CreaturesPolicy.Json));
        Assert.Equal("CREATURE_DISPLAY_RESOURCE_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadCreatureDisplayTargetAsync(h.Opening, Display, default))).Code);
    }

    [Fact]
    public async Task ArrayPreflightIsPathSpecificAndDoesNotImposeAnUnrelatedGenericArrayCap()
    {
        await using var h = await Harness.Create(); var json = JsonSerializer.Serialize(h.Targets[0].Admission.Artifact, CreaturesPolicy.Json);
        json = "{\"unrelated\":{\"columns\":[" + string.Join(',', Enumerable.Repeat("{}", 513)) + "]}," + json[1..];
        await h.Sql("UPDATE table_artifacts SET descriptor=$v WHERE content_key=(SELECT content_key FROM table_admissions WHERE table_id='bfdaf9f1')", json);
        Assert.NotNull(await h.Catalog.ReadCreatureDisplayTargetAsync(h.Opening, Display, default));
    }

    [Theory]
    [InlineData("source")] [InlineData("association")] [InlineData("descriptor")]
    public async Task SmallMalformedDescriptorFailsTypedWithoutAcquisition(string column)
    {
        await using var h = await Harness.Create();
        await h.Sql(column == "descriptor" ? "UPDATE table_artifacts SET descriptor=$v WHERE content_key=(SELECT content_key FROM table_admissions WHERE table_id='bfdaf9f1')" : "UPDATE table_admissions SET " + column + "=$v WHERE table_id='bfdaf9f1'", "null");
        Assert.Equal("CREATURE_DISPLAY_BINDING_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadCreatureDisplayTargetAsync(h.Opening, Display, default))).Code);
    }

    [Theory]
    [InlineData("decoder")] [InlineData("columns")] [InlineData("memberships")] [InlineData("metadata")]
    public async Task NestedNullEvidenceFailsTyped(string field)
    {
        await using var h = await Harness.Create(); var target = h.Targets[0].Admission;
        if (field is "decoder" or "columns")
        {
            var rows = field == "decoder" ? target.Artifact with { Binding = target.Artifact.Binding with { DecoderRevision = null! } } : target.Artifact with { Header = target.Artifact.Header with { Columns = null! } };
            await h.Sql("UPDATE table_artifacts SET descriptor=$v WHERE content_key=(SELECT content_key FROM table_admissions WHERE table_id='bfdaf9f1')", JsonSerializer.Serialize(rows, CreaturesPolicy.Json));
        }
        else
        {
            var association = field == "memberships" ? target.Association with { Memberships = null! } : target.Association with { EncodingSource = null! };
            await h.Sql("UPDATE table_admissions SET association=$v WHERE table_id='bfdaf9f1'", JsonSerializer.Serialize(association, CreaturesPolicy.Json));
        }
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadCreatureDisplayTargetAsync(h.Opening, Display, default));
        Assert.Equal(field == "decoder" ? "TABLE_ARTIFACT_INVALID" : "CREATURE_DISPLAY_BINDING_INVALID", error.Code);
    }

    [Fact]
    public async Task LatestOpeningReplacementRejectsOldCoreAndLocalTargetRead()
    {
        await using var h = await Harness.Create(); await h.Refresh();
        Assert.Equal("CREATURES_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, h.Targets, default))).Code);
        Assert.Equal("CREATURES_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadCreatureDisplayTargetAsync(h.Opening, Display, default))).Code);
    }

    [Fact]
    public async Task SameBytesAndLengthDoNotAuthorizeForgedCoreDescriptorFacts()
    {
        await using var h = await Harness.Create(); var forged = h.Core with { Counts = h.Core.Counts with { ValidNames = "0", EmptyNames = "1" } };
        Assert.Equal("CREATURES_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, forged, [], default))).Code);
    }

    [Fact]
    public async Task MalformedCapturedCoreFailsTypedBeforeCatalogAuthorityTraversal()
    {
        await using var h = await Harness.Create();
        Assert.Equal("CREATURES_PROJECTION_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core with { Binding = null! }, [], default))).Code);
    }

    [Fact]
    public async Task OnlyVerificationTimeMayDifferAndOriginalRetrievalAuthorityCannotBeForged()
    {
        await using var h = await Harness.Create(); var target = h.Targets[0];
        var verified = target with { Admission = target.Admission with { Source = target.Admission.Source with { VerifiedAt = "2026-10-20T00:00:00Z" } } };
        await h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, [verified], default);
        var forged = target with { Admission = target.Admission with { Source = target.Admission.Source with { RetrievedAt = "2026-10-20T00:00:00Z" } } };
        Assert.Equal("CREATURE_DISPLAY_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, [forged], default))).Code);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task LocalTargetReadRechecksOriginalCanonicalReceiptAuthority(bool metadata)
    {
        await using var h = await Harness.Create(); var source = h.Targets[0].Admission;
        if (metadata)
            await h.Sql("UPDATE metadata_objects SET receipt=$v WHERE ekey='" + source.Association.EncodingSource.EncodingKey + "'", JsonSerializer.Serialize(source.Association.EncodingSource with { RetrievedAt = "2026-10-20T00:00:00Z" }, CreaturesPolicy.Json));
        else
            await h.Sql("UPDATE content_objects SET receipt=$v", JsonSerializer.Serialize(source.Source with { RetrievedAt = "2026-10-20T00:00:00Z" }, CreaturesPolicy.Json));
        Assert.Equal("CREATURE_DISPLAY_BINDING_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadCreatureDisplayTargetAsync(h.Opening, Display, default))).Code);
    }

    [Fact]
    public async Task MissingDeliveredSourceIsStaleWhileMissingUnrequestedSourceIsSimplyLocalNotPrepared()
    {
        await using var h = await Harness.Create(); await h.Sql("DELETE FROM table_admissions WHERE table_id='bfdaf9f1'");
        Assert.Null(await h.Catalog.ReadCreatureDisplayTargetAsync(h.Opening, Display, default));
        Assert.Equal("CREATURE_DISPLAY_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, h.Targets, default))).Code);
        await h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, [], default);
    }

    [Fact]
    public async Task WrongTableReversedDuplicateAndModelOnlyBindingsFailClosed()
    {
        await using var h = await Harness.Create();
        foreach (var bad in new[] { new[] { h.Targets[1] }, new[] { h.Targets[1], h.Targets[0] }, new[] { h.Targets[0], h.Targets[0] }, new[] { new CreatureDisplayCatalogBinding("c9d6b6b3", h.Targets[0].Admission) } })
            Assert.Equal("CREATURE_DISPLAY_BINDING_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, bad, default))).Code);
        Assert.Equal("CREATURE_DISPLAY_BINDING_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadCreatureDisplayTargetAsync(h.Opening, "c9d6b6b3", default))).Code);
    }

    [Fact]
    public async Task CancellationBeforeFenceLeavesOriginalAdmissionsReusable()
    {
        await using var h = await Harness.Create(); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, h.Targets, stop.Token));
        await h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, h.Targets, default);
    }

    [Fact]
    public async Task CallerCountIsReadOnceAndVanishingIndexIsTypedBeforeScheduling()
    {
        await using var h = await Harness.Create(); var changing = new ChangingTargets(h.Targets[0], false);
        await h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, changing, default); Assert.Equal(1, changing.CountReads);
        var vanishing = new ChangingTargets(h.Targets[0], true);
        Assert.Equal("CREATURE_DISPLAY_BINDING_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ValidateCreatureDisplayCurrentAsync(h.Opening, h.Core, vanishing, default))).Code);
    }

    private sealed class ChangingTargets(CreatureDisplayCatalogBinding target, bool vanish) : IReadOnlyList<CreatureDisplayCatalogBinding>
    {
        internal int CountReads;
        public int Count => ++CountReads == 1 ? 1 : int.MaxValue;
        public CreatureDisplayCatalogBinding this[int index] => vanish ? throw new ArgumentOutOfRangeException(nameof(index)) : target;
        public IEnumerator<CreatureDisplayCatalogBinding> GetEnumerator() => throw new InvalidOperationException("The caller collection must not be enumerated.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal sealed class Harness : IAsyncDisposable
    {
        internal readonly CreaturesProjectionTests.Fixture Projection = new();
        private readonly InventoryFixture fixture = InventoryFixture.Create();
        internal DiscoveryCatalog Catalog = null!; internal CapturedOpeningContext Opening = null!; internal CreaturesArtifact Core = null!;
        internal CreatureDisplayCatalogBinding[] Targets = [];
        private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
        internal static async Task<Harness> Create()
        {
            var h = new Harness(); h.Catalog = await DiscoveryCatalog.OpenAsync(h.Projection.DirectoryPath); await h.Refresh();
            var configurations = new List<ConfigurationReceipt>();
            foreach (var (role, key, bytes) in new[] { ("buildConfig", h.fixture.BuildKey, h.fixture.BuildConfig), ("cdnConfig", h.fixture.CdnKey, h.fixture.CdnConfig) })
            {
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                await h.Catalog.RecordConfigurationAsync(new(key, hash, bytes.Length, InventoryFixture.Url("config", key), Time), default);
                configurations.Add(new(role, key, hash, bytes.Length, InventoryFixture.Url("config", key), Time, Time, 1));
            }
            await h.Catalog.CommitWorkspaceAsync(h.Opening, configurations, Time, default);
            MetadataReceipt Metadata(string role)
            {
                var bytes = h.fixture.Metadata[role]; return new(role, bytes.ContentKey, bytes.EncodingKey, Convert.ToHexStringLower(SHA256.HashData(bytes.Encoded)), N(bytes.Encoded.Length), N(bytes.Decoded.Length), InventoryFixture.Url("data", bytes.EncodingKey), Time, Time, MetadataAcquirer.SourceRevision);
            }
            foreach (var role in new[] { "encoding", "root" }) await h.Catalog.RecordMetadataAsync(Metadata(role), default);
            var payload = h.fixture.PayloadA;
            var source = new ContentReceipt(payload.ContentKey, payload.EncodingKey, Convert.ToHexStringLower(SHA256.HashData(payload.Encoded)), N(payload.Encoded.Length), N(payload.Decoded.Length), InventoryFixture.Url("data", payload.EncodingKey), Time, Time, "content-blte-v1", new("loose", "0", N(payload.Encoded.Length), null));
            await h.Catalog.RecordContentAsync(source, default);
            var template = await h.Projection.Binding([new(1, "Authored Creature")]);
            var rowsBinding = template.Rows.Binding with { SourceSha256 = source.Sha256 };
            await using var table = new TableRowArtifacts(h.Projection.Objects); await using var old = new TableRowArtifacts(h.Projection.Objects);
            await using var writer = await table.CreateWriterAsync(rowsBinding, default); await writer.HeaderAsync(template.Rows.Header, default);
            foreach (var row in (await old.ReadPageAsync(template.Rows, 0, 50, null, default)).Rows) await writer.RowAsync(row, default);
            var rows = await writer.CompleteAsync(template.Rows.Completion with { ConsumedBytes = payload.Decoded.Length }, default);
            var association = new SelectedFileAssociation(CreaturesPolicy.FileDataId, "enUS", "enus-consistent-root-variants-v1", Metadata("encoding"), Metadata("root"), [new("0", "0", source.ContentKey, "2", "0", "0", "0", "0", null)]);
            var binding = template with { Context = new("wow", h.Opening.Build.Id, "us", "enUS"), SourceReference = h.Opening.Reference, Configurations = configurations, Rows = rows,
                Source = template.Source with { BuildIdentity = h.Opening.Build.Id, ArtifactId = rows.ContentKey, Source = source, Association = association } };
            await h.Catalog.RecordTableArtifactAsync(rows, default); await h.Catalog.CommitTableAdmissionAsync(h.Opening, CreaturesPolicy.Table, "enUS", source, association, rows, Time, default);
            await using var coreReader = new TableRowArtifacts(h.Projection.Objects);
            h.Core = await h.Projection.Projection.BuildAsync(binding, coreReader, _ => Task.CompletedTask, default);
            await h.Catalog.RecordCreaturesArtifactAsync(h.Core, default); await h.Catalog.CommitCreaturesAdmissionAsync(h.Opening, h.Core, Time, default);
            foreach (var (id, fdid, salt) in new[] { (Display, "1108759", 'd'), (Model, "1365368", 'e') })
            {
                // These are authored catalog identities, not a claim that Creature columns qualify a CDI/CMD semantic profile.
                var targetBinding = rows.Binding with { DefinitionSha256 = new(salt, 64) };
                var targetRows = rows with { Binding = targetBinding, ContentKey = TableRowArtifacts.ContentKey(targetBinding), Header = rows.Header with { TableHash = id.ToUpperInvariant() } };
                var targetAssociation = association with { FileDataId = fdid };
                await h.Catalog.RecordTableArtifactAsync(targetRows, default); await h.Catalog.CommitTableAdmissionAsync(h.Opening, id, "enUS", source, targetAssociation, targetRows, Time, default);
                h.Targets = [.. h.Targets, new(id, new(targetRows, source, targetAssociation, Time))];
            }
            return h;
        }
        internal async Task Refresh()
        {
            var versions = Fixtures.Versions($"us|{fixture.BuildKey}|{fixture.CdnKey}||70448|12.1.5.70448||creature-display");
            var cdns = Encoding.UTF8.GetString(fixture.Discovery.Documents.Single(d => d.Role == "cdns").Bytes);
            var attempt = await Fixtures.Attempt(versions, cdns); var generation = await Catalog.BeginRefreshAsync("wow", default);
            var page = await Catalog.CommitAsync("wow", generation, attempt, Time, default);
            Opening ??= await Catalog.ResolveOpeningAsync(new("wow", page.Builds.Single().Id, "us"), default);
        }
        internal async Task Sql(string sql, string? value = null)
        {
            await using var db = new SqliteConnection("Data Source=" + Path.Combine(Projection.DirectoryPath, "discovery-v1.sqlite3")); await db.OpenAsync();
            await using var command = db.CreateCommand(); command.CommandText = sql; if (value is not null) command.Parameters.AddWithValue("$v", value); await command.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync() { await Catalog.DisposeAsync(); await Projection.DisposeAsync(); }
    }
}
