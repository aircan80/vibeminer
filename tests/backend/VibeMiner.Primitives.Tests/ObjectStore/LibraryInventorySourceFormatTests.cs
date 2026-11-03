using VibeMiner.Objects;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryInventoryTestKit;
using static VibeMiner.Primitives.Tests.LibraryInventorySourceContractTests;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public sealed class LibraryInventorySourceFormatTests
{
    private static ILibraryInventory Inventory(ObjectStore library, string owner = "a") => library.ForOwner(owner, LibraryOwnerScope.Build, "encoding", LibraryObjectRole.Original).Inventory;
    private static async Task SeedAsync(string directory)
    {
        await using var library = await ObjectStore.OpenLibraryAsync(directory, Quick); var inventory = Inventory(library);
        var stage = await inventory.BeginAsync(); await inventory.AppendAsync(stage, [Key(1)]); await inventory.PublishSourceAsync(stage, Descriptor(1));
        var other = Inventory(library, "b"); var second = await other.BeginAsync(); await other.AppendAsync(second, [Key(2)]); await other.PublishAsync(second);
    }
    [Theory]
    [InlineData("DROP TABLE library_inventory_source", false)]
    [InlineData("UPDATE library_format SET version=4", true)]
    [InlineData("UPDATE library_inventory_source SET format='another-valid-format'", false)]
    [InlineData("UPDATE library_inventory_source SET revision='another-valid-revision'", false)]
    [InlineData("UPDATE library_inventory_source SET source='another-valid-source'", false)]
    [InlineData("UPDATE library_inventory_source SET expected_keys=expected_keys+1", false)]
    [InlineData("UPDATE library_inventory_source SET generation=(SELECT active FROM library_inventory_identity WHERE owner='b')", false)]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_source SET format=zeroblob(100000)", false)]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE library_inventory_source SET row_check=CAST(row_check AS TEXT)", false)]
    [InlineData("UPDATE library_inventory_source SET row_check=zeroblob(32)", false)]
    public async Task MissingMalformedNewerOrRelocatedSourceProofIsRefusedUnchanged(string mutation, bool newer)
    {
        using var temp = new TempDirectory(); var directory = DirectoryPath(temp); await SeedAsync(directory); MutateIndex(directory, mutation);
        var before = HashFolder(directory);
        Assert.Equal(newer ? ObjectStoreCodes.NewerFormat : ObjectStoreCodes.IndexUnreadable,
            (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenLibraryAsync(directory, Quick))).Code);
        Assert.Equal(before, HashFolder(directory));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedProofSchemaAndAHiddenDuplicateCannotBypassAuthoritativeValidation(bool addHiddenBadRow)
    {
        using var temp = new TempDirectory(); var directory = DirectoryPath(temp); await SeedAsync(directory);
        MutateIndex(directory, """
            ALTER TABLE library_inventory_source RENAME TO previous_source;
            CREATE TABLE library_inventory_source(generation INTEGER,format TEXT,revision TEXT,source TEXT,expected_keys INTEGER,row_check BLOB);
            INSERT INTO library_inventory_source SELECT generation,format,revision,source,expected_keys,row_check FROM previous_source;
            """);
        if (addHiddenBadRow) MutateIndex(directory, "INSERT INTO library_inventory_source SELECT generation,format,revision,'changed-source',expected_keys,row_check FROM previous_source");
        MutateIndex(directory, "DROP TABLE previous_source"); var before = HashFolder(directory);
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(() => ObjectStore.OpenLibraryAsync(directory, Quick))).Code);
        Assert.Equal(before, HashFolder(directory));
    }

    [Theory]
    [InlineData("UPDATE library_inventory_source SET revision='changed-valid-revision'")]
    [InlineData("UPDATE library_inventory_source SET expected_keys=2")]
    public async Task ActiveProofIntegrityIsCheckedBeforeMismatchCanBeReportedAsACacheMiss(string mutation)
    {
        using var temp = new TempDirectory(); var directory = DirectoryPath(temp); await SeedAsync(directory);
        await using var library = await ObjectStore.OpenLibraryAsync(directory, Quick); MutateIndex(directory, mutation);
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await Inventory(library).MatchSourceAsync(Descriptor(1)))).Code);
    }
    [Fact]
    public async Task ExplicitSourceUpgradeCanAddBothInventoryLayersToAnOwnerOnlyV1Library()
    {
        using var temp = new TempDirectory(); var directory = DirectoryPath(temp);
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, Quick)) { }
        MutateIndex(directory, """
            DROP TABLE library_inventory_source;
            DROP TABLE library_inventory_key;
            DROP TABLE library_inventory_generation;
            DROP TABLE library_inventory_identity;
            UPDATE library_format SET version=1;
            """);
        await using var upgraded = await ObjectStore.OpenLibraryAsync(directory, Quick with { UpgradeLibraryInventorySources = true });
        var inventory = Inventory(upgraded); var stage = await inventory.BeginAsync(); await inventory.PublishSourceAsync(stage, Descriptor(0));
        Assert.Equal(stage.Value, await inventory.MatchSourceAsync(Descriptor(0))); Assert.Equal(0, (await upgraded.StatsAsync()).Objects);
    }
}
