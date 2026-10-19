using System.Security.Cryptography;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ObjectPresenceTests
{
    [Fact]
    public async Task Presence_observes_exact_length_but_does_not_hash_same_length_changed_bytes()
    {
        using var directory = new TempDirectory(); await using var store = new VerifiedObjectStore(directory.Path);
        byte[] bytes = [1, 2, 3]; var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        await store.PublishAsync(bytes, sha, default);
        Assert.Equal(new(ObjectPresenceKind.Present, 3), await store.ObservePresenceAsync(sha, 3, default));
        await File.WriteAllBytesAsync(PathFor(directory.Path, sha), [3, 2, 1]);
        Assert.Equal(new(ObjectPresenceKind.Present, 3), await store.ObservePresenceAsync(sha, 3, default));
        Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() => store.OpenVerifiedAsync(sha, 3, default))).Code);
    }

    [Fact]
    public async Task Missing_and_wrong_length_are_distinct_and_neither_claims_corruption_proof()
    {
        using var directory = new TempDirectory(); await using var store = new VerifiedObjectStore(directory.Path);
        byte[] bytes = [1, 2, 3]; var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal(new(ObjectPresenceKind.Absent, null), await store.ObservePresenceAsync(sha, 3, default));
        await store.PublishAsync(bytes, sha, default);
        Assert.Equal(new(ObjectPresenceKind.WrongLength, 3), await store.ObservePresenceAsync(sha, 4, default));
        File.Delete(PathFor(directory.Path, sha));
        Assert.Equal(new(ObjectPresenceKind.Absent, null), await store.ObservePresenceAsync(sha, 3, default));
    }

    [Fact]
    public async Task Directory_instead_of_object_is_unknown_rather_than_present_or_absent()
    {
        using var directory = new TempDirectory(); await using var store = new VerifiedObjectStore(directory.Path);
        var sha = new string('a', 64); await store.ObservePresenceAsync(sha, 3, default);
        Directory.CreateDirectory(PathFor(directory.Path, sha));
        Assert.Equal(new(ObjectPresenceKind.Unknown, null), await store.ObservePresenceAsync(sha, 3, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_or_shard_links_do_not_make_external_bytes_present(bool shardLink)
    {
        if (OperatingSystem.IsWindows()) return; // This authored symlink qualification is Linux-specific.
        using var directory = new TempDirectory(); await using var store = new VerifiedObjectStore(directory.Path);
        var sha = new string('b', 64); await store.ObservePresenceAsync(sha, 3, default);
        var target = Path.Combine(directory.Path, "authored-target");
        if (shardLink)
        {
            Directory.CreateDirectory(target); await File.WriteAllBytesAsync(Path.Combine(target, sha[2..]), [1, 2, 3]);
            Directory.CreateSymbolicLink(Path.GetDirectoryName(PathFor(directory.Path, sha))!, target);
        }
        else
        {
            await File.WriteAllBytesAsync(target, [1, 2, 3]); Directory.CreateDirectory(Path.GetDirectoryName(PathFor(directory.Path, sha))!);
            File.CreateSymbolicLink(PathFor(directory.Path, sha), target);
        }
        Assert.Equal(new(ObjectPresenceKind.Unknown, null), await store.ObservePresenceAsync(sha, 3, default));
    }

    [Theory]
    [InlineData(null, 3)]
    [InlineData("bad", 3)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", 3)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 0)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 536870913)]
    public async Task Invalid_identity_or_length_cannot_observe_a_path(string? sha, long bytes)
    {
        using var directory = new TempDirectory(); await using var store = new VerifiedObjectStore(directory.Path);
        Assert.Equal("CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => store.ObservePresenceAsync(sha!, bytes, default))).Code);
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "objects-v1")));
    }

    [Fact]
    public async Task Pre_cancellation_does_not_initialize_or_observe_the_store()
    {
        using var directory = new TempDirectory(); await using var store = new VerifiedObjectStore(directory.Path);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ObservePresenceAsync(new string('a', 64), 3, stop.Token));
        Assert.False(Directory.Exists(Path.Combine(directory.Path, "objects-v1")));
    }

    private static string PathFor(string root, string sha) => Path.Combine(root, "objects-v1", sha[..2], sha[2..]);
}
