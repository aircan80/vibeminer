using System.Text;
using System.Text.Json;
using Spectre.Console;
using VibeMiner.Builds;
using VibeMiner.Cli.Browse;
using VibeMiner.Files;
using VibeMiner.Formats;
using VibeMiner.Formats.Tables;
using VibeMiner.Formats.Testing;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Cli.Tests;

/// <summary><c>vm tables states</c> against <see cref="InMemoryTables"/> through the command's tables seam; no store, no network.</summary>
public sealed class TablesStatesTests
{
    private const string Data = "/data/vm";
    private static readonly string StoreFolder = Path.Combine(Data, ObjectStore.DefaultFolderName);
    private static readonly string A = new('a', 32), B = new('b', 32), C = new('c', 32), D = new('d', 32);
    private static readonly BuildRef UsBuild = new("wow", A, B, "us");

    private readonly FakeStores stores = new();
    private readonly StringWriter output = new(), error = new(), json = new();
    private readonly InMemoryTables fake = new();
    private readonly List<(bool Writer, string Data, bool Fetch)> composed = [];
    private int leasesClosed;
    private ITables? served;

    public TablesStatesTests() => fake.Add(UsBuild, "enUS", Seed());

    private static readonly Db2Column[] Columns = [new("ID", "integer", true), new("Name", "string", false)];

    private static Db2Row[] Rows(int count) =>
        Enumerable.Range(1, count).Select(i => new Db2Row((uint)i, false, null, [i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"row {i}"])).ToArray();

    /// <summary>ItemSparse (60 rows) and SpellName (3) Ready, 300 without a definition, Locked Encrypted, Gone Missing. No Creature.</summary>
    private static FakeTable[] Seed() =>
    [
        new(100, "ItemSparse", TableState.Ready, Columns, Rows(60)),
        new(200, "SpellName", TableState.Ready, Columns, Rows(3)),
        new(300, null, TableState.NoDefinition),
        new(400, "Locked", TableState.Encrypted, Columns, ProtectedIds: new Dictionary<uint, string> { [5] = "ABCDEF0123456789" }),
        new(500, "Gone", TableState.Missing, Reason: new TableStateReason("OBJECT_MISSING", "The CDN answered 404.", null, null, null, [])),
    ];

    private static CliEnvironment Env() => new("linux", name => name == "VIBEMINER_DATA_DIR" ? Data : null, "/home/nobody-cli-test");

    private Task<int> Run(params string[] args) => Run(CancellationToken.None, args);
    private Task<int> Run(CancellationToken ct, params string[] args) =>
        new VmCommands(Env(), stores, Consoles.Plain(output, 250), Consoles.Plain(error, 250), json, null, tables: (store, data, fetch, _) =>
        {
            composed.Add((store.Writer, data, fetch));
            return Task.FromResult(new TablesLease(served ?? fake, async () => { leasesClosed++; await store.DisposeAsync(); }));
        }).RunAsync(args, ct);

    private JsonElement JsonOut() => JsonDocument.Parse(json.ToString()).RootElement;

    /// <summary>The catalog lists one current wow build in eu then us (us is picked), and an older one.</summary>
    private async Task ServeAsync(string product = "wow")
    {
        const string header = "Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16";
        var versions = Encoding.UTF8.GetBytes($"{header}\n## seqn = 1\neu|{A}|{D}|{C}|58123|11.0.7.58123|{C}\nus|{A}|{B}|{C}|58123|11.0.7.58123|{C}\n");
        stores.Source.AddReply(product, "us", ObservationKind.Versions, versions);
        await stores.Source.ObserveAsync(new(ObservationKind.Versions, product, "us"), default);
    }

    [Fact]
    public async Task JsonReportsTheBuildEveryTableAndTheCounts()
    {
        await ServeAsync();
        Assert.Equal(VmCommands.Ok, await Run("--json", "tables", "states", "wow"));
        var doc = JsonOut();
        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.True(doc.GetProperty("store").GetProperty("writer").GetBoolean());
        var build = doc.GetProperty("build");
        Assert.Equal("wow", build.GetProperty("product").GetString());
        Assert.Equal("11.0.7.58123", build.GetProperty("version").GetString());
        Assert.Equal(A, build.GetProperty("buildConfig").GetString());
        Assert.Equal(B, build.GetProperty("cdnConfig").GetString());
        Assert.Equal("us", build.GetProperty("region").GetString());
        Assert.Equal("enUS", doc.GetProperty("locale").GetString());
        Assert.False(doc.GetProperty("fetchDefinitions").GetBoolean());
        Assert.Equal(5, doc.GetProperty("tableCount").GetInt32());
        Assert.True(doc.GetProperty("checkMs").GetDouble() >= 0);

        var counts = doc.GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("Ready").GetInt32());
        Assert.Equal(1, counts.GetProperty("NoDefinition").GetInt32());
        Assert.Equal(1, counts.GetProperty("Encrypted").GetInt32());
        Assert.Equal(1, counts.GetProperty("Missing").GetInt32());
        Assert.Equal(4, counts.EnumerateObject().Count());

