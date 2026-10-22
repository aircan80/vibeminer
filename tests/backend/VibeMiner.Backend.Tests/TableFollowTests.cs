using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;
using F = VibeMiner.Domain.TableFollowStates;
using Harness = VibeMiner.Backend.Tests.TableReferenceTextTests.Harness;
using Admitted = VibeMiner.Backend.Tests.TableReferenceTextTests.Admitted;

namespace VibeMiner.Backend.Tests;

public sealed class TableFollowTests
{
    private const string Item = "50238ec2", ItemClass = "b977271e", Material = "a4c11069", Creature = "c9d6b6b3";
    private static Db2Row R(uint id, params string[] values) => new(id, false, null, values);
    private static readonly Db2Column[] ItemClassColumns = [new("ID", "integer", true), new("ClassName_lang", "string", false), new("ClassID", "integer", false)];
    private static readonly Db2Column[] ItemColumns = [new("ID", "integer", true), new("ClassID", "integer", false), new("Material", "integer", false), new("ContentTuningID", "integer", false)];

    private static TableFollowRequest Follow(Harness h, Admitted source, int column, string value, Admitted target) =>
        new(h.Context.ProductId, h.Context.BuildIdentity, h.Context.SourceRegion, source.TableId, "enUS", source.Artifact.ContentKey,
            column, value, target.TableId, target.Artifact.ContentKey);

    [Fact]
    public async Task Item_ClassID_follows_ItemClass_ClassID_by_exact_value_never_the_record_ID()
    {
        await using var h = await Harness.Create();
        // Record IDs differ from ClassID on purpose: a join on ID would pick row 9.
        var itemClass = await h.Admit(ItemClass, "35680EB8", ItemClassColumns,
            [R(301, "301", "Recipe", "9"), R(303, "303", "First", "7"), R(304, "304", "Second", "7"), R(9, "9", "Wrong if joined on ID", "-128"), R(12, "12", "Twelve by ID", "1")]);
        var item = await h.Admit(Item, "996192AA", ItemColumns, [R(1, "1", "9", "1", "0")]);
        await using var store = h.Store();

        var one = await store.FollowAsync(Follow(h, item, 1, "9", itemClass), default);
        Assert.Equal((F.Ready, TableFollowRules.PolicyRevision, "complete", 2, false), (one.State, one.PolicyRevision, one.Scope, one.TargetColumnIndex, one.TargetIsId));
        Assert.Equal((new TableReference("ItemClass", "ClassID"), "ClassID", "9"), (one.Reference, one.Column, one.Value));
        Assert.Equal(new TableFollowRange("-128", "127"), one.TargetRange); // ClassID<8> in layout 35680EB8.
        Assert.Equal(("1", "301", ItemClass, itemClass.Artifact.ContentKey), (one.Page!.TotalCount, Assert.Single(one.Page.Rows).Id, one.Page.TableId, one.Page.ArtifactId));

        var several = await store.FollowAsync(Follow(h, item, 1, "7", itemClass), default);
        Assert.Equal("2", several.Page!.TotalCount);
        Assert.Equal(["303", "304"], several.Page.Rows.Select(r => r.Id));

        var none = await store.FollowAsync(Follow(h, item, 1, "12", itemClass), default);
        Assert.Equal((F.Ready, "0", "complete"), (none.State, none.Page!.TotalCount, none.Scope)); // Row 12 has ClassID 1.
        Assert.Empty(none.Page.Rows);

        Assert.Equal("9", Assert.Single((await store.FollowAsync(Follow(h, item, 1, "-128", itemClass), default)).Page!.Rows).Id);
        // Out of the signed 8-bit domain: no wrap of 200 to -56, no truncation of 300 to 44.
        foreach (var value in new[] { "300", "-129", "200", "18446744073709551615" })
        {
            var miss = await store.FollowAsync(Follow(h, item, 1, value, itemClass), default);
            Assert.Equal((F.KeyOutOfRange, (TableDataPage?)null), (miss.State, miss.Page));
        }
    }

