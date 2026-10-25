using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class InventoryCheckpointStorageTests
{
    [Fact]
    public async Task Migration_adds_durable_tables_without_changing_existing_source_or_inventory_rows()
    {
        await using var h = await Harness.Create();
        await h.Encoding();
        await h.Catalog.DisposeAsync();
        await h.Execute(CatalogMigrationFixture.RemoveV9 + "DROP TABLE encoded_objects; DROP TABLE content_locators; DROP TABLE table_admissions; DROP TABLE table_artifacts; DROP TABLE content_objects; DROP TABLE resolver_artifacts; DROP TABLE download_admissions; DROP TABLE download_artifact_sources; DROP TABLE download_artifacts; DROP INDEX IF EXISTS inventory_reference; DROP TABLE inventory_resolution_cursor; DROP TABLE inventory_merge_state; DROP TABLE inventory_merge_results; DROP TABLE inventory_checkpoints; PRAGMA user_version=4");
        h.Catalog = await DiscoveryCatalog.OpenAsync(h.Directory);
        Assert.Equal(21, await h.Scalar("PRAGMA user_version"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM en_content"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM inventory_generations"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM inventory_checkpoints"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='inventory_reference'"));
        var stages = new List<string>();
        await h.Catalog.EnsureInventoryReferenceIndexAsync(default, stage => { stages.Add(stage); return Task.CompletedTask; });
        Assert.Equal(["Preparing ordered inventory lookup index"], stages);
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='inventory_reference'"));
        await h.Catalog.EnsureInventoryReferenceIndexAsync(default, _ => throw new InvalidOperationException("An existing index needs no preparation."));
        Assert.Null(await h.Catalog.FindResumableInventoryAsync(h.Context, default));
    }

    [Fact]
    public async Task Completed_source_indexes_survive_restart_without_becoming_queryable_ready()
    {
        await using var h = await Harness.Create();
        await h.Encoding();
        await h.Catalog.AddOriginsAsync(h.Generation, [h.Root(10), h.Root(12)], default);
        var checkpoint = await h.Catalog.SaveInventoryCheckpointAsync(h.Generation, "root", h.Receipt("root"), default);
        await h.Catalog.AbandonInventoryAsync(h.Generation);
        await h.Restart();
        var restored = await h.Catalog.FindResumableInventoryAsync(h.Context, default);
        Assert.NotNull(restored);
        Assert.Equal(h.Generation, restored.Generation);
        Assert.Equal(12, restored.LastOriginId);
        Assert.Equal(2, restored.FamilyCounts["root"]);
        Assert.Equal(checkpoint, restored.Checkpoints["root"]);
        Assert.Equal(3, restored.Checkpoints.Count);
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM inventory_generations WHERE state='staging'"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM inventory_generations WHERE state='ready'"));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadInventoryAsync(
            new("wow", h.Context.Build.Id, "us", h.Generation.ToString(CultureInfo.InvariantCulture)), default));
    }

    [Fact]
    public async Task Partial_rows_do_not_enter_hydration_and_only_unfinished_source_families_are_pruned()
    {
        await using var h = await Harness.Create();
        await h.Encoding();
        await h.Catalog.AddOriginsAsync(h.Generation, [h.Root(1)], default);
        await h.Catalog.SaveInventoryCheckpointAsync(h.Generation, "root", h.Receipt("root"), default);
        var partial = Enumerable.Range(2, 1100).Select(id => h.Install(id)).ToArray();
        await h.Catalog.AddOriginsAsync(h.Generation, partial, default);
        await h.Catalog.AddTagAsync(h.Generation, "install", 0, "incomplete", 0, new byte[] { 0xff }, default);
        await h.Execute($"INSERT INTO inventory_units VALUES({h.Generation},'{new string('a', 32)}',10,0,0)");
        var restored = await h.Catalog.FindResumableInventoryAsync(h.Context, default);
        Assert.NotNull(restored);
        Assert.Equal(1, restored.LastOriginId);
        Assert.Equal(new[] { "root" }, restored.FamilyCounts.Keys);
        var progress = new List<string>();
        await h.Catalog.ResetIncompleteInventoryAsync(h.Generation, default, message => { progress.Add(message); return Task.CompletedTask; });
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM inventory_origins"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM inventory_tags"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM en_content"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM en_objects"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM inventory_units"));
        Assert.Contains(progress, message => message.StartsWith("Cleaning up unfinished install index", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Encoding_content_checkpoint_preserves_only_its_complete_table_pair()
    {
        await using var h = await Harness.Create();
        await h.Encoding(completeObjects: false);
        var source = h.Receipt("encoding") with { ParserRevision = MetadataAcquirer.SourceRevision };
        var checkpoint = await h.Catalog.SaveInventoryCheckpointAsync(h.Generation, "encoding-content", source, default);
        Assert.Equal(1, checkpoint.Counts.ContentRecords);
        Assert.Equal(1, checkpoint.Counts.CandidateReferences);
        await h.Catalog.AddEncodingAsync(h.Generation, [], [new(h.Fixture.PayloadB.EncodingKey, 100)], default);
        var restored = await h.Catalog.FindResumableInventoryAsync(h.Context, default);
        Assert.NotNull(restored);
        Assert.Empty(restored.FamilyCounts);
        Assert.Equal(0, restored.LastOriginId);
        await h.Catalog.ResetIncompleteInventoryAsync(h.Generation, default);
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM en_content"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM en_candidates"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM en_objects"));
    }

    [Theory]
    [InlineData("build")]
    [InlineData("region")]
    [InlineData("source")]
    [InlineData("profile")]
    [InlineData("revision")]
    [InlineData("ready")]
    public async Task Restore_requires_exact_context_revision_and_unpublished_state(string changed)
    {
        await using var h = await Harness.Create();
        await h.Encoding();
        var context = h.Context;
        switch (changed)
        {
            case "build": context = context with { Build = context.Build with { Id = new string('a', 64) } }; break;
            case "region": context = context with { Reference = context.Reference with { Region = "eu" } }; break;
            case "source": context = context with { Reference = context.Reference with { VersionsSha256 = new string('a', 64) } }; break;
            case "profile": await h.Execute("UPDATE inventory_generations SET profile='other-profile'"); break;
            case "revision": await h.Execute("UPDATE inventory_checkpoints SET revision='old-checkpoint-revision'"); break;
            case "ready": await h.Execute("UPDATE inventory_generations SET state='ready'"); break;
        }
        Assert.Null(await h.Catalog.FindResumableInventoryAsync(context, default));
    }

    [Theory]
    [InlineData("UPDATE en_content SET bytes=bytes+1")]
    [InlineData("UPDATE en_candidates SET ekey='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'")]
    [InlineData("UPDATE en_objects SET bytes=bytes+1")]
    [InlineData("DELETE FROM en_content")]
    public async Task Counts_and_stable_digest_reject_missing_and_same_count_changed_encoding_rows(string corruption)
    {
        await using var h = await Harness.Create();
        await h.Encoding();
        await h.Catalog.AbandonInventoryAsync(h.Generation);
        await h.Execute(corruption);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.FindResumableInventoryAsync(h.Context, default));
        Assert.Equal("LOCAL_CHECKPOINT_CORRUPT", error.Code);
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM inventory_generations WHERE state='abandoned'"));
    }

    [Fact]
    public async Task Immutable_source_proofs_allow_later_resolution_but_resolution_proof_detects_changed_mappings()
    {
        await using var h = await Harness.Create();
        await h.Encoding();
        await h.Catalog.AddOriginsAsync(h.Generation, [h.Root(1)], default);
        await h.Catalog.SaveInventoryCheckpointAsync(h.Generation, "root", h.Receipt("root"), default);
        await h.Catalog.ResolveOriginsAsync(h.Generation, "root", 0, default);
        Assert.NotNull(await h.Catalog.FindResumableInventoryAsync(h.Context, default));
        await h.Catalog.SaveInventoryCheckpointAsync(h.Generation, "root-resolved", null, default);
        await h.Execute("UPDATE inventory_origins SET encoded=encoded+1");
        Assert.Equal("LOCAL_CHECKPOINT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Catalog.FindResumableInventoryAsync(h.Context, default))).Code);
    }

    [Theory]
    [InlineData("UPDATE inventory_origins SET name='changed'")]
    [InlineData("UPDATE inventory_origins SET raw='changed'")]
    [InlineData("UPDATE inventory_tags SET bits=X'00'")]
    public async Task Source_membership_and_tag_mutations_are_detected_without_count_changes(string corruption)
    {
        await using var h = await Harness.Create();
        await h.Encoding();
        await h.Catalog.AddOriginsAsync(h.Generation, [h.Install(1)], default);
        await h.Catalog.AddTagAsync(h.Generation, "install", 0, "source tag", 1, new byte[] { 0x80 }, default);
        await h.Catalog.SaveInventoryCheckpointAsync(h.Generation, "install", h.Receipt("install"), default);
        await h.Execute(corruption);
        Assert.Equal("LOCAL_CHECKPOINT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Catalog.FindResumableInventoryAsync(h.Context, default))).Code);
    }

    [Fact]
    public async Task Prerequisites_receipt_roles_and_unfinished_resolutions_cannot_be_marked_complete()
    {
        await using var h = await Harness.Create();
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.SaveInventoryCheckpointAsync(h.Generation, "root", h.Receipt("root"), default));
        await h.Encoding();
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.SaveInventoryCheckpointAsync(h.Generation, "root", h.Receipt("install"), default));
        await h.Catalog.AddOriginsAsync(h.Generation, [h.Root(1)], default);
        await h.Catalog.SaveInventoryCheckpointAsync(h.Generation, "root", h.Receipt("root"), default);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.SaveInventoryCheckpointAsync(h.Generation, "root-resolved", null, default));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM inventory_checkpoints WHERE kind='root-resolved'"));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.SaveInventoryCheckpointAsync(h.Generation, "unknown", null, default));
    }

    [Fact]
    public async Task Checkpoint_retry_is_idempotent_preserves_original_receipt_and_cancellation_does_not_reactivate()
    {
        await using var h = await Harness.Create();
        await h.Encoding();
        var original = await h.Catalog.SaveInventoryCheckpointAsync(h.Generation, "encoding", h.Receipt("encoding"), default);
        var repeated = await h.Catalog.SaveInventoryCheckpointAsync(h.Generation, "encoding",
            h.Receipt("encoding") with { VerifiedAt = "2026-10-19T00:00:00Z" }, default);
        Assert.Equal(original, repeated);
        await h.Catalog.AbandonInventoryAsync(h.Generation);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Catalog.FindResumableInventoryAsync(h.Context,
            cancel.Token, _ => { cancel.Cancel(); return Task.CompletedTask; }));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM inventory_generations WHERE state='abandoned'"));
    }

    [Fact]
    public async Task Saving_checkpoint_reports_and_awaits_integrity_work_before_persisting_completion()
    {
        await using var h = await Harness.Create(); await h.Encoding(completeObjects: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stages = new List<string>();
        var saving = h.Catalog.SaveInventoryCheckpointAsync(h.Generation, "encoding-content",
            h.Receipt("encoding") with { ParserRevision = MetadataAcquirer.SourceRevision }, default,
            async stage => { stages.Add(stage); entered.TrySetResult(); await release.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(saving.IsCompleted);
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM inventory_checkpoints"));
        release.SetResult(); await saving;
        Assert.Contains(stages, stage => stage.StartsWith("Verifying encoding-content checkpoint", StringComparison.Ordinal));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM inventory_checkpoints"));
    }

    [Fact]
    public async Task Checkpoint_verification_awaits_progress_and_auxiliary_rows_follow_generation_ownership()
    {
        await using var h = await Harness.Create(); await h.Encoding();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restore = h.Catalog.FindResumableInventoryAsync(h.Context, default,
            async _ => { entered.TrySetResult(); await release.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(restore.IsCompleted); release.SetResult(); Assert.NotNull(await restore);
        await h.Execute($"INSERT INTO inventory_merge_state VALUES({h.Generation},'download',1,1,0); INSERT INTO inventory_merge_results VALUES({h.Generation},'download',1,'{new string('a', 32)}',10,zeroblob(32)); INSERT INTO inventory_resolution_cursor VALUES({h.Generation},'root',0,0)");
        await h.Execute($"PRAGMA foreign_keys=ON; DELETE FROM inventory_generations WHERE id={h.Generation}");
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM inventory_checkpoints"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM inventory_merge_state"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM inventory_merge_results"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM inventory_resolution_cursor"));
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal InventoryFixture Fixture { get; } = InventoryFixture.Create();
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "vibeminer-checkpoint-" + Guid.NewGuid().ToString("N"));
        internal DiscoveryCatalog Catalog { get; set; } = null!;
        internal CapturedOpeningContext Context { get; private set; } = null!;
        internal long Generation { get; private set; }
        internal static async Task<Harness> Create()
        {
            var h = new Harness();
            h.Catalog = await DiscoveryCatalog.OpenAsync(h.Directory);
            var observed = await h.Catalog.BeginRefreshAsync("wow", default);
            await h.Catalog.CommitAsync("wow", observed, h.Fixture.Discovery, DateTimeOffset.UtcNow.ToString("O"), default);
            h.Context = await h.Catalog.ResolveOpeningAsync(new("wow", h.Fixture.BuildIdentity, "us"), default);
            h.Generation = await h.Catalog.StartInventoryAsync(h.Context, default);
            foreach (var role in h.Fixture.Metadata.Keys)
                await h.Catalog.RecordMetadataAsync(h.Receipt(role) with { ParserRevision = MetadataAcquirer.SourceRevision }, default);
            return h;
        }
        internal async Task Encoding(bool completeObjects = true)
        {
            var a = Fixture.PayloadA;
            await Catalog.AddEncodingAsync(Generation, [new(a.ContentKey, a.Decoded.Length, [a.EncodingKey])],
                completeObjects ? [new(a.EncodingKey, a.Encoded.Length)] : [], default);
            if (completeObjects)
            {
                await Catalog.SaveInventoryCheckpointAsync(Generation, "encoding-content",
                    Receipt("encoding") with { ParserRevision = MetadataAcquirer.SourceRevision }, default);
                await Catalog.SaveInventoryCheckpointAsync(Generation, "encoding", Receipt("encoding"), default);
            }
        }
        internal MetadataReceipt Receipt(string role)
        {
            var source = Fixture.Metadata[role];
            return new(role, source.ContentKey, source.EncodingKey, Convert.ToHexStringLower(SHA256.HashData(source.Encoded)),
                source.Encoded.Length.ToString(CultureInfo.InvariantCulture), source.Decoded.Length.ToString(CultureInfo.InvariantCulture),
                InventoryFixture.Url("data", source.EncodingKey), "2026-10-18T00:00:00Z", "2026-10-18T00:00:00Z", role + "-v1");
        }
        internal InventoryOrigin Root(long id) => new(id, "root", null, id.ToString(CultureInfo.InvariantCulture),
            Fixture.PayloadA.ContentKey, null, 0, null, null, "1", "0", null, "authored ROOT");
        internal InventoryOrigin Install(long id) => new(id, "install", "authored/name", null, Fixture.PayloadA.ContentKey,
            null, 0, null, Fixture.PayloadA.Decoded.Length, null, null, null, "authored IN");
        internal async Task Restart() { await Catalog.DisposeAsync(); Catalog = await DiscoveryCatalog.OpenAsync(Directory); }
        internal async Task Execute(string sql)
        {
            await using var database = new SqliteConnection($"Data Source={Path.Combine(Directory, "discovery-v1.sqlite3")};Pooling=False");
            await database.OpenAsync(); using var command = database.CreateCommand(); command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
        internal async Task<long> Scalar(string sql)
        {
            await using var database = new SqliteConnection($"Data Source={Path.Combine(Directory, "discovery-v1.sqlite3")};Pooling=False");
            await database.OpenAsync(); using var command = database.CreateCommand(); command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
        public async ValueTask DisposeAsync() { await Catalog.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }
}
