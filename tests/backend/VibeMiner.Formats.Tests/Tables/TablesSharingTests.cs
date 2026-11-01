using System.Globalization;
using System.Text;
using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Formats.Generated;
using VibeMiner.Formats.Tables;
using Xunit;
using static VibeMiner.Formats.Tests.Tables.TablesContract;

namespace VibeMiner.Formats.Tests.Tables;

/// <summary>
/// Generated tables are filed by what they are decoded from, not by build (#187, D350): builds that list the same DB2 file and
/// read it with the same fields share one table; another definition block, another key status or, for locstring tables, another
/// locale gets its own. Synthetic data only.
/// </summary>
public sealed class TablesSharingTests
{
    private static CancellationToken Ct => CancellationToken.None;
    private const TablePriority Now = TablePriority.Interactive;
    private static readonly BuildRef A = new("wow", new string('a', 32), new string('1', 32), "us");
    private static readonly BuildRef B = new("wowt", new string('b', 32), new string('2', 32), "eu");
    private static readonly IReadOnlyList<ScenarioColumn> Plain = [ScenarioColumn.Id(), ScenarioColumn.Int("Value")];
    private static readonly ScenarioRow[] Rows = [new(1, ["1", "5"]), new(2, ["2", "256"])];

    private static FakeFile Stored(uint id, IReadOnlyList<ScenarioColumn>? columns = null, ScenarioKeyedSection? keyed = null)
    {
        var (bytes, hole) = SyntheticTables.Wdc5(columns ?? Plain, columns is null ? Rows : [new(1, ["1", "Eins"]), new(2, ["2", "Zwei"])], keyed);
        return new(id, Content: bytes, Storage: FileStorage.Stored, Holes: hole is null ? null : [hole]);
    }

    /// <summary>A definition over <paramref name="blocks"/>: each a BUILD line (or none) and the names of the Value column's block.</summary>
    private static string Dbd(params (string? Build, string Value)[] blocks)
    {
        var text = new StringBuilder("COLUMNS\nint ID\nint Value\nint Other\n");
        foreach (var (build, value) in blocks)
        {
            text.Append(CultureInfo.InvariantCulture, $"\nLAYOUT {SyntheticTables.Layout}\n");
            if (build is not null) text.Append(CultureInfo.InvariantCulture, $"BUILD {build}\n");
            text.Append(CultureInfo.InvariantCulture, $"$noninline,id$ID<u32>\n{value}<32>\n");
        }
        return text.ToString();
    }

    /// <summary>The world with <see cref="A"/> on 1.2.3.4 and <paramref name="b"/> on <paramref name="bVersion"/>, both listing the same files.</summary>
    private static async Task<SyntheticWorld> WorldAsync(IReadOnlyList<SyntheticTable> tables, string bVersion, Func<BuildRef, ITactKeyStatus>? keys = null)
    {
        var world = await SyntheticTables.CreateAsync(A, tables, keys: keys,
            options: o => o with { BuildVersion = (asked, _) => Task.FromResult<string?>(asked == A ? SyntheticTables.Version : asked == B ? bVersion : null) });
        world.Files.Add(B, ["enUS", "deDE"], tables.Where(t => t.File is not null).Select(t => t.File!));
        return world;
    }

    private static async Task<IBuildTables> OpenAsync(SyntheticWorld world, BuildRef build, string locale = "enUS") =>
        (await world.Service.ForAsync(build, locale, Ct)).Tables!;

    private static async Task<(TableEntry Entry, string? Identity)> ReadAsync(IBuildTables tables, uint id)
    {
        // A Background page waits for the build, so the table is stored when it answers.
        var page = await tables.PageAsync(new TablePageRequest(id), TablePriority.Background, Ct);
        Assert.Equal(TablePageState.Ok, page.State);
        return (page.Table, page.Identity);
    }

    private static async Task<TableEntry> ListedAsync(IBuildTables tables, uint id)
    {
        await foreach (var entry in tables.ListAsync(new(), Now, Ct)) if (entry.FileDataId == id) return entry;
        throw new InvalidOperationException("Not listed.");
    }

