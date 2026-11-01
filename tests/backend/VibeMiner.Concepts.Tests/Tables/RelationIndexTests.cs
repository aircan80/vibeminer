using VibeMiner.Builds;
using VibeMiner.Concepts.Tables;
using VibeMiner.Formats;
using VibeMiner.Formats.Tables;
using VibeMiner.Formats.Testing;
using Xunit;
using static VibeMiner.Concepts.Tests.Items.ItemsWorld;

namespace VibeMiner.Concepts.Tests.Tables;

/// <summary>Layer 6 relation indexes (D471) over the layer 5 fake. Synthetic data only.</summary>
public sealed class RelationIndexTests
{
    private const TablePriority Now = TablePriority.Interactive;
    private static readonly Db2Column[] Columns =
        [new("ID", "integer", true), new("ParentID", "integer", false), new("Raw", "integer", false, new("raw-physical-integer-v1", 32, false, 8, true, "Raw")),
            new("Dup", "integer", false), new("Dup", "integer", false)];

    private static Db2Row Row(uint id, string parent, IReadOnlyList<Db2TextIssue>? issues = null) => new(id, false, null, [N(id), parent, "0", "0", "0"], issues);

    private static FakeTable Child(IReadOnlyList<Db2Row> rows, TableState state = TableState.Ready, string? contentKey = null) =>
        new(3001, "Child", state, Columns, rows, ContentKey: contentKey);

    private static readonly Db2Row[] Rows =
        [Row(1, "7"), Row(2, "8"), Row(3, "7"), Row(4, "-1"), Row(5, "4294967296"), Row(6, "", [new(1, "DB2_RELATION_ABSENT", "")]), Row(7, "7")];

    private static async Task<IBuildTables> OpenAsync(InMemoryTables tables) => (await tables.ForAsync(Build, Locale, default)).Tables!;

    private static async Task<(InMemoryTables Fake, IBuildTables Tables)> WorldAsync(params FakeTable[] tables)
    {
        var fake = new InMemoryTables { MaxScanRows = 1000 }.Add(Build, Locale, tables);
        return (fake, await OpenAsync(fake));
    }

    [Fact]
    public async Task A_key_gives_its_rows_in_table_order_from_one_build_per_column()
    {
        var (_, tables) = await WorldAsync(Child(Rows));
        var index = new RelationIndexes(tables);
        var seven = await index.LookupAsync("Child", "ParentID", 7, 10, Now, default);
        Assert.Equal((RelationLookupState.Ok, true, 3), (seven.State, seven.Complete, seven.Matches));
        Assert.Equal([1u, 3, 7], seven.Rows.Select(row => row.Row.Id));
        Assert.Equal([0L, 2, 6], seven.Rows.Select(row => row.Ordinal));
        var trimmed = await index.LookupAsync("Child", "ParentID", 7, 2, Now, default);
        Assert.Equal((2, 3), (trimmed.Rows.Count, trimmed.Matches));
        var none = await index.LookupAsync("Child", "ParentID", 9, 10, Now, default);
        Assert.Equal((RelationLookupState.Ok, true, 0), (none.State, none.Complete, none.Matches));
        Assert.Equal(1, index.Builds);
        // -1, 2^32 and a cell with a text issue can't be uint32 keys and aren't held.
        Assert.Equal(4, index.Entries);
    }

    [Fact]
    public async Task A_partial_table_answers_its_readable_rows_never_complete()
    {
        var (_, tables) = await WorldAsync(Child(Rows, TableState.Partial));
        var found = await new RelationIndexes(tables).LookupAsync("Child", "ParentID", 9, 10, Now, default);
        Assert.Equal((RelationLookupState.Ok, false, TableState.Partial), (found.State, found.Complete, found.Table!.State));
    }

