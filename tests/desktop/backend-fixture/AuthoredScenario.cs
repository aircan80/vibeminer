using System.Text;
using System.Text.Json;
using VibeMiner.Backend.Tests;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Desktop.BackendFixture;

internal sealed record AuthoredScenario(InventoryFixture Inventory, string Version,
    string BuildIdentity, string GatedEncodingKey, IReadOnlyDictionary<string, byte[]> Responses)
{
    private const string Marker = ".authored-retention-fixture.json";
    internal static AuthoredScenario Create()
    {
        var inventory = InventoryFixture.Create();
        const string version = "1.2.3.12345+AUTHORED-RETENTION-FIXTURE";
        var original = inventory.Discovery.Documents.Single(document => document.Role == "versions");
        var versions = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original.Bytes).Replace("1.2.3.12345", version, StringComparison.Ordinal));
        var row = MetadataParser.Parse(versions).Rows.Single();
        var identity = VibeMiner.Domain.BuildIdentity.Create("wow", row.Fields);
        var responses = new Dictionary<string, byte[]>(inventory.Responses, StringComparer.Ordinal)
        {
            [original.Url] = versions,
            [inventory.Discovery.Documents.Single(document => document.Role == "cdns").Url] =
                inventory.Discovery.Documents.Single(document => document.Role == "cdns").Bytes
        };
        foreach (var (key, bytes) in inventory.Payloads) responses.Add(InventoryFixture.Url("data", key), bytes);
        return new(inventory, version, identity.Id, inventory.Payloads.Keys.Order(StringComparer.Ordinal).First(), responses);
    }

    internal static async Task EnsureOwnedDirectoryAsync(string directory)
    {
        var marker = Path.Combine(directory, Marker);
        if (Directory.Exists(directory) && !File.Exists(marker) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidOperationException("The fixture requires a new empty directory or its own previously marked authored directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(marker))
        {
            if (new FileInfo(marker).Length > 4096) throw new InvalidOperationException("The fixture marker exceeds its bound.");
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(marker));
            if (!saved.RootElement.TryGetProperty("revision", out var revision) || revision.GetString() != "authored-retention-fixture-v1")
                throw new InvalidOperationException("This directory belongs to a different authored fixture revision.");
            return;
        }
        await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new
        {
            revision = "authored-retention-fixture-v1",
            authoredFixture = true,
            description = "Only authored synthetic metadata and payloads; no game content or external HTTP."
        }));
    }
}
