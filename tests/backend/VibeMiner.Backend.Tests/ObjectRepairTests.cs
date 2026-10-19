using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ObjectRepairTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static byte[] Encoded() => [.. "BLTE"u8, 0, 0, 0, 0, (byte)'N', 1, 2, 3, 4, 5, 6, 7, 8];
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string EKey(byte[] bytes) => Convert.ToHexStringLower(MD5.HashData(bytes));
    private static string ObjectPath(string root, string sha) => Path.Combine(root, "objects-v1", sha[..2], sha[2..]);
    private static async Task<string> Stage(VerifiedObjectStore objects, byte[] bytes)
    {
        await using var stream = await objects.CreateStagingFileAsync(default);
        await stream.WriteAsync(bytes); await stream.FlushAsync(); return stream.Name;
    }
    private static string[] Staging(string root) => Directory.EnumerateFiles(Path.Combine(root, "staging-v1")).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verified_encoded_input_atomically_repairs_hash_or_length_corruption_without_backups(bool wrongLength)
    {
        using var directory = new TempDirectory(); var observed = new ConcurrentQueue<(string Sha, long Length, string Reason)>();
        await using var objects = new VerifiedObjectStore(directory.Path, (sha, length, reason, _) =>
        { observed.Enqueue((sha, length, reason)); return Task.CompletedTask; });
        var encoded = Encoded(); var expectedSha = Sha(encoded);
        await objects.PublishFileAsync(await Stage(objects, encoded), encoded.Length, default);
        var damaged = wrongLength ? encoded[..^1] : encoded.Select(value => (byte)(value ^ 0xff)).ToArray();
        await File.WriteAllBytesAsync(ObjectPath(directory.Path, expectedSha), damaged);
        var published = await objects.PublishEncodedFileAsync(await Stage(objects, encoded), encoded.Length, EKey(encoded), default);
        Assert.Equal(expectedSha, published); Assert.Equal(encoded, await File.ReadAllBytesAsync(ObjectPath(directory.Path, expectedSha)));
        Assert.Equal((expectedSha, (long)encoded.Length, "LOCAL_OBJECT_CORRUPT"), Assert.Single(observed));
        await using var read = await objects.OpenVerifiedAsync(published, encoded.Length, default); Assert.Equal(0, read.Position);
        Assert.Single(observed); // Successful SHA proof does not silently clear journal history.
        Assert.Empty(Staging(directory.Path));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(directory.Path, "objects-v1"), "*", SearchOption.AllDirectories).Count()); // owner + object; no quarantine.
    }

    [Fact]
    public async Task Fresh_and_already_valid_encoded_publication_do_not_report_corruption()
    {
        using var directory = new TempDirectory(); var reports = 0;
        await using var objects = new VerifiedObjectStore(directory.Path, (_, _, _, _) => { Interlocked.Increment(ref reports); return Task.CompletedTask; });
        var bytes = Encoded(); var expected = Sha(bytes);
        Assert.Equal(expected, await objects.PublishEncodedFileAsync(await Stage(objects, bytes), bytes.Length, EKey(bytes), default));
        Assert.Equal(expected, await objects.PublishEncodedFileAsync(await Stage(objects, bytes), bytes.Length, EKey(bytes), default));
        Assert.Equal(0, reports); Assert.Empty(Staging(directory.Path));
    }

    [Fact]
    public async Task Generic_artifact_publication_still_refuses_an_existing_corrupt_destination()
    {
        using var directory = new TempDirectory(); await using var objects = new VerifiedObjectStore(directory.Path);
        var bytes = Encoded(); var sha = await objects.PublishFileAsync(await Stage(objects, bytes), bytes.Length, default);
        byte[] damaged = [9, 9]; await File.WriteAllBytesAsync(ObjectPath(directory.Path, sha), damaged);
        var staged = await Stage(objects, bytes);
        Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            objects.PublishFileAsync(staged, bytes.Length, default))).Code);
        Assert.Equal(damaged, await File.ReadAllBytesAsync(ObjectPath(directory.Path, sha))); Assert.False(File.Exists(staged));
    }

    [Fact]
    public async Task Corrupt_late_encoded_chunk_cannot_overwrite_existing_bytes_or_gain_publication()
    {
        using var directory = new TempDirectory(); var reports = 0;
        await using var objects = new VerifiedObjectStore(directory.Path, (_, _, _, _) => { reports++; return Task.CompletedTask; });
        var bytes = Headered(); var sha = Sha(bytes); await objects.PublishFileAsync(await Stage(objects, bytes), bytes.Length, default);
        byte[] originalBad = [7, 8, 9]; await File.WriteAllBytesAsync(ObjectPath(directory.Path, sha), originalBad);
        var invalid = bytes.ToArray(); invalid[^1] ^= 1; var path = await Stage(objects, invalid);
        var key = Convert.ToHexStringLower(MD5.HashData(bytes.AsSpan(0, 60)));
        Assert.Equal("BLTE_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            objects.PublishEncodedFileAsync(path, bytes.Length, key, default))).Code);
        Assert.Equal(originalBad, await File.ReadAllBytesAsync(ObjectPath(directory.Path, sha)));
        Assert.Equal(0, reports); Assert.Empty(Staging(directory.Path));
    }

    [Fact]
    public async Task Wrong_EKey_cannot_be_used_as_a_generic_overwrite_primitive()
    {
        using var directory = new TempDirectory(); await using var objects = new VerifiedObjectStore(directory.Path);
        var bytes = Encoded(); var sha = Sha(bytes); await objects.PublishFileAsync(await Stage(objects, bytes), bytes.Length, default);
        byte[] originalBad = [9, 8, 7]; await File.WriteAllBytesAsync(ObjectPath(directory.Path, sha), originalBad);
        var staged = await Stage(objects, bytes);
        Assert.Equal("BLTE_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            objects.PublishEncodedFileAsync(staged, bytes.Length, new string('f', 32), default))).Code);
        Assert.Equal(originalBad, await File.ReadAllBytesAsync(ObjectPath(directory.Path, sha))); Assert.False(File.Exists(staged));
    }

    [Fact]
    public async Task Missing_files_do_not_journal_but_actual_corruption_is_awaited_even_when_requester_cancels()
    {
        using var directory = new TempDirectory(); var entered = Signal(); var finish = Signal(); var committed = false;
        CancellationToken observedToken = new(true);
        await using var objects = new VerifiedObjectStore(directory.Path, async (_, _, _, token) =>
        { observedToken = token; entered.SetResult(); await finish.Task; committed = true; });
        var bytes = Encoded(); var sha = Sha(bytes);
        Assert.Equal("LOCAL_OBJECT_MISSING", (await Assert.ThrowsAsync<DiscoveryException>(() => objects.OpenVerifiedAsync(sha, bytes.Length, default))).Code);
        Assert.False(entered.Task.IsCompleted);
        await objects.PublishFileAsync(await Stage(objects, bytes), bytes.Length, default);
        await File.WriteAllBytesAsync(ObjectPath(directory.Path, sha), [1, 2]);
        using var cancellation = new CancellationTokenSource(); var reading = objects.OpenVerifiedAsync(sha, bytes.Length, cancellation.Token);
        await entered.Task.WaitAsync(Deadline); cancellation.Cancel();
        Assert.False(reading.IsCompleted); Assert.False(observedToken.CanBeCanceled);
        finish.SetResult();
        Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() => reading.WaitAsync(Deadline))).Code);
        Assert.True(committed);
    }

    [Fact]
    public async Task Invalidity_journal_failure_is_typed_and_never_returns_a_verified_lease()
    {
        using var directory = new TempDirectory();
        await using var objects = new VerifiedObjectStore(directory.Path, (_, _, _, _) => Task.FromException(new IOException("Authored journal failure.")));
        var bytes = Encoded(); var sha = await objects.PublishFileAsync(await Stage(objects, bytes), bytes.Length, default);
        await File.WriteAllBytesAsync(ObjectPath(directory.Path, sha), [1, 2]);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => objects.OpenVerifiedAsync(sha, bytes.Length, default));
        Assert.Equal("CONTENT_INTEGRITY_JOURNAL_UNAVAILABLE", error.Code); Assert.True(error.Retryable);
    }

    [Fact]
    public async Task Cancellation_before_waiting_repair_commit_preserves_old_bytes_and_cleans_stage()
    {
        using var directory = new TempDirectory(); var entered = Signal(); var release = Signal(); var calls = 0;
        await using var objects = new VerifiedObjectStore(directory.Path, async (_, _, _, _) =>
        { if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task; } });
        var bytes = Encoded(); var sha = await objects.PublishFileAsync(await Stage(objects, bytes), bytes.Length, default);
        byte[] damaged = [9, 8]; await File.WriteAllBytesAsync(ObjectPath(directory.Path, sha), damaged);
        var oldRead = objects.OpenVerifiedAsync(sha, bytes.Length, default); await entered.Task.WaitAsync(Deadline);
        var staged = await Stage(objects, bytes); using var cancellation = new CancellationTokenSource();
        var repairing = objects.PublishEncodedFileAsync(staged, bytes.Length, EKey(bytes), cancellation.Token); cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repairing.WaitAsync(Deadline));
            Assert.Equal(damaged, await File.ReadAllBytesAsync(ObjectPath(directory.Path, sha))); Assert.False(File.Exists(staged));
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<DiscoveryException>(() => oldRead.WaitAsync(Deadline));
    }

    [Fact]
    public async Task Corruption_observation_and_repair_share_a_serialized_publication_order()
    {
        using var directory = new TempDirectory(); var entered = Signal(); var release = Signal();
        var events = new ConcurrentQueue<string>(); var calls = 0;
        await using var objects = new VerifiedObjectStore(directory.Path, async (_, _, _, _) =>
        {
            var ordinal = Interlocked.Increment(ref calls); events.Enqueue($"begin-{ordinal}");
            if (ordinal == 1) { entered.SetResult(); await release.Task; }
            events.Enqueue($"commit-{ordinal}");
        });
        var bytes = Encoded(); var sha = await objects.PublishFileAsync(await Stage(objects, bytes), bytes.Length, default);
        await File.WriteAllBytesAsync(ObjectPath(directory.Path, sha), [9]);
        var stale = objects.OpenVerifiedAsync(sha, bytes.Length, default); await entered.Task.WaitAsync(Deadline);
        var repairing = objects.PublishEncodedFileAsync(await Stage(objects, bytes), bytes.Length, EKey(bytes), default);
        Assert.False(repairing.IsCompleted); release.SetResult();
        await Assert.ThrowsAsync<DiscoveryException>(() => stale.WaitAsync(Deadline)); Assert.Equal(sha, await repairing.WaitAsync(Deadline)); events.Enqueue("replaced");
        var order = events.ToArray(); Assert.True(Array.IndexOf(order, "commit-1") < Array.IndexOf(order, "replaced"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(ObjectPath(directory.Path, sha))); Assert.Empty(Staging(directory.Path));
    }

    [Fact]
    public async Task Concurrent_encoded_repairs_publish_one_canonical_verified_object_and_retire_all_stages()
    {
        using var directory = new TempDirectory(); await using var objects = new VerifiedObjectStore(directory.Path);
        var bytes = Encoded(); var sha = await objects.PublishFileAsync(await Stage(objects, bytes), bytes.Length, default);
        await File.WriteAllBytesAsync(ObjectPath(directory.Path, sha), [9]);
        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Stage(objects, bytes)));
        var results = await Task.WhenAll(paths.Select(path => objects.PublishEncodedFileAsync(path, bytes.Length, EKey(bytes), default))).WaitAsync(Deadline);
        Assert.All(results, result => Assert.Equal(sha, result)); Assert.Equal(bytes, await File.ReadAllBytesAsync(ObjectPath(directory.Path, sha)));
        Assert.Empty(Staging(directory.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repair_refuses_canonical_file_or_shard_links_without_touching_target(bool shardLink)
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory(); await using var objects = new VerifiedObjectStore(directory.Path);
        var bytes = Encoded(); var sha = Sha(bytes); var staged = await Stage(objects, bytes);
        var target = Path.Combine(directory.Path, "authored-outside-cas"); var targetFile = target;
        if (shardLink)
        {
            Directory.CreateDirectory(target); targetFile = Path.Combine(target, sha[2..]); await File.WriteAllBytesAsync(targetFile, [7]);
            Directory.CreateSymbolicLink(Path.GetDirectoryName(ObjectPath(directory.Path, sha))!, target);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ObjectPath(directory.Path, sha))!); await File.WriteAllBytesAsync(target, [7]);
            File.CreateSymbolicLink(ObjectPath(directory.Path, sha), target);
        }
        Assert.Equal("CONTENT_REPAIR_UNSAFE", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            objects.PublishEncodedFileAsync(staged, bytes.Length, EKey(bytes), default))).Code);
        Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(targetFile)); Assert.Empty(Staging(directory.Path));
    }

    [Fact]
    public async Task Integrity_admission_is_bounded_even_when_an_observer_is_blocked()
    {
        using var directory = new TempDirectory(); var entered = Signal(); var release = Signal();
        await using var objects = new VerifiedObjectStore(directory.Path, async (_, _, _, _) => { entered.TrySetResult(); await release.Task; });
        await objects.ObservePresenceAsync(new string('a', 64), 3, default);
        var tasks = new List<Task>();
        for (var i = 1; i <= 128; i++)
        {
            var sha = i.ToString("x64"); var path = ObjectPath(directory.Path, sha); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            tasks.Add(Assert.ThrowsAsync<DiscoveryException>(() => objects.OpenVerifiedAsync(sha, 3, default)));
        }
        await entered.Task.WaitAsync(Deadline);
        try
        {
            Assert.Equal("CONTENT_STORE_CAPACITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                objects.OpenVerifiedAsync(new string('f', 64), 3, default))).Code);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(tasks).WaitAsync(Deadline);
    }

    [Fact]
    public async Task Live_reader_replacement_failure_on_Windows_is_retryable_and_preserves_original()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows sharing behavior requires native qualification.
        using var directory = new TempDirectory(); await using var objects = new VerifiedObjectStore(directory.Path);
        var bytes = Encoded(); var sha = await objects.PublishFileAsync(await Stage(objects, bytes), bytes.Length, default);
        byte[] damaged = [1, 2]; await File.WriteAllBytesAsync(ObjectPath(directory.Path, sha), damaged);
        await using var reader = new FileStream(ObjectPath(directory.Path, sha), FileMode.Open, FileAccess.Read, FileShare.Read);
        var path = await Stage(objects, bytes);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => objects.PublishEncodedFileAsync(path, bytes.Length, EKey(bytes), default));
        Assert.Equal("CONTENT_REPAIR_UNAVAILABLE", error.Code); Assert.True(error.Retryable); Assert.False(File.Exists(path));
        Assert.Equal(damaged, await File.ReadAllBytesAsync(ObjectPath(directory.Path, sha)));
    }

    private static byte[] Headered()
    {
        byte[][] chunks = [[(byte)'N', 1, 2], [(byte)'E', 3, 4]];
        var header = new byte[60]; "BLTE"u8.CopyTo(header); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), 60);
        header[8] = 15; header[11] = 2;
        for (var i = 0; i < chunks.Length; i++)
        {
            var at = 12 + i * 24; BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(at), chunks[i].Length);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(at + 4), 2); MD5.HashData(chunks[i]).CopyTo(header, at + 8);
        }
        return [.. header, .. chunks[0], .. chunks[1]];
    }
}
