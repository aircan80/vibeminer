using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spectre.Console;
using VibeMiner.Builds;
using VibeMiner.Builds.Testing;
using VibeMiner.Cdn;
using VibeMiner.Cli.Browse;
using VibeMiner.Cli.Profile;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Cli.Tests;

/// <summary><c>vm reset</c> against temp data folders only, and the proof that a reset profile reopens from the store alone.</summary>
public sealed class ResetCommandTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibeminer-cli-reset-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(root, "data");
    private string StoreFolder => Path.Combine(Data, ObjectStore.DefaultFolderName);
    private string CatalogFile => Path.Combine(Data, RealStores.CatalogFolder, RealStores.CatalogFile);

    private readonly FakeStores stores = new();
    private readonly StringWriter output = new(), error = new(), json = new();

    public ResetCommandTests() => Directory.CreateDirectory(Data);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    private static CliEnvironment Env(string data) =>
        new("linux", name => name == "VIBEMINER_DATA_DIR" ? data : null, "/home/nobody-cli-reset");

    private Task<int> Run(params string[] args)
    {
        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        json.GetStringBuilder().Clear();
        return new VmCommands(Env(Data), stores, Consoles.Plain(output, 250), Consoles.Plain(error, 250), json).RunAsync(args, CancellationToken.None);
    }

    private JsonElement JsonOut() => JsonDocument.Parse(json.ToString()).RootElement;

    private List<string[]> Rows() => output.ToString().Split('\n')
        .Select(line => line.Split('│').Select(cell => cell.Trim()).ToArray())
        .Where(cells => cells.Length > 2).Select(cells => cells[1..^1]).ToList();

    private string At(params string[] parts) => Path.Combine([Data, .. parts]);

    private static void Write(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>A fake object store on disk, the targets and their sizes (catalog 4096 + generated 1000 + settings 50).</summary>
    private void FillProfile()
    {
        Write(At(ObjectStore.DefaultFolderName, "packs", "00", "pack-0001"), 5000);
        Write(At(ObjectStore.DefaultFolderName, "index", "store.idx"), 300);
        Write(CatalogFile, 4096);
        Write(At(ProfileReset.GeneratedFolder, "tables", "rows.bin"), 1000);
        Write(At(ProfileReset.SettingsFile), 50);
    }

    /// <summary>
    /// Every file under the (fake, on-disk) object store folder with its hash. The fake writer open writes nothing, so an unchanged
    /// snapshot proves the reset itself never touches the store's files; the real writer open still writes its own lock.
    /// </summary>
    private Dictionary<string, string> StoreFiles() => Directory.EnumerateFiles(StoreFolder, "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(StoreFolder, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    private bool AnyTargetLeft() => Directory.Exists(At(RealStores.CatalogFolder)) || Directory.Exists(At(ProfileReset.GeneratedFolder)) || File.Exists(At(ProfileReset.SettingsFile));

    [Fact]
    public async Task WithoutYesItSaysWhatItWouldWipeAndChangesNothing()
    {
        FillProfile();
        var storeFiles = StoreFiles();
        Assert.Equal(VmCommands.Usage, await Run("reset"));
        var rows = Rows();
        Assert.Contains(["Data folder", $"{Data} (VIBEMINER_DATA_DIR)"], rows);
        Assert.Equal(["catalog-v1/", "generated-v1/", "settings-v1.json", "settings-v1.json.tmp"], rows.Where(r => r[0] == "Would wipe").Select(r => r[1]));
        Assert.Contains(["Kept", "cdn-store-v1 (every object and the history log)"], rows);
        Assert.Contains(rows, r => r[0] == "Not touched" && r[1].StartsWith("Electron's data-folder-v1.json (/home/nobody-cli-reset/.config/@vibeminer/desktop/", StringComparison.Ordinal));
        Assert.Contains("--yes", error.ToString());
        Assert.Contains("Nothing was changed", error.ToString());
        Assert.Empty(stores.Opened);
        Assert.True(File.Exists(CatalogFile) && Directory.Exists(At(ProfileReset.GeneratedFolder)) && File.Exists(At(ProfileReset.SettingsFile)));
        Assert.Equal(storeFiles, StoreFiles()); // the reset left the store's files alone

        Assert.Equal(VmCommands.Usage, await Run("--json", "reset"));
        var plan = JsonOut();
        Assert.False(plan.GetProperty("ok").GetBoolean());
        Assert.Equal("RESET_NEEDS_YES", plan.GetProperty("code").GetString());
        Assert.Equal(4, plan.GetProperty("wouldWipe").GetArrayLength());
        Assert.Equal(Data, plan.GetProperty("dataFolder").GetProperty("path").GetString());
        Assert.True(File.Exists(CatalogFile));
    }

    [Fact]
    public async Task YesOnlyAppliesToReset()
    {
        Assert.Equal(VmCommands.Usage, await Run("--yes", "store", "info"));
        Assert.Contains("--yes only applies to reset.", error.ToString());
        Assert.Empty(stores.Opened);
    }

    [Fact]
    public async Task WithYesItWipesTheTargetsUnderTheWriterLockAndKeepsTheStore()
    {
        FillProfile();
        var storeFiles = StoreFiles();
        bool? leftAtRelease = null;
        stores.Disposing = writer => { if (writer) leftAtRelease = AnyTargetLeft(); };

        Assert.Equal(VmCommands.Ok, await Run("reset", "--yes"));
        // The lock was taken without creating a store, held until every target was gone, and released; no catalog was opened.
        Assert.Equal([(StoreFolder, true)], stores.Opened);
        Assert.Equal([false], stores.WriterCreate);
        Assert.False(leftAtRelease);
        Assert.Equal(0, stores.Open);
        Assert.Equal(0, stores.CatalogsCreated);
        Assert.False(AnyTargetLeft());
        Assert.Equal(storeFiles, StoreFiles()); // the reset left the store's files alone

        var rows = Rows();
        Assert.Contains(["Writer lock", $"held on {StoreFolder} for the reset"], rows);
        Assert.Contains(["catalog-v1/", "wiped, 4.0 KiB"], rows);
        Assert.Contains(["generated-v1/", "wiped, 1,000 B"], rows);
        Assert.Contains(["settings-v1.json", "wiped, 50 B"], rows);
        Assert.Contains(["settings-v1.json.tmp", "absent"], rows);
        Assert.Contains(["Kept", "cdn-store-v1 (every object and the history log)"], rows);
        Assert.Contains(rows, r => r[0] == "Not touched" && r[1] == "the legacy discovery-v1.sqlite3 and objects-v1/");
        Assert.Contains(["Freed", "5.0 KiB"], rows);

        // Again: nothing left to wipe is still a success.
        Assert.Equal(VmCommands.Ok, await Run("reset", "--yes"));
        Assert.All(Rows().Where(r => r[0].StartsWith("catalog-v1", StringComparison.Ordinal) || r[0].StartsWith("settings", StringComparison.Ordinal)),
            r => Assert.Equal("absent", r[1]));
    }

    [Fact]
    public async Task ABusyStoreFailsAndDeletesNothing()
    {
        FillProfile();
        stores.WriterFailure = ObjectStoreCodes.Busy;
        Assert.Equal(VmCommands.Failed, await Run("reset", "--yes"));
        Assert.Contains(ObjectStoreCodes.Busy, error.ToString());
        Assert.Contains("Nothing was reset.", error.ToString());
        Assert.True(File.Exists(CatalogFile) && Directory.Exists(At(ProfileReset.GeneratedFolder)) && File.Exists(At(ProfileReset.SettingsFile)));

        Assert.Equal(VmCommands.Failed, await Run("--json", "reset", "--yes"));
        Assert.Equal(ObjectStoreCodes.Busy, JsonOut().GetProperty("code").GetString());
        Assert.True(File.Exists(CatalogFile));
    }

    [Fact]
    public async Task AMissingStoreHasNoLockToTakeSoTheResetGoesAhead()
    {
        Write(CatalogFile, 10);
        stores.ReadOnlyFailure = ObjectStoreCodes.Missing;
        Assert.Equal(VmCommands.Ok, await Run("reset", "--yes"));
        Assert.Equal([false], stores.WriterCreate);
        Assert.False(Directory.Exists(StoreFolder));
        Assert.False(File.Exists(CatalogFile));
        Assert.Contains(["Writer lock", $"no object store at {StoreFolder}, so no lock to take"], Rows());

        Assert.Equal(VmCommands.Ok, await Run("--json", "reset", "--yes"));
        Assert.Equal("noStore", JsonOut().GetProperty("store").GetProperty("lock").GetString());
    }

    [Fact]
    public async Task ATargetThatCantBeWipedFailsTheResetAndTheOthersStillGo()
    {
        // A file where the catalog folder should be is refused, never deleted.
        Write(At(RealStores.CatalogFolder), 7);
        Write(At(ProfileReset.SettingsFile), 50);
        Assert.Equal(VmCommands.Failed, await Run("reset", "--yes"));
        Assert.Contains(Rows(), r => r[0] == "catalog-v1/" && r[1].StartsWith($"failed: {ProfileReset.KindMismatch}: ", StringComparison.Ordinal));
        Assert.True(File.Exists(At(RealStores.CatalogFolder)));
        Assert.False(File.Exists(At(ProfileReset.SettingsFile)));
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task JsonGivesEveryTargetsStateAndBytes()
    {
        FillProfile();
        // A folder an interrupted reset left behind goes too.
        Write(At(RealStores.CatalogFolder + ProfileReset.ResetMarker + "0123", "catalog.sqlite3"), 20);
        Assert.Equal(VmCommands.Ok, await Run("--json", "reset", "--yes"));
        Assert.Equal("", output.ToString());
        var doc = JsonOut();
        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.Equal("held", doc.GetProperty("store").GetProperty("lock").GetString());
        var targets = doc.GetProperty("targets").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);
        Assert.Equal(["catalog-v1", "generated-v1", "settings-v1.json", "settings-v1.json.tmp"], targets.Keys);
        Assert.Equal(("folder", "wiped", 4096L), (targets["catalog-v1"].GetProperty("kind").GetString(), targets["catalog-v1"].GetProperty("state").GetString(), targets["catalog-v1"].GetProperty("bytes").GetInt64()));
        Assert.Equal(("file", "absent"), (targets["settings-v1.json.tmp"].GetProperty("kind").GetString(), targets["settings-v1.json.tmp"].GetProperty("state").GetString()));
        var leftover = Assert.Single(doc.GetProperty("leftovers").EnumerateArray());
        Assert.Equal("wiped", leftover.GetProperty("state").GetString());
        Assert.Equal(["cdn-store-v1"], doc.GetProperty("kept").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal(2, doc.GetProperty("notTouched").GetArrayLength());
        Assert.Equal(4096 + 1000 + 50 + 20, doc.GetProperty("bytesFreed").GetInt64());
        Assert.False(doc.GetProperty("cancelled").GetBoolean());
        Assert.False(Directory.Exists(At(RealStores.CatalogFolder + ProfileReset.ResetMarker + "0123")));
    }

    private const string VersionsHeader = "Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16";
    private static readonly string A = new('a', 32), B = new('b', 32), C = new('c', 32);

    /// <summary>
    /// The acceptance proof: after a reset the build catalog is rebuilt from layer 2's history alone. The only outside calls are
    /// version-service observations (versions, cdns and the product summary), and every object result the catalog and <c>d</c>
    /// get is served from the store. The fake catalog lives while its marker in catalog-v1/ does, so the reset really ends it
    /// and the next handle starts a fresh one. The build itself opens through <see cref="InMemoryBuilds"/>, so "served from the
    /// store" covers the catalog's history and document reads and <c>d</c>'s stored-key count, not ENCODING or ROOT.
    /// </summary>
    [Fact]
    public async Task AfterAResetReopeningAsksOnlyTheVersionService()
    {
        var counting = new CountingObjectSource(stores.Source);
        stores.Objects = counting;
        stores.CatalogDataFolder = Data;
        var versions = Encoding.UTF8.GetBytes($"{VersionsHeader}\n## seqn = 1\nus|{A}|{B}|{C}|60000|12.0.1.60000|{C}\neu|{A}|{B}|{C}|60000|12.0.1.60000|{C}\n");
        // Every reviewed product answers, so builds list has history for each and fetches nothing itself.
        foreach (var id in Products.Ids)
            stores.Source.AddReply(id, "us", ObservationKind.Versions, versions)
                .AddReply(id, "us", ObservationKind.Cdns, "Name!STRING:0|Path!STRING:0|Hosts!STRING:0\nus|tpr/wow|cdn.example.invalid\n"u8.ToArray());
        stores.Source.AddReply("", "us", ObservationKind.Summary, "Product!STRING:0|Seqn!DEC:4|Flags!STRING:0\n## seqn = 9\nwow|1|\n"u8.ToArray());
        // Seeded history and documents, then a warm-up that builds the catalog in catalog-v1/.
        await stores.Source.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), default);
        await stores.Source.ObserveAsync(new(ObservationKind.Cdns, "wow", "us"), default);
        Assert.Equal(VmCommands.Ok, await Run("builds", "refresh"));
        Assert.Equal(VmCommands.Ok, await Run("builds", "list"));
        Assert.True(File.Exists(stores.CatalogMarker));
        Assert.Equal(1, stores.CatalogsCreated);

        // The build's objects are in the store: d measures it from there.
        var files = new List<FakeFile>();
        var keys = new List<ObjectKey>();
        foreach (var (id, size) in new[] { (1u, 4), (2u, 6) })
        {
            var eKey = new byte[16]; eKey[0] = (byte)id;
            var cKey = new byte[16]; cKey[0] = (byte)(0x80 + id);
            files.Add(new(id, null, [new FileVariant(0x2, 0, new CdnKey(cKey), size, new CdnKey(eKey), size, null)]));
            stores.Source.AddStored(new ObjectKey(ObjectKeyType.Encoded, eKey), new byte[size]);
        }
        var builds = new InMemoryBuilds();
        builds.Add(new BuildRef("wow", A, B, "eu"), new FakeBuild(new BuildMetadata("12.0.1.60000", default, default, default, 0, files.Count, ["enUS"]), files,
            new CdnContext(new CdnRoute(["us.cdn.blizzard.com"], "tpr/wow"))));

        Write(At(ProfileReset.GeneratedFolder, "tables", "rows.bin"), 1000);
        Write(At(ProfileReset.SettingsFile), 50);
        Write(At(ObjectStore.DefaultFolderName, "packs", "00", "pack-0001"), 5000);
        var storeFiles = StoreFiles();

        Assert.Equal(VmCommands.Ok, await Run("reset", "--yes"));
        Assert.False(AnyTargetLeft());
        Assert.Equal(storeFiles, StoreFiles()); // the reset left the store's files alone
        Assert.False(File.Exists(stores.CatalogMarker));
        Assert.Equal(1, stores.CatalogsCreated);
        counting.Clear();

        Assert.Equal(VmCommands.Ok, await Run("builds", "refresh"));
        Assert.Equal(VmCommands.Ok, await Run("builds", "list"));
        Assert.Contains(Rows(), r => r[0] == "12.0.1.60000" && r[5] == A);
        await using (var navigator = new Navigator(ViewRegistry.Default(),
            new BrowseSession(stores, new DataFolder(Data, DataFolderSource.Override), StoreFolder) { Builds = _ => builds }, a => a()))
        {
            var guard = TimeSpan.FromSeconds(10);
            Assert.True(navigator.Go(":builds"));
            await navigator.Loading.WaitAsync(guard);
            Assert.True(navigator.Drill(0));
            await navigator.Loading.WaitAsync(guard);
            Assert.True(navigator.Drill(0));
            await navigator.Loading.WaitAsync(guard);
            Assert.True(navigator.TryAction('d'));
            await navigator.Tasks.Current.WaitAsync(guard);
            await navigator.Loading.WaitAsync(guard);
            Assert.Equal("downloaded: 100.0% of 10 B, 2 of 2 objects (eu)", navigator.Tasks.Status);
        }

        // Exactly one fresh catalog, shared by every later handle, rebuilt from documents the store served.
        Assert.True(File.Exists(stores.CatalogMarker));
        Assert.Equal(2, stores.CatalogsCreated);
        Assert.True(counting.GetKeyCalls > 0, "the new catalog read the stored replies");
        Assert.Equal(new Dictionary<ObservationKind, int>
        {
            [ObservationKind.Versions] = Products.Ids.Count, [ObservationKind.Cdns] = Products.Ids.Count, [ObservationKind.Summary] = 1,
        }, counting.Observations);
        Assert.NotEmpty(counting.Served);
        Assert.DoesNotContain(ServedFrom.Cdn, counting.Served);
        Assert.All(counting.Served, s => Assert.Equal(ServedFrom.Store, s));
        Assert.Equal(0, stores.Open);
    }
}
