using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class LedgerIntegrityOrderingTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("validate", false)]
    [InlineData("validate", true)]
    [InlineData("page", false)]
    [InlineData("page", true)]
    [InlineData("assess", false)]
    [InlineData("assess", true)]
    [InlineData("session", false)]
    [InlineData("session", true)]
    [InlineData("ordered", false)]
    [InlineData("ordered", true)]
    public async Task Every_ledger_open_arrives_at_same_or_colliding_stripe_with_CPU_free(string operation, bool collision)
    {
        await using var fixture = new IntegrityStripeFixture();
        var bytes = LedgerBytes();
        var sha = await fixture.HoldAsync(bytes, collision);
        var artifact = new DownloadLedgerArtifact(sha, bytes.Length, 1, 7);
        var ledger = new DownloadLedger(fixture.Objects, fixture.Resources, null);
        var opening = Open(ledger, artifact, operation, fixture.Token);
        await fixture.AssertCpuAvailableAtArrivalAsync(opening);
        if (collision) await opening;
        else Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() => opening)).Code);
    }

    [Theory]
    [InlineData("validate")]
    [InlineData("page")]
    [InlineData("assess")]
    [InlineData("session")]
    [InlineData("ordered")]
    public async Task Every_ledger_open_uses_live_constructor_context_without_an_outer_CPU_permit(string operation)
    {
        using var directory = new TempDirectory();
        await using var objects = new VerifiedObjectStore(directory.Path);
        var artifact = await PublishLedger(objects);
        var resources = new AcquisitionResources(maximumCpu: 2, maximumBackgroundCpu: 1);
        var context = new LedgerWork(AcquisitionPriority.Background);
        var ledger = new DownloadLedger(objects, resources, context);
        using var background = await resources.EnterCpuAsync(new LedgerWork(AcquisitionPriority.Background), default);
        var opening = Open(ledger, artifact, operation);
        await context.Waiting.Task.WaitAsync(Deadline);
        Assert.False(opening.IsCompleted);
        // A second background CPU unit is forbidden, while independent foreground work fits.
        using (await resources.EnterCpuAsync(null, default).WaitAsync(Deadline))
            Assert.False(opening.IsCompleted);
        context.Promote();
        await opening.WaitAsync(Deadline);
        // The background permit above is still held: service depended on the actual live context.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Membership_explicit_scheduling_pair_overrides_constructor_pair(bool ordered)
    {
        using var directory = new TempDirectory();
        await using var objects = new VerifiedObjectStore(directory.Path);
        var artifact = await PublishLedger(objects);
        var defaults = new AcquisitionResources(maximumCpu: 1, maximumBackgroundCpu: 1);
        var defaultContext = new LedgerWork(AcquisitionPriority.Interactive);
        using var blockedDefault = await defaults.EnterCpuAsync(null, default);
        var actual = new AcquisitionResources(maximumCpu: 2, maximumBackgroundCpu: 1);
        var actualContext = new LedgerWork(AcquisitionPriority.Background);
        using var background = await actual.EnterCpuAsync(new LedgerWork(AcquisitionPriority.Background), default);
        var ledger = new DownloadLedger(objects, defaults, defaultContext);
        var opening = OpenMembership();
        await actualContext.Waiting.Task.WaitAsync(Deadline);
        Assert.False(defaultContext.Waiting.Task.IsCompleted);
        using (await actual.EnterCpuAsync(null, default).WaitAsync(Deadline))
            Assert.False(opening.IsCompleted);
        actualContext.Promote();
        await opening.WaitAsync(Deadline);
        Assert.False(defaultContext.Waiting.Task.IsCompleted);

        async Task OpenMembership()
        {
            if (ordered)
            {
                await using var cursor = await ledger.OpenOrderedAsync(artifact, default, actual, actualContext);
                Assert.True(await cursor.NextAsync(default));
                Assert.Equal(7, cursor.Current.Bytes);
            }
            else
            {
                await using var session = await ledger.OpenReadSessionAsync(artifact, default, actual, actualContext);
                Assert.Equal(7, (await session.FindAsync(Key, 7, default))!.EncodedBytes);
            }
        }
    }

    [Fact]
    public async Task Resource_aware_build_publishes_then_reopens_with_one_CPU_slot()
    {
        using var directory = new TempDirectory();
        await using var objects = new VerifiedObjectStore(directory.Path);
        var fixture = InventoryFixture.Create().Metadata["download"];
        var receipt = new MetadataReceipt("download", fixture.ContentKey, fixture.EncodingKey,
            Convert.ToHexStringLower(SHA256.HashData(fixture.Encoded)), fixture.Encoded.Length.ToString(),
            fixture.Decoded.Length.ToString(), InventoryFixture.Url("data", fixture.EncodingKey),
            "2026-10-19T12:00:00.0000000+00:00", "2026-10-19T12:00:00.0000000+00:00", "download-v1");
        var ledger = new DownloadLedger(objects, new AcquisitionResources(maximumCpu: 1, maximumBackgroundCpu: 1), null);
        var artifact = await ledger.BuildAsync(new MemoryStream(fixture.Decoded), receipt, _ => Task.CompletedTask, default).WaitAsync(Deadline);
        await ledger.ValidateAsync(artifact, default).WaitAsync(Deadline);
        Assert.Equal(2, (await ledger.ReadPageAsync(artifact, 0, 10, default).WaitAsync(Deadline)).Count);
    }

    private static async Task Open(DownloadLedger ledger, DownloadLedgerArtifact artifact, string operation, CancellationToken token = default)
    {
        switch (operation)
        {
            case "validate": await ledger.ValidateAsync(artifact, token); break;
            case "page": Assert.Single(await ledger.ReadPageAsync(artifact, 0, 1, token)); break;
            case "assess": Assert.Equal([Key], await ledger.AssessAsync(artifact, new Dictionary<string, long> { [Key] = 7 }, token)); break;
            case "session":
                await using (var session = await ledger.OpenReadSessionAsync(artifact, token))
                    Assert.Equal(7, (await session.FindAsync(Key, 7, token))!.EncodedBytes);
                break;
            case "ordered":
                await using (var cursor = await ledger.OpenOrderedAsync(artifact, token))
                    Assert.True(await cursor.NextAsync(token));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    internal const string Key = "00000000000000000000000000000001";
    internal static async Task<DownloadLedgerArtifact> PublishLedger(VerifiedObjectStore objects)
    {
        var bytes = LedgerBytes();
        string path;
        await using (var output = await objects.CreateStagingFileAsync(default))
        { path = output.Name; await output.WriteAsync(bytes); }
        return new(await objects.PublishFileAsync(path, bytes.Length, default), bytes.Length, 1, 7);
    }
    private static byte[] LedgerBytes()
    {
        var bytes = new byte[DownloadLedger.HeaderBytes + DownloadLedger.RecordBytes];
        "VMDLEDG1"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), DownloadLedger.RecordBytes);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(16), 1);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(24), 7);
        Convert.FromHexString(Key).CopyTo(bytes, DownloadLedger.HeaderBytes);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(DownloadLedger.HeaderBytes + 16), 7);
        return bytes;
    }

    private sealed class LedgerWork(AcquisitionPriority priority) : IAcquisitionWorkContext
    {
        private readonly object gate = new();
        private AcquisitionWorkSnapshot snapshot = new(0, priority, []);
        private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AcquisitionWorkSnapshot Snapshot { get { lock (gate) return snapshot; } }
        public Task ReportAsync(AcquisitionProgress progress) => Task.CompletedTask;
        public Task WaitForChangeAsync(long observedRevision, CancellationToken token = default)
        {
            lock (gate)
            {
                Waiting.TrySetResult();
                return snapshot.Revision != observedRevision ? Task.CompletedTask : changed.Task.WaitAsync(token);
            }
        }
        internal void Promote()
        {
            TaskCompletionSource previous;
            lock (gate)
            {
                snapshot = snapshot with { Revision = snapshot.Revision + 1, Priority = AcquisitionPriority.Interactive };
                previous = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            previous.TrySetResult();
        }
    }
}
