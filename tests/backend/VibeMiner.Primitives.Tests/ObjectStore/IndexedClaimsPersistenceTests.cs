using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public sealed class IndexedClaimsPersistenceTests
{
    private static LibraryObjectOwner Owner(ObjectStore library, string id = "consumer") => library.ForOwner(id, LibraryOwnerScope.Build, "acquisition", LibraryObjectRole.Original);
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IndexCreditDoesNoPayloadReadAndItsExactClaimSurvivesReopen(bool missingPack)
    {
        using var temp = new TempDirectory(); var directory = Path.Combine(temp.Path, "library"); var item = Synthetic.Headerless(2267, 2000);
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            await Owner(library, "publisher").PutAsync([item], true); var (pack, offset, _) = Row(directory, item.Key);
            if (missingPack) File.Delete(PackPath(directory, pack)); else Flip(PackPath(directory, pack), offset + PackFormat.EntryHeaderBytes + 30);
            var rows = Rows(directory); var lastRead = LastRead(directory, item.Key); var consumer = Owner(library);
            Assert.Equal([true, false, true], await consumer.ClaimIndexedAsync([item.Key, Synthetic.Headerless(2299).Key, item.Key]));
            Assert.True(await consumer.OwnsAsync(item.Key)); Assert.Equal(rows, Rows(directory)); Assert.Equal(lastRead, LastRead(directory, item.Key));
            Assert.Equal(missingPack ? ReadState.Lost : ReadState.Corrupt, (await library.ReadAsync(item.Key)).State);
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick); Assert.True(await Owner(reopened).OwnsAsync(item.Key));
    }
    [Theory]
    [InlineData(nameof(StorePoint.LibraryClaimBeforeCommit), false)]
    [InlineData(nameof(StorePoint.LibraryClaimInsideCommit), true)]
    [InlineData(nameof(StorePoint.LibraryClaimPublished), true)]
    public async Task ClaimCancellationRespectsTheSingleDurableTransaction(string phase, bool committed)
    {
        using var temp = new TempDirectory(); using var canceled = new CancellationTokenSource(); var armed = false; var point = Enum.Parse<StorePoint>(phase);
        var options = Quick with { Fault = current => { if (armed && current == point) canceled.Cancel(); } };
        var directory = Path.Combine(temp.Path, "library"); var first = Synthetic.Headerless(2268, 1000); var second = Synthetic.Headerless(2269, 1000);
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, options))
        {
            await Owner(library, "publisher").PutAsync([first, second], true); var consumer = Owner(library); armed = true;
            if (committed) Assert.Equal([true, true, true], await consumer.ClaimIndexedAsync([first.Key, second.Key, first.Key], canceled.Token));
            else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.ClaimIndexedAsync([first.Key, second.Key], canceled.Token));
            Assert.Equal(committed, await consumer.OwnsAsync(first.Key)); Assert.Equal(committed, await consumer.OwnsAsync(second.Key));
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick); Assert.Equal(committed, await Owner(reopened).OwnsAsync(first.Key)); Assert.Equal(committed, await Owner(reopened).OwnsAsync(second.Key));
    }
    [Fact]
    public async Task FailedAttributionTransactionCannotPublishAPrefixOfTheCapturedClaims()
    {
        using var temp = new TempDirectory(); var directory = Path.Combine(temp.Path, "library"); var armed = false;
        var options = Quick with { Fault = point => { if (armed && point == StorePoint.LibraryClaimInsideCommit) throw new IOException("authored attribution failure"); } };
        var first = Synthetic.Headerless(2270, 1000); var second = Synthetic.Headerless(2271, 1000);
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, options))
        {
            await Owner(library, "publisher").PutAsync([first, second], true); var consumer = Owner(library); armed = true;
            await Assert.ThrowsAsync<ObjectStoreException>(() => consumer.ClaimIndexedAsync([first.Key, second.Key]));
            Assert.False(await consumer.OwnsAsync(first.Key)); Assert.False(await consumer.OwnsAsync(second.Key));
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        Assert.False(await Owner(reopened).OwnsAsync(first.Key)); Assert.False(await Owner(reopened).OwnsAsync(second.Key)); Assert.True(await Owner(reopened, "publisher").OwnsAsync(first.Key));
    }
    [Fact]
    public async Task LibraryCloseDrainsAcceptedClaimsAndRejectsNewAttributionWithoutAFalseSuccess()
    {
        using var temp = new TempDirectory(); var directory = Path.Combine(temp.Path, "library"); var armed = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        var options = Quick with { Fault = point =>
        {
            if (!armed || point != StorePoint.LibraryClaimInsideCommit) return;
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Claim drain test was not released.");
        } };
        var library = await ObjectStore.OpenLibraryAsync(directory, options); var item = Synthetic.Headerless(2272, 1000);
        await Owner(library, "publisher").PutAsync([item], true); var consumer = Owner(library); armed = true; var claiming = consumer.ClaimIndexedAsync([item.Key]);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var closing = library.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
            Assert.Equal(ObjectStoreCodes.Closed, (await Assert.ThrowsAsync<ObjectStoreException>(() => consumer.ClaimIndexedAsync([item.Key]))).Code);
            release.Set(); Assert.Equal([true], await claiming.WaitAsync(TimeSpan.FromSeconds(10))); await closing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.Set(); await claiming; await library.DisposeAsync(); }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick); Assert.True(await Owner(reopened).OwnsAsync(item.Key));
    }
}
