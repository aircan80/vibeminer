using VibeMiner.Builds;
using VibeMiner.Concepts.Creatures;
using VibeMiner.Concepts.Testing;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Models;
using VibeMiner.Formats.Tables;
using Xunit;
using static VibeMiner.Concepts.Tests.Creatures.CreaturesWorld;

namespace VibeMiner.Concepts.Tests.Creatures;

/// <summary>One creature of a contract scenario. <see cref="Name"/> null is a Name_lang with a text issue (Invalid).</summary>
public sealed record ScenarioCreature(uint Id, string? Name, int[] Displays);

/// <summary>
/// A build's creatures described neutrally, so each side of <see cref="CreaturesContract"/> builds it its own way:
/// CreatureDisplayInfo ID to ModelID, CreatureModelData ID to FileDataID, and the model FileDataIDs the build stores.
/// </summary>
public sealed record CreaturesScenario(IReadOnlyList<ScenarioCreature> Creatures, IReadOnlyDictionary<uint, uint> Displays,
    IReadOnlyDictionary<uint, uint> Models, IReadOnlyList<uint> StoredModels);

/// <summary>One behaviour suite for <see cref="ICreatures"/>: the real service over layer 5 and 4 fakes, and the layer 6 fake. Synthetic data only.</summary>
public abstract class CreaturesContract
{
    private const TablePriority Now = TablePriority.Interactive;
    public static readonly BuildRef Other = new("wow", new string('c', 32), new string('d', 32), "us");

    protected abstract Task<ICreatures> CreateAsync(CreaturesScenario scenario);

    public static CreaturesScenario Scenario() => new(
    [
        new(10, "Hogger", [100, 100, 0, -1]),
        new(20, "  ", [200, 999, 0, 0]),
        new(30, "", [300, 0, 0, 0]),
        new(40, null, [0, 0, 0, 0]),
        new(50, "hog rider", [400, 0, 0, 0]),
        new(60, "Wolf", [500, 0, 0, 0]),
    ],
    new Dictionary<uint, uint> { [100] = 33, [200] = 0, [300] = 44, [400] = 55, [500] = 66 },
    new Dictionary<uint, uint> { [33] = 7001, [55] = 0, [66] = 9001 },
    [7001]);

    public static CreatureSlotState SlotState(int display) => display switch
    {
        > 0 => CreatureSlotState.Declared,
        0 => CreatureSlotState.NoDeclaredDisplay,
        _ => CreatureSlotState.InvalidDisplayReference,
    };

    private async Task<IBuildCreatures> OpenAsync()
    {
        var opened = await (await CreateAsync(Scenario())).ForAsync(Build, Locale, default);
        Assert.Equal(FilesOpenState.Open, opened.State);
        Assert.Equal(Build, opened.Creatures!.Build);
        Assert.Equal(Locale, opened.Creatures.Locale);
        return opened.Creatures;
    }

    [Fact]
    public async Task A_build_that_isnt_stored_needs_the_network_and_a_bad_locale_throws()
    {
        var creatures = await CreateAsync(Scenario());
        var other = await creatures.ForAsync(Other, Locale, default);
        Assert.Equal(FilesOpenState.NeedsNetwork, other.State);
        Assert.Null(other.Creatures);
        await Assert.ThrowsAsync<ArgumentException>(() => creatures.ForAsync(Build, "xxXX", default));
    }

    [Fact]
    public async Task An_empty_query_lists_every_creature_with_its_name_state_and_slots()
    {
        await using var creatures = await OpenAsync();
        var all = await AllAsync(creatures, new());
        Assert.Equal([10u, 20, 30, 40, 50, 60], all.Select(creature => creature.Id));
        Assert.Equal([CreatureNameState.Valid, CreatureNameState.Valid, CreatureNameState.Empty, CreatureNameState.Invalid, CreatureNameState.Valid, CreatureNameState.Valid],
            all.Select(creature => creature.Name.State));
        Assert.Equal(["Hogger", "  ", "", null, "hog rider", "Wolf"], all.Select(creature => creature.Name.Value));
        Assert.Equal([100, 100, 0, -1], all[0].Slots.Select(slot => slot.DisplayId));
        Assert.Equal([CreatureSlotState.Declared, CreatureSlotState.Declared, CreatureSlotState.NoDeclaredDisplay, CreatureSlotState.InvalidDisplayReference],
            all[0].Slots.Select(slot => slot.State));
    }

