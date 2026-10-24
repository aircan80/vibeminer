using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;
using Row = VibeMiner.Backend.Tests.CreaturesProjectionTests.Row;

namespace VibeMiner.Backend.Tests;

[Collection("Creature projection builder")]
public sealed class CreaturesStoreTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static Task Progress(CreaturesPreparationProgress _) => Task.CompletedTask;
    [Fact]
    public async Task CreatureOnlyReadPreparePageInspectAndFreshOfflineReopenPreserveEvidence()
    {
        await using var h = await Harness.Create();
        var before = await h.Store.ReadAsync(h.Context, default); Assert.Null(before.Snapshot); Assert.Equal(CreatureDependencyState.Ready, before.DependencyState); Assert.Equal(h.Binding.Rows.ContentKey, before.SourceArtifactId);
        var ready = await h.Store.PrepareAsync(h.Context, Progress, default); Assert.Empty(h.PreparedTables);
        var page = await h.Store.ReadPageAsync(new(h.Context, ready.ArtifactId), default); Assert.Equal(["0", "9", "4294967295"], page.Entries.Select(e => e.Id));
        var inspect = await h.Store.InspectAsync(new(h.Context, ready.ArtifactId, "9"), default); Assert.Equal(CreatureInspectState.Found, inspect.State);
        Assert.True(inspect.Detail!.CreatureRow.IsCopy); Assert.Equal("0", inspect.Detail.CreatureRow.CopySourceId); Assert.Null(inspect.Detail.Source.ColumnIndex);
        Assert.Equal("Name", inspect.Detail.Summary.Name.Value); Assert.Equal(h.Binding.Source.Coverage, inspect.Coverage);
        var absent = await h.Store.InspectAsync(new(h.Context, ready.ArtifactId, "8"), default); Assert.Equal(CreatureInspectState.NotFoundInAdmittedScope, absent.State); Assert.Null(absent.Detail);
        await h.Store.DisposeAsync(); h.Store = new(h.Catalog, h.Projection.Objects, h.Tables, h.Resources);
        Assert.Equal(JsonSerializer.Serialize(ready, CreaturesPolicy.Json), JsonSerializer.Serialize((await h.Store.ReadAsync(h.Context, default)).Snapshot, CreaturesPolicy.Json));
        var reopened = await h.Store.ReadPageAsync(new(h.Context, ready.ArtifactId), default);
        Assert.Equal(JsonSerializer.Serialize(page, CreaturesPolicy.Json), JsonSerializer.Serialize(reopened, CreaturesPolicy.Json));
        Assert.All(h.ReadTables, t => Assert.Equal(CreaturesPolicy.Table, t)); Assert.Empty(h.PreparedTables);
    }
    [Fact]
    public async Task AutomaticPrerequisiteUsesSharedProducerAndIndependentConsumerSurvivesStop()
    {
        await using var h = await Harness.Create(missing: true); var entered = Signal(); var release = Signal();
        h.BeforePrepare = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var independent = h.Tables.PrepareAsync(h.Table); await entered.Task.WaitAsync(Deadline);
        using var stop = new CancellationTokenSource(); var waiting = h.Store.PrepareAsync(h.Context, Progress, stop.Token);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting); Assert.False(independent.IsCompleted);
        release.SetResult(); await independent.WaitAsync(Deadline); await h.Store.PrepareAsync(h.Context, Progress, default);
        Assert.Equal([CreaturesPolicy.Table], h.PreparedTables);
    }
    [Fact]
    public async Task DirectShutdownAwaitsAcceptedSharedPrerequisiteCleanup()
    {
        await using var h = await Harness.Create(missing: true); var entered = Signal(); var cleanupEntered = Signal(); var cleanup = Signal(); var never = Signal();
        h.BeforePrepare = async ct => { entered.TrySetResult(); try { await never.Task.WaitAsync(ct); } finally { cleanupEntered.TrySetResult(); await cleanup.Task; } };
        var accepted = h.Store.PrepareAsync(h.Context, Progress, default); await entered.Task.WaitAsync(Deadline);
        var closing = h.Store.DisposeAsync().AsTask(); await cleanupEntered.Task.WaitAsync(Deadline); Assert.False(closing.IsCompleted); Assert.False(accepted.IsCompleted);
        cleanup.SetResult(); await closing.WaitAsync(Deadline); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accepted);
    }
    [Fact]
    public async Task SourceReplacementBeforeCommitRejectsPublicationAndRetryRecovers()
    {
        await using var h = await Harness.Create(); var replaced = false;
        Assert.Equal("CREATURES_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(h.Context, async p =>
        { if (!replaced && p.Completed > 0) { replaced = true; await h.Replace("Replacement"); } }, default))).Code);
        Assert.Null(await h.Catalog.ReadCreaturesAdmissionAsync(h.Opening, default));
        var ready = await h.Store.PrepareAsync(h.Context, Progress, default); Assert.Equal("Replacement", Assert.Single((await h.Store.ReadPageAsync(new(h.Context, ready.ArtifactId), default)).Entries).Name.Value);
    }
    [Fact]
    public async Task FinalFenceRejectsOldSourceAndCachedReplacementRetryUsesCurrentArtifact()
    {
        await using var h = await Harness.Create(); var first = await h.Store.PrepareAsync(h.Context, Progress, default); var old = (await h.Catalog.ReadCreaturesAdmissionAsync(h.Opening, default))!.Artifact;
        await h.Replace("Replacement"); Assert.Equal("CREATURES_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReauthorizeCreaturesAsync(h.Opening, old, default))).Code);
        var second = await h.Store.PrepareAsync(h.Context, Progress, default); Assert.NotEqual(first.ArtifactId, second.ArtifactId);
        Assert.Equal("CREATURES_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.ReadPageAsync(new(h.Context, first.ArtifactId), default))).Code);
        Assert.Equal("Replacement", Assert.Single((await h.Store.ReadPageAsync(new(h.Context, second.ArtifactId), default)).Entries).Name.Value);
    }
    [Theory]
    [InlineData("config")] [InlineData("content")] [InlineData("encoding")] [InlineData("root")]
    public async Task IdentityTimeNormalizationNeverWeakensOriginalReceiptAuthority(string receipt)
    {
        await using var h = await Harness.Create(); var ready = await h.Store.PrepareAsync(h.Context, Progress, default); var original = (await h.Catalog.ReadCreaturesAdmissionAsync(h.Opening, default))!.Artifact;
        var b = original.Binding; const string forged = "2026-10-20T00:00:00Z";
        var changed = receipt switch
        {
            "config" => b with { Configurations = b.Configurations.Select(c => c with { RetrievedAt = forged }).ToArray() },
            "content" => b with { Source = b.Source with { Source = b.Source.Source with { RetrievedAt = forged } } },
            "encoding" => b with { Source = b.Source with { Association = b.Source.Association with { EncodingSource = b.Source.Association.EncodingSource with { RetrievedAt = forged } } } },
            _ => b with { Source = b.Source with { Association = b.Source.Association with { RootSource = b.Source.Association.RootSource with { RetrievedAt = forged } } } }
        };
        Assert.Equal(CreaturesPolicy.ContentKey(b), CreaturesPolicy.ContentKey(changed));
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitCreaturesAdmissionAsync(h.Opening, original with { Binding = changed }, ready.PreparedAt, default));
        Assert.Contains(error.Code, new[] { "CREATURES_STALE", "CREATURES_PROJECTION_INVALID" });
        Assert.Equal(ready.ArtifactId, (await h.Store.ReadAsync(h.Context, default)).Snapshot!.ArtifactId);
    }
    [Theory]
    [InlineData("{\"binding\":null}")] [InlineData("{}")]
    public async Task MalformedPersistedCoreDescriptorFailsTyped(string json)
    {
        await using var h = await Harness.Create(); await h.Store.PrepareAsync(h.Context, Progress, default);
        await h.Sql("UPDATE creatures_artifacts SET descriptor=$v", json);
        Assert.Equal("CREATURES_PROJECTION_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.ReadAsync(h.Context, default))).Code);
    }
    [Theory]
    [InlineData("source")] [InlineData("association")] [InlineData("descriptor")]
    public async Task PersistedSourceJsonLengthIsGuardedBeforeDeserialize(string column)
    {
        await using var h = await Harness.Create();
        await h.Sql(column == "descriptor" ? "UPDATE table_artifacts SET descriptor=$v" : "UPDATE table_admissions SET " + column + "=$v", new string('x', 131073));
        Assert.Equal("CREATURES_PROJECTION_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.ReadAsync(h.Context, default))).Code);
        Assert.Empty(h.ReadTables); Assert.Empty(h.PreparedTables);
    }
    [Fact]
    public async Task CurrentOpeningFenceRejectsHistoricalReferenceEvenWhenRetainedBytesRemain()
    {
        await using var h = await Harness.Create(); await h.Store.PrepareAsync(h.Context, Progress, default); var old = (await h.Catalog.ReadCreaturesAdmissionAsync(h.Opening, default))!.Artifact;
        await h.RefreshOpening(); Assert.Equal("CREATURES_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReauthorizeCreaturesAsync(h.Opening, old, default))).Code);
    }
    [Fact]
    public async Task AnUnlistedBuildFailsBeforeAnyCreatureDownload()
    {
        // D080 lets Tables open an unlisted build by layout; Creatures still needs a listed build and must not download first.
        await using var h = await Harness.Create(missing: true, version: "12.1.5.70575");
        Assert.Equal("SCHEMA_BUILD_NOT_LISTED", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(h.Context, Progress, default))).Code);
        Assert.Empty(h.ReadTables); Assert.Empty(h.PreparedTables);
    }
    [Fact]
    public async Task PreparationNeverRetargetsDiscoveryDuringSharedPrerequisite()
    {
        await using var h = await Harness.Create(missing: true);
        h.BeforePrepare = _ => h.RefreshOpening();
        Assert.Equal("CREATURES_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(h.Context, Progress, default))).Code);
        Assert.Null(await h.Catalog.FindCreaturesArtifactAsync(CreaturesPolicy.ContentKey(h.Binding), default));
    }
    [Fact]
    public async Task ThreeContextsRetainTwoSessionsAndFourBorrowedQueriesDoNotShareAScanLock()
    {
        await using var h = await Harness.Create(); var contexts = new List<(CreaturesContext Context, string Artifact)>();
        for (var i = 0; i < 3; i++)
        {
            if (i != 0) await h.AddContext(i);
            var prepared = await h.Store.PrepareAsync(h.Context, _ => { Assert.InRange(h.Store.Pressure.Sessions, 0, 2); return Task.CompletedTask; }, default);
            contexts.Add((h.Context, prepared.ArtifactId)); Assert.InRange(h.Store.Pressure.Sessions, 1, 2);
        }
        // Warm two contexts, then hold the shared CPU. Both accepted queries must independently borrow their sessions.
        foreach (var context in contexts.Take(2)) await h.Store.ReadAsync(context.Context, default);
        using var cpu = await h.Resources.EnterCpuAsync(null, default); using var stop = new CancellationTokenSource();
        var first = h.Store.ReadPageAsync(new(contexts[0].Context, contexts[0].Artifact), stop.Token);
        var second = h.Store.ReadPageAsync(new(contexts[1].Context, contexts[1].Artifact), default);
        await Until(() => h.Store.Pressure.Borrowed == 2); Assert.Equal(2, h.Store.Pressure.Sessions);
        Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(contexts[2].Context, Progress, default))).Code);
        var third = h.Store.ReadPageAsync(new(contexts[0].Context, contexts[0].Artifact), default); var fourth = h.Store.ReadPageAsync(new(contexts[1].Context, contexts[1].Artifact), default);
        await Until(() => h.Store.Pressure.Borrowed == 4); Assert.Equal(4, h.Store.Pressure.Queries);
        Assert.Equal("CREATURES_QUERY_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.ReadAsync(contexts[0].Context, default))).Code);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); Assert.False(second.IsCompleted); cpu.Dispose();
        await Task.WhenAll(second, third, fourth).WaitAsync(Deadline); Assert.Equal(0, h.Store.Pressure.Borrowed);
    }
    [Fact]
    public async Task TwoColdOpeningSlotsShareOneExactOpenAndCancelOnePreservesOtherWaiter()
    {
        await using var h = await Harness.Create(); var ready = await h.Store.PrepareAsync(h.Context, Progress, default); await h.Store.DisposeAsync(); h.Store = new(h.Catalog, h.Projection.Objects, h.Tables, h.Resources);
        using var cpu = await h.Resources.EnterCpuAsync(null, default); using var stop = new CancellationTokenSource();
        var first = h.Store.ReadPageAsync(new(h.Context, ready.ArtifactId), stop.Token); var second = h.Store.ReadPageAsync(new(h.Context, ready.ArtifactId), default);
        await Until(() => h.Store.Pressure.Borrowed == 2); Assert.Equal(1, h.Store.Pressure.Sessions); Assert.Equal(1, h.Store.Pressure.Opening);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); Assert.False(second.IsCompleted); Assert.Equal(1, h.Store.Pressure.Sessions);
        cpu.Dispose(); Assert.Equal(3, (await second.WaitAsync(Deadline)).Entries.Count);
    }
    [Fact]
    public async Task TwoStoppingSourceOpensKeepCapacityThroughArbitraryCancellationNotifications()
    {
        await using var h = await Harness.Create(); var contexts = new List<(CreaturesContext Context, string Artifact)>();
        for (var i = 0; i < 3; i++) { if (i != 0) await h.AddContext(i); var ready = await h.Store.PrepareAsync(h.Context, Progress, default); contexts.Add((h.Context, ready.ArtifactId)); }
        await h.Store.DisposeAsync(); using var release = new ManualResetEventSlim(); var never = Signal(); var entered = 0; var notifications = 0; var block = true;
        var registrations = new List<CancellationTokenRegistration>();
        h.Store = new(h.Catalog, h.Projection.Objects, h.Tables, h.Resources, async (sha, length, ct) =>
        {
            if (!block) return await h.Projection.Objects.OpenVerifiedAsync(sha, length, ct);
            registrations.Add(ct.Register(() => { Interlocked.Increment(ref notifications); if (!release.Wait(Deadline)) throw new TimeoutException(); }));
            Interlocked.Increment(ref entered); await never.Task.WaitAsync(ct); throw new IOException("unreachable");
        });
        using var stopA = new CancellationTokenSource(); using var stopB = new CancellationTokenSource();
        var a = h.Store.ReadPageAsync(new(contexts[0].Context, contexts[0].Artifact), stopA.Token);
        try
        {
            await Until(() => Volatile.Read(ref entered) == 1); stopA.Cancel(); await Until(() => Volatile.Read(ref notifications) == 1);
            var b = h.Store.ReadPageAsync(new(contexts[1].Context, contexts[1].Artifact), stopB.Token);
            await Until(() => Volatile.Read(ref entered) == 2); stopB.Cancel(); await Until(() => Volatile.Read(ref notifications) == 2);
            Assert.False(a.IsCompleted); Assert.False(b.IsCompleted); Assert.Equal(2, h.Store.Pressure.Sessions);
            Assert.Equal("CREATURES_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(contexts[2].Context, Progress, default))).Code);
            release.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
            Assert.Equal(0, h.Store.Pressure.Sessions); block = false;
            Assert.Equal(contexts[2].Artifact, (await h.Store.PrepareAsync(contexts[2].Context, Progress, default)).ArtifactId);
        }
        finally { release.Set(); foreach (var registration in registrations) await registration.DisposeAsync(); }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FreshSessionNeverCallsMissingOriginalRowsOrEncodedSourceReady(bool encoded)
    {
        await using var h = await Harness.Create(); await h.Store.PrepareAsync(h.Context, Progress, default); await h.Store.DisposeAsync();
        var sha = encoded ? h.Binding.Source.Source.Sha256 : h.Binding.Rows.Sha256;
        var path = Path.Combine(h.Projection.DirectoryPath, "objects-v1", sha[..2], sha[2..]); File.Delete(path);
        h.Store = new(h.Catalog, h.Projection.Objects, h.Tables, h.Resources);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.ReadAsync(h.Context, default)); Assert.Equal(0, h.Store.Pressure.Sessions);
    }
    [Fact]
    public async Task WarmQueriesKeepOriginalLeasesWithoutRepeatedSourceOpens()
    {
        await using var h = await Harness.Create(); await h.Store.DisposeAsync(); var opens = 0;
        h.Store = new(h.Catalog, h.Projection.Objects, h.Tables, h.Resources, (sha, length, ct) => { Interlocked.Increment(ref opens); return h.Projection.Objects.OpenVerifiedAsync(sha, length, ct); });
        var ready = await h.Store.PrepareAsync(h.Context, Progress, default);
        for (var i = 0; i < 3; i++) { await h.Store.ReadAsync(h.Context, default); await h.Store.ReadPageAsync(new(h.Context, ready.ArtifactId, "none"), default); await h.Store.InspectAsync(new(h.Context, ready.ArtifactId, "9"), default); }
        Assert.Equal(1, opens); Assert.Empty(h.PreparedTables);
    }
    [Fact]
    public async Task ShutdownAttemptsBothHeldSessionsEvenWhenOneFileCloseReportsFailure()
    {
        await using var h = await Harness.Create(); await h.Store.DisposeAsync(); var closes = 0; var opens = 0;
        var store = new CreaturesStore(h.Catalog, h.Projection.Objects, h.Tables, h.Resources, async (sha, length, ct) =>
        {
            await using var verified = await h.Projection.Objects.OpenVerifiedAsync(sha, length, ct);
            return new CloseFailureFile(verified.Name, ++opens == 1, () => Interlocked.Increment(ref closes));
        });
        await store.PrepareAsync(h.Context, Progress, default); await h.AddContext(1); await store.PrepareAsync(h.Context, Progress, default);
        Assert.Equal("CREATURES_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() => store.DisposeAsync().AsTask())).Code);
        Assert.Equal(2, closes); Assert.Equal(1, store.Pressure.Sessions);
    }
    private sealed class CloseFailureFile(string path, bool fail, Action closed) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous)
    {
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); closed(); if (fail) throw new IOException("authored close failure after resource retirement"); }
    }
    [Fact]
    public async Task DependencyReadinessHasCoherentSourceFenceAfterSharedTableRead()
    {
        await using var h = await Harness.Create(); h.BeforeRead = _ => h.Replace("Changed during readiness");
        Assert.Equal("CREATURES_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.ReadAsync(h.Context, default))).Code);
        h.BeforeRead = null; var current = await h.Store.ReadAsync(h.Context, default); Assert.Null(current.Snapshot); Assert.Equal(h.Binding.Rows.ContentKey, current.SourceArtifactId);
    }
    [Theory]
    [InlineData("source", "null")] [InlineData("association", """{"memberships":[null]}""")] [InlineData("descriptor", """{"binding":null}""")]
    public async Task NestedMalformedSourceAuthorityFailsTyped(string column, string json)
    {
        await using var h = await Harness.Create(); await h.Sql(column == "descriptor" ? "UPDATE table_artifacts SET descriptor=$v" : "UPDATE table_admissions SET " + column + "=$v", json);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.ReadAsync(h.Context, default)); Assert.Empty(h.ReadTables);
    }
    [Fact]
    public async Task CompactDisplayAuthorizationReturnsOriginalProofAndReleasesEveryBorrowBeforeNetwork()
    {
        await using var h = await Harness.Create(); var ready = await h.Store.PrepareAsync(h.Context, Progress, default);
        var authorization = await h.Store.AuthorizeDisplayAsync(h.Context, ready.ArtifactId, "9", default);
        Assert.Equal(h.Opening.Reference, authorization.Opening.Reference); Assert.Equal(ready.ArtifactId, authorization.Artifact.ContentKey);
        Assert.Equal(ready.PreparedAt, authorization.Snapshot.PreparedAt); Assert.Equal("9", authorization.Summary!.Id);
        Assert.Equal(["123", "123", "0", "-1"], authorization.Summary.DisplaySlots.Select(s => s.DisplayId));
        Assert.Equal(0, h.Store.Pressure.Borrowed); Assert.Equal(0, h.Store.Pressure.Queries); Assert.Empty(h.PreparedTables);
        var absent = await h.Store.AuthorizeDisplayAsync(h.Context, ready.ArtifactId, "8", default); Assert.Null(absent.Summary); Assert.Equal(ready.CreatureSource.Coverage, absent.Snapshot.CreatureSource.Coverage);
        await h.Replace("Changed after authorization");
        Assert.Equal("CREATURES_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReauthorizeCreaturesAsync(authorization.Opening, authorization.Artifact, default))).Code);
        Assert.Equal("CREATURES_NOT_PREPARED", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.AuthorizeDisplayAsync(h.Context, ready.ArtifactId, "9", default))).Code);
    }
    [Theory]
    [InlineData("x")] [InlineData("g64")] [InlineData("oversize")]
    public async Task PersistedAdmissionKeyIsBoundedAndCanonicalBeforeMaterialization(string poison)
    {
        await using var h = await Harness.Create(); var ready = await h.Store.PrepareAsync(h.Context, Progress, default);
        var artifact = (await h.Catalog.ReadCreaturesAdmissionAsync(h.Opening, default))!.Artifact;
        var key = poison == "oversize" ? new string('a', 131073) : poison == "g64" ? new string('g', 64) : "x";
        await h.Sql("PRAGMA foreign_keys=OFF; UPDATE creatures_admissions SET content_key=$v", key);
        Assert.Equal("CREATURES_PROJECTION_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.ReadAsync(h.Context, default))).Code);
        Assert.Equal("CREATURES_PROJECTION_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReauthorizeCreaturesAsync(h.Opening, artifact, default))).Code);
    }
    [Theory]
    [InlineData("content", false)] [InlineData("encoding", false)] [InlineData("root", false)]
    [InlineData("content", true)] [InlineData("encoding", true)] [InlineData("root", true)]
    public async Task CanonicalOriginalReceiptsAreBoundedBeforeSharedTablesEntry(string receipt, bool prepare)
    {
        await using var h = await Harness.Create();
        var table = receipt == "content" ? "content_objects" : "metadata_objects";
        var role = receipt == "content" ? "" : " WHERE receipt LIKE '%" + receipt + "%'";
        await h.Sql("UPDATE " + table + " SET receipt=$v" + role, new string('x', 131073));
        if (prepare) Assert.Equal("CREATURES_PROJECTION_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.PrepareAsync(h.Context, Progress, default))).Code);
        else Assert.Equal("CREATURES_PROJECTION_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.ReadAsync(h.Context, default))).Code);
        Assert.Empty(h.ReadTables); Assert.Empty(h.PreparedTables);
    }
    [Fact]
    public async Task RegisteredCoreProofPreservesOriginalPreparedSourceAuthority()
    {
        await using var h = await Harness.Create(); var ready = await h.Store.PrepareAsync(h.Context, Progress, default); var artifact = (await h.Catalog.ReadCreaturesAdmissionAsync(h.Opening, default))!.Artifact;
        var forged = artifact with { Binding = artifact.Binding with { Source = artifact.Binding.Source with { PreparedAt = "2026-10-20T00:00:00Z" } } };
        Assert.Equal(artifact.ContentKey, CreaturesPolicy.ContentKey(forged.Binding));
        Assert.Equal("CREATURES_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReauthorizeCreaturesAsync(h.Opening, forged, default))).Code);
        Assert.Equal("CREATURES_PROJECTION_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.CommitCreaturesAdmissionAsync(h.Opening, forged, ready.PreparedAt, default))).Code);
    }
    private static async Task Until(Func<bool> condition)
    { using var timeout = new CancellationTokenSource(Deadline); while (!condition()) await Task.Delay(1, timeout.Token); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal sealed class Harness : IAsyncDisposable
    {
        internal readonly CreaturesProjectionTests.Fixture Projection = new(); internal readonly InventoryFixture Fixture = InventoryFixture.Create();
        internal DiscoveryCatalog Catalog = null!; internal CapturedOpeningContext Opening = null!; internal CreaturesContext Context = null!; internal CreaturesBinding Binding = null!;
        internal CreaturesStore Store = null!; internal TableDataService Tables = null!; internal readonly AcquisitionResources Resources = new(maximumCpu: 1, maximumBackgroundCpu: 1);
        internal readonly List<string> ReadTables = [], PreparedTables = []; internal bool Missing; internal Func<CancellationToken, Task>? BeforePrepare; internal Func<CancellationToken, Task>? BeforeRead;
        internal TableDataRequest Table => new("wow", Context.BuildIdentity, "us", CreaturesPolicy.Table, "enUS");
        internal string Version = "12.1.5.70448";
        internal static async Task<Harness> Create(bool missing = false, string version = "12.1.5.70448")
        {
            var h = new Harness { Version = version }; h.Catalog = await DiscoveryCatalog.OpenAsync(h.Projection.DirectoryPath); await h.RefreshOpening();
            var configs = new List<ConfigurationReceipt>(); foreach (var (role, key, bytes) in new[] { ("buildConfig", h.Fixture.BuildKey, h.Fixture.BuildConfig), ("cdnConfig", h.Fixture.CdnKey, h.Fixture.CdnConfig) })
            {
                var sha = Convert.ToHexStringLower(SHA256.HashData(bytes)); await h.Projection.Objects.PublishAsync(bytes, sha, default);
                await h.Catalog.RecordConfigurationAsync(new(key, sha, bytes.Length, InventoryFixture.Url("config", key), CreaturesProjectionTests.Fixture.Time), default);
                configs.Add(new(role, key, sha, bytes.Length, InventoryFixture.Url("config", key), CreaturesProjectionTests.Fixture.Time, CreaturesProjectionTests.Fixture.Time, 1));
            }
            await h.Catalog.CommitWorkspaceAsync(h.Opening, configs, CreaturesProjectionTests.Fixture.Time, default);
            foreach (var role in new[] { "encoding", "root" }) await h.Catalog.RecordMetadataAsync(h.Metadata(role), default);
            h.Binding = await h.Source([new(0, "Name"), new(9, "Name", 0), new(4294967295, "")], configs, h.Fixture.PayloadA);
            h.Missing = missing; if (!missing) await h.Publish(); h.Tables = new(new ReadyTables(h)); h.Store = new(h.Catalog, h.Projection.Objects, h.Tables, h.Resources); return h;
        }
        internal async Task RefreshOpening()
        {
            var versions = Fixtures.Versions($"us|{Fixture.BuildKey}|{Fixture.CdnKey}||{Version.Split('.')[3]}|{Version}||creatures");
            var cdns = Encoding.UTF8.GetString(Fixture.Discovery.Documents.Single(d => d.Role == "cdns").Bytes);
            var discovery = await Fixtures.Attempt(versions, cdns); var generation = await Catalog.BeginRefreshAsync("wow", default);
            var page = await Catalog.CommitAsync("wow", generation, discovery, CreaturesProjectionTests.Fixture.Time, default); Context = new("wow", page.Builds.Single().Id, "us", "enUS");
            var current = await Catalog.ResolveOpeningAsync(new("wow", Context.BuildIdentity, "us"), default); if (Opening is null) Opening = current;
        }
        internal async Task AddContext(int ordinal)
        {
            var configs = Binding.Configurations;
            var versions = Fixtures.Versions($"us|{Fixture.BuildKey}|{Fixture.CdnKey}||{70448 + ordinal}|12.1.5.70448||creatures-{ordinal}");
            var cdns = Encoding.UTF8.GetString(Fixture.Discovery.Documents.Single(d => d.Role == "cdns").Bytes);
            var discovery = await Fixtures.Attempt(versions, cdns); var generation = await Catalog.BeginRefreshAsync("wow", default);
            var page = await Catalog.CommitAsync("wow", generation, discovery, CreaturesProjectionTests.Fixture.Time, default);
            var build = page.Builds.Single(b => b.BuildId == (70448 + ordinal).ToString(CultureInfo.InvariantCulture)); Context = new("wow", build.Id, "us", "enUS");
            Opening = await Catalog.ResolveOpeningAsync(new("wow", Context.BuildIdentity, "us"), default);
            await Catalog.CommitWorkspaceAsync(Opening, configs, CreaturesProjectionTests.Fixture.Time, default);
            Binding = Binding with { Context = Context, SourceReference = Opening.Reference, Source = Binding.Source with { BuildIdentity = Context.BuildIdentity } }; await Publish();
        }
        private MetadataReceipt Metadata(string role)
        {
            var s = Fixture.Metadata[role]; return new(role, s.ContentKey, s.EncodingKey, Convert.ToHexStringLower(SHA256.HashData(s.Encoded)), N(s.Encoded.Length), N(s.Decoded.Length),
                InventoryFixture.Url("data", s.EncodingKey), CreaturesProjectionTests.Fixture.Time, CreaturesProjectionTests.Fixture.Time, MetadataAcquirer.SourceRevision);
        }
        private async Task<CreaturesBinding> Source(Row[] rows, IReadOnlyList<ConfigurationReceipt> configs, InventoryFixture.FixtureObject bytes)
        {
            var b = await Projection.Binding(rows); var content = new ContentReceipt(bytes.ContentKey, bytes.EncodingKey, Convert.ToHexStringLower(SHA256.HashData(bytes.Encoded)), N(bytes.Encoded.Length), N(bytes.Decoded.Length),
                InventoryFixture.Url("data", bytes.EncodingKey), CreaturesProjectionTests.Fixture.Time, CreaturesProjectionTests.Fixture.Time, "content-blte-v1", new("loose", "0", N(bytes.Encoded.Length), null));
            string path; await using (var file = await Projection.Objects.CreateStagingFileAsync(default)) { path = file.Name; await file.WriteAsync(bytes.Encoded); }
            await Projection.Objects.PublishFileAsync(path, bytes.Encoded.Length, default); await Catalog.RecordContentAsync(content, default);
            var association = new SelectedFileAssociation(CreaturesPolicy.FileDataId, "enUS", "enus-consistent-root-variants-v1", Metadata("encoding"), Metadata("root"), [new("0", "0", content.ContentKey, "2", "0", "0", "0", "0", null)]);
            await using var source = new TableRowArtifacts(Projection.Objects); await using var target = new TableRowArtifacts(Projection.Objects);
            await using var writer = await target.CreateWriterAsync(b.Rows.Binding with { SourceSha256 = content.Sha256 }, default); await writer.HeaderAsync(b.Rows.Header, default);
            var page = await source.ReadPageAsync(b.Rows, 0, 50, null, default); foreach (var row in page.Rows) await writer.RowAsync(row, default);
            var artifact = await writer.CompleteAsync(b.Rows.Completion with { ConsumedBytes = bytes.Decoded.Length }, default);
            var snapshot = b.Source with { BuildIdentity = Context.BuildIdentity, ArtifactId = artifact.ContentKey, Source = content, Association = association };
            return b with { Context = Context, SourceReference = Opening.Reference, Configurations = configs, Source = snapshot, Rows = artifact };
        }
        internal async Task Replace(string name) { Binding = await Source([new(7, name)], Binding.Configurations, Fixture.PayloadB); await Publish(); }
        internal async Task Publish()
        { await Catalog.RecordTableArtifactAsync(Binding.Rows, default); await Catalog.CommitTableAdmissionAsync(Opening, CreaturesPolicy.Table, "enUS", Binding.Source.Source, Binding.Source.Association, Binding.Rows, CreaturesProjectionTests.Fixture.Time, default); }
        internal async Task Sql(string sql, string value)
        { await using var db = new SqliteConnection("Data Source=" + Path.Combine(Projection.DirectoryPath, "discovery-v1.sqlite3")); await db.OpenAsync(); await using var command = db.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("$v", value); await command.ExecuteNonQueryAsync(); }
        private sealed class ReadyTables(Harness h) : ITableDataStore
        {
            public async Task<TableDataReadiness> ReadAsync(TableDataRequest request, CancellationToken ct)
            { h.ReadTables.Add(request.TableId); var snapshot = h.Missing ? null : h.Binding.Source; if (h.BeforeRead is not null) await h.BeforeRead(ct); return new(snapshot); }
            public async Task<TableDataSnapshot> PrepareAsync(TableDataRequest request, Func<string, Task> progress, CancellationToken ct)
            { h.PreparedTables.Add(request.TableId); if (h.BeforePrepare is not null) await h.BeforePrepare(ct); await h.Publish(); h.Missing = false; return h.Binding.Source; }
            public Task<TableDataPage> ReadRowsAsync(TableRowsRequest request, CancellationToken ct) => throw new InvalidOperationException("No per-Creature original-row queries through shared Tables.");
        }
        private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
        public async ValueTask DisposeAsync() { if (Store is not null) await Store.DisposeAsync(); if (Tables is not null) await Tables.DisposeAsync(); if (Catalog is not null) await Catalog.DisposeAsync(); await Projection.DisposeAsync(); }
    }
}
