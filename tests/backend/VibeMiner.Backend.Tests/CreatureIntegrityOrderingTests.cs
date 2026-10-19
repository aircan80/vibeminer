using System.Security.Cryptography;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Creature projection builder")]
public sealed class CreatureIntegrityOrderingTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static Task Progress(CreaturesPreparationProgress _) => Task.CompletedTask;

    [Theory]
    [InlineData("source", false)] [InlineData("source", true)]
    [InlineData("rows", false)] [InlineData("rows", true)]
    [InlineData("projection", false)] [InlineData("projection", true)]
    public async Task Core_default_source_cold_rows_and_projection_wait_for_real_integrity_stripe_without_CPU(string target, bool collision)
    {
        await using var h = await CreaturesStoreTests.Harness.Create();
        var ready = await h.Store.PrepareAsync(h.Context, Progress, default);
        var saved = (await h.Catalog.ReadCreaturesAdmissionAsync(h.Opening, default))!;
        var sha = target switch { "source" => h.Binding.Source.Source.Sha256, "rows" => h.Binding.Rows.Sha256, _ => saved.Artifact.Sha256 };
        await using var stripe = new BlockedIntegrity(h.Projection.DirectoryPath);
        await stripe.HoldAsync(sha, collision);
        await using var store = new CreaturesStore(h.Catalog, stripe.Objects, h.Tables, stripe.Resources);
        var reading = store.ReadPageAsync(new(h.Context, ready.ArtifactId), stripe.Token);
        await stripe.AssertCpuAvailableAsync(reading);
        if (!collision) Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() => reading)).Code);
        else
        {
            var page = await reading.WaitAsync(Deadline);
            Assert.Equal(["0", "9", "4294967295"], page.Entries.Select(row => row.Id));
            Assert.Equal("Name", page.Entries[1].Name.Value);
            var detail = await store.InspectAsync(new(h.Context, ready.ArtifactId, "9"), stripe.Token).WaitAsync(Deadline);
            Assert.True(detail.Detail!.CreatureRow.IsCopy); Assert.Equal("0", detail.Detail.CreatureRow.CopySourceId);
            Assert.Empty(h.PreparedTables);
        }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public async Task Display_default_source_and_cold_rows_wait_for_actual_integrity_stripe_without_CPU(bool rows, bool collision)
    {
        await using var fixture = await CreatureDisplaySourcesTests.Fixture.Create();
        var target = fixture.Catalog.Targets.Single(t => t.TableId == CreatureDisplayPolicy.DisplayTable).Admission;
        var current = await fixture.Catalog.Catalog.ReadCreatureDisplayTargetAsync(fixture.Catalog.Opening, CreatureDisplayPolicy.DisplayTable, default);
        var sha = rows ? current!.Artifact.Sha256 : target.Source.Sha256;
        await using var stripe = new BlockedIntegrity(fixture.Catalog.Projection.DirectoryPath);
        await stripe.HoldAsync(sha, collision);
        await using var sources = new CreatureDisplaySources(fixture.Catalog.Catalog, stripe.Objects, fixture.Creatures, fixture.Tables, stripe.Resources);
        var probing = sources.ProbeAsync(fixture.Core, CreatureDisplayTargetRole.Display, "123", stripe.Token);
        await stripe.AssertCpuAvailableAsync(probing);
        if (!collision) Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() => probing)).Code);
        else
        {
            var result = await probing.WaitAsync(Deadline);
            Assert.NotNull(result); Assert.Equal("33", result.Edge); Assert.True(result.Target.Row!.IsCopy); Assert.Equal("122", result.Target.Row.CopySourceId);
            Assert.Empty(fixture.Prepared);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Core_and_display_custom_source_delegate_waits_leave_CPU_available(bool display)
    {
        await using var fixture = await CreatureDisplaySourcesTests.Fixture.Create();
        var resources = new AcquisitionResources(maximumCpu: 1, maximumBackgroundCpu: 1);
        var entered = Signal(); var release = Signal();
        async Task<FileStream> Open(string sha, long length, CancellationToken token)
        { entered.TrySetResult(); await release.Task.WaitAsync(token); return await fixture.Catalog.Projection.Objects.OpenVerifiedAsync(sha, length, resources, null, token); }
        await using var core = new CreaturesStore(fixture.Catalog.Catalog, fixture.Catalog.Projection.Objects, fixture.Tables, resources, Open);
        await using var sources = new CreatureDisplaySources(fixture.Catalog.Catalog, fixture.Catalog.Projection.Objects, fixture.Creatures, fixture.Tables, resources, Open);
        Task work = display ? sources.ProbeAsync(fixture.Core, CreatureDisplayTargetRole.Display, "123", default) : core.ReadAsync(fixture.Core.Snapshot.Context, default);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            using (await resources.EnterCpuAsync(null, default).WaitAsync(Deadline)) Assert.False(work.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await work.WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Projection_spool_IO_and_progress_do_not_retain_CPU_and_cancel_cleans_staging(bool progress)
    {
        await using var fixture = new CreaturesProjectionTests.Fixture();
        var binding = await fixture.Binding([new(1, "Name")]);
        var resources = new AcquisitionResources(maximumCpu: 1, maximumBackgroundCpu: 1);
        var entered = Signal(); var release = Signal(); var paths = new List<string>(); var count = 0;
        using var stop = new CancellationTokenSource();
        var projection = new CreaturesProjection(fixture.Objects, resources, createStaging: async token =>
        {
            var file = await fixture.Objects.CreateStagingFileAsync(token); var path = file.Name; paths.Add(path);
            if (progress || ++count != 1) return file;
            await file.DisposeAsync(); return new HeldWrite(path, entered, release);
        });
        await using var reader = new TableRowArtifacts(fixture.Objects, resources, null);
        var building = projection.BuildAsync(binding, reader, async _ => { if (progress) { entered.TrySetResult(); await release.Task.WaitAsync(stop.Token); } }, stop.Token);
        try
        {
            await entered.Task.WaitAsync(Deadline);
            using (await resources.EnterCpuAsync(null, default).WaitAsync(Deadline)) Assert.False(building.IsCompleted);
            stop.Cancel();
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => building);
        Assert.All(paths, path => Assert.False(File.Exists(path)));
        // Builder and storage reservation retire after actual close/delete, permitting a fresh attempt.
        var ready = await projection.BuildAsync(binding, reader, Progress, default).WaitAsync(Deadline);
        await using var session = await projection.OpenAsync(ready, CreaturesProjectionTests.Fixture.Time, default).WaitAsync(Deadline);
        Assert.Equal("Name", (await session.FindAsync(1, default))!.Name.Value);
    }

    private sealed class HeldWrite(string path, TaskCompletionSource entered, TaskCompletionSource release)
        : FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous)
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { entered.TrySetResult(); await release.Task.WaitAsync(cancellationToken); await base.WriteAsync(buffer, cancellationToken); }
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Clone only authored immutable fixture objects into a separately owned test store.
    // This observer/journal own all tested operations and the actual same/colliding stripe.
    private sealed class BlockedIntegrity : IAsyncDisposable
    {
        private readonly string directory, originals;
        private readonly TempDirectory temporary = new();
        private readonly TaskCompletionSource journal = Signal(), release = Signal(), arrival = Signal();
        private readonly CancellationTokenSource stop = new(Deadline);
        private string? observing; private Task? blocker; private Task? adapter;
        internal AcquisitionResources Resources { get; } = new(maximumCpu: 1, maximumBackgroundCpu: 1);
        internal VerifiedObjectStore Objects { get; }
        internal CancellationToken Token => stop.Token;
        internal BlockedIntegrity(string directory)
        {
            originals = directory; this.directory = temporary.Path;
            Objects = new(this.directory, async (_, _, _, _) => { journal.TrySetResult(); await release.Task; },
                sha => { if (sha == Volatile.Read(ref observing)) arrival.TrySetResult(); });
        }
        private string PathFor(string sha) => Path.Combine(directory, "objects-v1", sha[..2], sha[2..]);
        internal async Task HoldAsync(string sha, bool collision)
        {
            foreach (var source in Directory.EnumerateFiles(Path.Combine(originals, "objects-v1"), "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(Path.Combine(originals, "objects-v1"), source);
                var parts = relative.Split(Path.DirectorySeparatorChar);
                if (parts.Length != 2 || parts[0].Length != 2 || parts[1].Length != 62 || !(parts[0] + parts[1]).All(Uri.IsHexDigit)) continue;
                var destination = Path.Combine(directory, "objects-v1", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
                await input.CopyToAsync(output, Token);
            }
            var original = await File.ReadAllBytesAsync(PathFor(sha), Token);
            var bytes = collision ? Enumerable.Range(0, 65536).Select(i => new byte[] { (byte)i, (byte)(i >> 8), 7 })
                .First(bytes => Hash(bytes) != sha && Stripe(Hash(bytes)) == Stripe(sha)) : original;
            var blockedSha = sha;
            if (collision)
            {
                string path; await using (var file = await Objects.CreateStagingFileAsync(Token)) { path = file.Name; await file.WriteAsync(bytes, Token); }
                blockedSha = await Objects.PublishFileAsync(path, bytes.Length, Resources, null, Token);
            }
            await File.WriteAllBytesAsync(PathFor(blockedSha), [9], Token);
            blocker = Objects.OpenVerifiedAsync(blockedSha, bytes.Length, Resources, null, Token);
            await journal.Task.WaitAsync(Deadline); Volatile.Write(ref observing, sha);
        }
        internal async Task AssertCpuAvailableAsync(Task operation)
        {
            adapter = operation;
            try
            {
                await arrival.Task.WaitAsync(Deadline); Assert.False(operation.IsCompleted);
                using (await Resources.EnterCpuAsync(null, Token).WaitAsync(Deadline))
                { Assert.False(release.Task.IsCompleted); Assert.False(operation.IsCompleted); }
            }
            finally { release.TrySetResult(); }
        }
        private static int Stripe(string sha) => Convert.ToByte(sha[..2], 16) % 64;
        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        public async ValueTask DisposeAsync()
        {
            release.TrySetResult(); stop.Cancel();
            try { if (adapter is not null) await adapter.WaitAsync(Deadline); } catch (Exception) { /* Observe test failure cleanup. */ }
            try { if (blocker is not null) await blocker.WaitAsync(Deadline); } catch (DiscoveryException error) when (error.Code == "LOCAL_OBJECT_CORRUPT") { }
            finally { await Objects.DisposeAsync(); stop.Dispose(); temporary.Dispose(); }
        }
    }
}
