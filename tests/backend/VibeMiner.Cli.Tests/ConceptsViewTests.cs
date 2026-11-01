using System.Text;
using VibeMiner.Builds;
using VibeMiner.Cli.Browse;
using VibeMiner.Concepts.Creatures;
using VibeMiner.Concepts.ItemEffects;
using VibeMiner.Concepts.Items;
using VibeMiner.Concepts.Testing;
using VibeMiner.Files;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Models;
using VibeMiner.Formats.Tables;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Cli.Tests;

/// <summary>The <c>:items</c> and <c>:creatures</c> views against the layer 6 fakes, through the navigator; no tables, no files.</summary>
public sealed class ConceptsViewTests : IAsyncDisposable
{
    private const string Data = "/data/vm";
    private static readonly string StoreFolder = Path.Combine(Data, ObjectStore.DefaultFolderName);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static readonly string A = new('a', 32), B = new('b', 32), C = new('c', 32);
    private static readonly BuildRef Build = new("wow", A, B, "eu");
    private const string Key = "ABCDEF0123456789";

    private readonly FakeStores stores = new();
    private readonly ManualTime time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly BrowseSession session;
    private readonly Navigator navigator;
    private InMemoryItems items = new();
    private readonly InMemoryItemEffects effects = new();
    private InMemoryCreatures creatures = new();
    private int leases, closed;

    public ConceptsViewTests()
    {
        stores.Source.Time = time;
        session = new BrowseSession(stores, new DataFolder(Data, DataFolderSource.Override), StoreFolder);
        navigator = new Navigator(ViewRegistry.Default(), session, a => a());
        items.Add(Build, "enUS", SeedItems());
        effects.Add(Build, "enUS", [new FakeItemEffects(1, Effects(20))]);
        creatures.Add(Build, "enUS", SeedCreatures(), Displays());
    }

    public async ValueTask DisposeAsync() => await navigator.DisposeAsync().AsTask().WaitAsync(Guard);

    private void Wire() => session.OpenConcepts = (_, _) =>
    {
        Interlocked.Increment(ref leases);
        return Task.FromResult(new ConceptsLease(items, effects, creatures, () => { Interlocked.Increment(ref closed); return ValueTask.CompletedTask; }));
    };

    private static ItemName Valid(string name) => new(ItemNameState.Valid, name);

    private static readonly ItemIcon HearthIcon =
        new(ItemIconState.Ready, 134414, BlpImageState.Ready, new BlpHeader(2, 8, 7, 1, 64, 64, 7), Blp2Encoding.Bc3, 7, ContentKey: C);

    private static FakeItem[] SeedItems() =>
    [
        new(1, Valid("Hearthstone"), [new("Item", "ClassID", 1, "15")], IconFileDataId: 134414, Icon: HearthIcon,
            Appearances: ConceptsFixtures.Appearances(), Icons: ConceptsFixtures.Icons(1, HearthIcon)),
        new(2, new ItemName(ItemNameState.Empty, ""), IconFileDataId: 0),
        new(3, new ItemName(ItemNameState.MissingSparse, null)),
        new(4, new ItemName(ItemNameState.SparseProtected, null), SparseKey: Key, IconFileDataId: 99,
            Icon: new ItemIcon(ItemIconState.Protected, 99, BlpImageState.Protected, HoleKeys: [Key])),
        new(5, Valid("   ")),
        new(6, new ItemName(ItemNameState.SparseUnknown, null), Item: ItemRowState.Unknown),
        new(7, Valid("Hearth candle"), [.. Enumerable.Range(0, 40).Select(i => new ItemSourceValue("Item", $"Cell{i}", i, $"{i}"))]),
    ];

    private static ItemEffectEntry[] Effects(int count) => [.. Enumerable.Range(0, count).Select(i => new ItemEffectEntry(
        new TableRow(i, new VibeMiner.Formats.Db2Row((uint)(100 + i), false, null, [$"{100 + i}"])), "1", $"{500 + i}",
        i == 0 ? new ItemEffect(ItemEffectState.NotFoundInAdmittedScope, Absence: ItemEffectsAbsence.UnknownOutsideReadableScope) : new ItemEffect(ItemEffectState.ObservedReady, SpellId: $"{8690 + i}"),
        i == 0 ? new ItemEffectName(ItemEffectNameState.EffectNotReady) : new ItemEffectName(ItemEffectNameState.Ready, Text: $"Spell {i}")))];