    [Fact]
    public async Task Two_builds_with_the_same_file_and_fields_share_one_table()
    {
        var world = await WorldAsync([new(10, "T10", Dbd(("1.2.3.4", "Value"), ("1.2.3.5", "Value")), Stored(10))], "1.2.3.5");
        await using var a = await OpenAsync(world, A);
        var (first, identity) = await ReadAsync(a, 10);
        Assert.Equal((TableState.Ready, 1), (first.State, world.Generated.Decodes));
        var objects = world.Store.Keys.Count;

        await using var b = await OpenAsync(world, B);
        // Before B reads a byte of the file, its list already finds A's table.
        var listed = await ListedAsync(b, 10);
        Assert.Equal((TableState.Ready, true), (listed.State, listed.Checked));
        var (second, again) = await ReadAsync(b, 10);
        Assert.Equal(TableState.Ready, second.State);
        Assert.Equal(identity, again);
        Assert.Equal(1, world.Generated.Decodes);
        Assert.Equal(objects, world.Store.Keys.Count);
    }

    [Fact]
    public async Task A_build_no_block_lists_reuses_the_table_by_the_layout_the_store_remembers()
    {
        // 1.2.3.9 is unlisted: the block is picked by the file's layout, which only reading the file, or the store, knows.
        var world = await WorldAsync([new(10, "T10", Dbd(("1.2.3.4", "Value")), Stored(10))], "1.2.3.9");
        await using (var a = await OpenAsync(world, A)) await ReadAsync(a, 10);
        var reads = world.Counted.Reads;

        await using var b = await OpenAsync(world, B);
        var listed = await ListedAsync(b, 10);
        Assert.Equal((TableState.Ready, true), (listed.State, listed.Checked));
        Assert.Equal(TableState.Ready, (await b.CheckAsync(10, Now, Ct)).State);
        Assert.Equal(1, world.Generated.Decodes);
        Assert.Equal(reads, world.Counted.Reads);
    }

    [Fact]
    public async Task Another_resolved_definition_gets_its_own_table()
    {
        // The same file, but build B's block names the column differently: its rows' header differs, so it is another table.
        var world = await WorldAsync([new(10, "T10", Dbd(("1.2.3.4", "Value"), ("1.2.3.5", "Other")), Stored(10))], "1.2.3.5");
        await using var a = await OpenAsync(world, A);
        var (_, identity) = await ReadAsync(a, 10);

        await using var b = await OpenAsync(world, B);
        Assert.Equal((TableState.NotChecked, false), ((await ListedAsync(b, 10)).State, (await ListedAsync(b, 10)).Checked));
        var (entry, other) = await ReadAsync(b, 10);
        Assert.Equal(TableState.Ready, entry.State);
        Assert.NotEqual(identity, other);
        Assert.Equal(2, world.Generated.Decodes);
        Assert.Equal("Other", (await b.SchemaAsync(10, Now, Ct)).Columns![1].Name);
        Assert.Equal("Value", (await a.SchemaAsync(10, Now, Ct)).Columns![1].Name);
    }

    [Fact]
    public async Task Another_status_of_a_key_the_table_uses_gets_its_own_table()
    {
        var keyed = new ScenarioKeyedSection(Key, [10, 11]);
        var known = new TactKeyStatus(new Dictionary<string, string> { [Key] = "fp-known" });
        var world = await WorldAsync([new(10, "T10", Dbd(("1.2.3.4", "Value"), ("1.2.3.5", "Value")), Stored(10, keyed: keyed))], "1.2.3.5",
            keys: build => build == B ? known : TactKeyStatus.None);
        await using var a = await OpenAsync(world, A);
        var (_, identity) = await ReadAsync(a, 10);

        await using var b = await OpenAsync(world, B);
        var (_, other) = await ReadAsync(b, 10);
        Assert.NotEqual(identity, other);
        Assert.Equal(2, world.Generated.Decodes);
    }

