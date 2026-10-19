using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ResourceAwareTableRowTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);
    private static readonly TableArtifactBinding Binding = new(new('a', 64), new('b', 64), new('c', 64), "provider-pin", "enUS", Wdc5TableReader.Revision, new('d', 64));
    private static readonly Wdc5TableHeader Header = new(1, "50238EC2", "9A2A4834", 1, 0, 8, [new("ID", "integer", true), new("Name", "string", false)]);
    private static readonly Db2Row Single = new(7, false, null, ["7", "Name"]);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task ExplicitConstructorWaitsForOnlyCpuWhileCompatibilityReaderRemainsUngated()
    {
        await using var h = new Harness(); var artifact = await h.Build(h.Legacy, [Single]);
        var cpu = await h.Resources.EnterCpuAsync(null, default);
        try
        {
            Assert.Equal(1, (await h.Legacy.ReadUniqueAsync(artifact, 7, default).WaitAsync(Deadline)).MatchCount);
            using var stop = new CancellationTokenSource(); var cold = h.Aware.ReadUniqueAsync(artifact, 7, stop.Token);
            Assert.False(cold.IsCompleted); stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cold.WaitAsync(Deadline));
        }
        finally { cpu.Dispose(); }
        Assert.Equal("Name", (await h.Aware.ReadUniqueAsync(artifact, 7, default).WaitAsync(Deadline)).Row!.Values[1]);
        Assert.Throws<ArgumentNullException>(() => new TableRowArtifacts(h.Objects, null!, null));
    }

    [Fact]
    public async Task OneCpuColdPageAndUniqueUseOneSerializedReaderAndCanceledWaiterPreservesOwner()
    {
        await using var h = new Harness(); var artifact = await h.Build(h.Legacy, [Single, new(0, false, null, ["0", "Zero"])]);
        var cpu = await h.Resources.EnterCpuAsync(null, default);
        var owner = h.Aware.ReadPageAsync(artifact, 0, 1, null, default);
        try
        {
            Assert.False(owner.IsCompleted);
            using var stop = new CancellationTokenSource(); var waiter = h.Aware.ReadUniqueAsync(artifact, 7, stop.Token);
            Assert.False(waiter.IsCompleted); stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(Deadline)); Assert.False(owner.IsCompleted);
        }
        finally { cpu.Dispose(); }
        Assert.Equal(0u, Assert.Single((await owner.WaitAsync(Deadline)).Rows).Id);
        Assert.Equal("Name", (await h.Aware.ReadUniqueAsync(artifact, 7, default).WaitAsync(Deadline)).Row!.Values[1]);
        using var independent = await h.Resources.EnterCpuAsync(null, default).WaitAsync(Deadline);
    }

    [Fact]
    public async Task ResourceAwareReaderPreservesTheJoinedDynamicPriorityInsteadOfTreatingBackgroundAsInteractive()
    {
        await using var h = new Harness(); var artifact = await h.Build(h.Legacy, [Single]);
        var resources = new AcquisitionResources(maximumCpu: 2, maximumBackgroundCpu: 1); var resident = new MutableWork();
        using var background = await resources.EnterCpuAsync(resident, default); var work = new MutableWork();
        await using var aware = new TableRowArtifacts(h.Objects, resources, work);
        var query = aware.ReadUniqueAsync(artifact, 7, default);
        await work.Waiting.Task.WaitAsync(Deadline); Assert.False(query.IsCompleted);
        using (await resources.EnterCpuAsync(null, default).WaitAsync(Deadline)) Assert.False(query.IsCompleted);
        work.Promote();
        Assert.Equal("Name", (await query.WaitAsync(Deadline)).Row!.Values[1]); // Original background holder is still active.
    }

    [Fact]
    public async Task WarmPageAndUniqueKeepTheOriginalVerifiedFileLeaseInsteadOfOpeningAnotherPath()
    {
        await using var h = new Harness(); var artifact = await h.Build(h.Legacy, [Single]);
        await h.Aware.ReadPageAsync(artifact, 0, 1, null, default).WaitAsync(Deadline);
        var path = h.PathFor(artifact);
        try { File.Move(path, path + ".held"); }
        catch (IOException) when (OperatingSystem.IsWindows())
        { Assert.True(File.Exists(path)); } // Windows denies rename while that exact read lease is held.
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("Name", (await h.Aware.ReadUniqueAsync(artifact, 7, default).WaitAsync(Deadline)).Row!.Values[1]);
            Assert.Equal(7u, Assert.Single((await h.Aware.ReadPageAsync(artifact, 0, 1, null, default).WaitAsync(Deadline)).Rows).Id);
        }
        if (!OperatingSystem.IsWindows())
        {
            await using var fresh = new TableRowArtifacts(h.Objects, h.Resources, null);
            Assert.Equal("LOCAL_OBJECT_MISSING", (await Assert.ThrowsAsync<DiscoveryException>(() => fresh.ReadUniqueAsync(artifact, 7, default).WaitAsync(Deadline))).Code);
        }
    }

    [Fact]
    public async Task WriterReleasesCpuBeforeProgressAndPublicationAndMatchesOriginalSortBytes()
    {
        await using var h = new Harness(); var values = Enumerable.Range(0, 4097).Reverse().Select(i => new Db2Row((uint)(i % 127), i % 5 == 0, i % 5 == 0 ? 0u : null,
            [(i % 127).ToString(CultureInfo.InvariantCulture), "Row " + i.ToString(CultureInfo.InvariantCulture)])).ToArray();
        var callbacks = 0;
        var actual = await h.Build(h.Aware, values, async _ =>
        {
            using var stop = new CancellationTokenSource(Deadline);
            using (await h.Resources.EnterCpuAsync(null, stop.Token)) callbacks++;
        }).WaitAsync(Deadline);
        Assert.Equal(2, callbacks); // Header and the original4096-row report, both outside CPU ownership.
        var expected = await h.Build(h.Legacy, values).WaitAsync(Deadline);
        Assert.Equal(expected.Sha256, actual.Sha256); Assert.Equal(expected.ByteLength, actual.ByteLength); Assert.Equal(expected.ContentKey, actual.ContentKey);
        Assert.Equal(await File.ReadAllBytesAsync(h.PathFor(expected)), await File.ReadAllBytesAsync(h.PathFor(actual)));
        var ambiguous = await h.Aware.ReadUniqueAsync(actual, 7, default).WaitAsync(Deadline); Assert.True(ambiguous.MatchCount > 1); Assert.Null(ambiguous.Row);
        using var cpu = await h.Resources.EnterCpuAsync(null, default).WaitAsync(Deadline);
    }

    [Fact]
    public async Task ExistingPureWdc5BuildRemainsFunctionalWithoutAnOuterCpuScope()
    {
        await using var h = new Harness();
        var definition = DbdDefinitionReader.Parse("COLUMNS\nint ID\n\nLAYOUT 9A2A4834\nBUILD 1.0.0.1\n$id$ID<u32>\n"u8);
        var source = new byte[208]; "WDC5"u8.CopyTo(source); Put(4, 5); Put(140, 1); Put(144, 4); Put(152, 0x50238EC2); Put(156, 0x9A2A4834); Put(176, 1);
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(offset), value);
        using var input = new MemoryStream(source, writable: false);
        var binding = Binding with { SchemaSelectionSha256 = TableRowArtifacts.SchemaSelectionKey(definition, "1.0.0.1") };
        var artifact = await h.Aware.BuildAsync(input, definition, "1.0.0.1", binding, _ => Task.CompletedTask, default).WaitAsync(Deadline);
        Assert.Equal(0, artifact.Completion.PhysicalRows); Assert.Equal(208, artifact.Completion.ConsumedBytes);
        Assert.Equal(0, (await h.Aware.ReadUniqueAsync(artifact, 0, default).WaitAsync(Deadline)).MatchCount);
    }

    [Theory]
    [InlineData(8193)]
    [InlineData(TableRowArtifacts.MaxRows)]
    public async Task CanceledCompletionWaitingForCpuPublishesNothingAndDisposalDeletesStaging(int count)
    {
        await using var h = new Harness(); await using var writer = await h.Aware.CreateWriterAsync(Binding, default);
        await writer.HeaderAsync(Header with { DeclaredRecords = count }, default);
        for (var i = count - 1; i >= 0; i--) await writer.RowAsync(new((uint)i, false, null, [i.ToString(CultureInfo.InvariantCulture), "Name"]), default);
        using var cpu = await h.Resources.EnterCpuAsync(null, default); using var stop = new CancellationTokenSource();
        var completing = writer.CompleteAsync(new(count, count, 0, 0, 0, 0, "complete", 100), stop.Token);
        Assert.False(completing.IsCompleted); stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completing.WaitAsync(Deadline));
        await writer.DisposeAsync(); Assert.Empty(Directory.EnumerateFiles(Path.Combine(h.DirectoryPath, "staging-v1")));
        Assert.DoesNotContain(Directory.EnumerateFiles(Path.Combine(h.DirectoryPath, "objects-v1"), "*", SearchOption.AllDirectories), p => Path.GetFileName(p) != ".owner");
    }

    [Fact]
    public async Task ColdHashAndJournalHoldNoOuterCpuAndCanceledFollowerCannotCloseTheOwner()
    {
        var journal = Signal(); var release = Signal();
        await using var h = new Harness(async resources =>
        {
            using (await resources.EnterCpuAsync(null, default)) journal.TrySetResult();
            await release.Task;
        });
        var artifact = await h.Build(h.Legacy, [Single]); var bytes = await File.ReadAllBytesAsync(h.PathFor(artifact)); bytes[40] = 1; await File.WriteAllBytesAsync(h.PathFor(artifact), bytes);
        var owner = h.Aware.ReadPageAsync(artifact, 0, 1, null, default);
        await journal.Task.WaitAsync(Deadline);
        try
        {
            using (await h.Resources.EnterCpuAsync(null, default).WaitAsync(Deadline)) Assert.False(owner.IsCompleted);
            using var stop = new CancellationTokenSource(); var follower = h.Aware.ReadUniqueAsync(artifact, 7, stop.Token);
            stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => follower.WaitAsync(Deadline)); Assert.False(owner.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() => owner.WaitAsync(Deadline))).Code);
        using var cpu = await h.Resources.EnterCpuAsync(null, default).WaitAsync(Deadline);
    }

    [Theory]
    [InlineData("row")] [InlineData("id")] [InlineData("text")]
    public async Task ResourceAwareLookupRetainsFramingIdentityAndUnavailableTextValidators(string defect)
    {
        await using var h = new Harness();
        var value = defect == "text" ? new Db2Row(7, false, null, ["7", ""], [new(1, TableTextEvidence.InvalidUtf8, "ff")]) : Single;
        var artifact = await h.Build(h.Legacy, [value]); var bytes = await File.ReadAllBytesAsync(h.PathFor(artifact));
        var index = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24)); var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)index + 12));
        var json = System.Text.Encoding.UTF8.GetString(bytes, 64, length);
        var changed = defect switch { "row" => "null" + new string(' ', length - 4), "id" => json.Replace("\"id\":7", "\"id\":8", StringComparison.Ordinal), _ => json.Replace("\"ff\"", "\"00\"", StringComparison.Ordinal) };
        System.Text.Encoding.UTF8.GetBytes(changed).CopyTo(bytes, 64);
        string path; await using (var file = await h.Objects.CreateStagingFileAsync(default)) { path = file.Name; await file.WriteAsync(bytes); }
        var sha = await h.Objects.PublishFileAsync(path, bytes.Length, default); var invalid = artifact with { Sha256 = sha };
        var expected = defect == "text" ? "TABLE_TEXT_EVIDENCE_INVALID" : "TABLE_ARTIFACT_INVALID";
        Assert.Equal(expected, (await Assert.ThrowsAsync<DiscoveryException>(() => h.Aware.ReadUniqueAsync(invalid, 7, default).WaitAsync(Deadline))).Code);
        Assert.Equal(expected, (await Assert.ThrowsAsync<DiscoveryException>(() => h.Aware.ReadPageAsync(invalid, 0, 1, null, default).WaitAsync(Deadline))).Code);
        using var cpu = await h.Resources.EnterCpuAsync(null, default).WaitAsync(Deadline);
    }

    private sealed class MutableWork : IAcquisitionWorkContext
    {
        private readonly object gate = new(); private AcquisitionWorkSnapshot snapshot = new(0, AcquisitionPriority.Background, []);
        private TaskCompletionSource changed = Signal(); internal readonly TaskCompletionSource Waiting = Signal();
        public AcquisitionWorkSnapshot Snapshot { get { lock (gate) return snapshot; } }
        public Task WaitForChangeAsync(long revision, CancellationToken token = default)
        {
            Task pending;
            lock (gate) { Waiting.TrySetResult(); pending = snapshot.Revision != revision ? Task.CompletedTask : changed.Task; }
            return pending.WaitAsync(token);
        }
        internal void Promote()
        {
            TaskCompletionSource wake;
            lock (gate) { snapshot = new(snapshot.Revision + 1, AcquisitionPriority.Interactive, []); wake = changed; changed = Signal(); }
            wake.TrySetResult();
        }
        public Task ReportAsync(AcquisitionProgress progress) => Task.CompletedTask;
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "vm-resource-rows-" + Guid.NewGuid().ToString("N"));
        internal readonly AcquisitionResources Resources = new(maximumCpu: 1, maximumBackgroundCpu: 1);
        internal readonly VerifiedObjectStore Objects; internal readonly TableRowArtifacts Legacy, Aware;
        internal Harness(Func<AcquisitionResources, Task>? journal = null)
        {
            Objects = new(DirectoryPath, journal is null ? null : (_, _, _, _) => journal(Resources)); Legacy = new(Objects); Aware = new(Objects, Resources, null);
        }
        internal string PathFor(TableRowsArtifact artifact) => Path.Combine(DirectoryPath, "objects-v1", artifact.Sha256[..2], artifact.Sha256[2..]);
        internal async Task<TableRowsArtifact> Build(TableRowArtifacts rows, Db2Row[] values, Func<string, Task>? progress = null)
        {
            var copies = values.Count(v => v.IsCopy); await using var writer = await rows.CreateWriterAsync(Binding, default, progress);
            await writer.HeaderAsync(Header with { DeclaredRecords = values.Length - copies, DeclaredCopyRecords = copies }, default);
            foreach (var row in values) await writer.RowAsync(row, default);
            using var stop = new CancellationTokenSource(Deadline);
            return await writer.CompleteAsync(new(values.Length - copies, values.Length - copies, copies, 0, 0, 0, "complete", 100, values.Sum(v => v.TextIssues?.Count ?? 0)), stop.Token);
        }
        public async ValueTask DisposeAsync() { await Aware.DisposeAsync(); await Legacy.DisposeAsync(); await Objects.DisposeAsync(); Directory.Delete(DirectoryPath, true); }
    }
}
