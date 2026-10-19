using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Items projection builder")]
public sealed class ItemsIntegrityOrderingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cold_build_and_open_wait_for_colliding_stripe_without_owning_cpu(bool reopen)
    {
        await using var f = new IntegrityOrderingFixture();
        var binding = await f.ItemsBinding();
        var projection = new ItemsProjection(f.Objects, f.Resources);
        ItemsProjectionArtifact? artifact = reopen ? await projection.BuildAsync(binding, _ => Task.CompletedTask, default) : null;
        var target = artifact?.Sha256 ?? binding.ItemRows.Sha256;
        await f.WhileCollidingStripeIsHeld(target, async () =>
        {
            artifact ??= await projection.BuildAsync(binding, _ => Task.CompletedTask, default);
            await using var session = await projection.OpenAsync(artifact, IntegrityOrderingFixture.Time, default);
            var page = await session.ReadPageAsync(new(IntegrityOrderingFixture.Context, artifact.ContentKey, ""), default);
            Assert.Equal(50, page.Entries.Count);
            Assert.Equal("101", artifact.Counts.ItemRows);
            var detail = await session.InspectAsync(new(IntegrityOrderingFixture.Context, artifact.ContentKey, "101"), default);
            Assert.NotNull(detail.Detail);
        });
        Assert.Empty(f.StagingFiles);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Unconfirmed_private_delete_attempts_all_paths_retains_credit_and_refuses_only_this_builder(int failing)
    {
        await using var f = new IntegrityOrderingFixture(); var binding = await f.ItemsBinding();
        var limits = new ItemsProjectionLimits(2 << 20, 16 << 10, 128);
        var credit = limits.MaxArtifactBytes + limits.MaxNameBytes + limits.MaxRows * 32L + (1 << 20);
        var resources = new AcquisitionResources(maximumCpu: 1, maximumBackgroundCpu: 1,
            maximumStagingBytes: credit * 2, maximumBackgroundStagingBytes: credit * 2);
        var deleted = new List<string>();
        var projection = new ItemsProjection(f.Objects, resources, configuredLimits: limits, deleteStaging: path =>
        {
            deleted.Add(path); if (deleted.Count - 1 == failing) throw new IOException("Authored deletion failure."); File.Delete(path);
        });
        Assert.Equal("ITEMS_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            projection.BuildAsync(binding, _ => Task.CompletedTask, default))).Code);
        Assert.Equal(3, deleted.Count); Assert.Equal(3, deleted.Distinct().Count());
        await AssertCreditRetained(resources, credit, async () =>
        {
            Assert.Equal("ITEMS_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                projection.BuildAsync(binding, _ => Task.CompletedTask, default))).Code);
            Assert.Equal(3, deleted.Count);
            var independent = new ItemsProjection(f.Objects, resources, configuredLimits: limits);
            var artifact = await independent.BuildAsync(binding, _ => Task.CompletedTask, default);
            await using var session = await projection.OpenAsync(artifact, IntegrityOrderingFixture.Time, default);
        });
    }

    internal static async Task AssertCreditRetained(AcquisitionResources resources, long credit, Func<Task> independent)
    {
        using var cancel = new CancellationTokenSource();
        var witness = resources.ReserveStagingAsync(credit + 1, null, cancel.Token);
        try
        {
            // Both builders retired; a queued request larger than known remaining capacity proves the exact debt is charged.
            Assert.False(witness.IsCompleted);
            await independent(); Assert.False(witness.IsCompleted);
        }
        finally
        {
            await cancel.CancelAsync();
            try { using var unexpected = await witness; Assert.Fail("Unconfirmed staging credit was released."); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        }
    }

    [Fact]
    public async Task Confirmed_private_cleanup_returns_complete_staging_credit()
    {
        await using var f = new IntegrityOrderingFixture(); var binding = await f.ItemsBinding();
        var limits = new ItemsProjectionLimits(2 << 20, 16 << 10, 128);
        var credit = limits.MaxArtifactBytes + limits.MaxNameBytes + limits.MaxRows * 32L + (1 << 20);
        var resources = new AcquisitionResources(maximumStagingBytes: credit, maximumBackgroundStagingBytes: credit);
        var projection = new ItemsProjection(f.Objects, resources, configuredLimits: limits);
        await projection.BuildAsync(binding, _ => Task.CompletedTask, default);
        using var returned = await resources.ReserveStagingAsync(credit, null, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(f.StagingFiles);
    }

    [Fact]
    public async Task Large_names_force_join_turns_below_the_row_count_cap_with_cpu_free()
    {
        await using var f = new IntegrityOrderingFixture();
        var binding = await f.ItemsBinding(24, new string('x', 50000)); var turns = new List<long>();
        var projection = new ItemsProjection(f.Objects, f.Resources);
        var artifact = await projection.BuildAsync(binding, async stage =>
        {
            if (!stage.Stage.StartsWith("Joining", StringComparison.Ordinal)) return;
            turns.Add(stage.Completed!.Value);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var cpu = await f.Resources.EnterCpuAsync(null, stop.Token);
        }, default);
        Assert.Equal("24", artifact.Counts.ValidNames); Assert.NotEmpty(turns);
        Assert.All(turns, completed => Assert.InRange(completed, 1, 47));
    }

    [Fact]
    public async Task Join_progress_runs_after_cpu_is_released_and_callback_failure_retires_builder()
    {
        await using var f = new IntegrityOrderingFixture();
        var binding = await f.ItemsBinding(4100);
        var projection = new ItemsProjection(f.Objects, f.Resources);
        var expected = new IOException("authored progress failure");
        var failure = await Assert.ThrowsAsync<IOException>(() => projection.BuildAsync(binding, async stage =>
        {
            if (!stage.Stage.StartsWith("Joining", StringComparison.Ordinal)) return;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var cpu = await f.Resources.EnterCpuAsync(null, stop.Token);
            throw expected;
        }, default));
        Assert.Same(expected, failure); Assert.Empty(f.StagingFiles);
        var retry = await projection.BuildAsync(binding, _ => Task.CompletedTask, default);
        Assert.Equal("4100", retry.Counts.ItemRows);
    }
}

[Collection("Classification index builder")]
public sealed class ClassificationIntegrityOrderingTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public async Task Failed_delete_retains_exact_credit_all_paths_and_only_the_affected_builder(bool composite, int failing)
    {
        await using var f = new IntegrityOrderingFixture(); var binding = await f.ClassificationBinding(composite);
        const long credit = 32L << 20;
        var resources = new AcquisitionResources(maximumCpu: 1, maximumBackgroundCpu: 1,
            maximumStagingBytes: credit * 2, maximumBackgroundStagingBytes: credit * 2);
        var paths = new List<string>();
        void Delete(string path) { paths.Add(path); if (paths.Count - 1 == failing) throw new IOException("Authored deletion failure."); File.Delete(path); }
        var classes = new ClassificationIndex(f.Objects, resources, deleteStaging: Delete);
        var pairs = new CompositeClassificationIndex(f.Objects, resources, deleteStaging: Delete);
        Task<ClassificationIndexArtifact> Build() => composite
            ? pairs.BuildAsync(binding, _ => Task.CompletedTask, default) : classes.BuildAsync(binding, _ => Task.CompletedTask, default);
        Assert.Equal("ITEM_CLASSIFICATIONS_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(Build)).Code);
        Assert.Equal(composite ? 2 : 1, paths.Count); Assert.Equal(paths.Count, paths.Distinct().Count());
        await ItemsIntegrityOrderingTests.AssertCreditRetained(resources, credit, async () =>
        {
            Assert.Equal("ITEM_CLASSIFICATIONS_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(Build)).Code);
            Assert.Equal(composite ? 2 : 1, paths.Count);
            // Static builder admission is released; another instance can use the remaining shared engine credit.
            if (composite) await new CompositeClassificationIndex(f.Objects, resources).BuildAsync(binding, _ => Task.CompletedTask, default);
            else await new ClassificationIndex(f.Objects, resources).BuildAsync(binding, _ => Task.CompletedTask, default);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Index_build_and_open_wait_for_colliding_stripe_without_owning_cpu(bool composite, bool reopen)
    {
        await using var f = new IntegrityOrderingFixture();
        var binding = await f.ClassificationBinding(composite);
        var index = new ClassificationIndex(f.Objects, f.Resources);
        var pairs = new CompositeClassificationIndex(f.Objects, f.Resources);
        Task<ClassificationIndexArtifact> Build() => composite
            ? pairs.BuildAsync(binding, _ => Task.CompletedTask, default)
            : index.BuildAsync(binding, _ => Task.CompletedTask, default);
        ClassificationIndexArtifact? artifact = reopen ? await Build() : null;
        await f.WhileCollidingStripeIsHeld(artifact?.Sha256 ?? binding.Rows.Sha256, async () =>
        {
            artifact ??= await Build();
            await using ClassificationIndexSession session = composite
                ? await pairs.OpenAsync(artifact, default) : await index.OpenAsync(artifact, default);
            var value = await session.ResolveAsync(composite ? ((2 + 128) << 8) | (3 + 128) : 2, [], default);
            Assert.Equal(ItemClassificationState.Ready, value.State);
            Assert.Equal("Label", value.Text);
        });
        Assert.Empty(f.StagingFiles);
    }
}

/// <summary>Authored app-owned files; the stripe observer reports an actual attempted wait, not elapsed time.</summary>
internal sealed class IntegrityOrderingFixture : IAsyncDisposable
{
    internal const string Time = "2026-10-19T00:00:00Z";
    internal static readonly ItemsContext Context = new("wow", new('a', 64), "us", "enUS");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vm-integrity-order-" + Guid.NewGuid().ToString("N"));
    private string? blockingSha, observedSha;
    private readonly TaskCompletionSource journalEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource journalRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource waitAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly AcquisitionResources Resources = new(maximumCpu: 1, maximumBackgroundCpu: 1);
    internal readonly VerifiedObjectStore Objects;
    internal IntegrityOrderingFixture() => Objects = new(directory, async (sha, _, _, _) =>
    {
        if (sha != blockingSha) return;
        journalEntered.TrySetResult(); await journalRelease.Task;
    }, sha => { if (sha == observedSha) waitAttempted.TrySetResult(); });
    internal IEnumerable<string> StagingFiles => Directory.EnumerateFiles(Path.Combine(directory, "staging-v1"));
    internal string ObjectPath(string sha) => Path.Combine(directory, "objects-v1", sha[..2], sha[2..]);
    internal async Task<string> Publish(byte[] bytes)
    {
        string path;
        await using (var stage = await Objects.CreateStagingFileAsync(default)) { path = stage.Name; await stage.WriteAsync(bytes); }
        return await Objects.PublishFileAsync(path, bytes.Length, default);
    }
    internal async Task WhileCollidingStripeIsHeld(string targetSha, Func<Task> operation)
    {
        byte[] blocker = new byte[16]; string sha;
        for (var candidate = 0; ; candidate++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(blocker, candidate);
            sha = Convert.ToHexStringLower(SHA256.HashData(blocker));
            if (sha != targetSha && Convert.ToByte(sha[..2], 16) % 64 == Convert.ToByte(targetSha[..2], 16) % 64) break;
            Assert.True(candidate < 4096, "The authored digest search must remain bounded.");
        }
        Assert.Equal(sha, await Publish(blocker)); blockingSha = sha;
        blocker[15] ^= 1; await File.WriteAllBytesAsync(ObjectPath(sha), blocker);
        var holding = Objects.OpenVerifiedAsync(sha, blocker.Length, Resources, null, default);
        Task? working = null;
        try
        {
            await journalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            observedSha = targetSha; working = operation();
            await waitAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Only after the real target stripe wait is observed, prove the sole CPU permit is free.
            using var witnessStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using (await Resources.EnterCpuAsync(null, witnessStop.Token))
                Assert.False(working.IsCompleted);
        }
        finally
        {
            journalRelease.TrySetResult();
            try { Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() => holding)).Code); }
            finally { if (working is not null) await working.WaitAsync(TimeSpan.FromSeconds(10)); }
        }
    }
    internal async Task<ItemsProjectionBinding> ItemsBinding(int count = 101, string? name = null)
    {
        Db2Column[] itemColumns = [new("ID", "integer", true), new("ClassID", "integer", false), new("SubclassID", "integer", false)];
        Db2Column[] sparseColumns = [new("ID", "integer", true), new("Display_lang", "string", false)];
        var item = await Table(ItemsProjection.ItemTable, itemColumns,
            Enumerable.Range(1, count).Select(i => new Db2Row((uint)i, false, null, [N(i), "2", "3"])).ToArray(), 'b');
        var sparse = await Table(ItemsProjection.SparseTable, sparseColumns,
            Enumerable.Range(1, count).Select(i => new Db2Row((uint)i, false, null, [N(i), name ?? "Name " + N(i)])).ToArray(), 'c');
        return new(Context, Snapshot(item, "841626"), Snapshot(sparse, "1572924"), item, sparse);
    }
    internal async Task<ClassificationIndexBinding> ClassificationBinding(bool composite)
    {
        Db2Column[] columns = composite
            ? [new("ID", "integer", true), new("ClassID", "integer", false), new("SubClassID", "integer", false), new("DisplayName_lang", "string", false)]
            : [new("ID", "integer", true), new("ClassID", "integer", false), new("ClassName_lang", "string", false)];
        var rows = await Table(composite ? ClassificationPolicy.SubclassTable : ClassificationPolicy.ClassTable, columns,
            [new(1, false, null, composite ? ["1", "2", "3", "Label"] : ["1", "2", "Label"])], 'd');
        var source = Snapshot(rows, composite ? ClassificationPolicy.SubclassFile : ClassificationPolicy.ClassFile);
        ConfigurationReceipt[] configurations = [new("buildConfig", new('1', 32), new('2', 64), 10, source.Source.SourceUrl, Time, Time, 1),
            new("cdnConfig", new('3', 32), new('4', 64), 10, source.Source.SourceUrl, Time, Time, 1)];
        return new(Context, "12.1.5.70448", new("1", "us", new('a', 64), 0, new('b', 64), 0), configurations,
            composite ? ItemClassificationKind.Subclass : ItemClassificationKind.Class, source, rows,
            composite ? [new("ClassID", 1, "integer", 8, false, true), new("SubClassID", 2, "integer", 8, false, true)] : [new("ClassID", 1, "integer", 8, false, true)],
            new(composite ? "DisplayName_lang" : "ClassName_lang", composite ? 3 : 2, "string", null, null, true));
    }
    private async Task<TableRowsArtifact> Table(string table, Db2Column[] columns, Db2Row[] rows, char key)
    {
        await using var reader = new TableRowArtifacts(Objects);
        await using var writer = await reader.CreateWriterAsync(new(new(key, 64), new('d', 64), new('e', 64), "authored-provider", "enUS", "authored-decoder", new('f', 64)), default);
        await writer.HeaderAsync(new(1, table.ToUpperInvariant(), "12345678", rows.Length, 0, 4, columns), default);
        foreach (var row in rows) await writer.RowAsync(row, default);
        return await writer.CompleteAsync(new(rows.Length, rows.Length, 0, 0, 0, 0, "complete", 10, 0), default);
    }
    private static TableDataSnapshot Snapshot(TableRowsArtifact rows, string file)
    {
        var b = rows.Binding; var c = rows.Completion;
        var metadata = new MetadataReceipt("encoding", new('1', 32), new('2', 32), new('3', 64), "10", "10", "https://us.cdn.blizzard.com/tpr/wow/data/authored", Time, Time, "authored-v1");
        var source = new ContentReceipt(new('4', 32), new('5', 32), b.SourceSha256, "10", "10", metadata.SourceUrl, Time, Time, "authored-v1", new("loose", "0", "10", null));
        return new(Context.ProductId, Context.BuildIdentity, Context.SourceRegion, rows.Header.TableHash.ToLowerInvariant(), "enUS", rows.ContentKey, Time,
            rows.Header.LayoutHash, b.DefinitionSha256, b.ProviderRevision, b.DecoderRevision, source,
            new(file, "enUS", "authored-v1", metadata, metadata with { Role = "root" }, []),
            rows.Header.Columns.Select(column => new TableDataColumn(column.Name, column.Kind, column.IsId, null)).ToArray(),
            new(c.Scope, N(c.DeclaredRecords), N(c.PhysicalRows), "0", "0", "0", "0", "0", "0"), N(c.PhysicalRows), "0");
    }
    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
    public async ValueTask DisposeAsync() { journalRelease.TrySetResult(); await Objects.DisposeAsync(); Directory.Delete(directory, true); }
}

public sealed class DerivedArtifactStagingOrderingTests
{
    [Fact]
    public async Task Uncertain_reservation_retirement_is_retained_and_never_retried_as_success()
    {
        var reservation = new UncertainReservation();
        var owner = new DerivedArtifactStaging(reservation, File.Delete, "ITEMS_CLEANUP_FAILED");
        var retirement = owner.RetireAsync();
        Assert.False(await retirement); Assert.Same(retirement, owner.RetireAsync());
        Assert.False(await owner.RetireAsync()); Assert.Equal(1, reservation.Attempts);
    }

    private sealed class UncertainReservation : IDisposable
    {
        internal int Attempts;
        public void Dispose() { Attempts++; throw new IOException("Authored uncertain credit retirement."); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_real_close_starts_before_wait_and_uncertain_retirement_keeps_the_reservation(bool fail)
    {
        var directory = Directory.CreateTempSubdirectory("vm-derived-close-").FullName;
        var resources = new AcquisitionResources(maximumStagingBytes: 100, maximumBackgroundStagingBytes: 100);
        var reservation = await resources.ReserveStagingAsync(100, null, default);
        var deleted = new List<string>();
        var owner = new DerivedArtifactStaging(reservation, path => { deleted.Add(path); File.Delete(path); }, "ITEMS_CLEANUP_FAILED");
        var files = Enumerable.Range(0, 3).Select(i => new ControlledClose(Path.Combine(directory, i + ".part"), fail && i == 0)).ToArray();
        using var cancel = new CancellationTokenSource(); Task<IDisposable>? witness = null;
        try
        {
            foreach (var file in files) await owner.CreateAsync(_ => Task.FromResult<FileStream>(file), default);
            var retiring = owner.RetireAsync();
            foreach (var file in files) await file.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(retiring.IsCompleted); Assert.Empty(deleted);
            witness = resources.ReserveStagingAsync(1, null, cancel.Token); Assert.False(witness.IsCompleted);
            files[0].Release.TrySetResult(); files[2].Release.TrySetResult();
            Assert.False(retiring.IsCompleted); Assert.False(witness.IsCompleted);
            files[1].Release.TrySetResult();
            Assert.Equal(!fail, await retiring.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(3, deleted.Count); Assert.All(files, file => Assert.Equal(1, file.Attempts));
            Assert.All(files, file => Assert.True(file.CapturedHandle.IsClosed));
            Assert.Same(retiring, owner.RetireAsync()); Assert.Equal(3, deleted.Count);
            if (fail) Assert.False(witness.IsCompleted);
            else { using var returned = await witness.WaitAsync(TimeSpan.FromSeconds(5)); witness = null; }
        }
        finally
        {
            foreach (var file in files) file.Release.TrySetResult();
            await owner.RetireAsync();
            await cancel.CancelAsync();
            if (witness is not null)
            {
                try { using var returned = await witness; }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            }
            Directory.Delete(directory, true);
        }
    }

    private sealed class ControlledClose : FileStream
    {
        private readonly bool fail;
        internal readonly Microsoft.Win32.SafeHandles.SafeFileHandle CapturedHandle;
        internal ControlledClose(string path, bool fail) : base(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
        { this.fail = fail; CapturedHandle = SafeFileHandle; }
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Attempts;
        public override async ValueTask DisposeAsync()
        {
            Attempts++; Entered.TrySetResult(); await Release.Task; await base.DisposeAsync();
            if (fail) throw new IOException("Authored uncertain close.");
        }
    }
}
