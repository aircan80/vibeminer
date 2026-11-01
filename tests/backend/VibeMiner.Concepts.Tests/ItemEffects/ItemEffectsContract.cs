using VibeMiner.Builds;
using VibeMiner.Concepts.ItemEffects;
using VibeMiner.Concepts.Testing;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Tables;
using Xunit;
using static VibeMiner.Concepts.Tests.ItemEffects.EffectsWorld;

namespace VibeMiner.Concepts.Tests.ItemEffects;

/// <summary>
/// One behaviour suite for <see cref="IItemEffects"/>: the real service over the layer 5 fake, and the layer 6 fake. The scenario
/// is <see cref="EffectsWorld.Retail"/>: item 1 with links 500, 501 (effect 0), 502 (effect -1) and 503 (copy of 500); item 2
/// with link 504 (effect 7); item 0 with none; item 2147483648 exists. Synthetic data only.
/// </summary>
public abstract class ItemEffectsContract
{
    public static readonly BuildRef Other = new("wow", new string('c', 32), new string('d', 32), "us");
    public const uint High = 2147483648;

    protected abstract Task<IItemEffects> CreateAsync();

    private async Task<IBuildItemEffects> OpenAsync()
    {
        var opened = await (await CreateAsync()).ForAsync(Build, Locale, default);
        Assert.Equal(FilesOpenState.Open, opened.State);
        Assert.Equal((Build, Locale), (opened.Effects!.Build, opened.Effects.Locale));
        return opened.Effects;
    }

    [Fact]
    public async Task A_build_that_isnt_stored_needs_the_network_and_a_bad_locale_throws()
    {
        var effects = await CreateAsync();
        var other = await effects.ForAsync(Other, Locale, default);
        Assert.Equal(FilesOpenState.NeedsNetwork, other.State);
        Assert.Null(other.Effects);
        await Assert.ThrowsAsync<ArgumentException>(() => effects.ForAsync(Build, "xxXX", default));
    }

    [Fact]
    public async Task Pages_list_every_link_in_link_order_with_effects_and_names()
    {
        await using var effects = await OpenAsync();
        var all = await AllAsync(effects, 1, 3);
        Assert.Equal([500u, 501, 502, 503], all.Select(entry => entry.Link.Row.Id));
        Assert.Equal(["0", "0", "-1", "0"], all.Select(entry => entry.ItemEffectId));
        Assert.Equal([ItemEffectState.ObservedReady, ItemEffectState.ObservedReady, ItemEffectState.EffectKeyOutOfRange, ItemEffectState.ObservedReady],
            all.Select(entry => entry.Effect.State));
        Assert.Equal([" ", " ", null, " "], all.Select(entry => entry.Name.Text));
        var other = await effects.GetAsync(2, new(), Now, default);
        Assert.Equal((ItemEffectsPageState.Ok, ItemEffectsLink.ItemXItemEffect), (other.State, other.Link));
        Assert.Equal("Seven", Assert.Single(other.Entries).Name.Text);
    }

    [Fact]
    public async Task An_item_with_no_links_is_an_empty_ok_page_and_needs_no_effect_or_name()
    {
        await using var effects = await OpenAsync();
        var page = await effects.GetAsync(0, new(), Now, default);
        Assert.Equal(ItemEffectsPageState.Ok, page.State);
        Assert.Empty(page.Entries);
        Assert.Null(page.Next);
        Assert.Equal((ItemEffectsSourceState.Ready, ItemEffectsSourceState.NotChecked, ItemEffectsSourceState.NotChecked),
            (page.Bridge.State, page.Effect.State, page.Name.State));
    }

    [Fact]
    public async Task A_missing_item_and_an_item_above_int32_read_no_links()
    {
        await using var effects = await OpenAsync();
        var missing = await effects.GetAsync(9, new(), Now, default);
        Assert.Equal((ItemEffectsPageState.MissingItem, ItemEffectsSourceState.NotChecked), (missing.State, missing.Bridge.State));
        var high = await effects.GetAsync(High, new(), Now, default);
        Assert.Equal((ItemEffectsPageState.ItemKeyOutOfRange, High), (high.State, high.ItemId));
        Assert.Empty(high.Entries);
    }

    [Fact]
    public async Task A_cursor_belongs_to_its_item_and_limit()
    {
        await using var effects = await OpenAsync();
        var first = await effects.GetAsync(1, new(2), Now, default);
        Assert.NotNull(first.Next);
        Assert.Equal(ItemEffectsPageState.InvalidCursor, (await effects.GetAsync(1, new(3, first.Next), Now, default)).State);
        Assert.Equal(ItemEffectsPageState.InvalidCursor, (await effects.GetAsync(2, new(2, first.Next), Now, default)).State);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => effects.GetAsync(1, new(17), Now, default));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => effects.GetAsync(1, new(0), Now, default));
    }

    [Fact]
    public async Task A_disposed_handle_throws_and_a_canceled_call_throws()
    {
        var effects = await OpenAsync();
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => effects.GetAsync(1, new(), Now, canceled.Token));
        await effects.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => effects.GetAsync(1, new(), Now, default));
    }
}

/// <summary>The <see cref="ItemEffectsContract"/> against the real <see cref="ItemEffectsService"/>.</summary>
public sealed class ItemEffectsServiceContractTests : ItemEffectsContract
{
    protected override async Task<IItemEffects> CreateAsync()
    {
        var tables = await Retail();
        tables[0] = ItemTable([Item(1), Item(2), Item(0), Item(High)]);
        return new EffectsWorld().With(tables).Service;
    }
}

/// <summary>The <see cref="ItemEffectsContract"/> against <see cref="InMemoryItemEffects"/>.</summary>
public sealed class InMemoryItemEffectsContractTests : ItemEffectsContract
{
    protected override Task<IItemEffects> CreateAsync()
    {
        static ItemEffectEntry Entry(uint link, long effect, uint item, long? spell, string? name, bool copy = false)
        {
            var row = new TableRow(link, new Db2Row(link, copy, copy ? 500u : null, [N(link), N(effect), N(item)]));
            if (effect < 0) return new(row, N(item), N(effect), new(ItemEffectState.EffectKeyOutOfRange), new(ItemEffectNameState.EffectNotReady));
            var effectRow = new TableRow(effect, Effect((uint)effect, spell!.Value));
            return new(row, N(item), N(effect), new(ItemEffectState.ObservedReady, effectRow, [], N(spell.Value)),
                new(ItemEffectNameState.Ready, new TableRow(spell.Value, Name((uint)spell.Value, name!)), name));
        }
        var fake = new InMemoryItemEffects().Add(Build, Locale,
        [
            new(1, [Entry(500, 0, 1, 0, " "), Entry(501, 0, 1, 0, " "), Entry(502, -1, 1, null, null), Entry(503, 0, 1, 0, " ", true)]),
            new(2, [Entry(504, 7, 2, 7, "Seven")]),
            new(0, []),
            new(High, []),
        ]);
        return Task.FromResult<IItemEffects>(fake);
    }
}
