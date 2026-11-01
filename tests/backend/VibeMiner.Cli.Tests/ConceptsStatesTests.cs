using System.Text;
using System.Text.Json;
using VibeMiner.Builds;
using VibeMiner.Concepts.Creatures;
using VibeMiner.Concepts.ItemEffects;
using VibeMiner.Concepts.Items;
using VibeMiner.Concepts.Testing;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Models;
using VibeMiner.Formats.Tables;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Cli.Tests;

/// <summary><c>vm concepts states</c> against the layer 6 fakes through the command's concepts seam; no store, no network.</summary>
public sealed class ConceptsStatesTests
{
    private const string Data = "/data/vm";
    private static readonly string A = new('a', 32), B = new('b', 32), C = new('c', 32), D = new('d', 32);
    private static readonly BuildRef UsBuild = new("wow", A, B, "us");
    private const string Key = "ABCDEF0123456789";

    private readonly FakeStores stores = new();
    private readonly StringWriter output = new(), error = new(), json = new();
    private InMemoryItems items = new();
    private readonly InMemoryItemEffects effects = new();
    private readonly InMemoryCreatures creatures = new();
    private readonly List<(bool Writer, string Data)> composed = [];
    private int leasesClosed;

    public ConceptsStatesTests()
    {
        items.Add(UsBuild, "enUS", Items(120));
        effects.Add(UsBuild, "enUS", [new FakeItemEffects(1, [Effect(0, ItemEffectState.ObservedReady, ItemEffectNameState.Ready), Effect(1, ItemEffectState.Protected, ItemEffectNameState.EffectNotReady)]),
            new FakeItemEffects(3, [], State: ItemEffectsPageState.BridgeNotReady, Bridge: new(ItemEffectsRole.Bridge, ItemEffectsSourceState.Unavailable))]);
        creatures.Add(UsBuild, "enUS", Creatures(), new FakeCreatureDisplays(new Dictionary<uint, uint> { [100] = 7 }, new Dictionary<uint, uint> { [7] = 4242 },
            new Dictionary<uint, CreatureModel> { [4242] = new(CreatureModelState.Read, 4242, ModelReadState.Read, new(CreatureAppearanceState.Ready), []) }));
    }

    /// <summary>Items 1..count: every third has no ItemSparse row (MissingSparse), item 2 an empty name, item 1 a Ready icon.</summary>
    private static FakeItem[] Items(int count) => [.. Enumerable.Range(1, count).Select(i => new FakeItem((uint)i,
        i == 2 ? new ItemName(ItemNameState.Empty, "") : i % 3 == 0 ? new ItemName(ItemNameState.MissingSparse, null) : new ItemName(ItemNameState.Valid, $"Item {i}"),
        IconFileDataId: i == 1 ? 134414u : i == 2 ? 0u : null,
        Icon: i == 1 ? Own : null, Appearances: i == 1 ? ConceptsFixtures.Appearances() : null, Icons: i == 1 ? ConceptsFixtures.Icons(1, Own) : null))];

    private static readonly ItemIcon Own = new(ItemIconState.Ready, 134414, BlpImageState.Ready, ContentKey: C);

    private static ItemEffectEntry Effect(int i, ItemEffectState state, ItemEffectNameState name) => new(
        new TableRow(i, new Db2Row((uint)(10 + i), false, null, [$"{10 + i}"])), "1", $"{50 + i}",
        new ItemEffect(state, ProtectedKey: state == ItemEffectState.Protected ? Key : null), new ItemEffectName(name, Text: name == ItemEffectNameState.Ready ? "Use" : null));

    private static FakeCreature[] Creatures() =>
    [
        new(10, new CreatureName(CreatureNameState.Valid, "Hogger"), Slots: [new(0, 100, 1f, CreatureSlotState.Declared), new(1, 0, 0f, CreatureSlotState.NoDeclaredDisplay),
            new(2, 300, 0f, CreatureSlotState.Declared), new(3, 0, 0f, CreatureSlotState.NoDeclaredDisplay)]),
        new(11, new CreatureName(CreatureNameState.Invalid, null, "DB2_TEXT_ENCODING_INVALID")),
        new(12, new CreatureName(CreatureNameState.Valid, "Locked"), Outcome: TableRowOutcome.Protected, ProtectedKey: Key),
    ];

