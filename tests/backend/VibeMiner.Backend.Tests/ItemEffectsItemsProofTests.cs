using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;
using Harness = VibeMiner.Backend.Tests.ItemsStoreTests.Harness;

namespace VibeMiner.Backend.Tests;

[Collection("Items projection builder")]
public sealed class ItemEffectsItemsProofTests
{
    private const string Later = "2026-10-19T01:00:00Z";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactCompactMembershipReopensOfflineAndProofOutlivesAllSessionBorrows(bool cold)
    {
        await using var h = await Harness.Create();
        var ready = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        if (cold) { await h.Store.DisposeAsync(); h.Store = new(h.Catalog, h.Objects, h.Tables, h.Resources); }
        var proof = await h.Store.AuthorizeItemEffectsAsync(new(h.Request, ready.ArtifactId, "1"), default);
        Assert.Equal(JsonSerializer.Serialize(h.Opening), JsonSerializer.Serialize(proof.Opening)); Assert.Equal(ready.ArtifactId, proof.Artifact.ContentKey);
        Assert.Equal(h.Request, proof.Snapshot.Context); Assert.Equal(ready.ArtifactId, proof.Snapshot.ArtifactId);
        Assert.Equal("1", proof.Item.ItemId); Assert.True(proof.Item.Admitted);
        Assert.Equal(["2", "4"], proof.Item.BaseValues.Select(value => value.Value));
        var readOnly = Assert.IsAssignableFrom<IList<ItemSourceValue>>(proof.Item.BaseValues);
        Assert.True(readOnly.IsReadOnly); Assert.Throws<NotSupportedException>(() => readOnly[0] = readOnly[0] with { Value = "999" });
        Assert.Equal(["buildConfig", "cdnConfig"], proof.Configurations.Select(value => value.Role));
        Assert.All(proof.Configurations, receipt => Assert.Equal(Harness.Time, receipt.RetrievedAt));
        var configurations = Assert.IsAssignableFrom<IList<ConfigurationReceipt>>(proof.Configurations);
        Assert.True(configurations.IsReadOnly); Assert.Throws<NotSupportedException>(() => configurations[0] = configurations[0] with { RetrievedAt = Later });
        Assert.Empty(h.PreparedTables);
        // A proof is owned metadata, not a held source/session borrow.
        await h.Store.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["2", "4"], proof.Item.BaseValues.Select(value => value.Value));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("2")]
    [InlineData("4294967295")]
    public async Task MissingCanonicalUnsignedItemIsCompactUnadmittedEvidenceWithoutPrerequisites(string id)
    {
        await using var h = await Harness.Create();
        var ready = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var proof = await h.Store.AuthorizeItemEffectsAsync(new(h.Request, ready.ArtifactId, id), default);
        Assert.Equal(id, proof.Item.ItemId); Assert.False(proof.Item.Admitted); Assert.Empty(proof.Item.BaseValues);
        Assert.Empty(h.PreparedTables);
    }

    [Fact]
    public async Task InvalidFixedScalarsRejectBeforeAnyCatalogOrObjectsExist()
    {
        await using var store = new ItemsStore(null!, null!, null!, null!);
        var context = new ItemsContext("wow", new('a', 64), "us", "enUS");
        foreach (var selection in new ItemEffectsSelection?[] { null, new(context, new('A', 64), "1"),
                     new(context, new('a', 64), "01"), new(context, new('a', 64), "4294967296"),
                     new(context with { ResearchLocale = "frFR" }, new('a', 64), "1") })
            Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => store.AuthorizeItemEffectsAsync(selection!, default))).Code);
    }

    [Fact]
    public async Task CanceledOrDisposedWorkDoesNotRetainTheSharedItemsGate()
    {
        await using var h = await Harness.Create();
        var ready = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var selection = new ItemEffectsSelection(h.Request, ready.ArtifactId, "1");
        // Controlled existing ownership: regardless of which awaited stage observes
        // cancellation, this waiter must not release somebody else's gate credit.
        var gate = (SemaphoreSlim)typeof(ItemsStore).GetField("sessionsGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.Store)!;
        await gate.WaitAsync();
        using var stop = new CancellationTokenSource();
        var waiting = h.Store.AuthorizeItemEffectsAsync(selection, stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, gate.CurrentCount); gate.Release();
        Assert.True((await h.Store.AuthorizeItemEffectsAsync(selection, default)).Item.Admitted);
        await h.Store.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Store.AuthorizeItemEffectsAsync(selection, default));
    }

    [Fact]
    public async Task ReplacedSourcesAndUnknownFeaturesCannotAuthorizeCachedItemMembership()
    {
        await using var h = await Harness.Create();
        var ready = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        Assert.True((await h.Store.AuthorizeItemEffectsAsync(new(h.Request, ready.ArtifactId, "1"), default)).Item.Admitted);
        await h.ReplaceItem();
        Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Store.AuthorizeItemEffectsAsync(new(h.Request, ready.ArtifactId, "1"), default))).Code);
        await using var absent = await Harness.Create();
        Assert.Equal("ITEMS_NOT_PREPARED", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            absent.Store.AuthorizeItemEffectsAsync(new(absent.Request, new('f', 64), "1"), default))).Code);
    }

    [Fact]
    public async Task CurrentObservationsRefreshWithoutChangingFirstDescriptorOrOriginalReceiptAuthority()
    {
        await using var h = await Harness.Create();
        var ready = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var original = await h.Store.AuthorizeItemEffectsAsync(new(h.Request, ready.ArtifactId, "1"), default);
        foreach (var source in new[] { h.Item, h.Sparse })
            await h.Catalog.CommitTableAdmissionAsync(h.Opening, source.Snapshot.TableId, "enUS",
                source.Source with { VerifiedAt = Later }, source.Association, source.Rows, Later, default);
        await h.Catalog.CommitItemsAdmissionAsync(h.Opening, "enUS", original.Artifact, Later, default);
        var current = await h.Store.AuthorizeItemEffectsAsync(new(h.Request, ready.ArtifactId, "1"), default);
        Assert.Equal(JsonSerializer.Serialize(original.Artifact), JsonSerializer.Serialize(current.Artifact)); Assert.Equal(Later, current.Snapshot.PreparedAt);
        Assert.Equal(Harness.Time, current.Artifact.Binding.ItemSource.PreparedAt);
        Assert.Equal(Later, current.Snapshot.ItemSource.PreparedAt); Assert.Equal(Later, current.Snapshot.SparseSource.PreparedAt);
        Assert.Equal(Later, current.Snapshot.ItemSource.Source.VerifiedAt);
        Assert.Equal(Harness.Time, current.Snapshot.ItemSource.Source.RetrievedAt);
        await h.Catalog.CommitTableAdmissionAsync(h.Opening, h.Item.Snapshot.TableId, "enUS",
            h.Item.Source with { RetrievedAt = Later, VerifiedAt = Later }, h.Item.Association, h.Item.Rows, Later, default);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.AuthorizeItemEffectsAsync(new(h.Request, ready.ArtifactId, "1"), default));
        Assert.Contains(error.Code, new[] { "ITEMS_ARTIFACT_STALE", "ITEMS_SOURCE_INVALID" });
    }

    [Fact]
    public async Task PoisonedRegisteredFeatureFailsClosedBeforeReusingWarmMetadata()
    {
        await using var h = await Harness.Create();
        var ready = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(h.DirectoryPath, "discovery-v1.sqlite3"), Pooling = false }.ToString());
        await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items_artifacts SET descriptor=$descriptor WHERE content_key=$key";
        command.Parameters.AddWithValue("$descriptor", new string(' ', (256 << 10) + 1)); command.Parameters.AddWithValue("$key", ready.ArtifactId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
        Assert.Equal("ITEM_EFFECTS_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Store.AuthorizeItemEffectsAsync(new(h.Request, ready.ArtifactId, "1"), default))).Code);
    }

    [Fact]
    public async Task WarmProofStillVerifiesCapturedConfigurationBytesAndFailureReleasesOwnership()
    {
        await using var h = await Harness.Create();
        var ready = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var sha = Convert.ToHexStringLower(SHA256.HashData(h.Fixture.BuildConfig)); string path;
        await using (var input = await h.Objects.OpenVerifiedAsync(sha, h.Fixture.BuildConfig.Length, default)) path = input.Name;
        var corrupt = (byte[])h.Fixture.BuildConfig.Clone(); corrupt[^1] ^= 1; await File.WriteAllBytesAsync(path, corrupt);
        var selection = new ItemEffectsSelection(h.Request, ready.ArtifactId, "1");
        Assert.Equal("LOCAL_CONFIGURATION_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Store.AuthorizeItemEffectsAsync(selection, default))).Code);
        await File.WriteAllBytesAsync(path, h.Fixture.BuildConfig);
        Assert.True((await h.Store.AuthorizeItemEffectsAsync(selection, default)).Item.Admitted);
    }

    [Fact]
    public async Task HistoricalSourceDecoderCannotAuthorizeAnOtherwiseCurrentCompactFeature()
    {
        await using var h = await Harness.Create();
        var stale = await h.Source(ItemsProjection.ItemTable, h.Fixture.PayloadB, "historical-decoder-v1");
        await h.Catalog.RecordTableArtifactAsync(stale.Rows, default);
        await h.Catalog.CommitTableAdmissionAsync(h.Opening, ItemsProjection.ItemTable, "enUS", stale.Source, stale.Association, stale.Rows, Harness.Time, default);
        var binding = new ItemsProjectionBinding(h.Request, stale.Snapshot, h.Sparse.Snapshot, stale.Rows, h.Sparse.Rows);
        var artifact = await new ItemsProjection(h.Objects, h.Resources).BuildAsync(binding, _ => Task.CompletedTask, default);
        await h.Catalog.RecordItemsArtifactAsync(artifact, default);
        await h.Catalog.CommitItemsAdmissionAsync(h.Opening, "enUS", artifact, Harness.Time, default);
        Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Store.AuthorizeItemEffectsAsync(new(h.Request, artifact.ContentKey, "1"), default))).Code);
    }

    [Fact]
    public async Task OpeningCapturedBeforeAnAsyncResourceWaitIsNeverSilentlyRefreshed()
    {
        await using var h = await Harness.Create();
        var ready = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        using var first = await h.Resources.EnterCpuAsync(null, default);
        using var second = await h.Resources.EnterCpuAsync(null, default);
        var pending = h.Store.AuthorizeItemEffectsAsync(new(h.Request, ready.ArtifactId, "1"), default);
        // Observe a real resource wait after bounded SQL capture without adding a
        // production callback or racing on elapsed time as a completion signal.
        var budget = typeof(AcquisitionResources).GetField("cpu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.Resources)!;
        var budgetGate = budget.GetType().GetField("gate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(budget)!;
        var waiters = (System.Collections.ICollection)budget.GetType().GetField("waiting", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(budget)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true) { lock (budgetGate) { if (waiters.Count > 0) break; } await Task.Delay(1, timeout.Token); }
        var versions = Fixtures.Versions($"us|{h.Fixture.BuildKey}|{h.Fixture.CdnKey}||70448|12.1.5.70448||items");
        var cdns = Encoding.UTF8.GetString(h.Fixture.Discovery.Documents.Single(d => d.Role == "cdns").Bytes);
        var attempt = await Fixtures.Attempt(versions, cdns);
        var generation = await h.Catalog.BeginRefreshAsync("wow", default);
        await h.Catalog.CommitAsync("wow", generation, attempt, Later, default);
        first.Dispose(); second.Dispose();
        var proof = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(h.Opening.Reference, proof.Opening.Reference);
        Assert.NotEqual(generation, long.Parse(proof.Opening.Reference.Generation));
        // This is captured evidence awaiting the final coherent delivery fence,
        // never a promise that authority stays current after every awaited stage.
        Assert.Equal("ITEMS_ARTIFACT_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Catalog.ReadItemEffectsItemsAuthorityAsync(proof.Opening, "enUS", ready.ArtifactId, default))).Code);
    }

    [Fact]
    public async Task WarmCacheCannotPublishValidShapePoisonedDescriptorCountsForUnchangedKeyAndBytes()
    {
        await using var h = await Harness.Create();
        var ready = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        await PoisonValidShapeCounts(h.DirectoryPath, ready.ArtifactId);
        Assert.Equal("ITEMS_PROJECTION_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Store.AuthorizeItemEffectsAsync(new(h.Request, ready.ArtifactId, "1"), default))).Code);
    }

    [Fact]
    public async Task ReusedBoundedComparisonScratchIgnoresOldTailsAndResetsAfterEitherSideExceedsItsCap()
    {
        await using var h = await Harness.Create();
        await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var shortValue = (await h.Catalog.ReadItemsAdmissionAsync(h.Opening, "enUS", default))!.Artifact;
        var compare = typeof(ItemsStore).GetMethod("SameEffectsDescriptor", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var gate = (SemaphoreSlim)typeof(ItemsStore).GetField("sessionsGate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(h.Store)!;
        bool Equal(ItemsProjectionArtifact left, ItemsProjectionArtifact right) => (bool)compare.Invoke(h.Store, [left, right])!;
        // These authored graphs exercise comparison lifetime/bounds only, not
        // source admission. The real warm valid-shape poison test is separate.
        var longLeft = shortValue with { Groups = [new(new('a', 60_000), [])] };
        var longRight = shortValue with { Groups = [new(new('b', 60_000), [])] };
        var tooLong = shortValue with { Groups = [new(new('x', 300_000), [])] };
        await gate.WaitAsync();
        try
        {
            Assert.True(Equal(longLeft, longLeft)); Assert.False(Equal(longLeft, longRight));
            Assert.True(Equal(shortValue, shortValue));
            Assert.False(Equal(tooLong, shortValue)); Assert.True(Equal(shortValue, shortValue));
            Assert.False(Equal(shortValue, tooLong)); Assert.True(Equal(shortValue, shortValue));
        }
        finally { gate.Release(); }
    }

    internal static async Task PoisonValidShapeCounts(string directory, string artifactId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(directory, "discovery-v1.sqlite3"), Pooling = false }.ToString());
        await connection.OpenAsync(); await using var read = connection.CreateCommand();
        read.CommandText = "SELECT descriptor FROM items_artifacts WHERE content_key=$key"; read.Parameters.AddWithValue("$key", artifactId);
        var descriptor = JsonNode.Parse((string)(await read.ExecuteScalarAsync())!)!;
        var counts = descriptor["counts"]!;
        counts["matchedRows"] = "0"; counts["itemWithoutSparseRows"] = "1"; counts["sparseOnlyRows"] = "1"; counts["validNames"] = "0";
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE items_artifacts SET descriptor=$descriptor WHERE content_key=$key";
        update.Parameters.AddWithValue("$descriptor", descriptor.ToJsonString()); update.Parameters.AddWithValue("$key", artifactId);
        Assert.Equal(1, await update.ExecuteNonQueryAsync());
    }
}
