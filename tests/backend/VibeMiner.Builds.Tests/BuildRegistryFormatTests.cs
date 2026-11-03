using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Xunit;
using static VibeMiner.Builds.Tests.BuildRegistryTestKit;
using static VibeMiner.Builds.Tests.BuildRegistryContractTests;

namespace VibeMiner.Builds.Tests;

public sealed class BuildRegistryFormatTests
{
    [Theory]
    [InlineData("PRAGMA application_id=123", false)]
    [InlineData("PRAGMA user_version=2", true)]
    [InlineData("PRAGMA user_version=0", false)]
    [InlineData("DROP TABLE registry", false)]
    [InlineData("DROP TABLE builds", false)]
    [InlineData("CREATE TRIGGER unexpected AFTER UPDATE ON builds BEGIN DELETE FROM builds; END", false)]
    [InlineData("CREATE VIEW unexpected AS SELECT id FROM builds", false)]
    [InlineData("CREATE INDEX unexpected ON builds(data)", false)]
    [InlineData("UPDATE registry SET entry_count=entry_count+1", false)]
    [InlineData("UPDATE registry SET digest=zeroblob(32)", false)]
    [InlineData("UPDATE registry SET registry_id='00000000000000000000000000000000'", false)]
    [InlineData("DELETE FROM builds", false)]
    [InlineData("UPDATE builds SET data='{}'", false)]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE builds SET data=CAST(data AS BLOB)", false)]
    [InlineData("PRAGMA ignore_check_constraints=ON; UPDATE builds SET sha256=CAST(sha256 AS TEXT)", false)]
    public async Task DamagedForeignOrNewerAuthoritativeStorageIsRefusedWithoutMutation(string sql, bool newer)
    {
        using var directory = new RegistryDirectory();
        await using (var registry = await BuildRegistry.OpenAsync(directory.Path)) await registry.ObserveAsync(Observation());
        Mutate(directory.Database, sql); var before = HashFiles(directory.Path);
        Assert.Equal(newer ? BuildRegistryCodes.Newer : BuildRegistryCodes.Unreadable,
            (await Assert.ThrowsAsync<BuildRegistryException>(() => BuildRegistry.OpenAsync(directory.Path))).Code);
        Assert.Equal(before, HashFiles(directory.Path));
    }

    [Theory]
    [InlineData("registry-v1")]
    [InlineData("registry.sqlite3")]
    public async Task MissingEstablishedAuthoritativeFileIsNeverRecreated(string name)
    {
        using var directory = new RegistryDirectory();
        await using (var registry = await BuildRegistry.OpenAsync(directory.Path)) await registry.ObserveAsync(Observation());
        File.Delete(Path.Combine(directory.Path, name)); var before = HashFiles(directory.Path);
        Assert.Equal(BuildRegistryCodes.Unreadable, (await Assert.ThrowsAsync<BuildRegistryException>(() => BuildRegistry.OpenAsync(directory.Path))).Code);
        Assert.Equal(before, HashFiles(directory.Path)); Assert.False(File.Exists(Path.Combine(directory.Path, name)));
    }

    [Fact]
    public async Task CorruptDatabaseHeaderAndForeignMarkerAreRefusedUnchanged()
    {
        foreach (var marker in new[] { false, true })
        {
            using var directory = new RegistryDirectory();
            await using (var registry = await BuildRegistry.OpenAsync(directory.Path)) await registry.ObserveAsync(Observation());
            if (marker) File.WriteAllText(Path.Combine(directory.Path, "registry-v1"), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            else
            {
                using var stream = new FileStream(directory.Database, FileMode.Open, FileAccess.Write); stream.Write(new byte[100]);
            }
            var before = HashFiles(directory.Path);
            Assert.Equal(BuildRegistryCodes.Unreadable, (await Assert.ThrowsAsync<BuildRegistryException>(() => BuildRegistry.OpenAsync(directory.Path))).Code);
            Assert.Equal(before, HashFiles(directory.Path));
        }
    }

    [Theory]
    [InlineData("negative-generation")]
    [InlineData("discovered-with-attempt")]
    [InlineData("inconsistent-identity")]
    [InlineData("noncanonical-locale")]
    public async Task ValidChecksumsDoNotMakeStructurallyInvalidRegistryEntriesAuthoritative(string mutation)
    {
        using var directory = new RegistryDirectory();
        await using (var registry = await BuildRegistry.OpenAsync(directory.Path)) await registry.ObserveAsync(Observation());
        RehashMalformedEntry(directory.Database, mutation); var before = HashFiles(directory.Path);
        Assert.Equal(BuildRegistryCodes.Unreadable, (await Assert.ThrowsAsync<BuildRegistryException>(() => BuildRegistry.OpenAsync(directory.Path))).Code);
        Assert.Equal(before, HashFiles(directory.Path));
    }

    private static void RehashMalformedEntry(string path, string mutation)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); connection.Open();
        using var read = connection.CreateCommand(); read.CommandText = "SELECT data FROM builds LIMIT 1";
        var saved = JsonNode.Parse((string)read.ExecuteScalar()!)!.AsObject(); var entry = saved["Entry"]!.AsObject();
        switch (mutation)
        {
            case "negative-generation": entry["Generation"] = -1; break;
            case "discovered-with-attempt": entry["AttemptLocale"] = "enUS"; break;
            case "inconsistent-identity": entry["Identity"]!["Id"] = new string('0', 64); break;
            case "noncanonical-locale": entry["ObservedLocale"] = "enus"; break;
        }
        var text = saved.ToJsonString(); var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        using var write = connection.CreateCommand(); write.CommandText = "UPDATE builds SET data=$data,sha256=$hash; UPDATE registry SET digest=$hash";
        write.Parameters.AddWithValue("$data", text); write.Parameters.AddWithValue("$hash", hash); write.ExecuteNonQuery();
    }
}
