using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeMiner.Backend.Tests;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Formats.Definitions.Tests;

/// <summary>
/// #55 / D080 / D081 / D101 / D120: which definition a table reads with, ported from the choice-level legacy
/// FetchedDefinitionTests. The bundled side is the real pinned provider (Item) or an authored one; fetched .dbd text is
/// synthetic. A legacy "prepare" is two resolutions with one budget: before the file (no layout), then with its layout.
/// Only definition-level outcomes are asserted; nothing is downloaded here.
/// </summary>
public sealed class TableDefinitionsTests
{
    private const string Revision = "1111111111111111111111111111111111111111";
    private const string Newer = "2222222222222222222222222222222222222222";
    private const string ItemTable = "50238ec2";
    private static readonly TimeSpan Gap = FetchedDefinitions.DefaultMinimumGap;

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        /// <summary>Pinned so dates in messages don't depend on the machine's time zone.</summary>
        public TimeZoneInfo Zone = TimeZoneInfo.Utc;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => Zone;
    }

    private sealed class Switch(bool enabled) : IDefinitionFetchSwitch
    {
        public bool Enabled = enabled;
        public Exception? Failure;
        public Task<bool> FetchEnabledAsync(CancellationToken token) => Failure is null ? Task.FromResult(Enabled) : Task.FromException<bool>(Failure);
    }

    /// <summary>A synthetic Item-like definition with one block for <paramref name="layout"/> listing <paramref name="build"/>.</summary>
    private static byte[] ItemWith(string layout, string build) =>
        Encoding.UTF8.GetBytes($"COLUMNS\nint ID\nint Value\n\nLAYOUT {layout}\nBUILD {build}\n$id$ID<32>\nValue<32>\n");

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record Harness(TableDefinitions Definitions, InMemoryObjectSource Source, Clock Clock, Switch Switch, BundledDefinitions Bundled)
    {
        internal int Asked => Source.Observed.Count(o => o.Kind == ObservationKind.Definition);
        internal void Serve(string revision, byte[] bytes, string table = ItemTable, string name = "Item") => Source.AddDefinition(table, name, revision, bytes);
        internal void Offline(string table = ItemTable) =>
            Source.FailObservation(table, ObservationRequest.DefinitionRegion, ObservationKind.Definition, ObjectState.Unavailable, "CDN_OFFLINE");
        /// <summary>The same object source with a new engine: fetched state from history only.</summary>
        internal TableDefinitions Restart() => new(Bundled, new FetchedDefinitions(Source, Clock), Switch, Clock);
        internal Task<DefinitionResolution> LocalAsync(string build, string? layout, string table = ItemTable) =>
            Definitions.ResolveAsync(table, build, layout, WowLocale.EnUS, DefinitionLookup.Local, default);

        /// <summary>A legacy prepare: before the file, then with its layout, both deliberate and sharing one budget.</summary>
        internal async Task<DefinitionResolution> PrepareAsync(string build, string layout, DefinitionFetchBudget? budget = null,
            List<string>? stages = null, bool deliberate = true, TableDefinitions? definitions = null, string table = ItemTable)
        {
            budget ??= new(); definitions ??= Definitions;
            var lookup = DefinitionLookup.Ask(budget, stage => { stages?.Add(stage); return Task.CompletedTask; }, deliberate);
            var before = await definitions.ResolveAsync(table, build, null, WowLocale.EnUS, lookup, default);
            if (before.State != DefinitionState.Resolved) return before;
            return await definitions.ResolveAsync(table, build, layout, WowLocale.EnUS, lookup, default);
        }
    }

    private static async Task<Harness> Create(bool enabled = true, BundledDefinitions? bundled = null)
    {
        var clock = new Clock(); var source = new InMemoryObjectSource { Time = clock }; var fetchSwitch = new Switch(enabled);
        bundled ??= await BundledDefinitions.OpenAsync(default);
        return new(new(bundled, new FetchedDefinitions(source, clock), fetchSwitch, clock), source, clock, fetchSwitch, bundled);
    }

    /// <summary>An authored provider over the internal CreateAsync; table ids are 00000001, 00000002, … in order.</summary>
    private static Task<BundledDefinitions> Authored(params (string Name, string Text)[] files)
    {
        var manifest = "[" + string.Join(",", files.Select((file, index) => $"{{\"tableName\":\"{file.Name}\",\"tableHash\":\"{index + 1:X8}\",\"db2FileDataID\":{index + 100}}}")) + "]";
        var decoded = files.SelectMany(file => Encoding.UTF8.GetBytes(file.Text)).ToArray();
        var stream = new byte[BrotliEncoder.GetMaxCompressedLength(decoded.Length)];
        Assert.True(BrotliEncoder.TryCompress(decoded, stream, out var written, 11, 24)); stream = stream[..written];
        var sources = JsonSerializer.SerializeToUtf8Bytes(new
        {
            provider = "WoWDBDefs contributors", revision = BundledDefinitions.Revision, definitionLicense = "CC-BY-SA-4.0",
            sourceBase = BundledDefinitions.Origin, resources = Array.Empty<object>(),
            definitions = new
            {
                path = "definitions.br", compression = "brotli", sha256 = Sha(stream), byteLength = stream.Length, decodedLength = decoded.Length,
                files = files.Select(file => new { path = $"definitions/{file.Name}.dbd", sha256 = Sha(Encoding.UTF8.GetBytes(file.Text)), byteLength = Encoding.UTF8.GetByteCount(file.Text) })
            }
        });
        return BundledDefinitions.CreateAsync(Encoding.UTF8.GetBytes(manifest), sources, () => new MemoryStream(stream, writable: false), new(), default);
    }

    // ---- Exact build first, then layout (D080, D081) ----

    [Fact]
    public async Task Exact_build_first_a_fetched_definition_that_lists_the_build_beats_a_bundled_layout_match()
    {
        // D080 would open 1.60.1.70500 by layout with bundled; WoWDBDefs lists it exactly, so that wins.
        var h = await Create();
        Assert.Equal(SchemaMatch.Layout, (await h.Restart().ResolveAsync(ItemTable, "1.60.1.70500", "9A2A4834", WowLocale.EnUS, DefinitionLookup.Local, default)).Match);
        var fake = ItemWith("9A2A4834", "1.60.1.70500"); h.Serve(Revision, fake);
        var chosen = await h.PrepareAsync("1.60.1.70500", "9A2A4834");
        Assert.Equal(DefinitionState.Resolved, chosen.State); Assert.Equal(SchemaMatch.Build, chosen.Match);
        Assert.Equal(DefinitionSource.Fetched, chosen.Definition!.Source); Assert.Equal(Sha(fake), chosen.Definition.Sha256);
        Assert.Equal(Revision, chosen.Definition.Revision); Assert.Equal(Revision, chosen.Definition.Fetched!.Revision);
        Assert.Null(chosen.Problem); Assert.Equal("on", chosen.DefinitionFetch);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("noBuild")]
    public async Task Without_an_exact_fetched_definition_the_bundled_layout_match_still_opens(string kind)
    {
        var h = await Create();
        if (kind == "offline") h.Offline(); else h.Serve(Revision, PinnedDefinitionFiles.Bytes("Item"));
        var chosen = await h.PrepareAsync("1.60.1.70500", "9A2A4834");
        Assert.Equal(DefinitionState.Resolved, chosen.State); Assert.Equal(SchemaMatch.Layout, chosen.Match);
        Assert.Equal(DefinitionSource.Bundled, chosen.Definition!.Source); Assert.Null(chosen.Definition.Fetched); Assert.Null(chosen.Because);
        Assert.Equal(BundledDefinitions.Revision, chosen.Definition.Revision);
        Assert.Equal(kind == "offline" ? "DEFINITION_FETCH_OFFLINE" : null, chosen.FetchFailure?.Code);
        Assert.Equal(kind != "offline", chosen.Fetched is not null);
    }

    [Fact]
    public async Task A_table_opened_by_layout_upgrades_to_a_fetched_exact_definition_once_upstream_lists_the_build()
    {
        var h = await Create();
        h.Serve(Revision, PinnedDefinitionFiles.Bytes("Item"));
        var first = await h.PrepareAsync("1.60.1.70500", "9A2A4834");
        Assert.Equal(SchemaMatch.Layout, first.Match); Assert.Equal(DefinitionSource.Bundled, first.Definition!.Source);
        var asked = h.Asked;
        // Within the gap nothing is due and a re-check asks nothing.
        h.Clock.Now += TimeSpan.FromMinutes(5);
        Assert.False(await h.Definitions.RefreshDueAsync(ItemTable, "1.60.1.70500", "9A2A4834", default));
        Assert.Equal(DefinitionSource.Bundled, (await h.PrepareAsync("1.60.1.70500", "9A2A4834", deliberate: false)).Definition!.Source);
        Assert.Equal(asked, h.Asked);
        // WoWDBDefs now lists the build. After the gap it is due, and the next prepare reads exactly.
        var exact = ItemWith("9A2A4834", "1.60.1.70500"); h.Serve(Newer, exact);
        h.Clock.Now += Gap;
        Assert.True(await h.Definitions.RefreshDueAsync(ItemTable, "1.60.1.70500", "9A2A4834", default));
        var upgraded = await h.PrepareAsync("1.60.1.70500", "9A2A4834", deliberate: false);
        Assert.Equal(SchemaMatch.Build, upgraded.Match); Assert.Equal(Newer, upgraded.Definition!.Revision); Assert.Equal(Sha(exact), upgraded.Definition.Sha256);
        Assert.Equal(FetchedBecause.ExactBuild, upgraded.Because);
    }

    [Fact]
    public async Task While_GitHub_is_unreachable_a_table_asks_once_per_gap_and_still_opens_by_layout()
    {
        var h = await Create();
        h.Offline();
        var budget = new DefinitionFetchBudget();
        var first = await h.PrepareAsync("1.60.1.70500", "9A2A4834", budget);
        // The look before the file asked; the one with the layout reused the budget's outcome.
        Assert.Equal(SchemaMatch.Layout, first.Match); Assert.True(budget.Asked); Assert.False(first.Asked); Assert.Equal("DEFINITION_FETCH_OFFLINE", first.FetchFailure!.Code);
        var attempts = h.Asked;
        Assert.False(await h.Definitions.RefreshDueAsync(ItemTable, "1.60.1.70500", "9A2A4834", default));
        Assert.Equal(DefinitionState.Resolved, (await h.PrepareAsync("1.60.1.70500", "9A2A4834", deliberate: false)).State);
        Assert.Equal(attempts, h.Asked);
        h.Clock.Now += Gap;
        Assert.True(await h.Definitions.RefreshDueAsync(ItemTable, "1.60.1.70500", "9A2A4834", default));
        Assert.Equal(SchemaMatch.Layout, (await h.PrepareAsync("1.60.1.70500", "9A2A4834", deliberate: false)).Match);
        Assert.True(h.Asked > attempts);
    }

    [Fact]
    public async Task Retry_after_the_network_comes_back_fetches_at_once_inside_the_wait()
    {
        // No bundled layout opens 77.x, so only a fetched definition can. The failed first open must not lock Retry out.
        var h = await Create();
        h.Offline();
        var offline = await h.PrepareAsync("77.0.0.100", "9A2A4834");
        Assert.Equal(DefinitionState.FetchFailed, offline.State);
        Assert.Equal("DEFINITION_FETCH_OFFLINE", offline.Problem!.Code); Assert.Contains("use Retry table data", offline.Problem.Message);
        h.Serve(Revision, ItemWith("9A2A4834", "77.0.0.100"));
        h.Clock.Now += TimeSpan.FromMinutes(1);
        var retried = await h.PrepareAsync("77.0.0.100", "9A2A4834");
        Assert.Equal(DefinitionState.Resolved, retried.State); Assert.Equal(Revision, retried.Definition!.Fetched!.Revision);
    }

    [Fact]
    public async Task With_the_switch_off_an_unknown_layout_on_an_unlisted_build_says_fetching_is_off()
    {
        var h = await Create(enabled: false);
        var failure = await h.PrepareAsync("1.60.1.70500", "1234ABCD");
        Assert.Equal(DefinitionState.FetchingOff, failure.State); Assert.Equal("off", failure.DefinitionFetch);
        Assert.Equal("DB2_SCHEMA_MISSING", failure.Problem!.Code); Assert.False(failure.Problem.Retryable);
        Assert.Contains("layout 1234ABCD", failure.Problem.Message);
        Assert.Contains("Fetching newer table definitions from WoWDBDefs is off in Settings", failure.Problem.Message);
        Assert.Equal(0, h.Asked);
    }

    [Fact]
    public async Task Bundled_wins_when_it_covers_the_file_and_GitHub_is_never_asked()
    {
        var h = await Create();
        h.Serve(Revision, ItemWith("9A2A4834", "1.60.1.70450"));
        var chosen = await h.PrepareAsync("1.60.1.70450", "9A2A4834");
        Assert.Equal(DefinitionState.Resolved, chosen.State); Assert.Equal(SchemaMatch.Build, chosen.Match);
        Assert.Equal(DefinitionSource.Bundled, chosen.Definition!.Source); Assert.Equal(BundledDefinitions.Revision, chosen.Definition.Revision);
        Assert.Null(chosen.Fetched); Assert.False(chosen.Asked); Assert.Null(chosen.BundledFallback);
        Assert.Null(await h.Definitions.LastCheckAsync(ItemTable, "1.60.1.70450", "9A2A4834", default));
        Assert.False(await h.Definitions.RefreshDueAsync(ItemTable, "1.60.1.70450", "9A2A4834", default));
        Assert.Equal(0, h.Asked);
    }

    [Fact]
    public async Task A_layout_the_bundled_definition_lacks_reads_with_a_fetched_block_for_that_layout()
    {
        var h = await Create();
        var fake = ItemWith("1234ABCD", "1.60.1.70450"); h.Serve(Revision, fake);
        var before = await h.LocalAsync("1.60.1.70450", null);
        Assert.Equal(DefinitionSource.Bundled, before.Definition!.Source); Assert.Null(before.Problem);
        var chosen = await h.PrepareAsync("1.60.1.70450", "1234ABCD");
        Assert.Equal(Revision, chosen.Definition!.Fetched!.Revision); Assert.Equal(Sha(fake), chosen.Definition.Sha256);
        Assert.Equal(["ID", "Value"], chosen.Schema!.Columns.Select(column => column.Name));
        // Turned off, the fetched definition no longer reads the file and the table can't open by bundled.
        h.Switch.Enabled = false;
        var asked = h.Asked;
        var off = await h.PrepareAsync("1.60.1.70450", "1234ABCD");
        Assert.Equal(DefinitionState.FetchingOff, off.State); Assert.Equal("DB2_SCHEMA_MISSING", off.Problem!.Code); Assert.Null(off.Fetched);
        Assert.Equal(asked, h.Asked);
    }

    [Fact]
    public async Task A_build_line_the_bundled_definition_lacks_opens_with_a_fetched_definition_and_says_so()
    {
        var h = await Create();
        var fake = ItemWith("9A2A4834", "77.0.0.100"); h.Serve(Revision, fake);
        // Locally, before any fetch: nothing lists the build, fetching is on.
        var before = await h.LocalAsync("77.0.0.100", null);
        Assert.Equal(DefinitionState.NoMatch, before.State); Assert.Equal("on", before.DefinitionFetch);
        Assert.Equal("SCHEMA_BUILD_NOT_LISTED", before.Problem!.Code); Assert.Equal(0, h.Asked);
        var stages = new List<string>();
        var chosen = await h.PrepareAsync("77.0.0.100", "9A2A4834", stages: stages);
        Assert.Equal(new FetchedDefinitionInfo(Revision, h.Clock.Now), chosen.Definition!.Fetched);
        Assert.Equal(Revision, chosen.Definition.Revision); Assert.Equal(Sha(fake), chosen.Definition.Sha256);
        Assert.Equal($"https://raw.githubusercontent.com/wowdev/WoWDBDefs/{Revision}/definitions/Item.dbd", chosen.Definition.SourceUrl);
        Assert.Equal(SchemaMatch.Build, chosen.Match); Assert.Equal(FetchedBecause.ExactBuild, chosen.Because); Assert.False(chosen.BundledFallback);
        Assert.Contains("Checking WoWDBDefs for the newest Item definition", stages); Assert.Equal(1, h.Asked);
        // Locally afterwards, the fetched definition is chosen before any file is read too.
        var after = await h.LocalAsync("77.0.0.100", null);
        Assert.Equal(Sha(fake), after.Definition!.Sha256); Assert.Null(after.Problem); Assert.Equal(SchemaMatch.Build, after.Match);
        // A restart offline reads the same definition from history, asking nothing.
        h.Offline();
        var restarted = await h.Restart().ResolveAsync(ItemTable, "77.0.0.100", "9A2A4834", WowLocale.EnUS, DefinitionLookup.Local, default);
        Assert.Equal(chosen.Definition with { Document = restarted.Definition!.Document }, restarted.Definition); Assert.Equal(1, h.Asked);
    }

    [Fact]
    public async Task With_the_switch_off_an_uncovered_line_fails_before_any_download_or_fetch()
    {
        var h = await Create(enabled: false);
        h.Serve(Revision, ItemWith("9A2A4834", "77.0.0.100"));
        var failure = await Assert.ThrowsAsync<DefinitionException>(() => h.Definitions.RequireBuildAsync(ItemTable, "Items", "77.0.0.100", default));
        Assert.Equal("SCHEMA_BUILD_NOT_LISTED", failure.Code);
        var resolved = await h.PrepareAsync("77.0.0.100", "9A2A4834");
        Assert.Equal(DefinitionState.FetchingOff, resolved.State); Assert.Equal("SCHEMA_BUILD_NOT_LISTED", resolved.Problem!.Code);
        Assert.Equal("off", resolved.DefinitionFetch);
        Assert.Equal(0, h.Asked);
        // On, preparing decides; a build the bundled definition opens by layout passes either way.
        h.Switch.Enabled = true;
        await h.Definitions.RequireBuildAsync(ItemTable, "Items", "77.0.0.100", default);
        h.Switch.Enabled = false;
        await h.Definitions.RequireBuildAsync(ItemTable, "Items", "1.60.1.70500", default);
        Assert.Equal(0, h.Asked);
    }

    [Theory]
    [InlineData("offline", "DEFINITION_FETCH_OFFLINE")]
    [InlineData("missing", "DEFINITION_FETCH_NOT_FOUND")]
    [InlineData("large", "DEFINITION_FETCH_TOO_LARGE")]
    [InlineData("malformed", "DEFINITION_FETCH_INVALID")]
    [InlineData("noLine", "SCHEMA_BUILD_NOT_LISTED")]
    public async Task Each_fetch_failure_is_its_own_state(string kind, string code)
    {
        var h = await Create();
        if (kind == "offline") h.Offline();
        if (kind == "large") h.Source.FailObservation(ItemTable, ObservationRequest.DefinitionRegion, ObservationKind.Definition, ObjectState.Refused, "CDN_TOO_LARGE");
        if (kind == "malformed") h.Serve(Revision, "COLUMNS\n"u8.ToArray());
        if (kind == "noLine") h.Serve(Revision, PinnedDefinitionFiles.Bytes("Item"));
        var failure = await h.PrepareAsync("77.0.0.100", "9A2A4834");
        Assert.Equal(kind == "noLine" ? DefinitionState.NoMatch : DefinitionState.FetchFailed, failure.State);
        Assert.Equal(code, failure.Problem!.Code); Assert.Null(failure.Definition);
        Assert.Equal(new DefinitionProblem(ItemTable, "on"), failure.Problem.Definition);
        if (kind == "noLine") Assert.Contains("Neither the pinned definition nor the WoWDBDefs definition fetched 2026-10-23 (1111111)", failure.Problem.Message);
        else Assert.Equal(code, failure.FetchFailure!.Code);
        Assert.Contains("Tables the bundled definitions cover still open.", failure.Problem.Message);
        if (kind == "offline") Assert.Contains("turn off Fetch newer table definitions in Settings", failure.Problem.Message);
        if (kind == "missing") Assert.Contains("only the bundled Item definition can be used", failure.Problem.Message);
        Assert.Equal(1, h.Asked);
    }

    [Fact]
    public async Task The_fetch_date_in_messages_is_in_the_clocks_local_time_zone()
    {
        var h = await Create();
        h.Clock.Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+14", TimeSpan.FromHours(14), "UTC+14", "UTC+14");
        h.Serve(Revision, PinnedDefinitionFiles.Bytes("Item"));
        var failure = await h.PrepareAsync("77.0.0.100", "9A2A4834");
        Assert.Contains("WoWDBDefs definition fetched 2026-10-24 (1111111)", failure.Problem!.Message);
    }

    [Fact]
    public async Task A_failed_fetch_for_an_uncovered_layout_names_the_layout_and_keeps_its_state()
    {
        var h = await Create();
        h.Offline();
        var failure = await h.PrepareAsync("1.60.1.70450", "1234ABCD");
        Assert.Equal(DefinitionState.FetchFailed, failure.State);
        Assert.Equal("DEFINITION_FETCH_OFFLINE", failure.Problem!.Code); Assert.True(failure.Problem.Retryable);
        Assert.Contains("layout 1234ABCD", failure.Problem.Message);
        // Back online a minute later, Retry asks at once; the fetched definition has no block for this layout either.
        h.Clock.Now += TimeSpan.FromMinutes(1); h.Serve(Revision, ItemWith("ABCD1234", "1.60.1.70450"));
        var missing = await h.PrepareAsync("1.60.1.70450", "1234ABCD");
        Assert.Equal(DefinitionState.NoMatch, missing.State);
        Assert.Equal("DB2_SCHEMA_MISSING", missing.Problem!.Code); Assert.Contains("layout 1234ABCD", missing.Problem.Message);
        Assert.Contains("WoWDBDefs definition fetched", missing.Problem.Message);
    }

    [Fact]
    public async Task Switching_fetching_off_and_on_reaches_a_table_that_was_already_open()
    {
        var h = await Create();
        h.Serve(Revision, ItemWith("9A2A4834", "1.60.1.70500"));
        var fetched = await h.PrepareAsync("1.60.1.70500", "9A2A4834");
        Assert.Equal(FetchedBecause.ExactBuild, fetched.Because);
        // Off: the next choice is bundled by layout, from local state, and nothing is asked.
        h.Switch.Enabled = false;
        var asked = h.Asked;
        var bundled = await h.PrepareAsync("1.60.1.70500", "9A2A4834");
        Assert.Equal(DefinitionSource.Bundled, bundled.Definition!.Source); Assert.Null(bundled.Because); Assert.Equal(SchemaMatch.Layout, bundled.Match);
        Assert.Null(bundled.Fetched); Assert.Equal("off", bundled.DefinitionFetch);
        Assert.Null(await h.Definitions.LastCheckAsync(ItemTable, "1.60.1.70500", "9A2A4834", default));
        // On again: the exact fetched definition in the cache wins, without asking.
        h.Switch.Enabled = true;
        var again = await h.PrepareAsync("1.60.1.70500", "9A2A4834");
        Assert.Equal(fetched.Definition!.Sha256, again.Definition!.Sha256); Assert.Equal(fetched.Definition.Fetched, again.Definition.Fetched); Assert.Equal(FetchedBecause.ExactBuild, again.Because);
        Assert.NotNull(await h.Definitions.LastCheckAsync(ItemTable, "1.60.1.70500", "9A2A4834", default));
        Assert.Equal(asked, h.Asked);
    }

    [Theory]
    [InlineData("77.0.0.100", "9A2A4834", "77.0.0.100", FetchedBecause.ExactBuild, false, false)]
    [InlineData("1.60.1.70500", "9A2A4834", "1.60.1.70500", FetchedBecause.ExactBuild, false, true)]
    [InlineData("1.60.1.70450", "1234ABCD", "1.60.1.70450", FetchedBecause.LayoutNotBundled, false, false)]
    // The bundled definition lists the build; the fetched one has the layout only on another build of that line.
    [InlineData("1.60.1.70450", "1234ABCD", "1.60.1.69000", FetchedBecause.LayoutNotBundled, true, false)]
    [InlineData("77.0.0.200", "9A2A4834", "77.0.0.100", FetchedBecause.LayoutMatch, true, false)]
    public async Task Rows_say_which_rule_chose_the_fetched_definition(string build, string layout, string listed, FetchedBecause because,
        bool byLayout, bool fallback)
    {
        var h = await Create();
        h.Serve(Revision, ItemWith(layout, listed));
        var chosen = await h.PrepareAsync(build, layout);
        Assert.Equal(DefinitionState.Resolved, chosen.State); Assert.Equal(DefinitionSource.Fetched, chosen.Definition!.Source);
        Assert.Equal(because, chosen.Because);
        Assert.Equal(byLayout ? SchemaMatch.Layout : SchemaMatch.Build, chosen.Match); Assert.Equal(chosen.Match, chosen.Schema!.Match);
        Assert.Equal(fallback, chosen.BundledFallback);
    }

    [Fact]
    public async Task Stored_rows_whose_re_check_failed_cost_one_attempt_per_prepare()
    {
        var h = await Create();
        h.Serve(Revision, ItemWith("9A2A4834", "77.0.0.100"));
        Assert.NotNull((await h.PrepareAsync("77.0.0.100", "9A2A4834")).Definition!.Fetched);
        // The cache is gone (a fresh source) and GitHub can't be reached: the stored rows' re-check asks and fails, and the
        // load after it in the same prepare reuses the outcome instead of asking a second time.
        var empty = new InMemoryObjectSource { Time = h.Clock };
        empty.FailObservation(ItemTable, ObservationRequest.DefinitionRegion, ObservationKind.Definition, ObjectState.Unavailable, "CDN_OFFLINE");
        var restarted = new TableDefinitions(h.Bundled, new FetchedDefinitions(empty, h.Clock), h.Switch, h.Clock);
        var budget = new DefinitionFetchBudget();
        var recheck = await restarted.ResolveAsync(ItemTable, "77.0.0.100", "9A2A4834", WowLocale.EnUS,
            DefinitionLookup.Ask(budget, _ => Task.CompletedTask), default);
        Assert.True(recheck.Asked); Assert.True(budget.Asked); Assert.Equal("DEFINITION_FETCH_OFFLINE", budget.Failure!.Code);
        var load = await h.PrepareAsync("77.0.0.100", "9A2A4834", budget, definitions: restarted);
        Assert.Equal(DefinitionState.FetchFailed, load.State); Assert.Equal("DEFINITION_FETCH_OFFLINE", load.Problem!.Code); Assert.False(load.Asked);
        Assert.Equal(1, empty.Observed.Count(o => o.Kind == ObservationKind.Definition));
    }

    // Review fix (#84): a feature's definition failure carries the table and the switch as fields, on every path, also
    // when its words never name the table. contracts/examples/definition-errors.json holds exactly what the engine sends.
    [Fact]
    public async Task Feature_definition_failures_name_their_table_and_switch_as_fields_as_the_contract_example_shows()
    {
        var h = await Create();
        // WoWDBDefs' newest ItemSparse doesn't list the build either: the words name no table.
        h.Serve(Revision, PinnedDefinitionFiles.Bytes("ItemSparse"), "919be54e", "ItemSparse");
        var lookup = DefinitionLookup.Ask(new(), _ => Task.CompletedTask, deliberate: true);
        var neither = (await h.Definitions.ResolveAsync("919be54e", "99.0.0.1", null, WowLocale.EnUS, lookup, default, "Items")).Problem!;
        Assert.DoesNotContain("ItemSparse", neither.Message);
        // GitHub unreachable while Item needs a fetched definition.
        h.Offline();
        var unreachable = (await h.Definitions.ResolveAsync(ItemTable, "99.0.0.1", null, WowLocale.EnUS,
            DefinitionLookup.Ask(new(), _ => Task.CompletedTask, deliberate: true), default, "Items")).Problem!;
        // Fetching off, from the same start Items makes before any download.
        h.Switch.Enabled = false;
        var off = await Assert.ThrowsAsync<DefinitionException>(() => h.Definitions.RequireBuildAsync(ItemTable, "Items", "99.0.0.1", default));
        var creatures = await Assert.ThrowsAsync<DefinitionException>(() => h.Definitions.RequireBuildAsync("c9d6b6b3", "Creatures", "99.0.0.1", default));
        static JsonNode Body(DefinitionException error) => new JsonObject
        {
            ["code"] = error.Code, ["message"] = error.Message, ["retryable"] = error.Retryable,
            ["definition"] = new JsonObject { ["tableId"] = error.Definition!.TableId, ["definitionFetch"] = error.Definition.DefinitionFetch },
        };
        var actual = new JsonObject
        {
            ["creaturesFetchingOff"] = Body(creatures), ["itemSparseNotListed"] = Body(neither),
            ["itemFetchOffline"] = Body(unreachable), ["itemsFetchingOff"] = Body(off),
        };
        var example = JsonNode.Parse(File.ReadAllText(RepositoryPaths.File("contracts/examples/definition-errors.json")));
        Assert.True(JsonNode.DeepEquals(example, actual), actual.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Assert.Equal(new DefinitionProblem("919be54e", "on"), neither.Definition);
        Assert.Equal(new DefinitionProblem(ItemTable, "on"), unreachable.Definition);
        Assert.Equal(new DefinitionProblem(ItemTable, "off"), off.Definition);
    }

    // ---- The switch fails closed (D081) ----

    [Fact]
    public async Task A_switch_that_throws_reads_as_off_and_nothing_is_fetched()
    {
        var h = await Create();
        h.Serve(Revision, ItemWith("9A2A4834", "77.0.0.100"));
        h.Switch.Failure = new IOException("settings unreadable");
        Assert.False(await h.Definitions.FetchEnabledAsync(default));
        var resolved = await h.PrepareAsync("77.0.0.100", "9A2A4834");
        Assert.Equal(DefinitionState.FetchingOff, resolved.State); Assert.Equal("off", resolved.DefinitionFetch);
        Assert.Equal(new DefinitionProblem(ItemTable, "off"), resolved.Problem!.Definition);
        Assert.Equal("SCHEMA_BUILD_NOT_LISTED", (await Assert.ThrowsAsync<DefinitionException>(() =>
            h.Definitions.RequireBuildAsync(ItemTable, "Items", "77.0.0.100", default))).Code);
        Assert.False(await h.Definitions.RefreshDueAsync(ItemTable, "77.0.0.100", "9A2A4834", default));
        Assert.Equal(0, h.Asked);
    }

    [Fact]
    public async Task Cancelling_while_the_switch_reads_stops_the_resolution()
    {
        var h = await Create();
        using var cancel = new CancellationTokenSource(); await cancel.CancelAsync();
        h.Switch.Failure = new OperationCanceledException(cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Definitions.ResolveAsync(ItemTable, "77.0.0.100", null, WowLocale.EnUS, DefinitionLookup.Local, cancel.Token));
        // A cancellation that isn't the caller's is just a switch that failed: off.
        Assert.False(await h.Definitions.FetchEnabledAsync(default));
    }

    [Fact]
    public async Task Without_a_switch_or_a_fetched_source_fetching_is_off()
    {
        var bundled = await BundledDefinitions.OpenAsync(default);
        var source = new InMemoryObjectSource(); source.AddDefinition(ItemTable, "Item", Revision, ItemWith("9A2A4834", "77.0.0.100"));
        foreach (var definitions in new[] { new TableDefinitions(bundled, new FetchedDefinitions(source), null), new TableDefinitions(bundled, null, new Switch(true)) })
        {
            Assert.False(await definitions.FetchEnabledAsync(default));
            var resolved = await definitions.ResolveAsync(ItemTable, "77.0.0.100", "9A2A4834", WowLocale.EnUS,
                DefinitionLookup.Ask(new(), _ => Task.CompletedTask, true), default);
            Assert.Equal(DefinitionState.FetchingOff, resolved.State);
            Assert.Null(await definitions.LastCheckAsync(ItemTable, "77.0.0.100", "9A2A4834", default));
        }
        Assert.Empty(source.Observed);
    }

    // ---- Typed states ----

    [Fact]
    public async Task A_table_the_provider_does_not_list_is_not_in_the_provider()
    {
        var h = await Create();
        var resolved = await h.Definitions.ResolveAsync("ffffffff", "11.0.7.58123", null, WowLocale.EnUS,
            DefinitionLookup.Ask(new(), _ => Task.CompletedTask, true), default);
        Assert.Equal(DefinitionState.NotInProvider, resolved.State); Assert.Null(resolved.Table);
        Assert.Equal("TABLE_NOT_IN_PROVIDER", resolved.Problem!.Code);
        Assert.Equal("DEFINITION_NOT_BUNDLED", (await Assert.ThrowsAsync<DefinitionException>(() =>
            h.Definitions.RequireBuildAsync("ffffffff", "Items", "11.0.7.58123", default))).Code);
        Assert.Equal(0, h.Asked);
    }

    [Fact]
    public async Task A_placeholder_bundled_table_is_unreadable_and_never_fetches_a_replacement()
    {
        var bundled = await Authored(("Empty", "COLUMNS\n"));
        var h = await Create(bundled: bundled);
        h.Serve(Revision, ItemWith("00000001", "1.2.3.4"), "00000001", "Empty");
        var resolved = await h.PrepareAsync("1.2.3.4", "00000001", table: "00000001");
        Assert.Equal(DefinitionState.Unreadable, resolved.State); Assert.Equal("Empty", resolved.Table!.Name);
        Assert.Equal("DEFINITION_UNSUPPORTED", resolved.Problem!.Code); Assert.Null(resolved.Definition);
        Assert.Equal(new DefinitionProblem("00000001", "on"), resolved.Problem.Definition);
        Assert.False(await h.Definitions.RefreshDueAsync("00000001", "1.2.3.4", "00000001", default));
        Assert.Null(await h.Definitions.LastCheckAsync("00000001", "1.2.3.4", "00000001", default));
        Assert.Equal("DEFINITION_UNSUPPORTED", (await Assert.ThrowsAsync<DefinitionException>(() =>
            h.Definitions.RequireBuildAsync("00000001", "Items", "1.2.3.4", default))).Code);
        Assert.Empty(h.Source.Observed);
    }

    [Fact]
    public async Task A_bundled_table_without_a_feature_listing_fetches_too()
    {
        // D120: fetching applies to every named table, not only the feature ones.
        var bundled = await Authored(("Alpha", "COLUMNS\nint ID\n\nLAYOUT 00000001\nBUILD 1.2.3.4\n$id$ID<32>\n"));
        var h = await Create(bundled: bundled);
        h.Source.AddDefinition("00000001", "Alpha", Revision, Encoding.UTF8.GetBytes("COLUMNS\nint ID\n\nLAYOUT 00000001\nBUILD 2.0.0.1\n$id$ID<32>\n"));
        var resolved = await h.Definitions.ResolveAsync("00000001", "2.0.0.1", "00000001", WowLocale.EnUS,
            DefinitionLookup.Ask(new(), _ => Task.CompletedTask, true), default);
        Assert.Equal(DefinitionState.Resolved, resolved.State); Assert.Equal(DefinitionSource.Fetched, resolved.Definition!.Source);
        Assert.Equal(FetchedBecause.ExactBuild, resolved.Because);
    }

    [Fact]
    public async Task A_block_the_schema_refuses_is_its_own_state()
    {
        var bundled = await Authored(("Alpha", "COLUMNS\nint ID\nint Count\n\nLAYOUT 00000001\nBUILD 1.2.3.4\n$id$ID<32>\nCount\n"));
        var h = await Create(bundled: bundled);
        var resolved = await h.Definitions.ResolveAsync("00000001", "1.2.3.4", "00000001", WowLocale.EnUS, DefinitionLookup.Local, default);
        Assert.Equal(DefinitionState.BlockUnsupported, resolved.State);
        Assert.Equal("DEFINITION_BLOCK_UNSUPPORTED", resolved.Problem!.Code); Assert.Contains("'Count'", resolved.Problem.Message);
        Assert.NotNull(resolved.Definition); Assert.Null(resolved.Schema); Assert.Equal(new DefinitionProblem("00000001", "on"), resolved.Problem.Definition);
    }

    [Fact]
    public async Task Equally_near_layout_blocks_with_different_fields_are_ambiguous()
    {
        var bundled = await Authored(("Alpha",
            "COLUMNS\nint ID\nint Value\nfloat Scale\n\nLAYOUT 0000000A\nBUILD 1.2.3.10\n$id$ID<32>\nValue<32>\n\nLAYOUT 0000000A\nBUILD 1.2.3.30\n$id$ID<32>\nScale\n"));
        var h = await Create(enabled: false, bundled: bundled);
        var resolved = await h.Definitions.ResolveAsync("00000001", "1.2.3.20", "0000000A", WowLocale.EnUS, DefinitionLookup.Local, default);
        Assert.Equal(DefinitionState.Ambiguous, resolved.State); Assert.Equal("DB2_SCHEMA_AMBIGUOUS", resolved.Problem!.Code);
        Assert.Equal(SchemaMatch.Layout, resolved.Match); Assert.Null(resolved.Schema); Assert.NotNull(resolved.Definition);
    }

    [Fact]
    public async Task Locstring_columns_carry_the_requested_locale()
    {
        var bundled = await Authored(("Alpha", "COLUMNS\nint ID\nlocstring Name_lang\n\nLAYOUT 00000001\nBUILD 1.2.3.4\n$id$ID<32>\nName_lang\n"));
        var h = await Create(bundled: bundled);
        var resolved = await h.Definitions.ResolveAsync("00000001", "1.2.3.4", "00000001", WowLocale.DeDE, DefinitionLookup.Local, default);
        Assert.Equal(DefinitionState.Resolved, resolved.State); Assert.Equal(WowLocale.DeDE, resolved.Schema!.Locale);
        var name = resolved.Schema.Columns.Single(column => column.Name == "Name_lang");
        Assert.Equal(ColumnKind.LocString, name.Kind); Assert.Equal(WowLocale.DeDE, name.Locale);
        Assert.Null(resolved.Schema.Columns.Single(column => column.Name == "ID").Locale);
    }

    [Fact]
    public async Task Without_a_layout_the_schema_is_set_only_when_the_build_selects_one_block()
    {
        var h = await Create(enabled: false);
        // 1.60.1.70450 is listed by one Item block; 1.60.1.70500 opens only by layout, so there is no schema yet.
        var listed = await h.LocalAsync("1.60.1.70450", null);
        Assert.Equal(DefinitionState.Resolved, listed.State); Assert.Equal(SchemaMatch.Build, listed.Match); Assert.NotNull(listed.Schema);
        var byLayout = await h.LocalAsync("1.60.1.70500", null);
        Assert.Equal(DefinitionState.Resolved, byLayout.State); Assert.Equal(SchemaMatch.Layout, byLayout.Match); Assert.Null(byLayout.Schema);
    }
}
