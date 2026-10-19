using System.Security.Cryptography;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class StreamingObjectStoreTests
{
    [Fact]
    public async Task StreamsLargeMetadataIntoTheSharedCasAndReturnsAVerifiedRewoundReadLease()
    {
        using var directory = new TempDirectory();
        await using var store = new VerifiedObjectStore(directory.Path);
        var block = Enumerable.Range(0, 65_536).Select(i => (byte)(i * 31 + 7)).ToArray();
        const int repetitions = 49;
        string staged;
        string expected;
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            await using var output = await store.CreateStagingFileAsync(CancellationToken.None);
            staged = output.Name;
            Assert.True(output.IsAsync);
            Assert.True(output.CanRead);
            Assert.True(output.CanWrite);
            for (var i = 0; i < repetitions; i++)
            {
                await output.WriteAsync(block);
                hash.AppendData(block);
            }
            await output.FlushAsync();
            output.Position = 0;
            Assert.Equal(block[0], output.ReadByte());
            expected = Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        var length = block.Length * (long)repetitions;
        Assert.True(length > 1024 * 1024);
        var sha = await store.PublishFileAsync(staged, length, CancellationToken.None);
        Assert.Equal(expected, sha);
        Assert.False(File.Exists(staged));
        await using var input = await store.OpenVerifiedAsync(sha, length, CancellationToken.None);
        Assert.True(input.IsAsync);
        Assert.Equal(0, input.Position);
        Assert.Equal(length, input.Length);
        Assert.False(input.CanWrite);
        var observed = new byte[block.Length];
        for (var i = 0; i < repetitions; i++)
        {
            await input.ReadExactlyAsync(observed);
            Assert.Equal(block, observed);
        }
        Assert.Equal(0, await input.ReadAsync(observed));
        Assert.Single(ObjectFiles(directory.Path));
    }

    [Fact]
    public async Task DuplicatePublicationReusesOneVerifiedObjectAndConsumesBothStagingFiles()
    {
        using var directory = new TempDirectory();
        await using var store = new VerifiedObjectStore(directory.Path);
        var first = await Stage(store, "authored shared content"u8.ToArray());
        var sha = await store.PublishFileAsync(first, 23, CancellationToken.None);
        var second = await Stage(store, "authored shared content"u8.ToArray());
        Assert.Equal(sha, await store.PublishFileAsync(second, 23, CancellationToken.None));
        Assert.Single(ObjectFiles(directory.Path));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, "staging-v1")));
    }

    [Fact]
    public async Task CorruptExistingDuplicateIsRefusedWithoutOverwritingIt()
    {
        using var directory = new TempDirectory();
        await using var store = new VerifiedObjectStore(directory.Path);
        var content = "original"u8.ToArray();
        var staged = await Stage(store, content);
        var sha = await store.PublishFileAsync(staged, content.Length, CancellationToken.None);
        var path = ObjectPath(directory.Path, sha);
        await File.WriteAllBytesAsync(path, "modified"u8.ToArray());
        staged = await Stage(store, content);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => store.PublishFileAsync(staged, content.Length, CancellationToken.None));
        Assert.Equal("LOCAL_OBJECT_CORRUPT", error.Code);
        Assert.False(File.Exists(staged));
        Assert.Equal("modified"u8.ToArray(), await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedReadDistinguishesMissingAndCorruptBytes(bool corrupt)
    {
        using var directory = new TempDirectory();
        await using var store = new VerifiedObjectStore(directory.Path);
        var content = "integrity proof"u8.ToArray();
        var sha = await store.PublishFileAsync(await Stage(store, content), content.Length, CancellationToken.None);
        var path = ObjectPath(directory.Path, sha);
        if (corrupt)
        {
            var changed = content.ToArray(); changed[0] ^= 1;
            await File.WriteAllBytesAsync(path, changed);
        }
        else File.Delete(path);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => store.OpenVerifiedAsync(sha, content.Length, CancellationToken.None));
        Assert.Equal(corrupt ? "LOCAL_OBJECT_CORRUPT" : "LOCAL_OBJECT_MISSING", error.Code);
        Assert.Equal(corrupt, File.Exists(path));
    }

    [Fact]
    public async Task DeclaredReadLengthAndHashIdentityAreValidated()
    {
        using var directory = new TempDirectory();
        await using var store = new VerifiedObjectStore(directory.Path);
        var content = "length check"u8.ToArray();
        var sha = await store.PublishFileAsync(await Stage(store, content), content.Length, CancellationToken.None);
        Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            store.OpenVerifiedAsync(sha, content.Length + 1, CancellationToken.None))).Code);
        foreach (var length in new[] { 0L, -1L, VerifiedObjectStore.MaxObjectBytes + 1 })
            Assert.Equal("CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                store.OpenVerifiedAsync(sha, length, CancellationToken.None))).Code);
        Assert.Equal("CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            store.OpenVerifiedAsync("../invalid", 1, CancellationToken.None))).Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(536870913)]
    public async Task PublicationCapsAndFailureConsumeOnlyOwnedStaging(long declaredLength)
    {
        using var directory = new TempDirectory();
        await using var store = new VerifiedObjectStore(directory.Path);
        var staged = await Stage(store, [1]);
        Assert.Equal("CONTENT_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            store.PublishFileAsync(staged, declaredLength, CancellationToken.None))).Code);
        Assert.False(File.Exists(staged));
        Assert.Empty(ObjectFiles(directory.Path));
    }

    [Fact]
    public async Task PublicationRejectsBothTruncatedAndExcessActualLengthsAndCleansStaging()
    {
        using var directory = new TempDirectory();
        await using var store = new VerifiedObjectStore(directory.Path);
        foreach (var declared in new[] { 1, 3 })
        {
            var staged = await Stage(store, [1, 2]);
            Assert.Equal("CONTENT_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                store.PublishFileAsync(staged, declared, CancellationToken.None))).Code);
            Assert.False(File.Exists(staged));
        }
        Assert.Empty(ObjectFiles(directory.Path));
    }

    [Fact]
    public async Task ArbitraryNestedAndWrongPrefixInputsAreRefusedAndPreserved()
    {
        using var directory = new TempDirectory();
        await using var store = new VerifiedObjectStore(directory.Path);
        var owned = await Stage(store, [1]);
        var outside = Path.Combine(directory.Path, Path.GetFileName(owned));
        var nestedDirectory = Path.Combine(directory.Path, "staging-v1", "nested");
        Directory.CreateDirectory(nestedDirectory);
        var nested = Path.Combine(nestedDirectory, Path.GetFileName(owned));
        var wrongName = Path.Combine(directory.Path, "staging-v1", "configuration-" + Guid.NewGuid().ToString("N") + ".part");
        foreach (var path in new[] { outside, nested, wrongName })
        {
            await File.WriteAllBytesAsync(path, [1]);
            Assert.Equal("CONTENT_STAGING_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                store.PublishFileAsync(path, 1, CancellationToken.None))).Code);
            Assert.True(File.Exists(path));
        }
        Assert.True(File.Exists(owned));
        File.Delete(owned);
    }

    [Fact]
    public async Task SymbolicLinkStagingIsRefusedWithoutTouchingItsTarget()
    {
        // Creating symlinks may require privileges on Windows; this Linux qualification is explicit.
        if (OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        await using var store = new VerifiedObjectStore(directory.Path);
        var staged = await Stage(store, [1]);
        var target = Path.Combine(directory.Path, "authored-target");
        await File.WriteAllBytesAsync(target, [7]);
        File.Delete(staged);
        File.CreateSymbolicLink(staged, target);
        Assert.Equal("CONTENT_STAGING_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            store.PublishFileAsync(staged, 1, CancellationToken.None))).Code);
        Assert.Equal(new byte[] { 7 }, await File.ReadAllBytesAsync(target));
        Assert.NotNull(new FileInfo(staged).LinkTarget);
    }

    [Fact]
    public async Task ExclusiveStoreOwnerPreventsSecondStoreFromDeletingActiveStaging()
    {
        using var directory = new TempDirectory();
        await using var first = new VerifiedObjectStore(directory.Path);
        var staged = await Stage(first, [1]);
        await using var second = new VerifiedObjectStore(directory.Path);
        Assert.Equal("CONTENT_STORE_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            second.PublishFileAsync(staged, 1, CancellationToken.None))).Code);
        Assert.True(File.Exists(staged));
        File.Delete(staged);
    }

    [Fact]
    public async Task CanceledPublicationCleansItsStageAndCreatesNoReadyObject()
    {
        using var directory = new TempDirectory();
        await using var store = new VerifiedObjectStore(directory.Path);
        var staged = await Stage(store, new byte[2 * 1024 * 1024]);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PublishFileAsync(staged, 2L * 1024 * 1024, canceled.Token));
        Assert.False(File.Exists(staged));
        Assert.Empty(ObjectFiles(directory.Path));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CreateStagingFileAsync(canceled.Token));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.Path, "staging-v1")));
    }

    [Fact]
    public async Task CanceledReadDoesNotExposeAnUnverifiedLease()
    {
        using var directory = new TempDirectory();
        await using var store = new VerifiedObjectStore(directory.Path);
        var sha = await store.PublishFileAsync(await Stage(store, [1]), 1, CancellationToken.None);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.OpenVerifiedAsync(sha, 1, canceled.Token));
        await using var lease = await store.OpenVerifiedAsync(sha, 1, CancellationToken.None);
        Assert.Equal(0, lease.Position);
    }

    [Fact]
    public async Task RecoveryDeletesAtMost64RecognizedAbandonedStagesAndPreservesOtherFiles()
    {
        using var directory = new TempDirectory();
        var staging = Path.Combine(directory.Path, "staging-v1");
        Directory.CreateDirectory(staging);
        for (var i = 0; i < 80; i++)
            await File.WriteAllBytesAsync(Path.Combine(staging, "metadata-" + Guid.NewGuid().ToString("N") + ".part"), [1]);
        for (var i = 0; i < 4; i++)
            await File.WriteAllBytesAsync(Path.Combine(staging, "configuration-" + Guid.NewGuid().ToString("N") + ".part"), [1]);
        var unrelated = Path.Combine(staging, "unrelated.part"); await File.WriteAllBytesAsync(unrelated, [2]);
        await using var store = new VerifiedObjectStore(directory.Path);
        await using var current = await store.CreateStagingFileAsync(CancellationToken.None);
        Assert.True(File.Exists(unrelated));
        Assert.Empty(Directory.EnumerateFiles(staging, "configuration-*.part"));
        Assert.Equal(21, Directory.EnumerateFiles(staging, "metadata-*.part").Count());
    }

    [Fact]
    public async Task ExistingConfigurationMethodsRetainTheirOneMiBLimits()
    {
        using var directory = new TempDirectory();
        await using var store = new VerifiedObjectStore(directory.Path);
        var content = new byte[1024 * 1024 + 1];
        var sha = Convert.ToHexStringLower(SHA256.HashData(content));
        Assert.Equal("CONFIG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            store.PublishAsync(content, sha, CancellationToken.None))).Code);
        Assert.Equal("CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            store.ReadAsync(sha, content.Length, CancellationToken.None))).Code);
    }

    private static async Task<string> Stage(VerifiedObjectStore store, byte[] bytes)
    {
        await using var output = await store.CreateStagingFileAsync(CancellationToken.None);
        await output.WriteAsync(bytes);
        await output.FlushAsync();
        return output.Name;
    }
    private static string ObjectPath(string directory, string sha) => Path.Combine(directory, "objects-v1", sha[..2], sha[2..]);
    private static string[] ObjectFiles(string directory) => Directory.EnumerateFiles(Path.Combine(directory, "objects-v1"), "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != ".owner").ToArray();
}