    [Fact]
    public async Task An_unreadable_table_is_asked_again_and_a_column_that_cant_be_a_key_is_unsupported()
    {
        var (_, tables) = await WorldAsync(new FakeTable(3001, "Child", TableState.NeedsNetwork), Child(Rows) with { FileDataId = 3002, Name = "Other" });
        var index = new RelationIndexes(tables);
        Assert.Equal(RelationLookupState.NotReadable, (await index.LookupAsync("Child", "ParentID", 7, 10, Now, default)).State);
        Assert.Equal(RelationLookupState.NotReadable, (await index.LookupAsync("Child", "ParentID", 7, 10, Now, default)).State);
        Assert.Equal(2, index.Builds);
        foreach (var column in new[] { "Raw", "Dup", "Nope" })
        {
            var bad = await index.LookupAsync("Other", column, 0, 10, Now, default);
            Assert.Equal((RelationLookupState.Unsupported, RelationIndexes.ColumnCode), (bad.State, bad.Code));
        }
    }

    [Fact]
    public async Task Past_the_entry_budget_is_a_named_limit_kept_for_the_handle()
    {
        var (_, tables) = await WorldAsync(Child(Rows), Child([Row(1, "1")]) with { FileDataId = 3002, Name = "Small" });
        var index = new RelationIndexes(tables, maxEntries: 3);
        var full = await index.LookupAsync("Child", "ParentID", 7, 10, Now, default);
        Assert.Equal((RelationLookupState.IndexLimit, RelationIndexes.LimitCode), (full.State, full.Code));
        Assert.Equal(RelationLookupState.IndexLimit, (await index.LookupAsync("Child", "ParentID", 8, 10, Now, default)).State);
        Assert.Equal((1, 0), (index.Builds, index.Entries));
        Assert.Single((await index.LookupAsync("Small", "ParentID", 1, 10, Now, default)).Rows);
        Assert.Equal(1, index.Entries);
    }

    [Fact]
    public async Task A_changed_identity_rebuilds_once_then_is_stale()
    {
        var (fake, inner) = await WorldAsync(Child(Rows, contentKey: new string('1', 32)));
        var version = 1;
        void Swap() => fake.Add(Build, Locale, [Child(Rows, contentKey: new string((char)('0' + ++version % 10), 32))]);

        // One change after the build: the read back is stale, the rebuild answers.
        var swaps = 0;
        var once = new HookedTables(inner, _ => { if (swaps++ == 0) Swap(); return Task.CompletedTask; });
        var index = new RelationIndexes(once);
        var found = await index.LookupAsync("Child", "ParentID", 8, 10, Now, default);
        Assert.Equal((RelationLookupState.Ok, 2u), (found.State, Assert.Single(found.Rows).Row.Id));
        Assert.Equal(2, index.Builds);

        // A change after every page: never an answer from a stale index.
        var always = new RelationIndexes(new HookedTables(inner, _ => { Swap(); return Task.CompletedTask; }));
        var stale = await always.LookupAsync("Child", "ParentID", 8, 10, Now, default);
        Assert.Equal(RelationLookupState.Stale, stale.State);
        Assert.Empty(stale.Rows);
        Assert.Equal(0, always.Entries);
    }

    [Fact]
    public async Task Concurrent_lookups_share_one_build_and_a_canceled_waiter_leaves_it_running()
    {
        var (_, inner) = await WorldAsync(Child(Rows));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var index = new RelationIndexes(new HookedTables(inner, _ => release.Task));
        var first = index.LookupAsync("Child", "ParentID", 7, 10, Now, default);
        using var canceled = new CancellationTokenSource();
        var waiter = index.LookupAsync("Child", "ParentID", 8, 10, Now, canceled.Token);
        var second = index.LookupAsync("Child", "ParentID", 8, 10, Now, default);
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.SetResult();
        Assert.Equal(3, (await first).Matches);
        Assert.Equal(1, (await second).Matches);
        Assert.Equal(1, index.Builds);
    }