    private static FakeCreature[] SeedCreatures() =>
    [
        new(10, new CreatureName(CreatureNameState.Valid, "Hogger"), [new("Classification", 1, "1")],
            [new(0, 100, 1f, CreatureSlotState.Declared), new(1, 0, 0f, CreatureSlotState.NoDeclaredDisplay), new(2, -3, 0.5f, CreatureSlotState.InvalidDisplayReference),
             new(3, 200, 0.25f, CreatureSlotState.Declared)], ClassificationLabel: "Elite"),
        new(11, new CreatureName(CreatureNameState.Invalid, null, "DB2_TEXT_ENCODING_INVALID")),
        new(12, new CreatureName(CreatureNameState.Empty, "")),
        new(13, new CreatureName(CreatureNameState.Valid, "Locked"), Outcome: TableRowOutcome.Protected, ProtectedKey: Key),
    ];

    private static FakeCreatureDisplays Displays() => new(
        new Dictionary<uint, uint> { [100] = 7, [200] = 8 },
        new Dictionary<uint, uint> { [7] = 123456, [8] = 0 },
        new Dictionary<uint, CreatureModel>
        {
            [123456] = new(CreatureModelState.Read, 123456, ModelReadState.Read, new CreatureAppearance(CreatureAppearanceState.TextureUnresolved, "CREATURE_APPEARANCE_TEXTURE_UNRESOLVED", TextureOrdinal: 1),
                [.. Enumerable.Range(0, 35).Select(i => new CreatureTexture(i, i == 1 ? 11u : 0u, 0, "", i == 1 ? CreatureTextureSource.Variation : CreatureTextureSource.Txid,
                    i == 1 ? 0 : null, i == 1 ? 0u : (uint)(9000 + i), i == 1 ? CreatureTextureState.Unresolved : CreatureTextureState.Resolved, Drawn: i < 2,
                    Image: i == 0 ? BlpImageState.Ready : null))],
                MainSkinFileDataId: 123457, SkinState: ModelReadState.Read),
        });

    private const string CdnsHeader = "Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0|ConfigPath!STRING:0";