    [Theory]
    [InlineData("HOG", new uint[] { 10, 50 })]
    [InlineData("  ", new uint[] { 20 })]
    [InlineData("wolf", new uint[] { 60 })]
    [InlineData("nothing", new uint[0])]
    public async Task A_name_query_is_a_literal_ignore_case_substring_of_valid_names(string query, uint[] expected)
    {
        await using var creatures = await OpenAsync();
        Assert.Equal(expected, (await AllAsync(creatures, new(query))).Select(creature => creature.Id));
    }

    [Fact]
    public async Task An_exact_id_lists_that_creature_only_and_cant_take_a_query()
    {
        await using var creatures = await OpenAsync();
        var found = await creatures.ListAsync(new(ExactId: 40), Now, default);
        Assert.Equal([40u], found.Entries.Select(creature => creature.Id));
        Assert.Equal(TableRowOutcome.Found, found.Exact);
        var absent = await creatures.ListAsync(new(ExactId: 99), Now, default);
        Assert.Equal((0, TableRowOutcome.Absent), (absent.Entries.Count, absent.Exact));
        await Assert.ThrowsAsync<ArgumentException>(() => creatures.ListAsync(new("Wolf", ExactId: 60), Now, default));
    }

    [Fact]
    public async Task Pages_follow_the_cursor_to_the_end_and_a_cursor_only_fits_its_own_request()
    {
        await using var creatures = await OpenAsync();
        var first = await creatures.ListAsync(new(Limit: 4), Now, default);
        Assert.Equal([10u, 20, 30, 40], first.Entries.Select(creature => creature.Id));
        Assert.NotNull(first.Next);
        var second = await creatures.ListAsync(new(Limit: 4, After: first.Next), Now, default);
        Assert.Equal([50u, 60], second.Entries.Select(creature => creature.Id));
        Assert.Null(second.Next);
        Assert.Equal(CreaturesPageState.InvalidCursor, (await creatures.ListAsync(new(Limit: 3, After: first.Next), Now, default)).State);
        Assert.Equal(CreaturesPageState.InvalidCursor, (await creatures.ListAsync(new("hog", Limit: 4, After: first.Next), Now, default)).State);
        Assert.Equal(CreaturesPageState.InvalidCursor, (await creatures.ListAsync(new(ExactId: 10, Limit: 4, After: first.Next), Now, default)).State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task A_limit_outside_1_to_50_throws(int limit)
    {
        await using var creatures = await OpenAsync();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => creatures.ListAsync(new(Limit: limit), Now, default));
    }

    [Fact]
    public async Task A_bad_query_or_cursor_framing_throws()
    {
        await using var creatures = await OpenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => creatures.ListAsync(new("bad\0name"), Now, default));
        await Assert.ThrowsAsync<ArgumentException>(() => creatures.ListAsync(new(After: "e30="), Now, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => creatures.ListAsync(null!, Now, default));
    }

    [Fact]
    public async Task A_detail_follows_each_slot_through_display_and_model_data_to_the_model()
    {
        await using var creatures = await OpenAsync();
        var hogger = await creatures.GetAsync(10, Now, default);
        Assert.Equal(CreatureResultState.Found, hogger.State);
        Assert.Equal(10u, hogger.Detail!.Row.Row.Id);
        var chains = hogger.Detail.Displays;
        Assert.Equal([CreatureDisplayChainState.MetadataResolved, CreatureDisplayChainState.MetadataResolved, CreatureDisplayChainState.NoDeclaredDisplay,
            CreatureDisplayChainState.InvalidDisplayReference], chains.Select(chain => chain.State));
        Assert.Equal((33u, 7001u), (chains[0].ModelId!.Value, chains[0].ModelFileDataId!.Value));
        Assert.Equal((CreatureModelState.Read, ModelReadState.Read), (chains[0].Model!.State, chains[0].Model!.ReadState));

        Assert.Equal([CreatureDisplayChainState.NoDeclaredModel, CreatureDisplayChainState.DisplayAbsent],
            (await creatures.GetAsync(20, Now, default)).Detail!.Displays.Take(2).Select(chain => chain.State));
        Assert.Equal(CreatureDisplayChainState.ModelAbsent, (await creatures.GetAsync(30, Now, default)).Detail!.Displays[0].State);
        var noFile = (await creatures.GetAsync(50, Now, default)).Detail!.Displays[0];
        Assert.Equal((CreatureDisplayChainState.NoDeclaredModelFile, 0u), (noFile.State, noFile.ModelFileDataId!.Value));
        Assert.Null(noFile.Model);
        var unlisted = (await creatures.GetAsync(60, Now, default)).Detail!.Displays[0];
        Assert.Equal(CreatureDisplayChainState.MetadataResolved, unlisted.State);
        Assert.Equal((CreatureModelState.ReadFailed, ModelReadState.UnknownFile), (unlisted.Model!.State, unlisted.Model.ReadState));
        Assert.Equal(CreatureAppearanceState.ModelNotRead, unlisted.Model.Appearance.State);
    }

    [Fact]
    public async Task An_id_no_row_has_is_absent()
    {
        await using var creatures = await OpenAsync();
        Assert.Equal(CreatureResultState.Absent, (await creatures.GetAsync(99, Now, default)).State);
    }

    [Fact]
    public async Task A_disposed_handle_throws_and_a_canceled_call_throws()
    {
        var creatures = await OpenAsync();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creatures.ListAsync(new(), Now, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => creatures.GetAsync(10, Now, canceled.Token));
        await creatures.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => creatures.ListAsync(new(), Now, default));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => creatures.GetAsync(10, Now, default));
    }
}

