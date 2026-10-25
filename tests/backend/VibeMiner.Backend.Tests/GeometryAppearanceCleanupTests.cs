using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;
using static VibeMiner.Backend.Tests.AppearanceModels;

namespace VibeMiner.Backend.Tests;

// D103 (#76): superseded retained appearances and the VMA1 files no remaining row names. Real catalog, CAS and geometry
// pipeline over authored bytes. A row is made superseded by rewriting its key revision, as a later engine's key would leave it.
[Collection("Creature projection builder")]
public sealed class GeometryAppearanceCleanupTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private static Task Progress(CreatureGeometryProgress _) => Task.CompletedTask;

    private sealed class Harness : IAsyncDisposable
    {
        internal CreatureGeometrySourcesFixture F = null!;
        internal readonly GeometryAppearanceFileUses Uses = new();
        internal CreatureGeometryStore Store = null!;
        internal CreatureGeometryAppearanceManifest Manifest = null!;
        internal string Key = null!;
        internal static async Task<Harness> CreateAsync()
        {
            var h = new Harness { F = await CreatureGeometrySourcesFixture.CreateAsync(textured: new(Simple(), [new()])) };
            h.Store = new(h.F.Catalog, h.F.Projection.Objects, h.F.Displays, h.F.Sessions, h.F.Shared, h.F.Resources, uses: h.Uses);
            var ready = await h.Store.PrepareAsync(new(h.F.Request), Progress, default).WaitAsync(Guard);
            Assert.Equal(CreatureGeometryAppearanceState.Ready, ready.Appearance!.State);
            h.Manifest = ready.Appearance.Manifest!; h.Key = (string)h.Scalar("SELECT appearance_key FROM geometry_appearances")!;
            return h;
        }
        internal GeometryAppearanceCleanup Cleanup(Func<string, Task>? afterFile = null, Action<int>? batch = null, Func<string, Task>? before = null) =>
            new(F.Catalog, F.Projection.Objects, Uses, null) { AfterFileDeleted = afterFile, BatchRead = batch, BeforeDelete = before };
        internal string FilePath => Path.Combine(F.Projection.DirectoryPath, "objects-v1", Manifest.Sha256[..2], Manifest.Sha256[2..]);
        internal SqliteConnection Db()
        {
            var db = new SqliteConnection($"Data Source={Path.Combine(F.Projection.DirectoryPath, "discovery-v1.sqlite3")};Pooling=False"); db.Open(); return db;
        }
        internal object? Scalar(string sql) { using var db = Db(); using var c = db.CreateCommand(); c.CommandText = sql; return c.ExecuteScalar(); }
        internal void Execute(string sql) { using var db = Db(); using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
        internal long Rows => (long)Scalar("SELECT count(*) FROM geometry_appearances")!;
        // A copy of the current row under another key, as a row written under an older key revision.
        internal void Copy(string key, string? revision = "superseded") => Execute($"""
            INSERT INTO geometry_appearances(appearance_key,geometry_artifact_id,artifact_id,descriptor,key_revision,artifact_sha256,artifact_bytes)
            SELECT '{key}',geometry_artifact_id,artifact_id,descriptor,{(revision is null ? "NULL" : $"'{revision}'")},
                {(revision is null ? "NULL" : "artifact_sha256")},{(revision is null ? "NULL" : "artifact_bytes")}
            FROM geometry_appearances WHERE appearance_key='{Key}'
            """);
        internal async Task<CreatureGeometryAppearanceState> ReadAsync() => (await Store.ReadAsync(new(F.Request), default)).Appearance!.State;
        public async ValueTask DisposeAsync() { await Store.DisposeAsync(); await F.DisposeAsync(); }
    }

    [Fact]
    public async Task Superseded_rows_go_and_their_file_goes_only_with_the_last_row_that_names_it()
    {
        await using var h = await Harness.CreateAsync();
        Assert.Equal(DiscoveryCatalog.GeometryAppearanceKeyRevision, h.Scalar("SELECT key_revision FROM geometry_appearances"));
        Assert.Equal((h.Manifest.Sha256, h.Manifest.ByteLength), ((string)h.Scalar("SELECT artifact_sha256 FROM geometry_appearances")!, (long)h.Scalar("SELECT artifact_bytes FROM geometry_appearances")!));
        await using var cleanup = h.Cleanup();
        Assert.Equal(new(0, 0, 0, 0), await cleanup.PassAsync(default));
        // A superseded copy that names the same file: the row goes, the file stays for the current row.
        h.Copy(new string('a', 64));
        Assert.Equal(new(1, 0, 0, 0), await cleanup.PassAsync(default));
        Assert.Equal(1, h.Rows); Assert.True(File.Exists(h.FilePath)); Assert.Equal(CreatureGeometryAppearanceState.Ready, await h.ReadAsync());
        // Once the current row is superseded too, its row and file go; preparing again restores both.
        h.Execute("UPDATE geometry_appearances SET key_revision='superseded'");
        Assert.Equal(new(1, 1, h.Manifest.ByteLength, 0), await cleanup.PassAsync(default));
        Assert.Equal(0, h.Rows); Assert.False(File.Exists(h.FilePath)); Assert.Equal(CreatureGeometryAppearanceState.NotPrepared, await h.ReadAsync());
        Assert.Equal(CreatureGeometryAppearanceState.Ready, (await h.Store.PrepareAsync(new(h.F.Request), Progress, default).WaitAsync(Guard)).Appearance!.State);
        Assert.True(File.Exists(h.FilePath)); Assert.Equal(CreatureGeometryAppearanceState.Ready, await h.ReadAsync());
    }

    [Fact]
    public async Task Rows_of_a_v20_catalog_upgrade_unrecorded_then_name_their_file_and_are_removed_once()
    {
        await using var h = await Harness.CreateAsync();
        // A v20 catalog: the current row, a second row naming the same file and a retained refusal, then the columns dropped.
        h.Copy(new string('b', 64));
        h.Execute($"INSERT INTO geometry_appearances(appearance_key,geometry_artifact_id,artifact_id,descriptor) SELECT '{new string('c', 64)}',geometry_artifact_id,NULL,'{{}}' FROM geometry_appearances LIMIT 1");
        h.Execute(CatalogMigrationFixture.RemoveV21 + "PRAGMA user_version=20;");
        Assert.Equal(0L, h.Scalar("SELECT count(*) FROM pragma_table_info('geometry_appearances') WHERE name='key_revision'"));
        await using (await DiscoveryCatalog.OpenAsync(h.F.Projection.DirectoryPath)) { }
        Assert.Equal(21L, h.Scalar("PRAGMA user_version")); Assert.Equal(3, h.Rows);
        Assert.Equal(3L, h.Scalar("SELECT count(*) FROM geometry_appearances WHERE key_revision IS NULL AND artifact_sha256 IS NULL AND artifact_bytes IS NULL"));
        // Still read as before the upgrade: the key reaches the row.
        Assert.Equal(CreatureGeometryAppearanceState.Ready, await h.ReadAsync());
        // Naming reads each file from its descriptor; the refusal has none.
        Assert.Equal(2, await h.F.Catalog.NameGeometryAppearanceFilesAsync(GeometryAppearanceCleanup.BatchRows, default));
        Assert.Equal(2L, h.Scalar($"SELECT count(*) FROM geometry_appearances WHERE artifact_sha256='{h.Manifest.Sha256}' AND artifact_bytes={h.Manifest.ByteLength}"));
        await using var cleanup = h.Cleanup();
        Assert.Equal(new(3, 1, h.Manifest.ByteLength, 0), await cleanup.PassAsync(default));
        Assert.Equal(0, h.Rows); Assert.False(File.Exists(h.FilePath)); Assert.Equal(CreatureGeometryAppearanceState.NotPrepared, await h.ReadAsync());
        Assert.Equal(CreatureGeometryAppearanceState.Ready, (await h.Store.PrepareAsync(new(h.F.Request), Progress, default).WaitAsync(Guard)).Appearance!.State);
        Assert.Equal(DiscoveryCatalog.GeometryAppearanceKeyRevision, h.Scalar("SELECT key_revision FROM geometry_appearances"));
    }

    [Fact]
    public async Task Rows_that_name_no_readable_file_are_kept_marked_and_a_malformed_file_skips_only_its_row()
    {
        await using var h = await Harness.CreateAsync();
        h.Execute("UPDATE geometry_appearances SET key_revision=NULL,artifact_sha256=NULL,artifact_bytes=NULL");
        // Pre-v21 rows whose descriptor names no file: one without a manifest file, one over the descriptor bound.
        h.Execute($"INSERT INTO geometry_appearances(appearance_key,geometry_artifact_id,artifact_id,descriptor) SELECT '{new string('d', 64)}',geometry_artifact_id,'{new string('e', 64)}','{{\"appearance\":{{}}}}' FROM geometry_appearances LIMIT 1");
        h.Execute($"INSERT INTO geometry_appearances(appearance_key,geometry_artifact_id,artifact_id,descriptor) SELECT '{new string('f', 64)}',geometry_artifact_id,'{new string('e', 64)}','{{\"x\":\"{new string('a', 70_000)}\"}}' FROM geometry_appearances LIMIT 1");
        // A superseded row whose recorded file is malformed, read in the same pass.
        h.Execute($"INSERT INTO geometry_appearances(appearance_key,geometry_artifact_id,artifact_id,descriptor,key_revision,artifact_sha256,artifact_bytes) SELECT '{new string('0', 64)}',geometry_artifact_id,'{new string('e', 64)}','{{}}','superseded','not-a-file',10 FROM geometry_appearances LIMIT 1");
        await using var cleanup = h.Cleanup();
        // The malformed row is skipped and the pass goes on; the marked rows no longer stand for every file, so the legacy row's file goes.
        Assert.Equal(new(1, 1, h.Manifest.ByteLength, 0, 1), await cleanup.PassAsync(default));
        Assert.False(File.Exists(h.FilePath)); Assert.Equal(3, h.Rows);
        Assert.Equal(2L, h.Scalar("SELECT count(*) FROM geometry_appearances WHERE artifact_bytes=-1 AND artifact_sha256 IS NULL"));
        Assert.Equal(0, await h.F.Catalog.NameGeometryAppearanceFilesAsync(GeometryAppearanceCleanup.BatchRows, default));
        Assert.Equal(new(0, 0, 0, 0, 1), await cleanup.PassAsync(default));
    }

    [Fact]
    public async Task A_row_a_Prepare_makes_current_after_the_batch_read_keeps_its_file()
    {
        await using var h = await Harness.CreateAsync();
        h.Execute("UPDATE geometry_appearances SET key_revision='superseded'");
        var stamped = false;
        await using var cleanup = h.Cleanup(batch: count =>
        {
            if (count == 0 || stamped) return; stamped = true;
            h.Execute($"UPDATE geometry_appearances SET key_revision='{DiscoveryCatalog.GeometryAppearanceKeyRevision}'");
        });
        Assert.Equal(new(0, 0, 0, 0), await cleanup.PassAsync(default));
        Assert.True(stamped); Assert.Equal(1, h.Rows); Assert.True(File.Exists(h.FilePath)); Assert.Equal(CreatureGeometryAppearanceState.Ready, await h.ReadAsync());
    }

    [Fact]
    public async Task A_Prepare_that_rebuilds_onto_a_pre_v21_row_makes_it_current_and_cleanup_keeps_it()
    {
        await using var h = await Harness.CreateAsync();
        // A row as a v20 catalog left it, under the current key, whose VMA1 file is gone: Prepare rebuilds onto that same row.
        h.Execute("UPDATE geometry_appearances SET key_revision=NULL,artifact_sha256=NULL,artifact_bytes=NULL");
        File.Delete(h.FilePath);
        Assert.Equal(CreatureGeometryAppearanceState.Ready, (await h.Store.PrepareAsync(new(h.F.Request), Progress, default).WaitAsync(Guard)).Appearance!.State);
        Assert.Equal(1, h.Rows); Assert.Equal(h.Key, h.Scalar("SELECT appearance_key FROM geometry_appearances"));
        Assert.Equal(DiscoveryCatalog.GeometryAppearanceKeyRevision, h.Scalar("SELECT key_revision FROM geometry_appearances"));
        Assert.Equal((h.Manifest.Sha256, h.Manifest.ByteLength), ((string)h.Scalar("SELECT artifact_sha256 FROM geometry_appearances")!, (long)h.Scalar("SELECT artifact_bytes FROM geometry_appearances")!));
        await using var cleanup = h.Cleanup();
        Assert.Equal(new(0, 0, 0, 0), await cleanup.PassAsync(default));
        Assert.Equal(1, h.Rows); Assert.True(File.Exists(h.FilePath)); Assert.Equal(CreatureGeometryAppearanceState.Ready, await h.ReadAsync());
    }

    [Fact]
    public async Task A_row_with_a_corrupt_key_is_skipped_and_the_pass_goes_on()
    {
        await using var h = await Harness.CreateAsync();
        h.Execute("UPDATE geometry_appearances SET key_revision='superseded'");
        // Superseded refusal rows under a short key and an oversized one, written before the real row is read.
        foreach (var key in new[] { "short", new string('a', 300) })
            h.Execute($"INSERT INTO geometry_appearances(appearance_key,geometry_artifact_id,artifact_id,descriptor,key_revision) SELECT '{key}',geometry_artifact_id,NULL,'{{}}','superseded' FROM geometry_appearances LIMIT 1");
        await using var cleanup = h.Cleanup();
        Assert.Equal(new(1, 1, h.Manifest.ByteLength, 0, 2), await cleanup.PassAsync(default));
        Assert.Equal(2, h.Rows); Assert.False(File.Exists(h.FilePath));
    }

    [Fact]
    public async Task A_row_naming_the_file_committed_just_before_the_delete_window_keeps_it()
    {
        await using var h = await Harness.CreateAsync();
        h.Execute("UPDATE geometry_appearances SET key_revision='superseded'");
        await using var cleanup = h.Cleanup(before: _ => { h.Copy(new string('a', 64), DiscoveryCatalog.GeometryAppearanceKeyRevision); return Task.CompletedTask; });
        Assert.Equal(new(1, 0, 0, 0), await cleanup.PassAsync(default));
        Assert.True(File.Exists(h.FilePath));
        Assert.Equal(DiscoveryCatalog.GeometryAppearanceKeyRevision, h.Scalar("SELECT key_revision FROM geometry_appearances"));
    }

    [Fact]
    public async Task A_Prepare_holds_its_file_from_writing_it_until_its_row_commits()
    {
        await using var h = await Harness.CreateAsync();
        // A superseded row names the file a Prepare is about to publish again; the current row is gone, so Prepare produces.
        h.Copy(new string('a', 64)); h.Execute($"DELETE FROM geometry_appearances WHERE appearance_key='{h.Key}'");
        await using var cleanup = h.Cleanup(); GeometryAppearanceCleanupResult? during = null;
        h.Uses.AfterPublished = async _ => during = await cleanup.PassAsync(default);
        Assert.Equal(CreatureGeometryAppearanceState.Ready, (await h.Store.PrepareAsync(new(h.F.Request), Progress, default).WaitAsync(Guard)).Appearance!.State);
        h.Uses.AfterPublished = null;
        Assert.Equal(new(0, 0, 0, 1), during);
        Assert.True(File.Exists(h.FilePath)); Assert.Equal(CreatureGeometryAppearanceState.Ready, await h.ReadAsync());
        Assert.Equal(new(1, 0, 0, 0), await cleanup.PassAsync(default));
        Assert.True(File.Exists(h.FilePath)); Assert.Equal(CreatureGeometryAppearanceState.Ready, await h.ReadAsync());
    }

    [Fact]
    public async Task A_file_an_open_view_holds_is_kept_with_its_row_until_the_view_closes()
    {
        await using var h = await Harness.CreateAsync();
        var lease = await h.Store.OpenAsync(new(h.F.Request, h.Manifest.ArtifactId), default);
        h.Execute("UPDATE geometry_appearances SET key_revision='superseded'");
        await using var cleanup = h.Cleanup();
        Assert.Equal(new(0, 0, 0, 1), await cleanup.PassAsync(default));
        Assert.Equal(1, h.Rows); Assert.True(File.Exists(h.FilePath)); Assert.Equal(1, h.Uses.InUse(h.Manifest.Sha256));
        var bytes = new byte[h.Manifest.ByteLength]; await lease.Content.ReadExactlyAsync(bytes);
        Assert.Equal(h.Manifest.Sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        await lease.DisposeAsync(); await lease.Retirement;
        Assert.Equal(0, h.Uses.InUse(h.Manifest.Sha256));
        Assert.Equal(new(1, 1, h.Manifest.ByteLength, 0), await cleanup.PassAsync(default));
        Assert.Equal(0, h.Rows); Assert.False(File.Exists(h.FilePath));
    }

    [Fact]
    public async Task A_publication_holds_its_file_and_a_use_waits_for_a_delete_in_progress()
    {
        await using var h = await Harness.CreateAsync();
        var uses = new GeometryAppearanceFileUses(); var sha = h.Manifest.Sha256;
        var held = await uses.UseAsync(sha, default);
        Assert.False(uses.TryBeginDelete(sha));
        held.Dispose(); held.Dispose(); Assert.Equal(0, uses.InUse(sha));
        Assert.True(uses.TryBeginDelete(sha)); Assert.False(uses.TryBeginDelete(sha));
        var waiting = uses.UseAsync(sha, default);
        Assert.False(waiting.IsCompleted);
        using (var canceled = new CancellationTokenSource())
        { var abandoned = uses.UseAsync(sha, canceled.Token); canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned); }
        uses.EndDelete(sha);
        using (await waiting.WaitAsync(Guard)) Assert.Equal(1, uses.InUse(sha));
        // Prepare released its publication's use once its row committed; a use held as a publication holds it keeps the file.
        Assert.Equal(0, h.Uses.InUse(sha));
        h.Execute("UPDATE geometry_appearances SET key_revision='superseded'");
        await using var cleanup = h.Cleanup();
        using (await h.Uses.UseAsync(sha, default)) Assert.Equal(new(0, 0, 0, 1), await cleanup.PassAsync(default));
        Assert.True(File.Exists(h.FilePath)); Assert.Equal(1, h.Rows);
        Assert.Equal(new(1, 1, h.Manifest.ByteLength, 0), await cleanup.PassAsync(default));
    }

    [Fact]
    public async Task A_crash_between_deleting_the_file_and_the_row_is_finished_by_the_next_run()
    {
        await using var h = await Harness.CreateAsync();
        h.Execute("UPDATE geometry_appearances SET key_revision='superseded'");
        await using (var crashing = h.Cleanup(afterFile: _ => throw new IOException("Authored crash after the file delete.")))
            await Assert.ThrowsAsync<IOException>(() => crashing.PassAsync(default));
        Assert.Equal(1, h.Rows); Assert.False(File.Exists(h.FilePath)); Assert.Equal(0, h.Uses.InUse(h.Manifest.Sha256));
        // The row left behind names a missing file: a read reports notPrepared, never a broken appearance.
        Assert.Equal(CreatureGeometryAppearanceState.NotPrepared, await h.ReadAsync());
        await using var next = h.Cleanup();
        Assert.Equal(new(1, 0, 0, 0), await next.PassAsync(default));
        Assert.Equal(0, h.Rows);
    }

    [Fact]
    public async Task A_file_with_another_length_at_its_path_is_kept_and_so_is_its_row()
    {
        await using var h = await Harness.CreateAsync();
        h.Execute("UPDATE geometry_appearances SET artifact_bytes=artifact_bytes+1, key_revision='superseded'");
        await using var cleanup = h.Cleanup();
        Assert.Equal(new(0, 0, 0, 1), await cleanup.PassAsync(default));
        Assert.Equal(1, h.Rows); Assert.True(File.Exists(h.FilePath));
    }

    [Fact]
    public async Task Every_catalog_operation_reads_at_most_one_batch_and_the_worker_runs_once_after_start()
    {
        await using var h = await Harness.CreateAsync();
        for (var i = 0; i < 40; i++) h.Copy(Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(i))));
        var batches = new List<int>();
        await using (var cleanup = h.Cleanup(batch: batches.Add))
        {
            cleanup.Start(); await cleanup.Completion.WaitAsync(Guard);
            Assert.Equal(new(40, 0, 0, 0), cleanup.Last);
        }
        Assert.Equal([16, 16, 8, 0], batches); Assert.All(batches, count => Assert.InRange(count, 0, GeometryAppearanceCleanup.BatchRows));
        Assert.Equal(1, h.Rows); Assert.True(File.Exists(h.FilePath)); Assert.Equal(CreatureGeometryAppearanceState.Ready, await h.ReadAsync());
        // Disposed before it starts, or while it runs: nothing escapes and the loop has ended.
        var idle = h.Cleanup(); await idle.DisposeAsync(); Assert.Throws<ObjectDisposedException>(idle.Start);
    }
}