    [Fact]
    public async Task Record_ID_target_uses_the_ID_directory_counts_copies_and_never_wraps_negative_values()
    {
        await using var h = await Harness.Create();
        var creature = await h.Admit(Creature, "6E14C900", [new("ID", "integer", true), new("Name_lang", "string", false), new("MountCreatureID", "integer", false)],
            [R(0, "0", "Zero", "5"), R(5, "5", "Mount", "0"), R(6, "6", "Original", "-1"), new(6, true, 5, ["6", "Copy", "-1"]), R(7, "7", "Seven", "6")]);
        await using var store = h.Store();
        var zero = await store.FollowAsync(Follow(h, creature, 2, "0", creature), default);
        Assert.Equal((F.Ready, true, 0, "0"), (zero.State, zero.TargetIsId, zero.TargetColumnIndex, Assert.Single(zero.Page!.Rows).Id));
        Assert.Equal(new TableFollowRange("0", "4294967295"), zero.TargetRange);
        var twice = await store.FollowAsync(Follow(h, creature, 2, "6", creature), default);
        Assert.Equal("2", twice.Page!.TotalCount);
        Assert.Equal([false, true], twice.Page.Rows.Select(r => r.IsCopy));
        Assert.Equal(F.KeyOutOfRange, (await store.FollowAsync(Follow(h, creature, 2, "-1", creature), default)).State);
        Assert.Equal(F.KeyOutOfRange, (await store.FollowAsync(Follow(h, creature, 2, "4294967296", creature), default)).State);
    }