        var tables = doc.GetProperty("tables").EnumerateArray().ToList();
        Assert.Equal([100u, 200u, 300u, 400u, 500u], tables.Select(t => t.GetProperty("fileDataId").GetUInt32()));
        Assert.All(tables, t => Assert.True(t.GetProperty("checked").GetBoolean()));
        var item = tables[0];
        Assert.Equal("ItemSparse", item.GetProperty("name").GetString());
        Assert.Equal("Ready", item.GetProperty("state").GetString());
        Assert.Equal(60, item.GetProperty("readableRows").GetInt64());
        Assert.Equal(60, item.GetProperty("declaredRows").GetInt64());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("reasonCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, tables[2].GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, tables[2].GetProperty("readableRows").ValueKind);
        Assert.Equal("OBJECT_MISSING", tables[4].GetProperty("reasonCode").GetString());
        // Encrypted: opened, its one row in a keyed section, so nothing readable.
        Assert.Equal(0, tables[3].GetProperty("readableRows").GetInt64());
        Assert.Equal(1, tables[3].GetProperty("declaredRows").GetInt64());

        // The three big tables, each paged twice; one the build lacks says so instead of failing the run.
        var pages = doc.GetProperty("pages").EnumerateArray().ToList();
        Assert.Equal(VmCommands.TimedTables, pages.Select(p => p.GetProperty("name").GetString()));
        Assert.Equal("Ok", pages[0].GetProperty("pageState").GetString());
        Assert.Equal(VmCommands.TimedPageRows, pages[0].GetProperty("rows").GetInt32());
        Assert.Equal(100u, pages[0].GetProperty("fileDataId").GetUInt32());
        Assert.Equal(3, pages[1].GetProperty("rows").GetInt32());
        Assert.Equal("NoDefinition", pages[2].GetProperty("state").GetString());
        Assert.Equal("NotReadable", pages[2].GetProperty("pageState").GetString());
        Assert.All(pages, p => Assert.True(p.GetProperty("firstMs").GetDouble() >= 0 && p.GetProperty("secondMs").GetDouble() >= 0));

        Assert.Equal(6, fake.Calls(nameof(IBuildTables.PageAsync)));
        Assert.Equal(5, fake.Calls(nameof(IBuildTables.CheckAsync)));
        Assert.Equal([(true, Data, false)], composed);
        Assert.Equal(1, leasesClosed);
        Assert.Equal(0, stores.Open);
        Assert.Equal([false], stores.WriterCreate);
    }

    [Fact]
    public async Task FetchDefinitionsIsPassedToTheComposition()
    {
        await ServeAsync();
        Assert.Equal(VmCommands.Ok, await Run("--json", "--fetch-definitions", "tables", "states", "wow"));
        Assert.True(JsonOut().GetProperty("fetchDefinitions").GetBoolean());
        Assert.Equal([(true, Data, true)], composed);
    }

    [Fact]
    public async Task TextPrintsTheCountsAndTimings()
    {
        await ServeAsync();
        Assert.Equal(VmCommands.Ok, await Run("tables", "states", "wow"));
        var text = output.ToString();
        Assert.Contains("wow 11.0.7.58123 (us) enUS", text);
        Assert.Matches(@"│ Ready\s+│\s+2 │", text);
        Assert.Contains("5 tables checked", text);
        Assert.Contains("ItemSparse", text);
        Assert.Equal("", json.ToString());
    }

    [Fact]
    public async Task UsageErrors()
    {
        Assert.Equal(VmCommands.Usage, await Run("tables", "states", "nope"));
        Assert.Contains("nope isn't a reviewed product", error.ToString());
        Assert.Equal(VmCommands.Usage, await Run("tables", "states"));
        Assert.Equal(VmCommands.Usage, await Run("--fetch-definitions", "store", "info"));
        Assert.Empty(stores.Opened);
        Assert.Contains("tables states <product>", VmCommands.UsageText);
    }