    private async Task ServeBuildAsync()
    {
        const string header = "Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16";
        stores.Source.AddReply("wow", "us", ObservationKind.Versions, Encoding.UTF8.GetBytes($"{header}\n## seqn = 1\neu|{A}|{B}|{C}|58123|11.0.7.58123|{C}\n"));
        await stores.Source.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), default);
        stores.Source.AddReply("wow", "us", ObservationKind.Cdns, Encoding.UTF8.GetBytes($"{CdnsHeader}\n## seqn = 1\neu|tpr/wow|eu.cdn.blizzard.com|x|tpr/configs/data\n"));
        await stores.Source.ObserveAsync(new(ObservationKind.Cdns, "wow", "us"), default);
        time.Advance(TimeSpan.FromMinutes(1));
    }

    private async Task<BrowseFrame> GoAsync(string name)
    {
        Assert.True(navigator.Go(name));
        await navigator.Loading.WaitAsync(Guard);
        Assert.Null(navigator.Current.Error);
        return navigator.Current;
    }

    private async Task<BrowseFrame> DrillAsync(int row)
    {
        Assert.True(navigator.Drill(row));
        await navigator.Loading.WaitAsync(Guard);
        Assert.Null(navigator.Current.Error);
        return navigator.Current;
    }

    private async Task BackAsync()
    {
        Assert.True(navigator.Back());
        await navigator.Loading.WaitAsync(Guard);
    }

    private async Task<BrowseFrame> KeyAsync(char key, string? answer = null)
    {
        Assert.True(navigator.TryAction(key));
        if (answer is not null) navigator.Answer(answer);
        await navigator.Loading.WaitAsync(Guard);
        Assert.Null(navigator.Current.Error);
        return navigator.Current;
    }

    private async Task<BrowseFrame> FilterAsync(string filter)
    {
        navigator.SetFilter(filter);
        await navigator.Loading.WaitAsync(Guard);
        Assert.Null(navigator.Current.Error);
        return navigator.Current;
    }

    private static List<string[]> Cells(BrowseFrame frame) => frame.Visible.Select(r => r.Cells.ToArray()).ToList();

    private async Task<BrowseFrame> OpenAsync(string view)
    {
        await ServeBuildAsync();
        Wire();
        var top = await GoAsync(view);
        Assert.Equal([["wow", "11.0.7.58123", "eu", A]], Cells(top));
        return await DrillAsync(0);
    }

    [Fact]
    public async Task WithoutTheSeamTheViewsSayLayer6IsntWired()
    {
        await ServeBuildAsync();
        var top = await GoAsync(":items");
        Assert.Equal("1 current builds. Enter lists a build's items (enUS). " + ConceptsAccess.NotWired, top.Content!.Note);
        var list = await DrillAsync(0);
        Assert.Equal("items > wow 11.0.7.58123 enUS", navigator.Path);
        Assert.Equal(ConceptsAccess.NotWired, list.Content!.Note);
    }

    [Fact]
    public async Task TheItemListNamesEveryStateAndClosesEachLease()
    {
        var list = await OpenAsync(":items");
        Assert.Equal(["Id", "Name", "Name state", "Item row", "Icon FDID", "Copy of"], list.Content!.Columns);
        Assert.Equal(
        [
            ["1", "Hearthstone", "Valid", "Found", "134414", ""],
            ["2", "(Empty)", "Empty", "Found", "0", ""],
            ["3", "(MissingSparse)", "MissingSparse", "Found", "(unavailable)", ""],
            ["4", "(SparseProtected)", "SparseProtected", "Found", "99", ""],
            ["5", "\"   \"", "Valid", "Found", "(unavailable)", ""],
            ["6", "(SparseUnknown)", "SparseUnknown", "Unknown", "(unavailable)", ""],
            ["7", "Hearth candle", "Valid", "Found", "(unavailable)", ""],
        ], Cells(list));
        Assert.Equal("Page 1: 7 items, 7 rows scanned (last). wow 11.0.7.58123 enUS.", list.Content.Note);
        // One lease for the session, kept until it closes.
        Assert.Equal((1, 0), (leases, closed));
        await session.DisposeAsync();
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task AShortPageThatContinuesSaysTheScanBudgetRanOutAndNPagesOn()
    {
        items = new InMemoryItems { MaxScanRows = 3 }.Add(Build, "enUS", SeedItems());
        var list = await OpenAsync(":items");
        var found = await FilterAsync("hearth");
        Assert.Equal([["1", "Hearthstone", "Valid", "Found", "134414", ""]], Cells(found));
        Assert.Equal("Page 1: 1 items, 3 rows scanned; scan budget spent, n scans on.", found.Content!.Note);
        var second = await KeyAsync('n');
        Assert.Empty(second.Visible);
        Assert.StartsWith("Page 2: 0 items, 3 rows scanned; scan budget spent, n scans on. p back.", second.Content!.Note);
        var third = await KeyAsync('n');
        Assert.Equal("7", Cells(third).Single()[0]);
        Assert.StartsWith("Page 3: 1 items, 1 rows scanned (last). p back.", third.Content!.Note);
        await KeyAsync('n');
        Assert.Equal("This is the last page.", navigator.Message);
        var back = await KeyAsync('p');
        Assert.StartsWith("Page 2:", back.Content!.Note);
        Assert.Equal(1, leases);
    }

    [Fact]
    public async Task IListsOneExactIdAndRefusesItBesideASearch()
    {
        await OpenAsync(":items");
        var one = await KeyAsync('i', "4");
        Assert.Equal("4", Cells(one).Single()[0]);
        Assert.Contains("ID 4.", one.Content!.Note);
        var none = await KeyAsync('i', "404");
        Assert.Empty(none.Visible);
        Assert.Equal("No item 404: Item is complete and has no such row.", none.Content!.Note);
        var both = await FilterAsync("hearth");
        Assert.Equal("ID 404 and a name search don't combine: Esc clears the search, i the ID.", both.Content!.Note);
        await FilterAsync("");
        var all = await KeyAsync('i', "");
        Assert.Equal(7, all.Visible.Count);
    }

    [Fact]
    public async Task TheItemDetailShowsIconFactsLabelsAndEffectsAPageAtATime()
    {
        await OpenAsync(":items");
        var detail = await DrillAsync(0);
        var text = detail.Content!.Text!;
        Assert.Contains("  Name state      Valid", text);
        Assert.Contains("  State           Ready", text);
        Assert.Contains("  FileDataID      134414", text);
        Assert.Contains($"  CKey            {C}", text);
        Assert.Contains("  Header          64 x 64, color encoding 2, alpha depth 8, alpha encoding 7, 7 declared mips", text);
        Assert.Contains("  Encoding        Bc3, 7 mips", text);
        Assert.Contains("Facts           Ready, ItemSparse Present", text);
        Assert.Contains("  Class           TableNotReadable, keys ClassID=15", text);
        Assert.Contains("Effects         link ItemXItemEffect, 1 page(s) read", text);
        Assert.Contains("  Sources         Bridge Ready, Effect Ready, Name Ready", text);
        Assert.Contains("  Page 1          Ok, 16 entries of 20 link rows, continues", text);
        Assert.Contains("    link 100: effect 500 NotFoundInAdmittedScope (UnknownOutsideReadableScope), spell -, name EffectNotReady", text);
        Assert.Contains("    link 101: effect 501 ObservedReady, spell 8691, name Ready \"Spell 1\"", text);
        Assert.DoesNotContain("link 116", text);
        Assert.Contains("More effects: n reads the next page.", detail.Content.Note);
        Assert.True(navigator.More());
        await navigator.Loading.WaitAsync(Guard);
        text = navigator.Current.Content!.Text!;
        Assert.Contains("  Page 2          Ok, 4 entries of 20 link rows", text);
        Assert.Contains("    link 119: effect 519 ObservedReady", text);
        Assert.False(navigator.Current.View.HasMore);
        Assert.Equal(1, leases);
    }

    [Fact]
    public async Task AnUnknownItemSparseRowIsNeverShownAsAbsent()
    {
        items = new InMemoryItems().Add(Build, "enUS", [new FakeItem(8, new ItemName(ItemNameState.SparseUnknown, null))]);
        await OpenAsync(":items");
        var text = (await DrillAsync(0)).Content!.Text!;
        Assert.Contains("  ItemSparse      Unknown: not in ItemSparse's readable rows, not proof it's absent", text);
        Assert.Contains("Facts           Ready, ItemSparse Unknown", text);
    }

    [Fact]
    public async Task DetailsAndEffectPagesOnOneBuildReuseItsHandles()
    {
        // Review of #204: each detail and each effects page opened new layer 6 handles, so the relation indexes (D471) were
        // built again with full scans. The session keeps the build's handles: the list, two details and a next effects page open
        // items and effects once.
        await OpenAsync(":items");
        await DrillAsync(0);
        Assert.True(navigator.More());
        await navigator.Loading.WaitAsync(Guard);
        Assert.Contains("  Page 2          Ok", navigator.Current.Content!.Text);
        await BackAsync();
        await DrillAsync(6);
        Assert.Equal(1, items.Calls(nameof(IItems.ForAsync)));
        Assert.Equal(1, effects.Calls(nameof(IItemEffects.ForAsync)));
        // Detail, its reload for page 2, the second detail; effects pages 1, then 1 and 2, then the second item's.
        Assert.Equal(3, items.Calls(nameof(IBuildItems.GetAsync)));
        Assert.Equal(4, effects.Calls(nameof(IBuildItemEffects.GetAsync)));
        Assert.Equal(1, leases);
        Assert.Equal([new FilesTarget(Build, "enUS")], session.Concepts.Kept);
    }

    [Fact]
    public async Task TheItemDetailShowsEverySourcedIconAndTheAppearanceChain()
    {
        await OpenAsync(":items");
        var text = (await DrillAsync(0)).Content!.Text!;
        Assert.Contains("Icon from Item.IconFileDataID of row 1", text);
        Assert.Contains("Icon from ItemAppearance.DefaultIconFileDataID of row 31", text);
        Assert.Contains("  FileDataID      777", text);
        Assert.Contains("  File state      NeedsNetwork", text);
        Assert.Contains("Appearances     Complete, default Default (row #1), 1 of 1 rows", text);
        Assert.Contains("    #1 row 900: modifier 0, order 0, ItemAppearanceID 31", text);
        Assert.Contains("      ItemAppearance Found, DefaultIconFileDataID 777, ItemDisplayInfoID 41", text);
        Assert.Contains("      ItemDisplayInfo 41 Found", text);
        Assert.Contains("        Slot 0 Model 51: Found, 1 of 1 files", text);
        Assert.Contains("          FDID 5550: component Found race 1 gender 0 class 0 position 2", text);
        Assert.Contains("        Slot 0 Material 61: Unknown, not complete, 0 of 0 files, hidden keys ABCDEF0123456789", text);
        Assert.Contains("        Slot 1 Model 0: NoResource, 0 of 0 files", text);
        Assert.Contains("  Models          Capped, 2 distinct ITEM_MODEL_CAP", text);
        Assert.Contains("    FDID 5550: Read, read Read, 1 skins, 1 textures", text);
        Assert.Contains("      #0 type 1 FDID 5551 Found", text);
        Assert.Contains("    FDID 5560: NotRead", text);
    }

    [Fact]
    public async Task ADetailNamesAProtectedIconAnUnknownRowAndCapsLongLists()
    {
        await OpenAsync(":items");
        var locked = await DrillAsync(3);
        var text = locked.Content!.Text!;
        Assert.Contains("  Name            (SparseProtected)", text);
        Assert.Contains($"  Name state      SparseProtected (key {Key})", text);
        Assert.Contains("  State           Protected", text);
        Assert.Contains($"  Keys needed     {Key}", text);
        // Review of #204: a missing ItemSparse row read "(no row)" for every state, so Protected and Unknown looked like absence.
        Assert.Contains($"  ItemSparse      Protected: in a keyed section (key {Key})", text);
        Assert.DoesNotContain("(no row)", text);
        Assert.Contains("Effects         link (none), 1 page(s) read", text);
        Assert.Contains("  Page 1          MissingItem, 0 entries of 0 link rows", text);
        Assert.Contains("Appearances     TableNotReadable, default Unknown, 0 of 0 rows", text);
        Assert.Contains("  Table           ItemModifiedAppearance (0) NoDefinition", text);
        navigator.Back();
        await navigator.Loading.WaitAsync(Guard);
        var missing = await DrillAsync(2);
        Assert.Contains("  ItemSparse      Missing: ItemSparse is complete and has no row", missing.Content!.Text);
        navigator.Back();
        await navigator.Loading.WaitAsync(Guard);
        var unknown = await DrillAsync(5);
        Assert.Contains("Result          Unknown", unknown.Content!.Text);
        Assert.Equal("Item 6: Unknown.", unknown.Content.Note);
        navigator.Back();
        await navigator.Loading.WaitAsync(Guard);
        var many = await DrillAsync(6);
        Assert.Contains("  Value           Item.Cell31 [31] = 31", many.Content!.Text);
        Assert.DoesNotContain("Cell32", many.Content.Text);
        Assert.Contains("    +8 more", many.Content.Text);
    }

    [Fact]
    public async Task TheCreatureListAndDetailShowSlotsChainsAndTheModel()
    {
        var list = await OpenAsync(":creatures");
        Assert.Equal(["Id", "Name", "Name state", "Displays", "Copy of"], list.Content!.Columns);
        Assert.Equal(
        [
            ["10", "Hogger", "Valid", "100 - (Invalid -3) 200", ""],
            ["11", "(Invalid)", "Invalid", "- - - -", ""],
            ["12", "(Empty)", "Empty", "- - - -", ""],
        ], Cells(list));
        var detail = await DrillAsync(0);
        var text = detail.Content!.Text!;
        Assert.Contains("  Classification  Elite", text);
        Assert.Contains("  Slot 0          DisplayID 100, probability 1, Declared", text);
        Assert.Contains("  Slot 2          DisplayID -3, probability 0.5, InvalidDisplayReference", text);
        Assert.Contains("  Slot 0          MetadataResolved", text);
        Assert.Contains("    ModelID         7", text);
        Assert.Contains("    Model FDID      123456", text);
        Assert.Contains("    Model           Read, FileDataID 123456, read Read", text);
        Assert.Contains("      Main skin       123457, Read", text);
        Assert.Contains("      Appearance      TextureUnresolved CREATURE_APPEARANCE_TEXTURE_UNRESOLVED, texture #1", text);
        Assert.Contains("      Textures        35 declared, 2 drawn", text);
        Assert.Contains("        #0 type 0 flags 0 Txid FDID 9000 Resolved, drawn yes, image Ready", text);
        Assert.Contains("        #1 type 11 flags 0 Variation[0] FDID 0 Unresolved, drawn yes", text);
        Assert.Contains("        +3 more", text);
        Assert.Contains("  Slot 1          NoDeclaredDisplay", text);
        Assert.Contains("  Slot 2          InvalidDisplayReference", text);
        Assert.Contains("  Slot 3          NoDeclaredModelFile", text);
        Assert.Contains("    Model FDID      0", text);
        Assert.Equal(1, leases);
    }

    [Fact]
    public async Task ACreatureExactIdThatIsProtectedIsNeverAbsent()
    {
        await OpenAsync(":creatures");
        var locked = await KeyAsync('i', "13");
        Assert.Empty(locked.Visible);
        Assert.Equal($"Creature 13 is Protected (key {Key}): not proof it's absent.", locked.Content!.Note);
        var absent = await KeyAsync('i', "99");
        Assert.Equal("No creature 99: Creature is complete and has no such row.", absent.Content!.Note);
        await KeyAsync('i', "x");
        Assert.Equal("Not an ID: x", navigator.Message);
    }

    [Fact]
    public async Task ABuildThatDoesntOpenSaysSo()
    {
        items = new InMemoryItems().AddFailing(Build, FilesOpenState.NeedsNetwork, "BUILD_NEEDS_NETWORK");
        var list = await OpenAsync(":items");
        Assert.Empty(list.Visible);
        Assert.StartsWith("Can't open the build's files for enUS: NeedsNetwork (BUILD_NEEDS_NETWORK)", list.Content!.Note);
    }

    [Fact]
    public async Task ANotReadableTableShowsItsStateRows()
    {
        creatures = new InMemoryCreatures();
        var broken = new ScriptedCreatures(new CreaturesPage(CreaturesPageState.CreatureNotReadable, [], null, 0,
            new TableEntry(42, "Creature", TableState.Encrypted, true, new TableStateReason("TABLE_ENCRYPTED", "Every section is keyed.", null, null, null, [Key]), null, null)));
        await ServeBuildAsync();
        session.OpenConcepts = (_, _) => Task.FromResult(new ConceptsLease(items, effects, broken));
        await GoAsync(":creatures");
        var list = await DrillAsync(0);
        Assert.Equal("CreatureNotReadable: Creature is Encrypted (TABLE_ENCRYPTED)", list.Content!.Note);
        Assert.Contains(["State", "Encrypted"], Cells(list));
        Assert.Contains(["Keys needed", Key], Cells(list));
    }

    /// <summary>Creatures that answer every list with one page.</summary>
    private sealed class ScriptedCreatures(CreaturesPage page) : ICreatures, IBuildCreatures
    {
        public BuildRef Build => ConceptsViewTests.Build;
        public string Locale => "enUS";
        public Task<CreaturesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken) =>
            Task.FromResult(new CreaturesOpenResult(build, locale, FilesOpenState.Open, this, null, null, null));
        public Task<CreaturesPage> ListAsync(CreatureQuery query, TablePriority priority, CancellationToken cancellationToken) => Task.FromResult(page);
        public Task<CreatureResult> GetAsync(uint id, TablePriority priority, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