    [Fact]
    public async Task Many_matches_page_by_cursor_and_a_partial_target_reports_its_scope()
    {
        await using var h = await Harness.Create();
        var itemClass = await h.Admit(ItemClass, "35680EB8", ItemClassColumns,
            Enumerable.Range(1, 60).Select(i => R((uint)i, i.ToString(CultureInfo.InvariantCulture), "Class", "7")).ToArray(), partial: true);
        var item = await h.Admit(Item, "996192AA", ItemColumns, [R(1, "1", "7", "1", "0")]);
        await using var store = h.Store();
        var first = await store.FollowAsync(Follow(h, item, 1, "7", itemClass), default);
        Assert.Equal(("unencryptedSections", "60", 50, "50", "0"), (first.Scope, first.Page!.TotalCount, first.Page.Rows.Count, first.Page.NextCursor, first.After));
        var second = await store.FollowAsync(Follow(h, item, 1, "7", itemClass) with { After = "50" }, default);
        Assert.Equal((10, (string?)null, "50", "51"), (second.Page!.Rows.Count, second.Page.NextCursor, second.After, second.Page.Rows[0].Id));
        var miss = await store.FollowAsync(Follow(h, item, 1, "3", itemClass), default);
        Assert.Equal(("0", "unencryptedSections"), (miss.Page!.TotalCount, miss.Scope)); // A miss in readable rows only.
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            store.FollowAsync(Follow(h, item, 1, "7", itemClass) with { After = "61" }, default))).Code);
        // The bad cursor is the caller's: the same reader still answers.
        Assert.Equal("60", (await store.FollowAsync(Follow(h, item, 1, "7", itemClass), default)).Page!.TotalCount);
    }

    [Fact]
    public async Task Large_non_ID_target_is_unindexed_and_an_unusable_target_layout_is_unsupported()
    {
        await using var h = await Harness.Create();
        var rows = Enumerable.Range(1, TableFollowRules.MaximumScanRows + 1)
            .Select(i => R((uint)i, i.ToString(CultureInfo.InvariantCulture), "Class", (i % 100).ToString(CultureInfo.InvariantCulture))).ToArray();
        var large = await h.Admit(ItemClass, "35680EB8", ItemClassColumns, rows);
        var item = await h.Admit(Item, "996192AA", ItemColumns, [R(1, "1", "9", "1", "0")]);
        await using (var store = h.Store())
        {
            var result = await store.FollowAsync(Follow(h, item, 1, "9", large), default);
            Assert.Equal((F.Unindexed, "TABLE_FOLLOW_UNINDEXED", (TableDataPage?)null), (result.State, result.Problem!.Code, result.Page));
        }
        var noColumn = await h.Admit(ItemClass, "35680EB8", [new("ID", "integer", true), new("ClassName_lang", "string", false)], [R(1, "1", "Recipe")]);
        await using (var store = h.Store())
        {
            var result = await store.FollowAsync(Follow(h, item, 1, "9", noColumn), default);
            Assert.Equal((F.Unsupported, "TABLE_FOLLOW_LAYOUT", (int?)null), (result.State, result.Problem!.Code, result.TargetColumnIndex));
        }
    }

    [Fact]
    public async Task Only_a_declared_reference_to_that_exact_table_is_followed()
    {
        await using var h = await Harness.Create();
        var itemClass = await h.Admit(ItemClass, "35680EB8", ItemClassColumns, [R(1, "1", "Recipe", "1")]);
        var material = await h.Admit(Material, "BE3E0E4C", [new("ID", "integer", true), new("Flags", "integer", false)], [R(1, "1", "0")]);
        var item = await h.Admit(Item, "996192AA", ItemColumns, [R(1, "1", "1", "1", "0")]);
        await using var store = h.Store();
        Assert.Equal("1", Assert.Single((await store.FollowAsync(Follow(h, item, 2, "1", material), default)).Page!.Rows).Id);
        async Task<string> Code(TableFollowRequest request) => (await Assert.ThrowsAsync<DiscoveryException>(() => store.FollowAsync(request, default))).Code;
        Assert.Equal("TABLE_FOLLOW_NOT_DECLARED", await Code(Follow(h, item, 0, "1", itemClass))); // ID declares nothing.
        Assert.Equal("TABLE_FOLLOW_NOT_DECLARED", await Code(Follow(h, item, 9, "1", itemClass)));
        // ClassID declares ItemClass, so a Material target with the same number is refused.
        Assert.Equal("TABLE_FOLLOW_TARGET_MISMATCH", await Code(Follow(h, item, 1, "1", material)));
        // ContentTuning is declared but its definition is not bundled.
        Assert.Equal("TABLE_FOLLOW_TARGET_MISMATCH", await Code(Follow(h, item, 3, "1", itemClass)));
    }

    [Fact]
    public async Task Stale_or_unprepared_artifacts_invalid_values_and_cancellation_are_refused()
    {
        await using var h = await Harness.Create();
        var itemClass = await h.Admit(ItemClass, "35680EB8", ItemClassColumns, [R(1, "1", "Recipe", "9")]);
        var item = await h.Admit(Item, "996192AA", ItemColumns, [R(1, "1", "9", "1", "0")]);
        await using var store = h.Store();
        var service = new TableFollowService(store);
        async Task<string> Code(TableFollowRequest request) => (await Assert.ThrowsAsync<DiscoveryException>(() => service.FollowAsync(request))).Code;
        var good = Follow(h, item, 1, "9", itemClass);
        Assert.Equal("TABLE_ARTIFACT_STALE", await Code(good with { ArtifactId = new string('0', 64) }));
        Assert.Equal("TABLE_ARTIFACT_STALE", await Code(good with { TargetArtifactId = new string('0', 64) }));
        // Item.Material declares Material::ID, which has no admission yet.
        Assert.Equal("TABLE_NOT_PREPARED", await Code(good with { ColumnIndex = 2, Value = "1", TargetTableId = Material }));
        foreach (var value in new[] { "012", "-0", "+9", "9.0", "", " 9", "123456789012345678901" })
            Assert.Equal("INVALID_REQUEST", await Code(good with { Value = value }));
        Assert.Equal("INVALID_REQUEST", await Code(good with { Limit = 51 }));
        Assert.Equal("INVALID_REQUEST", await Code(good with { ColumnIndex = 512 }));
        Assert.Equal("INVALID_REQUEST", await Code(good with { TargetTableId = "ItemClass" }));
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.FollowAsync(good, stop.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.FollowAsync(good, stop.Token));
        Assert.Equal(F.Ready, (await service.FollowAsync(good)).State);
        await store.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.FollowAsync(good, default));
    }

    [Theory]
    [InlineData("0", true)] [InlineData("-1", true)] [InlineData("18446744073709551615", true)] [InlineData("-99999999999999999999", true)]
    [InlineData("-0", false)] [InlineData("00", false)] [InlineData("+1", false)] [InlineData("1e3", false)] [InlineData("-", false)]
    public void Values_are_canonical_decimal_integers(string value, bool valid) =>
        Assert.Equal(valid, TableFollowService.TryParseValue(value, out _));
}