    [Fact]
    public async Task NoCurrentBuildFailsWithoutComposing()
    {
        Assert.Equal(VmCommands.Failed, await Run("--json", "tables", "states", "wow"));
        Assert.Equal("TABLES_NO_CURRENT_BUILD", JsonOut().GetProperty("code").GetString());
        Assert.Empty(composed);
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task ABusyStoreFails()
    {
        stores.WriterFailure = ObjectStoreCodes.Busy;
        Assert.Equal(VmCommands.Failed, await Run("--json", "tables", "states", "wow"));
        Assert.Equal(ObjectStoreCodes.Busy, JsonOut().GetProperty("code").GetString());
        Assert.Empty(composed);
    }

    [Fact]
    public async Task AFailedOpenIsNamedAndClosesTheLease()
    {
        await ServeAsync();
        fake.AddFailing(UsBuild, FilesOpenState.NeedsNetwork, "BUILD_NEEDS_NETWORK");
        Assert.Equal(VmCommands.Failed, await Run("--json", "tables", "states", "wow"));
        Assert.Equal("BUILD_NEEDS_NETWORK", JsonOut().GetProperty("code").GetString());
        Assert.Equal(1, leasesClosed);
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task FullTableQueuesAreANamedFailure()
    {
        await ServeAsync();
        served = new BusyTables();
        Assert.Equal(VmCommands.Failed, await Run("--json", "tables", "states", "wow"));
        Assert.Equal(TablesBusyException.BusyCode, JsonOut().GetProperty("code").GetString());
        Assert.Equal(1, leasesClosed);
        Assert.Equal(0, stores.Open);
        Assert.StartsWith("TABLES_BUSY: ", BrowseSession.Explain(new TablesBusyException(TablePriority.Interactive)), StringComparison.Ordinal);
    }

    private sealed class BusyTables : ITables
    {
        public Task<TablesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken) =>
            throw new TablesBusyException(TablePriority.Background);
    }

    [Fact]
    public async Task CtrlCCancelsTheChecksCleanly()
    {
        await ServeAsync();
        using var cancel = new CancellationTokenSource();
        var blocking = new BlockingChecks(fake);
        served = blocking;
        var run = Run(cancel.Token, "--json", "tables", "states", "wow");
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        Assert.Equal(VmCommands.Canceled, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("CANCELED", JsonOut().GetProperty("code").GetString());
        Assert.Equal(1, leasesClosed);
        Assert.Equal(0, stores.Open);
        Assert.True(blocking.MostAtOnce <= VmCommands.CheckConcurrency);
    }

    [Fact]
    public async Task ChecksRunAtMostFourAtATime()
    {
        await ServeAsync();
        var many = Enumerable.Range(1, 40).Select(i => new FakeTable((uint)(1000 + i), $"T{i}", TableState.Ready, Columns, Rows(1))).ToArray();
        fake.Add(UsBuild, "enUS", many);
        var slow = new BlockingChecks(fake, block: false);
        served = slow;
        Assert.Equal(VmCommands.Ok, await Run("--json", "tables", "states", "wow"));
        Assert.Equal(40, JsonOut().GetProperty("tableCount").GetInt32());
        Assert.InRange(slow.MostAtOnce, 1, VmCommands.CheckConcurrency);
    }

    /// <summary>Wraps the fake: CheckAsync records how many run at once, and either waits for cancellation or yields briefly.</summary>
    private sealed class BlockingChecks(ITables inner, bool block = true) : ITables
    {
        private int running, most;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MostAtOnce => Volatile.Read(ref most);

        public async Task<TablesOpenResult> ForAsync(BuildRef build, string locale, CancellationToken cancellationToken)
        {
            var opened = await inner.ForAsync(build, locale, cancellationToken);
            return opened.Tables is { } tables ? opened with { Tables = new Handle(this, tables) } : opened;
        }

        private sealed class Handle(BlockingChecks owner, IBuildTables inner) : IBuildTables
        {
            public BuildRef Build => inner.Build;
            public string Locale => inner.Locale;
            public ValueTask DisposeAsync() => inner.DisposeAsync();
            public IAsyncEnumerable<TableEntry> ListAsync(TableListFilter filter, TablePriority priority, CancellationToken ct) => inner.ListAsync(filter, priority, ct);
            public async Task<TableEntry> CheckAsync(TableRef table, TablePriority priority, CancellationToken ct)
            {
                Assert.Equal(TablePriority.Background, priority);
                var now = Interlocked.Increment(ref owner.running);
                int seen;
                while ((seen = Volatile.Read(ref owner.most)) < now && Interlocked.CompareExchange(ref owner.most, now, seen) != seen) { }
                try
                {
                    owner.Started.TrySetResult();
                    if (owner.Blocks) await Task.Delay(Timeout.Infinite, ct);
                    else await Task.Delay(5, ct);
                    return await inner.CheckAsync(table, priority, ct);
                }
                finally { Interlocked.Decrement(ref owner.running); }
            }
            public Task<TableSchemaResult> SchemaAsync(TableRef table, TablePriority priority, CancellationToken ct) => inner.SchemaAsync(table, priority, ct);
            public Task<TablePage> PageAsync(TablePageRequest request, TablePriority priority, CancellationToken ct) => inner.PageAsync(request, priority, ct);
            public Task<TableRowResult> GetAsync(TableRef table, uint id, TablePriority priority, CancellationToken ct) => inner.GetAsync(table, id, priority, ct);
            public Task<TableFollowResult> FollowAsync(TableFollowRequest request, TablePriority priority, CancellationToken ct) => inner.FollowAsync(request, priority, ct);
            public Task<TableReferencesResult> ReferencedByAsync(TableReferencesRequest request, TablePriority priority, CancellationToken ct) => inner.ReferencedByAsync(request, priority, ct);
        }

        private bool Blocks { get; } = block;
    }
}
