using VibeMiner.Builds;
using VibeMiner.Concepts.Items;
using VibeMiner.Concepts.Testing;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Images;
using VibeMiner.Formats.Tables;
using Xunit;
using static VibeMiner.Concepts.Tests.Items.ItemsWorld;

namespace VibeMiner.Concepts.Tests.Items;

/// <summary>
/// One item of a contract scenario. <see cref="Name"/> null means no ItemSparse row (MissingSparse). <see cref="Icon"/> null means
/// an IconFileDataID out of uint32 range (no field). <see cref="IconSide"/> stores a BLP of that square size under the icon's ID;
/// without it a nonzero icon is a file the build doesn't list.
/// </summary>
public sealed record ScenarioItem(uint Id, string? Name, uint? Icon = 0, int? IconSide = null);

/// <summary>A build's items described neutrally, so each side of <see cref="ItemsContract"/> builds it its own way. <see cref="SparseOnly"/> are ItemSparse rows with no Item row.</summary>
public sealed record ItemsScenario(IReadOnlyList<ScenarioItem> Items, IReadOnlyList<(uint Id, string Name)> SparseOnly);

/// <summary>One behaviour suite for <see cref="IItems"/>: the real service over layer 5 and 4 fakes, and the layer 6 fake. Synthetic data only.</summary>
public abstract class ItemsContract
{
    private const TablePriority Now = TablePriority.Interactive;
    public static readonly BuildRef Other = new("wow", new string('c', 32), new string('d', 32), "us");

    protected abstract Task<IItems> CreateAsync(ItemsScenario scenario);

    public static ItemsScenario Scenario() => new(
    [
        new(10, "Sword of Dawn", 0),
        new(20, "  ", 500, 8),
        new(30, null, 600),
        new(40, "", null),
        new(50, "dawn shield", 500, 8),
        new(60, "Ring", 700, 16),
    ], [(35, "Sparse dawn only")]);

    /// <summary>The summary cells every scenario item has: Item ClassID, SubclassID, InventoryType; ItemSparse's three when it has a row.</summary>
    public static IReadOnlyList<ItemSourceValue> Values(bool sparse) =>
    [
        new("Item", "ClassID", 1, "2"), new("Item", "SubclassID", 2, "7"), new("Item", "InventoryType", 3, "13"),
        .. sparse ? new ItemSourceValue[] { new("ItemSparse", "OverallQualityID", 3, "42"), new("ItemSparse", "ItemLevel", 4, "500"),
            new("ItemSparse", "InventoryType", 5, "13") } : [],
    ];

    private async Task<IBuildItems> OpenAsync()
    {
        var opened = await (await CreateAsync(Scenario())).ForAsync(Build, Locale, default);
        Assert.Equal(FilesOpenState.Open, opened.State);
        Assert.Equal(Build, opened.Items!.Build);
        Assert.Equal(Locale, opened.Items.Locale);
        return opened.Items;
    }

    [Fact]
    public async Task A_build_that_isnt_stored_needs_the_network_and_a_bad_locale_throws()
    {
        var items = await CreateAsync(Scenario());
        var other = await items.ForAsync(Other, Locale, default);
        Assert.Equal(FilesOpenState.NeedsNetwork, other.State);
        Assert.Null(other.Items);
        await Assert.ThrowsAsync<ArgumentException>(() => items.ForAsync(Build, "xxXX", default));
    }

    [Fact]
    public async Task An_empty_query_lists_every_item_with_its_name_state_cells_and_icon_and_never_a_sparse_only_row()
    {
        await using var items = await OpenAsync();
        var all = await AllAsync(items, new());
        Assert.Equal([10u, 20, 30, 40, 50, 60], all.Select(item => item.Id));
        Assert.Equal([ItemNameState.Valid, ItemNameState.Valid, ItemNameState.MissingSparse, ItemNameState.Empty, ItemNameState.Valid, ItemNameState.Valid],
            all.Select(item => item.Name.State));
        Assert.Equal(["Sword of Dawn", "  ", null, "", "dawn shield", "Ring"], all.Select(item => item.Name.Value));
        Assert.Equal([0u, 500, 600, null, 500, 700], all.Select(item => item.IconFileDataId));
        Assert.Equal(Values(true), all[0].Values);
        Assert.Equal(Values(false), all[2].Values);
    }

    [Theory]
    [InlineData("DAWN", new uint[] { 10, 50 })]
    [InlineData("  ", new uint[] { 20 })]
    [InlineData("ring", new uint[] { 60 })]
    [InlineData("nothing", new uint[0])]
    public async Task A_name_query_is_a_literal_ignore_case_substring_of_valid_names(string query, uint[] expected)
    {
        await using var items = await OpenAsync();
        Assert.Equal(expected, (await AllAsync(items, new(query))).Select(item => item.Id));
    }

    [Fact]
    public async Task An_exact_id_lists_that_item_only_and_cant_take_a_query()
    {
        await using var items = await OpenAsync();
        Assert.Equal([30u], (await items.ListAsync(new(ExactId: 30), Now, default)).Entries.Select(item => item.Id));
        Assert.Empty((await items.ListAsync(new(ExactId: 35), Now, default)).Entries);
        Assert.Empty((await items.ListAsync(new(ExactId: 99), Now, default)).Entries);
        await Assert.ThrowsAsync<ArgumentException>(() => items.ListAsync(new("Ring", ExactId: 60), Now, default));
    }

