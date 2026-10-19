using System.Collections.Concurrent;
using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class CooperativeObjectStoreTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static AcquisitionResources Resources() => new(maximumCpu: 1, maximumBackgroundCpu: 1);
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Bytes(int length) => Enumerable.Range(0, length).Select(i => (byte)(i * 31)).ToArray();
    private static string ObjectPath(string root, string sha) => Path.Combine(root, "objects-v1", sha[..2], sha[2..]);
    private static async Task<string> Stage(VerifiedObjectStore objects, byte[] bytes)
    {
        await using var stream = await objects.CreateStagingFileAsync(default);
        await stream.WriteAsync(bytes); return stream.Name;
    }

    [Fact]
    public async Task Synchronous_irregular_reads_yield_at_exactly_one_MiB_without_a_second_pass()
    {
        var bytes = Bytes(2 * 1024 * 1024 + 37);
        using var stream = new ProbeStream(bytes, shortFirstRead: true);
        var context = new QueueContext(); var previous = SynchronizationContext.Current;
        Task<string> hashing;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            hashing = VerifiedObjectStore.HashFileAsync(stream, bytes.Length, "CONTENT_INTEGRITY", Resources(), null, default);
            Assert.False(hashing.IsCompleted);
            Assert.Equal(1024 * 1024, stream.BytesRead);
            Assert.Equal(1, stream.Requests[^1]); // 1MiB-1 followed by a one-byte read, not 64KiB.
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        var lastTurn = stream.BytesRead;
        while (context.RunNext())
        {
            Assert.InRange(stream.BytesRead - lastTurn, 0, 1024 * 1024);
            lastTurn = stream.BytesRead;
        }
        Assert.Equal(Sha(bytes), await hashing.WaitAsync(Deadline));
        Assert.Equal(bytes.Length, stream.BytesRead);
        Assert.All(stream.Requests, request => Assert.InRange(request, 1, 65536));
        Assert.Equal(1, stream.Requests[^1]); Assert.Equal(1, stream.EofReads);
        Assert.False(stream.WasDisposed); Assert.Equal(bytes.Length, stream.Position);
    }

    [Fact]
    public async Task Every_read_including_EOF_runs_without_CPU_ownership()
    {
        var resources = Resources(); var bytes = Bytes(131073); var admittedReads = 0;
        using var stream = new ProbeStream(bytes, beforeRead: async (_, _, token) =>
        {
            using var cpu = await resources.EnterCpuAsync(null, token);
            admittedReads++;
        });
        Assert.Equal(Sha(bytes), await VerifiedObjectStore.HashFileAsync(stream, bytes.Length,
            "CONTENT_INTEGRITY", resources, null, default).WaitAsync(Deadline));
        Assert.Equal(stream.Requests.Count, admittedReads);
    }

    [Fact]
    public async Task Read_completes_before_CPU_wait_and_cancellation_cannot_return_a_digest()
    {
        var resources = Resources(); using var held = await resources.EnterCpuAsync(null, default);
        using var cancellation = new CancellationTokenSource();
        using var stream = new ProbeStream(Bytes(65537));
        var hashing = VerifiedObjectStore.HashFileAsync(stream, stream.Length, "CONTENT_INTEGRITY", resources, null, cancellation.Token);
        Assert.Equal(65536, stream.BytesRead); Assert.Single(stream.Requests); Assert.False(hashing.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hashing.WaitAsync(Deadline));
        Assert.Single(stream.Requests); Assert.False(stream.WasDisposed);
        held.Dispose(); using var recovered = await resources.EnterCpuAsync(null, default).WaitAsync(Deadline);
    }

    [Fact]
    public async Task A_queued_foreground_CPU_consumer_runs_between_hash_appends()
    {
        var resources = Resources(); using var held = await resources.EnterCpuAsync(null, default);
        using var stream = new ProbeStream(Bytes(131073));
        var hashing = VerifiedObjectStore.HashFileAsync(stream, stream.Length, "CONTENT_INTEGRITY", resources, null, default);
        Assert.Equal(65536, stream.BytesRead); // First read finished; first append is queued.
        var foreground = resources.EnterCpuAsync(null, default);
        held.Dispose();
        using (await foreground.WaitAsync(Deadline))
        {
            Assert.False(hashing.IsCompleted);
            Assert.InRange(stream.BytesRead, 65536, 131072); // At most next I/O; its append cannot pass us.
        }
        Assert.Equal(Sha(Bytes(131073)), await hashing.WaitAsync(Deadline));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_before_read_or_at_forced_turn_never_returns_proof(bool atTurn)
    {
        using var cancellation = new CancellationTokenSource();
        using var stream = new ProbeStream(Bytes(1024 * 1024 + 1));
        var context = new QueueContext(); var previous = SynchronizationContext.Current;
        Task<string> hashing;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            if (!atTurn) cancellation.Cancel();
            hashing = VerifiedObjectStore.HashFileAsync(stream, stream.Length, "CONTENT_INTEGRITY", Resources(), null, cancellation.Token);
            Assert.Equal(atTurn ? 1024 * 1024 : 0, stream.BytesRead);
            cancellation.Cancel();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        while (context.RunNext()) { }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hashing.WaitAsync(Deadline));
        Assert.Equal(atTurn ? 1024 * 1024 : 0, stream.BytesRead);
    }

    [Fact]
    public async Task Final_digest_also_requires_CPU_and_observes_cancellation()
    {
        var resources = Resources(); IDisposable? held = null;
        using var cancellation = new CancellationTokenSource();
        using var stream = new ProbeStream([1, 2, 3], beforeRead: async (_, position, token) =>
        { if (position == 3) held = await resources.EnterCpuAsync(null, token); });
        var hashing = VerifiedObjectStore.HashFileAsync(stream, 3, "CONTENT_INTEGRITY", resources, null, cancellation.Token);
        try
        {
            Assert.Equal(1, stream.EofReads); Assert.NotNull(held); Assert.False(hashing.IsCompleted);
            cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hashing.WaitAsync(Deadline));
        }
        finally { held?.Dispose(); }
        using var recovered = await resources.EnterCpuAsync(null, default).WaitAsync(Deadline);
    }

    [Theory]
    [InlineData(7, 8)] // Truncated despite lying Length.
    [InlineData(9, 8)] // Extra byte despite lying Length.
    [InlineData(8, 9)] // Initial exact-length mismatch.
    public async Task Wrong_length_or_EOF_never_returns_proof(int actual, int declared)
    {
        using var stream = new ProbeStream(Bytes(actual), reportedLength: 8);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => VerifiedObjectStore.HashFileAsync(
            stream, declared, "LOCAL_OBJECT_CORRUPT", Resources(), null, default));
        Assert.Equal("LOCAL_OBJECT_CORRUPT", error.Code); Assert.False(stream.WasDisposed);
    }

    [Fact]
    public async Task Cancellation_during_read_waits_the_actual_read_and_leaves_stream_caller_owned()
    {
        var entered = Signal(); var release = Signal(); var resources = Resources();
        using var cancellation = new CancellationTokenSource();
        using var stream = new ProbeStream([1, 2, 3], beforeRead: async (_, _, _) =>
        { entered.TrySetResult(); await release.Task; });
        var hashing = VerifiedObjectStore.HashFileAsync(stream, 3, "CONTENT_INTEGRITY", resources, null, cancellation.Token);
        await entered.Task.WaitAsync(Deadline);
        try
        {
            cancellation.Cancel(); Assert.False(hashing.IsCompleted);
            using var foreground = await resources.EnterCpuAsync(null, default).WaitAsync(Deadline);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hashing.WaitAsync(Deadline));
        Assert.False(stream.WasDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_EOF_probe_is_not_reclassified_as_corruption(bool extraByte)
    {
        var entered = Signal(); var release = Signal();
        using var cancellation = new CancellationTokenSource();
        using var stream = new ProbeStream(extraByte ? [1, 2, 3, 4] : [1, 2, 3], reportedLength: 3,
            beforeRead: async (_, position, _) =>
            { if (position == 3) { entered.TrySetResult(); await release.Task; } });
        var hashing = VerifiedObjectStore.HashFileAsync(stream, 3, "LOCAL_OBJECT_CORRUPT", Resources(), null, cancellation.Token);
        await entered.Task.WaitAsync(Deadline);
        try { cancellation.Cancel(); Assert.False(hashing.IsCompleted); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hashing.WaitAsync(Deadline));
    }

    [Fact]
    public async Task Budgeted_publication_reuses_exact_bytes_and_returns_a_rewound_verified_lease()
    {
        using var directory = new TempDirectory(); await using var objects = new VerifiedObjectStore(directory.Path);
        var resources = Resources(); var bytes = Bytes(1024 * 1024 + 17); var sha = Sha(bytes);
        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Stage(objects, bytes)));
        var published = await Task.WhenAll(paths.Select(path => objects.PublishFileAsync(path, bytes.Length, resources, null, default))).WaitAsync(Deadline);
        Assert.All(published, actual => Assert.Equal(sha, actual));
        await using var verified = await objects.OpenVerifiedAsync(sha, bytes.Length, resources, null, default);
        Assert.Equal(0, verified.Position); var actualBytes = new byte[bytes.Length]; await verified.ReadExactlyAsync(actualBytes);
        Assert.Equal(bytes, actualBytes); Assert.All(paths, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public async Task Compatibility_calls_do_not_borrow_an_inner_CPU_permit_from_an_outer_owner()
    {
        using var directory = new TempDirectory(); await using var objects = new VerifiedObjectStore(directory.Path);
        var resources = Resources(); byte[] bytes = [1, 2, 3];
        using (await resources.EnterCpuAsync(null, default))
        {
            var sha = await objects.PublishFileAsync(await Stage(objects, bytes), bytes.Length, default).WaitAsync(Deadline);
            await using var legacy = await objects.OpenVerifiedAsync(sha, bytes.Length, default).WaitAsync(Deadline);
            Assert.Equal(0, legacy.Position);
        }
        // Migrated callers release the outer permit before using the explicit resource overload.
        await using var cooperative = await objects.OpenVerifiedAsync(Sha(bytes), bytes.Length, resources, null, default).WaitAsync(Deadline);
        Assert.Equal(0, cooperative.Position);
    }

    [Fact]
    public async Task Full_publication_capacity_can_verify_existing_destination_without_a_second_admission()
    {
        using var directory = new TempDirectory(); await using var objects = new VerifiedObjectStore(directory.Path);
        var resources = Resources(); byte[] bytes = [1, 2, 3];
        var sha = await objects.PublishFileAsync(await Stage(objects, bytes), bytes.Length, default);
        var paths = await Task.WhenAll(Enumerable.Range(0, 128).Select(_ => Stage(objects, bytes)));
        using var held = await resources.EnterCpuAsync(null, default);
        var publishing = paths.Select(path => objects.PublishFileAsync(path, bytes.Length, resources, null, default)).ToArray();
        try
        {
            Assert.All(publishing, operation => Assert.False(operation.IsCompleted));
            Assert.Equal("CONTENT_STORE_CAPACITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                objects.OpenVerifiedAsync(sha, bytes.Length, resources, null, default))).Code);
        }
        finally { held.Dispose(); }
        var results = await Task.WhenAll(publishing).WaitAsync(Deadline);
        Assert.All(results, result => Assert.Equal(sha, result));
        Assert.All(paths, path => Assert.False(File.Exists(path)));
        await using var recovered = await objects.OpenVerifiedAsync(sha, bytes.Length, resources, null, default);
    }

    [Fact]
    public async Task Canceled_publication_cleans_staging_and_CPU_wait_even_if_a_notification_throws()
    {
        using var directory = new TempDirectory(); await using var objects = new VerifiedObjectStore(directory.Path);
        var resources = Resources(); byte[] bytes = [1, 2, 3]; var path = await Stage(objects, bytes);
        using var held = await resources.EnterCpuAsync(null, default);
        using var cancellation = new CancellationTokenSource(); var callbackInvoked = false;
        using var registration = cancellation.Token.Register(() => { callbackInvoked = true; throw new InvalidOperationException("Authored notification failure."); });
        var context = new WaitingContext();
        var publishing = objects.PublishFileAsync(path, bytes.Length, resources, context, cancellation.Token);
        await context.Waiting.Task.WaitAsync(Deadline); // The real CPU admission has registered its wait.
        Assert.False(publishing.IsCompleted);
        Assert.Throws<AggregateException>(() => cancellation.Cancel()); Assert.True(callbackInvoked);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publishing.WaitAsync(Deadline));
        Assert.False(File.Exists(path)); Assert.False(File.Exists(ObjectPath(directory.Path, Sha(bytes))));
        held.Dispose(); using var recovered = await resources.EnterCpuAsync(null, default).WaitAsync(Deadline);
    }

    [Fact]
    public async Task Budgeted_invalidity_journal_failure_is_typed_and_closes_the_failed_read()
    {
        using var directory = new TempDirectory();
        await using var objects = new VerifiedObjectStore(directory.Path, (_, _, _, _) => Task.FromException(new IOException("Authored journal failure.")));
        byte[] bytes = [1, 2, 3]; var resources = Resources();
        var sha = await objects.PublishFileAsync(await Stage(objects, bytes), bytes.Length, resources, null, default);
        var path = ObjectPath(directory.Path, sha); await File.WriteAllBytesAsync(path, [3, 2, 1]);
        Assert.Equal("CONTENT_INTEGRITY_JOURNAL_UNAVAILABLE", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            objects.OpenVerifiedAsync(sha, bytes.Length, resources, null, default))).Code);
        await using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Distinct_colliding_objects_keep_stripe_order_without_holding_CPU_and_cancel_waiters()
    {
        using var directory = new TempDirectory(); var entered = Signal(); var release = Signal();
        await using var objects = new VerifiedObjectStore(directory.Path, async (_, _, _, _) => { entered.TrySetResult(); await release.Task; });
        var resources = Resources(); byte[] first = [1, 2, 3]; var firstSha = Sha(first);
        var second = Enumerable.Range(0, 65536).Select(i => new byte[] { (byte)i, (byte)(i >> 8), 7 })
            .First(bytes => Sha(bytes) != firstSha && Convert.ToByte(Sha(bytes)[..2], 16) % 64 == Convert.ToByte(firstSha[..2], 16) % 64);
        var secondSha = Sha(second);
        await objects.PublishFileAsync(await Stage(objects, first), first.Length, default);
        await objects.PublishFileAsync(await Stage(objects, second), second.Length, default);
        await File.WriteAllBytesAsync(ObjectPath(directory.Path, firstSha), [9]);
        var reading = objects.OpenVerifiedAsync(firstSha, first.Length, resources, null, default);
        await entered.Task.WaitAsync(Deadline);
        using var cancellation = new CancellationTokenSource(); var context = new WaitingContext();
        var collision = objects.OpenVerifiedAsync(secondSha, second.Length, resources, context, cancellation.Token);
        try
        {
            Assert.False(collision.IsCompleted); Assert.False(context.Waiting.Task.IsCompleted);
            using var foreground = await resources.EnterCpuAsync(null, default).WaitAsync(Deadline);
            cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collision.WaitAsync(Deadline));
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<DiscoveryException>(() => reading.WaitAsync(Deadline));
        await using var recovered = await objects.OpenVerifiedAsync(secondSha, second.Length, resources, null, default);
        Assert.Equal(0, recovered.Position);
    }

    [Fact]
    public async Task Budgeted_generic_refuses_corruption_but_encoded_repair_waits_the_old_journal()
    {
        using var directory = new TempDirectory(); var entered = Signal(); var release = Signal(); var calls = 0;
        await using var objects = new VerifiedObjectStore(directory.Path, async (_, _, _, token) =>
        { Assert.False(token.CanBeCanceled); if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task; } });
        var resources = Resources(); byte[] bytes = [.. "BLTE"u8, 0, 0, 0, 0, (byte)'N', 1, 2, 3];
        var sha = await objects.PublishFileAsync(await Stage(objects, bytes), bytes.Length, resources, null, default);
        byte[] damaged = [9, 8]; await File.WriteAllBytesAsync(ObjectPath(directory.Path, sha), damaged);
        using var cancellation = new CancellationTokenSource();
        var oldRead = objects.OpenVerifiedAsync(sha, bytes.Length, resources, null, cancellation.Token);
        await entered.Task.WaitAsync(Deadline);
        var path = await Stage(objects, bytes);
        var repair = objects.PublishEncodedFileAsync(path, bytes.Length, Convert.ToHexStringLower(MD5.HashData(bytes)), resources, null, default);
        try
        {
            cancellation.Cancel(); Assert.False(oldRead.IsCompleted); Assert.False(repair.IsCompleted);
            Assert.Equal(damaged, await File.ReadAllBytesAsync(ObjectPath(directory.Path, sha)));
            using var foreground = await resources.EnterCpuAsync(null, default).WaitAsync(Deadline);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() => oldRead.WaitAsync(Deadline))).Code);
        Assert.Equal(sha, await repair.WaitAsync(Deadline)); Assert.False(File.Exists(path));
        await File.WriteAllBytesAsync(ObjectPath(directory.Path, sha), damaged);
        var generic = await Stage(objects, bytes);
        Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            objects.PublishFileAsync(generic, bytes.Length, resources, null, default))).Code);
        Assert.Equal(damaged, await File.ReadAllBytesAsync(ObjectPath(directory.Path, sha))); Assert.False(File.Exists(generic));
    }

    [Fact]
    public async Task Existing_encoded_proof_waits_for_CPU_before_rejecting_invalid_BLTE()
    {
        using var directory = new TempDirectory(); await using var objects = new VerifiedObjectStore(directory.Path);
        var resources = Resources(); var path = await Stage(objects, new byte[9]);
        var context = new WaitingContext(); using var held = await resources.EnterCpuAsync(null, default);
        var publishing = objects.PublishEncodedFileAsync(path, 9, new string('1', 32), resources, context, default);
        bool admitted;
        try
        {
            await Task.WhenAny(context.Waiting.Task, publishing).WaitAsync(Deadline);
            admitted = context.Waiting.Task.IsCompleted;
        }
        finally { held.Dispose(); }
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => publishing.WaitAsync(Deadline));
        Assert.Equal("BLTE_UNSUPPORTED", error.Code); Assert.False(File.Exists(path));
        Assert.True(admitted, "The existing encoded proof must request the real CPU permit before examining BLTE bytes.");
    }

    [Theory]
    [InlineData("open")]
    [InlineData("generic")]
    [InlineData("encoded")]
    public async Task Internal_arrival_observer_precedes_each_actual_held_stripe_wait_without_CPU_ownership(string operation)
    {
        using var directory = new TempDirectory(); var journalEntered = Signal(); var releaseJournal = Signal();
        TaskCompletionSource<string>? observation = null;
        await using var objects = new VerifiedObjectStore(directory.Path,
            async (_, _, _, _) => { journalEntered.TrySetResult(); await releaseJournal.Task; },
            sha => Volatile.Read(ref observation)?.TrySetResult(sha));
        var resources = Resources(); byte[] bytes = [.. "BLTE"u8, 0, 0, 0, 0, (byte)'N', 1, 2, 3];
        var sha = await objects.PublishFileAsync(await Stage(objects, bytes), bytes.Length, default);
        await File.WriteAllBytesAsync(ObjectPath(directory.Path, sha), [9]);
        var oldRead = objects.OpenVerifiedAsync(sha, bytes.Length, resources, null, default);
        await journalEntered.Task.WaitAsync(Deadline);
        var arrival = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref observation, arrival);
        using var cancellation = new CancellationTokenSource();
        string? path = operation == "open" ? null : await Stage(objects, bytes);
        IDisposable? heldCpu = operation == "open" ? await resources.EnterCpuAsync(null, default) : null;
        Task waiting = operation switch
        {
            "open" => objects.OpenVerifiedAsync(sha, bytes.Length, resources, null, cancellation.Token),
            "generic" => objects.PublishFileAsync(path!, bytes.Length, resources, null, cancellation.Token),
            _ => objects.PublishEncodedFileAsync(path!, bytes.Length, Convert.ToHexStringLower(MD5.HashData(bytes)), resources, null, cancellation.Token)
        };
        try
        {
            // Open has no intervening I/O: arrival must already be visible even while CPU is held.
            if (operation == "open") Assert.True(arrival.Task.IsCompleted);
            Assert.Equal(sha, await arrival.Task.WaitAsync(Deadline));
            Assert.False(waiting.IsCompleted);
            heldCpu?.Dispose(); heldCpu = null;
            using var witness = await resources.EnterCpuAsync(null, default).WaitAsync(Deadline);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Deadline));
            Assert.False(oldRead.IsCompleted); // The observed operation never acquired this stripe.
            if (path is not null) Assert.False(File.Exists(path));
        }
        finally
        {
            heldCpu?.Dispose(); cancellation.Cancel(); releaseJournal.TrySetResult();
            try { await Assert.ThrowsAsync<DiscoveryException>(() => oldRead.WaitAsync(Deadline)); }
            finally
            {
                // Even a failed producer assertion must retire the observed waiter before disposal.
                try { await waiting.WaitAsync(Deadline); } catch (OperationCanceledException) { }
            }
        }
    }

    private sealed class QueueContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> queue = new();
        public override void Post(SendOrPostCallback callback, object? state) => queue.Enqueue((callback, state));
        internal bool RunNext()
        {
            if (!queue.TryDequeue(out var work)) return false;
            var previous = Current;
            try { SetSynchronizationContext(this); work.Callback(work.State); }
            finally { SetSynchronizationContext(previous); }
            return true;
        }
    }

    private sealed class WaitingContext : IAcquisitionWorkContext
    {
        public TaskCompletionSource Waiting { get; } = Signal();
        public AcquisitionWorkSnapshot Snapshot => new(0, AcquisitionPriority.Interactive, []);
        public Task WaitForChangeAsync(long observedRevision, CancellationToken token = default)
        {
            Waiting.TrySetResult();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        public Task ReportAsync(AcquisitionProgress progress) => Task.CompletedTask;
    }

    private sealed class ProbeStream(byte[] bytes, bool shortFirstRead = false, long? reportedLength = null,
        Func<int, long, CancellationToken, ValueTask>? beforeRead = null) : Stream
    {
        private long position;
        public List<int> Requests { get; } = [];
        public long BytesRead => position;
        public int EofReads { get; private set; }
        public bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => reportedLength ?? bytes.LongLength;
        public override long Position { get => position; set => throw new NotSupportedException("A hash must not rewind its source."); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Requests.Add(buffer.Length);
            if (beforeRead is not null) await beforeRead(buffer.Length, position, cancellationToken);
            var count = (int)Math.Min(buffer.Length, bytes.LongLength - position);
            if (shortFirstRead && position == 0) count = Math.Min(count, 65535);
            bytes.AsMemory((int)position, count).CopyTo(buffer); position += count;
            if (count == 0) EofReads++;
            return count;
        }
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
