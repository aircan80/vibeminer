using VibeMiner.Concepts.Items;
using VibeMiner.Formats.Tables;
using Xunit;
using static VibeMiner.Concepts.Tests.Items.ItemsWorld;

namespace VibeMiner.Concepts.Tests.Items;

/// <summary>The request rules ported from the old ItemsServiceTests: bad requests throw before any table is read; literal queries pass through untouched.</summary>
public sealed class ItemQueriesTests
{
    public static TheoryData<ItemQuery> InvalidQueries()
    {
        var data = new TheoryData<ItemQuery>
        {
            new(Limit: 0), new(Limit: 51), new(Query: null!), new("bad\0name"), new(new string((char)0xd800, 1)), new(new string((char)0xdc00, 1)),
            new(new string('a', 129)), new(string.Concat(Enumerable.Repeat("\U0001F600", 129))), new("42", ExactId: 42),
        };
        foreach (var cursor in new[] { "", "a", "AB", "e30=", "e30/", "e30\n", new string('a', 2049) }) data.Add(new(After: cursor));
        return data;
    }

    // Was Invalid_queries_never_reach_the_projection.
    [Theory, MemberData(nameof(InvalidQueries))]
    public async Task Invalid_queries_never_reach_the_tables(ItemQuery query)
    {
        var world = new ItemsWorld().With(ItemTable([Item(1)]), SparseTable([Sparse(1, "One")]));
        await using var items = await world.OpenAsync();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => items.ListAsync(query, TablePriority.Interactive, default));
        Assert.Equal(0, world.Tables.Calls("SchemaAsync") + world.Tables.Calls("PageAsync") + world.Tables.Calls("GetAsync"));
    }

    // The old ItemId rule, for callers that take an item ID as text (Inspect_requires_an_exact_canonical_unsigned_id and the ExactItemId cases).
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("-1")] [InlineData("01")] [InlineData("4294967296")] [InlineData(" 1")] [InlineData("1.0")]
    [InlineData("1\n")] [InlineData("+1")] [InlineData("٣")]
    public void An_item_id_is_canonical_decimal_uint32(string? id) => Assert.False(ItemQueries.TryParseItemId(id, out _));

    // Was Literal_queries_unicode_scalar_boundary_and_exact_id_modes_are_preserved.
    [Fact]
    public async Task Literal_queries_up_to_the_scalar_boundary_and_both_exact_id_extremes_are_accepted()
    {
        var world = new ItemsWorld().With(ItemTable([Item(0), Item(uint.MaxValue)]), SparseTable([Sparse(0, "  untouched  "), Sparse(uint.MaxValue, "Épée")]));
        await using var items = await world.OpenAsync();
        foreach (var query in new[] { "", "  untouched  ", "%_'\\ OR 1=1", "Épée", new string('a', 128), string.Concat(Enumerable.Repeat("\U0001F600", 128)) })
            Assert.Equal(ItemsPageState.Ok, (await items.ListAsync(new(query, Limit: 1), TablePriority.Interactive, default)).State);
        Assert.Equal("  untouched  ", Assert.Single((await items.ListAsync(new("  untouched  "), TablePriority.Interactive, default)).Entries).Name.Value);
        Assert.Equal("Épée", Assert.Single((await items.ListAsync(new("éPÉE"), TablePriority.Interactive, default)).Entries).Name.Value);
        foreach (var id in new[] { "0", "4294967295" })
        {
            Assert.True(ItemQueries.TryParseItemId(id, out var parsed));
            Assert.Equal(parsed, Assert.Single((await items.ListAsync(new(ExactId: parsed), TablePriority.Interactive, default)).Entries).Id);
            Assert.Equal(ItemResultState.Found, (await items.GetAsync(parsed, TablePriority.Interactive, default)).State);
        }
    }
}