    [Fact]
    public async Task Pages_follow_the_cursor_to_the_end_and_a_cursor_only_fits_its_own_request()
    {
        await using var items = await OpenAsync();
        var first = await items.ListAsync(new(Limit: 4), Now, default);
        Assert.Equal([10u, 20, 30, 40], first.Entries.Select(item => item.Id));
        Assert.NotNull(first.Next);
        var second = await items.ListAsync(new(Limit: 4, After: first.Next), Now, default);
        Assert.Equal([50u, 60], second.Entries.Select(item => item.Id));
        Assert.Null(second.Next);
        Assert.Equal(ItemsPageState.InvalidCursor, (await items.ListAsync(new(Limit: 3, After: first.Next), Now, default)).State);
        Assert.Equal(ItemsPageState.InvalidCursor, (await items.ListAsync(new("dawn", Limit: 4, After: first.Next), Now, default)).State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public async Task A_limit_outside_1_to_50_throws(int limit)
    {
        await using var items = await OpenAsync();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => items.ListAsync(new(Limit: limit), Now, default));
    }

    [Fact]
    public async Task A_bad_query_or_cursor_framing_throws()
    {
        await using var items = await OpenAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => items.ListAsync(new("bad\0name"), Now, default));
        await Assert.ThrowsAsync<ArgumentException>(() => items.ListAsync(new(After: "e30="), Now, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => items.ListAsync(null!, Now, default));
    }

    [Fact]
    public async Task A_detail_has_the_summary_rows_and_icon_state()
    {
        await using var items = await OpenAsync();
        var ready = await items.GetAsync(20, Now, default);
        Assert.Equal(ItemResultState.Found, ready.State);
        Assert.Equal(20u, ready.Detail!.Summary.Id);
        Assert.Equal(20u, ready.Detail.ItemRow.Row.Id);
        Assert.NotNull(ready.Detail.SparseRow);
        Assert.Equal(ItemIconState.Ready, ready.Detail.Icon.State);
        Assert.Equal(BlpImageState.Ready, ready.Detail.Icon.Image);
        Assert.Equal((8u, 8u), (ready.Detail.Icon.Header!.Width, ready.Detail.Icon.Header.Height));
        Assert.Equal((Blp2Encoding.Bgra8, 1), (ready.Detail.Icon.Encoding, ready.Detail.Icon.MipCount));
        Assert.Equal(ItemIconState.NoIcon, (await items.GetAsync(10, Now, default)).Detail!.Icon.State);
        Assert.Equal(ItemIconState.FieldUnavailable, (await items.GetAsync(40, Now, default)).Detail!.Icon.State);
        var missing = (await items.GetAsync(30, Now, default)).Detail!;
        Assert.Null(missing.SparseRow);
        Assert.Equal((ItemIconState.FileFailed, FileReadState.UnknownFile), (missing.Icon.State, missing.Icon.FileState));
        Assert.Equal(16u, (await items.GetAsync(60, Now, default)).Detail!.Icon.Header!.Width);
    }

    [Fact]
    public async Task An_id_no_item_row_has_is_absent_even_with_a_sparse_row()
    {
        await using var items = await OpenAsync();
        Assert.Equal(ItemResultState.Absent, (await items.GetAsync(35, Now, default)).State);
        Assert.Equal(ItemResultState.Absent, (await items.GetAsync(99, Now, default)).State);
    }

    [Fact]
    public async Task A_disposed_handle_throws_and_a_canceled_call_throws()
    {
        var items = await OpenAsync();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => items.ListAsync(new(), Now, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => items.GetAsync(10, Now, canceled.Token));
        await items.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => items.ListAsync(new(), Now, default));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => items.GetAsync(10, Now, default));
    }
}

/// <summary>The <see cref="ItemsContract"/> against the real <see cref="ItemsService"/>.</summary>
public sealed class ItemsServiceContractTests : ItemsContract
{
    protected override Task<IItems> CreateAsync(ItemsScenario scenario)
    {
        var world = new ItemsWorld();
        var sparse = scenario.Items.Where(item => item.Name is not null).Select(item => Sparse(item.Id, item.Name!))
            .Concat(scenario.SparseOnly.Select(row => Sparse(row.Id, row.Name))).OrderBy(row => row.Id);
        world.With(ItemTable([.. scenario.Items.Select(item => Item(item.Id, icon: item.Icon is { } icon ? N(icon) : "-1"))]), SparseTable([.. sparse]));
        world.WithFiles([.. scenario.Items.Where(item => item.IconSide is not null).DistinctBy(item => item.Icon)
            .Select(item => Icon(item.Icon!.Value, item.IconSide!.Value, item.IconSide.Value))]);
        return Task.FromResult<IItems>(world.Service);
    }
}

/// <summary>The <see cref="ItemsContract"/> against <see cref="InMemoryItems"/>.</summary>
public sealed class InMemoryItemsContractTests : ItemsContract
{
    protected override Task<IItems> CreateAsync(ItemsScenario scenario)
    {
        var items = scenario.Items.Select(item => new FakeItem(item.Id,
            item.Name is null ? new(ItemNameState.MissingSparse, null) : item.Name.Length == 0 ? new(ItemNameState.Empty, "") : new(ItemNameState.Valid, item.Name),
            Values(item.Name is not null), item.Icon,
            item.IconSide is { } side ? new ItemIcon(ItemIconState.Ready, item.Icon, BlpImageState.Ready, new BlpHeader(3, 8, 0, 0, (uint)side, (uint)side, 1),
                Blp2Encoding.Bgra8, 1, HoleKeys: []) : null));
        return Task.FromResult<IItems>(new InMemoryItems().Add(Build, Locale, items));
    }
}