/// <summary>The <see cref="CreaturesContract"/> against the real <see cref="CreaturesService"/>.</summary>
public sealed class CreaturesServiceContractTests : CreaturesContract
{
    protected override Task<ICreatures> CreateAsync(CreaturesScenario scenario)
    {
        var world = new CreaturesWorld();
        var rows = scenario.Creatures.Select(creature => creature.Name is null
            ? Creature(creature.Id, "", creature.Displays, issues: [new(1, "DB2_TEXT_ENCODING_INVALID", "ff")])
            : Creature(creature.Id, creature.Name, creature.Displays));
        world.With(CreatureTable([.. rows]), DisplayTable([.. scenario.Displays.OrderBy(pair => pair.Key).Select(pair => Display(pair.Key, N(pair.Value)))]),
            ModelTable([.. scenario.Models.OrderBy(pair => pair.Key).Select(pair => Model(pair.Key, N(pair.Value)))]));
        world.WithFiles([.. scenario.StoredModels.Select(id => Stored(id, M2()))]);
        return Task.FromResult<ICreatures>(world.Service);
    }
}

/// <summary>The <see cref="CreaturesContract"/> against <see cref="InMemoryCreatures"/>.</summary>
public sealed class InMemoryCreaturesContractTests : CreaturesContract
{
    protected override Task<ICreatures> CreateAsync(CreaturesScenario scenario)
    {
        var creatures = scenario.Creatures.Select(creature => new FakeCreature(creature.Id,
            creature.Name is null ? new(CreatureNameState.Invalid, null, "DB2_TEXT_ENCODING_INVALID")
                : creature.Name.Length == 0 ? new(CreatureNameState.Empty, "") : new(CreatureNameState.Valid, creature.Name),
            Slots: [.. creature.Displays.Select((display, slot) => new CreatureDisplaySlot(slot, display, slot == 0 ? 1 : 0, SlotState(display)))]));
        var files = scenario.StoredModels.ToDictionary(id => id, id => new CreatureModel(CreatureModelState.Read, id, ModelReadState.Read,
            new(CreatureAppearanceState.Ready), []));
        return Task.FromResult<ICreatures>(new InMemoryCreatures().Add(Build, Locale, creatures, new(scenario.Displays, scenario.Models, files)));
    }
}
