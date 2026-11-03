using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryInventoryTestKit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

/// <summary>The inventory index is authoritative intent; pack scans cannot recover it.</summary>
public sealed class LibraryInventoryFormatTests
{
    private static LibraryObjectOwner Owner(ObjectStore store) => store.ForOwner("build-a", LibraryOwnerScope.Build, "metadata", LibraryObjectRole.Original);

    [Fact]
    public async Task LibraryV1RequiresAnExplicitAdditiveUpgradeAndRetainsItsPayloadAndOwner()
    {
        using var temp = new TempDirectory();
        var directory = DirectoryPath(temp);
        var item = Synthetic.Headerless(2151, 3000);
        await using (var store = await ObjectStore.OpenLibraryAsync(directory, Quick)) await Owner(store).PutAsync([item], true);
        // Author a genuine S03/S04 schema: version 1 and no inventory tables. Existing payload/index rows are untouched.
        MutateIndex(directory, """
            DROP TABLE library_inventory_source;
            DROP TABLE library_inventory_key;
            DROP TABLE library_inventory_generation;
            DROP TABLE library_inventory_identity;
            UPDATE library_format SET version=1;
            """);
        var beforePacks = PackFiles(directory).ToDictionary(file => Path.GetFileName(file)!, Hash);
        await using (var legacyLibrary = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            Assert.True(await Owner(legacyLibrary).OwnsAsync(item.Key));
            Assert.Equal(item.Bytes.ToArray(), (await legacyLibrary.ReadAsync(item.Key)).Bytes);
            Assert.Equal(ObjectStoreCodes.InventoryUpgradeRequired, (await Assert.ThrowsAsync<ObjectStoreException>(() => Owner(legacyLibrary).Inventory.BeginAsync())).Code);
        }
        await using (var upgraded = await ObjectStore.OpenLibraryAsync(directory, Quick with { UpgradeLibraryInventory = true }))
        {
            Assert.True(await Owner(upgraded).OwnsAsync(item.Key));
            Assert.Equal(item.Bytes.ToArray(), (await upgraded.ReadAsync(item.Key)).Bytes);
            Assert.Null((await Owner(upgraded).Inventory.ReadPageAsync()).Generation);
            var generation = await Owner(upgraded).Inventory.BeginAsync();
            await Owner(upgraded).Inventory.AppendAsync(generation, [item.Key, Key(1)]);
            await Owner(upgraded).Inventory.PublishAsync(generation);
        }
        Assert.Equal(beforePacks, PackFiles(directory).ToDictionary(file => Path.GetFileName(file)!, Hash));
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        Assert.True((await Owner(reopened).Inventory.LookupAsync(item.Key)).Contains);
        Assert.True((await Owner(reopened).Inventory.LookupAsync(Key(1))).Contains);
        Assert.False(await Owner(reopened).OwnsAsync(Key(1)));
        Assert.Equal(1, (await reopened.StatsAsync()).Objects);
    }

    [Theory]
    [InlineData("DROP TABLE library_inventory_key", false)]
    [InlineData("DROP TABLE library_inventory_generation", false)]
    [InlineData("DROP TABLE library_inventory_identity", false)]
    [InlineData("UPDATE library_format SET version=999", true)]
    [InlineData("UPDATE library_inventory_identity SET owner=' '", false)]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_identity SET scope=99", false)]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_generation SET state=99", false)]
    [InlineData("UPDATE library_inventory_identity SET active=999999", false)]
    [InlineData("UPDATE library_inventory_identity SET latest=999999", false)]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_key SET k=zeroblob(17) WHERE k=(SELECT k FROM library_inventory_key ORDER BY k LIMIT 1)", false)]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_key SET k=zeroblob(18) WHERE k=(SELECT k FROM library_inventory_key ORDER BY k LIMIT 1)", false)]
    [InlineData("DELETE FROM library_inventory_key WHERE k=(SELECT k FROM library_inventory_key ORDER BY k LIMIT 1)", false)]
    [InlineData("UPDATE library_inventory_key SET k=X'070000000000000000000000000000000063' WHERE k=(SELECT k FROM library_inventory_key ORDER BY k LIMIT 1)", false)]
    [InlineData("DELETE FROM library_inventory_key", false)]
    [InlineData("DELETE FROM library_inventory_generation", false)]
    public async Task MissingMalformedOrNewerInventoryMetadataIsRefusedWithoutRebuildingIntent(string mutation, bool newer)
    {
        using var temp = new TempDirectory();
        var directory = DirectoryPath(temp);
        await using (var store = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            var inventory = Owner(store).Inventory;
            var generation = await inventory.BeginAsync();
            await inventory.AppendAsync(generation, [Key(1), Key(2)]);
            await inventory.PublishAsync(generation);
        }
        MutateIndex(directory, mutation);
        var before = HashFolder(directory);
        var failure = await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenLibraryAsync(directory, Quick));
        Assert.Equal(newer ? ObjectStoreCodes.NewerFormat : ObjectStoreCodes.IndexUnreadable, failure.Code);
        Assert.Equal(before, HashFolder(directory));
    }

    [Theory]
    [InlineData(ObjectStoreKind.Cdn)]
    [InlineData(ObjectStoreKind.Generated)]
    public async Task InventoryUpgradeCannotAdoptOrMutateLegacyStandaloneStores(ObjectStoreKind legacyKind)
    {
        using var temp = new TempDirectory();
        var directory = DirectoryPath(temp);
        await using (var legacy = await ObjectStore.OpenAsync(directory, Quick with { Kind = legacyKind })) { }
        var before = HashFolder(directory);
        var failure = await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenLibraryAsync(directory, Quick with { UpgradeLibraryInventory = true }));
        Assert.Equal(ObjectStoreCodes.KindMismatch, failure.Code);
        Assert.Equal(before, HashFolder(directory));
        await using var reopened = await ObjectStore.OpenAsync(directory, Quick with { Kind = legacyKind });
        Assert.Equal(legacyKind, reopened.Kind);
    }
}