    [Fact]
    public async Task A_table_with_holes_is_never_shared_but_a_clear_one_of_the_same_bytes_is()
    {
        // The same bytes: build A's encoding hides the keyed section behind a hole, build B's ships it clear. The key is unknown to both.
        var keyed = new ScenarioKeyedSection(Key, [10, 11]);
        var holed = Stored(10, keyed: keyed);
        var world = await SyntheticTables.CreateAsync(A, [new(10, "T10", Dbd(("1.2.3.4", "Value"), ("1.2.3.5", "Value")), holed)],
            options: o => o with { BuildVersion = (asked, _) => Task.FromResult<string?>(asked == A ? SyntheticTables.Version : "1.2.3.5") });
        world.Files.Add(B, ["enUS"], [holed with { Holes = null }]);
        var c = new BuildRef("wow", new string('c', 32), new string('3', 32), "us");
        world.Files.Add(c, ["enUS"], [holed]);

        await using var a = await OpenAsync(world, A);
        Assert.Equal(TableState.Partial, (await ReadAsync(a, 10)).Entry.State);
        // C has A's holes, but holes follow each build's encoding: it builds its own.
        await using var third = await OpenAsync(world, c);
        Assert.Equal(TableState.Partial, (await ReadAsync(third, 10)).Entry.State);
        Assert.Equal(2, world.Generated.Decodes);
        await using var b = await OpenAsync(world, B);
        Assert.Equal(TableState.Ready, (await ReadAsync(b, 10)).Entry.State);
        Assert.Equal(3, world.Generated.Decodes);
        // Once a clear table of the same bytes is stored, every build reads it in full, whatever it read before.
        var d = new BuildRef("wow", new string('d', 32), new string('4', 32), "us");
        world.Files.Add(d, ["enUS"], [holed]);
        await using var fourth = await OpenAsync(world, d);
        Assert.Equal(TableState.Ready, (await ReadAsync(fourth, 10)).Entry.State);
        Assert.Equal(TableState.Ready, (await a.CheckAsync(10, Now, Ct)).State);
        Assert.Equal(TableState.Ready, (await ListedAsync(third, 10)).State);
        Assert.Equal(3, world.Generated.Decodes);
    }

    [Fact]
    public async Task A_table_read_through_a_decrypted_chunk_carries_its_key()
    {
        // No keyed section: only layer 4 knows build A's encoding encrypts a chunk, which a known key opens. B ships it clear.
        // A wrong key can pass layer 4's check now and then (Salsa20 has no MAC), so such a table must never reach another build.
        var file = Stored(10);
        var known = new TactKeyStatus(new Dictionary<string, string> { [Key] = "fp-known" });
        var corrected = new TactKeyStatus(new Dictionary<string, string> { [Key] = "fp-corrected" });
        var status = known;
        var world = await SyntheticTables.CreateAsync(A, [new(10, "T10", Dbd(("1.2.3.4", "Value")), file with { DecryptedKeys = [Key] })],
            keys: _ => status,
            options: o => o with { BuildVersion = (_, _) => Task.FromResult<string?>(SyntheticTables.Version) });
        world.Files.Add(B, ["enUS"], [file]);

        await using var a = await OpenAsync(world, A);
        var (entry, identity) = await ReadAsync(a, 10);
        Assert.Equal(TableState.Ready, entry.State);
        // A corrected key on the same build gives another table: the chunk's key counts with its fingerprint.
        status = corrected;
        await using (var again = await OpenAsync(world, A)) Assert.NotEqual(identity, (await ReadAsync(again, 10)).Identity);
        Assert.Equal(2, world.Generated.Decodes);
        status = known;
        Assert.Equal(identity, (await ReadAsync(a, 10)).Identity);
        Assert.Equal(2, world.Generated.Decodes);
        // Even the same key on another build: which chunks are encrypted is each build's encoding, so it isn't shared.
        var d = new BuildRef("wow", new string('d', 32), new string('4', 32), "us");
        world.Files.Add(d, ["enUS"], [file with { DecryptedKeys = [Key] }]);
        await using var fourth = await OpenAsync(world, d);
        Assert.NotEqual(identity, (await ReadAsync(fourth, 10)).Identity);
        Assert.Equal(3, world.Generated.Decodes);
        // B ships the bytes clear: its table depends on no key and is the one every build shares, so A now reads it.
        await using var b = await OpenAsync(world, B);
        var clear = (await ReadAsync(b, 10)).Identity;
        Assert.Equal(4, world.Generated.Decodes);
        Assert.Equal(clear, (await ReadAsync(a, 10)).Identity);
        Assert.Equal(4, world.Generated.Decodes);
    }

    [Fact]
    public async Task Only_a_table_with_locstrings_is_filed_per_locale()
    {
        IReadOnlyList<ScenarioColumn> localized = [ScenarioColumn.Id(), ScenarioColumn.LocText("Name")];
        // Both locales list the same bytes here, so only the locale can tell their tables apart.
        var world = await SyntheticTables.CreateAsync(A, [new(10, "T10", SyntheticTables.Dbd(Plain), Stored(10)),
            new(11, "T11", SyntheticTables.Dbd(localized), Stored(11, localized))]);
        await using (var en = await OpenAsync(world, A)) { await ReadAsync(en, 10); await ReadAsync(en, 11); }
        Assert.Equal(2, world.Generated.Decodes);

        await using var de = await OpenAsync(world, A, "deDE");
        await ReadAsync(de, 10);
        Assert.Equal(2, world.Generated.Decodes);
        await ReadAsync(de, 11);
        Assert.Equal(3, world.Generated.Decodes);
    }
}