    [Fact]
    public async Task A_build_canceled_by_its_owner_is_started_again_by_the_next_caller()
    {
        var (_, inner) = await WorldAsync(Child(Rows));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var index = new RelationIndexes(new HookedTables(inner, _ => release.Task));
        using var canceled = new CancellationTokenSource();
        var owner = index.LookupAsync("Child", "ParentID", 7, 10, Now, canceled.Token);
        var next = index.LookupAsync("Child", "ParentID", 7, 10, Now, default);
        await canceled.CancelAsync();
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
        Assert.Equal(3, (await next).Matches);
        Assert.Equal(2, index.Builds);
    }

    private static Db2Row[] Many(int count) => [.. Enumerable.Range(1, count).Select(i => Row((uint)i, N(i % 5)))];

    [Fact]
    public async Task A_build_canceled_after_its_first_page_gives_its_rows_back()
    {
        var (_, inner) = await WorldAsync(Child(Many(2500)));
        using var canceled = new CancellationTokenSource();
        var pages = 0;
        var index = new RelationIndexes(new HookedTables(inner, _ => ++pages == 2 ? canceled.CancelAsync() : Task.CompletedTask));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.LookupAsync("Child", "ParentID", 1, 10, Now, canceled.Token));
        Assert.Equal(0, index.Entries);
        Assert.Equal(500, (await index.LookupAsync("Child", "ParentID", 1, 10, Now, default)).Matches);
        Assert.Equal(2500, index.Entries);
    }

    [Fact]
    public async Task An_empty_match_checks_the_identity_and_rebuilds_after_a_change()
    {
        var (fake, tables) = await WorldAsync(Child(Rows, TableState.Partial, new string('1', 32)));
        var index = new RelationIndexes(tables);
        Assert.False((await index.LookupAsync("Child", "ParentID", 9, 10, Now, default)).Complete);
        // The hidden part becomes readable: a new identity, and the kept index must not answer for it.
        fake.Add(Build, Locale, [Child([.. Rows, Row(8, "9")], TableState.Ready, new string('2', 32))]);
        var after = await index.LookupAsync("Child", "ParentID", 9, 10, Now, default);
        Assert.Equal((RelationLookupState.Ok, true, 8u), (after.State, after.Complete, Assert.Single(after.Rows).Row.Id));
        Assert.Equal(2, index.Builds);
    }

    [Fact]
    public async Task An_interactive_caller_joining_a_background_build_moves_its_later_pages_to_interactive()
    {
        var (_, inner) = await WorldAsync(Child(Many(2500)));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<TablePriority>();
        var index = new RelationIndexes(new HookedTables(inner, (_, priority) =>
        {
            lock (seen) seen.Add(priority);
            return seen.Count == 1 ? release.Task : Task.CompletedTask;
        }));
        var background = index.LookupAsync("Child", "ParentID", 1, 1, TablePriority.Background, default);
        var interactive = index.LookupAsync("Child", "ParentID", 2, 1, Now, default);
        release.SetResult();
        await Task.WhenAll(background, interactive);
        // Build pages: the first at Background, the two after the join at Interactive; then each caller's read back at its own.
        Assert.Equal([TablePriority.Background, Now, Now], seen.Take(3));
        Assert.Equal(1, index.Builds);
    }

    [Fact]
    public async Task Skip_pages_through_the_matches_under_one_identity()
    {
        var (_, tables) = await WorldAsync(Child(Rows));
        var index = new RelationIndexes(tables);
        var first = await index.LookupAsync("Child", "ParentID", 7, 2, Now, default);
        var rest = await index.LookupAsync("Child", "ParentID", 7, 2, Now, default, skip: 2);
        Assert.Equal([1u, 3], first.Rows.Select(row => row.Row.Id));
        Assert.Equal([7u], rest.Rows.Select(row => row.Row.Id));
        Assert.Equal(3, rest.Matches);
        Assert.NotNull(first.Identity);
        Assert.Equal(first.Identity, rest.Identity);
        var past = await index.LookupAsync("Child", "ParentID", 7, 2, Now, default, skip: 5);
        Assert.Equal((RelationLookupState.Ok, 0, 3), (past.State, past.Rows.Count, past.Matches));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => index.LookupAsync("Child", "ParentID", 7, 2, Now, default, skip: -1));
    }
}