    private static CliEnvironment Env() => new("linux", name => name == "VIBEMINER_DATA_DIR" ? Data : null, "/home/nobody-cli-test");

    private Task<int> Run(params string[] args) =>
        new VmCommands(Env(), stores, Consoles.Plain(output, 250), Consoles.Plain(error, 250), json, null, concepts: (store, data, _) =>
        {
            composed.Add((store.Writer, data));
            return Task.FromResult(new ConceptsLease(items, effects, creatures, async () => { leasesClosed++; await store.DisposeAsync(); }));
        }).RunAsync(args, CancellationToken.None);

    private JsonElement JsonOut() => JsonDocument.Parse(json.ToString()).RootElement;

    private async Task ServeAsync()
    {
        const string header = "Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16";
        var versions = Encoding.UTF8.GetBytes($"{header}\n## seqn = 1\neu|{A}|{D}|{C}|58123|11.0.7.58123|{C}\nus|{A}|{B}|{C}|58123|11.0.7.58123|{C}\n");
        stores.Source.AddReply("wow", "us", ObservationKind.Versions, versions);
        await stores.Source.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), default);
    }

    private static int Count(JsonElement counts, string group, string state) =>
        counts.GetProperty(group).TryGetProperty(state, out var n) ? n.GetInt32() : 0;

    [Fact]
    public async Task JsonCountsTheSampleByStateAndListsEachDetail()
    {
        await ServeAsync();
        Assert.Equal(VmCommands.Ok, await Run("--json", "concepts", "states", "wow", "--sample", "100", "--details", "3"));
        var doc = JsonOut();
        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.True(doc.GetProperty("store").GetProperty("writer").GetBoolean());
        Assert.Equal("us", doc.GetProperty("build").GetProperty("region").GetString());
        Assert.Equal("enUS", doc.GetProperty("locale").GetString());
        Assert.Equal(100, doc.GetProperty("sample").GetInt32());
        Assert.Equal(3, doc.GetProperty("details").GetInt32());

        var listed = doc.GetProperty("items");
        Assert.Equal(100, listed.GetProperty("listed").GetInt32());
        Assert.Equal("Sample", listed.GetProperty("ended").GetString());
        Assert.Equal(100, listed.GetProperty("scanned").GetInt64());
        var counts = listed.GetProperty("counts");
        Assert.Equal(2, Count(counts, "pageStates", "Ok"));
        Assert.Equal(33, Count(counts, "nameStates", "MissingSparse"));
        Assert.Equal(1, Count(counts, "nameStates", "Empty"));
        Assert.Equal(66, Count(counts, "nameStates", "Valid"));
        Assert.Equal(100, Count(counts, "itemRowStates", "Found"));
        Assert.Equal(67, Count(counts, "sparseStates", "Present"));
        Assert.Equal(33, Count(counts, "sparseStates", "Missing"));
        Assert.Equal(3, Count(counts, "resultStates", "Found"));
        Assert.Equal(1, Count(counts, "iconStates", "Ready"));
        Assert.Equal(1, Count(counts, "iconStates", "NoIcon"));
        Assert.Equal(1, Count(counts, "iconStates", "FieldUnavailable"));
        Assert.Equal(1, Count(counts, "factSparseStates", "Missing"));
        Assert.Equal(1, Count(counts, "effectPageStates", "Ok"));
        Assert.Equal(1, Count(counts, "effectPageStates", "MissingItem"));
        Assert.Equal(1, Count(counts, "effectPageStates", "BridgeNotReady"));
        Assert.Equal(1, Count(counts, "effectStates", "ObservedReady"));
        Assert.Equal(1, Count(counts, "effectStates", "Protected"));
        Assert.Equal(1, Count(counts, "effectNameStates", "Ready"));
        Assert.Equal(1, Count(counts, "effectNameStates", "EffectNotReady"));
        Assert.Equal(1, Count(counts, "iconSourceStates", "Item.IconFileDataID:Ready"));
        Assert.Equal(1, Count(counts, "iconSourceStates", "ItemAppearance.DefaultIconFileDataID:FileFailed"));
        Assert.Equal(1, Count(counts, "iconSourceStates", "Item.IconFileDataID:NoIcon"));
        Assert.Equal(1, Count(counts, "appearanceSetStates", "Complete"));
        Assert.Equal(2, Count(counts, "appearanceSetStates", "TableNotReadable"));
        Assert.Equal(1, Count(counts, "appearanceDefaultStates", "Default"));
        Assert.Equal(2, Count(counts, "appearanceDefaultStates", "Unknown"));
        Assert.Equal(1, Count(counts, "resourceStates", "Model:Found"));
        Assert.Equal(1, Count(counts, "resourceStates", "Model:NoResource"));
        Assert.Equal(1, Count(counts, "resourceStates", "Material:Unknown"));
        Assert.Equal(1, Count(counts, "modelStates", "Read"));
        Assert.Equal(1, Count(counts, "modelStates", "NotRead"));
        Assert.Equal(1, Count(counts, "modelReadStates", "Read"));
        Assert.Equal(VmCommands.FileReads, doc.GetProperty("fileReads").GetString());
        var entries = listed.GetProperty("entries").EnumerateArray().ToList();
        var icons = entries[0].GetProperty("icons").EnumerateArray().ToList();
        Assert.Equal(["Item.IconFileDataID", "ItemAppearance.DefaultIconFileDataID"], icons.Select(i => i.GetProperty("source").GetString()));
        Assert.Equal([134414u, ConceptsFixtures.DefaultIcon], icons.Select(i => i.GetProperty("fileDataId").GetUInt32()));
        Assert.Equal([ConceptsFixtures.Model, ConceptsFixtures.Model + 10], entries[0].GetProperty("modelFileDataIds").EnumerateArray().Select(e => e.GetUInt32()));
        Assert.Equal([1u, 2u, 3u], entries.Select(e => e.GetProperty("id").GetUInt32()));
        Assert.Equal("Item 1", entries[0].GetProperty("name").GetString());
        Assert.Equal(134414u, entries[0].GetProperty("iconFileDataId").GetUInt32());
        Assert.Equal(2, entries[0].GetProperty("effects").GetInt32());
        Assert.Equal("(Empty)", entries[1].GetProperty("name").GetString());
        Assert.Equal("(MissingSparse)", entries[2].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, entries[2].GetProperty("iconFileDataId").ValueKind);

        var beasts = doc.GetProperty("creatures");
        Assert.Equal(2, beasts.GetProperty("listed").GetInt32());
        Assert.Equal("End", beasts.GetProperty("ended").GetString());
        var creatureCounts = beasts.GetProperty("counts");
        Assert.Equal(1, Count(creatureCounts, "nameStates", "Invalid"));
        Assert.Equal(6, Count(creatureCounts, "slotStates", "NoDeclaredDisplay"));
        Assert.Equal(2, Count(creatureCounts, "slotStates", "Declared"));
        Assert.Equal(2, Count(creatureCounts, "resultStates", "Found"));
        Assert.Equal(1, Count(creatureCounts, "chainStates", "MetadataResolved"));
        Assert.Equal(1, Count(creatureCounts, "chainStates", "DisplayAbsent"));
        Assert.Equal(1, Count(creatureCounts, "modelReadStates", "Read"));
        Assert.Equal(1, Count(creatureCounts, "appearanceStates", "Ready"));
        var hogger = beasts.GetProperty("entries")[0];
        Assert.Equal([4242u], hogger.GetProperty("modelFileDataIds").EnumerateArray().Select(e => e.GetUInt32()));

        Assert.Equal([(true, Data)], composed);
        Assert.Equal(1, leasesClosed);
        Assert.Equal(0, stores.Open);
        Assert.Equal(3, items.Calls(nameof(IBuildItems.GetAsync)));
    }

    [Fact]
    public async Task DefaultsAreTheWholeShortTableAndTwentyDetails()
    {
        await ServeAsync();
        Assert.Equal(VmCommands.Ok, await Run("--json", "concepts", "states", "wow"));
        var doc = JsonOut();
        Assert.Equal(VmCommands.DefaultSample, doc.GetProperty("sample").GetInt32());
        Assert.Equal(120, doc.GetProperty("items").GetProperty("listed").GetInt32());
        Assert.Equal("End", doc.GetProperty("items").GetProperty("ended").GetString());
        Assert.Equal(VmCommands.DefaultDetails, doc.GetProperty("items").GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task AScanBoundedPageIsFollowedUntilTheSample()
    {
        await ServeAsync();
        items = new InMemoryItems { MaxScanRows = 7 }.Add(UsBuild, "enUS", Items(120));
        Assert.Equal(VmCommands.Ok, await Run("--json", "concepts", "states", "wow", "--sample", "30", "--details", "0"));
        var listed = JsonOut().GetProperty("items");
        Assert.Equal(30, listed.GetProperty("listed").GetInt32());
        Assert.Equal(5, Count(listed.GetProperty("counts"), "pageStates", "Ok"));
        Assert.Empty(listed.GetProperty("entries").EnumerateArray());
    }

    [Fact]
    public async Task TextPrintsTheCountsAndTheDetails()
    {
        await ServeAsync();
        Assert.Equal(VmCommands.Ok, await Run("concepts", "states", "wow", "--sample", "10", "--details", "2"));
        var text = output.ToString();
        Assert.Contains("wow 11.0.7.58123 (us) enUS", text);
        Assert.Contains("Items: 10 listed in 10 rows scanned (Sample), 2 details.", text);
        Assert.Matches(@"│ items\s+│ nameStates\s+│ MissingSparse\s+│\s+3 │", text);
        Assert.Matches(@"│ creature\s+│\s+10 │ Hogger\s+│ Found\s+│ 4242", text);
        Assert.Contains($"Icons and models: {VmCommands.FileReads}.", text);
        Assert.Contains("icons 134414 Ready (Item.IconFileDataID), 777 FileFailed (ItemAppearance.DefaultIconFileDataID); models 5550 5560; effects Ok (2)", text.Replace("\n", "").Replace("│", ""));
        Assert.Equal("", json.ToString());
    }

    [Fact]
    public async Task UsageErrors()
    {
        Assert.Equal(VmCommands.Usage, await Run("concepts", "states", "nope"));
        Assert.Equal(VmCommands.Usage, await Run("concepts", "states", "wow", "--sample", "0"));
        Assert.Equal(VmCommands.Usage, await Run("concepts", "states", "wow", "--sample", "x"));
        Assert.Equal(VmCommands.Usage, await Run("concepts", "states", "wow", "--sample", "5", "--details", "6"));
        Assert.Equal(VmCommands.Usage, await Run("concepts", "states", "wow", "--details"));
        Assert.Equal(VmCommands.Usage, await Run("--sample", "5", "store", "info"));
        Assert.Empty(stores.Opened);
        Assert.Contains("concepts states <product> [--sample N] [--details M]", VmCommands.UsageText);
    }

    [Fact]
    public async Task NoCurrentBuildFailsWithoutComposing()
    {
        Assert.Equal(VmCommands.Failed, await Run("--json", "concepts", "states", "wow"));
        Assert.Equal("CONCEPTS_NO_CURRENT_BUILD", JsonOut().GetProperty("code").GetString());
        Assert.Empty(composed);
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task AFailedOpenIsNamedAndClosesTheLease()
    {
        await ServeAsync();
        items = new InMemoryItems().AddFailing(UsBuild, FilesOpenState.NeedsNetwork, "BUILD_NEEDS_NETWORK");
        Assert.Equal(VmCommands.Failed, await Run("--json", "concepts", "states", "wow"));
        Assert.Equal("BUILD_NEEDS_NETWORK", JsonOut().GetProperty("code").GetString());
        Assert.Equal(1, leasesClosed);
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task ABusyStoreFails()
    {
        stores.WriterFailure = ObjectStoreCodes.Busy;
        Assert.Equal(VmCommands.Failed, await Run("--json", "concepts", "states", "wow"));
        Assert.Equal(ObjectStoreCodes.Busy, JsonOut().GetProperty("code").GetString());
        Assert.Empty(composed);
    }
}
