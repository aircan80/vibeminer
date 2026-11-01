using System.Text;
using System.Text.Json;
using VibeMiner.Builds;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using Spectre.Console;
using VibeMiner.Cli.Profile;
using Xunit;

namespace VibeMiner.Cli.Tests;

public sealed class CommandTests : IDisposable
{
    // A temp data folder: store scrub writes its record there. Created on first write, removed after each test.
    private readonly string Data = Path.Combine(Path.GetTempPath(), "vibeminer-cli-commands-" + Guid.NewGuid().ToString("N"));
    private string StoreFolder => Path.Combine(Data, ObjectStore.DefaultFolderName);

    public void Dispose() { if (Directory.Exists(Data)) Directory.Delete(Data, true); }

    private readonly FakeStores stores = new();
    private readonly StringWriter output = new(), error = new(), json = new();

    /// <summary>A plain, non-interactive console over a writer: no ANSI codes, wide enough that no cell wraps.</summary>
    private static IAnsiConsole Plain(TextWriter writer) => Consoles.Plain(writer, 250);

    private static CliEnvironment Env(params (string, string)[] variables) =>
        new("linux", name => variables.FirstOrDefault(v => v.Item1 == name).Item2, "/home/nobody-cli-test");

    private Task<int> Run(params string[] args) => Run(CancellationToken.None, args);
    private Task<int> Run(CancellationToken ct, params string[] args) =>
        new VmCommands(Env(("VIBEMINER_DATA_DIR", Data)), stores, Plain(output), Plain(error), json, time: clock).RunAsync(args, ct);

    private readonly ManualTime clock = new(new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero));

    private void ClearOutput()
    {
        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        json.GetStringBuilder().Clear();
    }

    private JsonElement JsonOut() => JsonDocument.Parse(json.ToString()).RootElement;

    /// <summary>The rows of the tables a command printed, as trimmed cell texts (borders and rules dropped).</summary>
    private static List<string[]> Rows(StringWriter writer) => writer.ToString().Split('\n')
        .Select(line => line.Split('│').Select(cell => cell.Trim()).ToArray())
        .Where(cells => cells.Length > 2).Select(cells => cells[1..^1]).ToList();

    [Fact]
    public void APlainConsoleStaysPlainOnACiRunner()
    {
        // #183: on GitHub runners Spectre's enrichers turned ANSI back on and table cells came back bold-coded.
        var ci = new Dictionary<string, string> { ["GITHUB_ACTIONS"] = "true", ["CI"] = "true", ["TERM"] = "xterm-256color" };
        var bold = new Text("Data folder", new Style(Color.Red, decoration: Decoration.Bold));

        // The environment really is ANSI-capable for a console that keeps the default enrichers.
        var enriched = new StringWriter();
        var withEnrichers = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(enriched), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, EnvironmentVariables = ci,
        });
        withEnrichers.Write(bold);
        Assert.Contains('\u001b', enriched.ToString());

        var plain = new StringWriter();
        var console = Consoles.Plain(plain, 250, ci);
        console.Write(bold);
        Assert.Equal("Data folder", plain.ToString());
        Assert.False(console.Profile.Capabilities.Ansi);
    }

    [Fact]
    public async Task InfoOpensReadOnlyAndShowsTheStoreFigures()
    {
        Assert.Equal(VmCommands.Ok, await Run("store", "info"));
        Assert.Equal([(StoreFolder, false)], stores.Opened);
        Assert.Equal(0, stores.Open);
        var rows = Rows(output);
        Assert.Contains(["Data folder", $"{Data} (VIBEMINER_DATA_DIR)"], rows);
        Assert.Contains(["Packs", "2"], rows);
        Assert.Contains(["Objects", "1,234 (56 history records)"], rows);
        Assert.Contains(["Bytes", "3.0 MiB in packs, 2.0 MiB live"], rows);
        Assert.Contains(["Last scrub", "none yet (vm store scrub runs one)"], rows);
        Assert.DoesNotContain(rows, r => r[0] == "Scrub problem");
        Assert.False(output.ToString().Contains('\u001b'), "no ANSI codes on a plain console");
        Assert.Equal("", json.ToString());
    }

    [Fact]
    public async Task InfoAsJsonIsOneDocumentScriptsCanRead()
    {
        Assert.Equal(VmCommands.Ok, await Run("--json", "store", "info"));
        var info = JsonOut();
        Assert.Equal("", output.ToString());
        Assert.True(info.GetProperty("ok").GetBoolean());
        Assert.Equal("VIBEMINER_DATA_DIR", info.GetProperty("dataFolder").GetProperty("source").GetString());
        Assert.False(info.GetProperty("store").GetProperty("writer").GetBoolean());
        Assert.Equal(1234, info.GetProperty("objects").GetInt64());
        Assert.Equal(3L << 20, info.GetProperty("packBytes").GetInt64());
        Assert.Equal("absent", info.GetProperty("lastScrub").GetProperty("state").GetString());
    }

    [Fact]
    public async Task AMissingStoreIsReportedAndNothingIsCreated()
    {
        stores.ReadOnlyFailure = ObjectStoreCodes.Missing;
        Assert.Equal(VmCommands.Failed, await Run("store", "info"));
        Assert.Contains("OBJECT_STORE_MISSING", error.ToString());
        Assert.Contains("Nothing was created.", error.ToString());
        Assert.Equal("", output.ToString());
        Assert.Equal(VmCommands.Failed, await Run("--json", "store", "info"));
        Assert.False(JsonOut().GetProperty("ok").GetBoolean());
        Assert.Equal(ObjectStoreCodes.Missing, JsonOut().GetProperty("code").GetString());
        Assert.All(stores.Opened, o => Assert.False(o.Writer));
    }

    [Fact]
    public async Task ScrubTakesTheWriterLockAndSaysSoWhenTheAppHoldsIt()
    {
        Assert.Equal(VmCommands.Ok, await Run("store", "scrub"));
        Assert.Equal([(StoreFolder, true)], stores.Opened);
        Assert.Contains(["Index", "sound"], Rows(output));
        Assert.Contains("as its writer", error.ToString());
        stores.WriterFailure = ObjectStoreCodes.Busy;
        Assert.Equal(VmCommands.Failed, await Run("--json", "store", "scrub"));
        Assert.Equal(ObjectStoreCodes.Busy, JsonOut().GetProperty("code").GetString());
        Assert.Contains("is VibeMiner running?", JsonOut().GetProperty("message").GetString());
    }

    [Fact]
    public async Task AScrubThatFindsDamageFailsAndNamesTheObjects()
    {
        var bad = ObjectKey.Parse(ObjectKeyType.Config, new string('a', 32));
        stores.Scrub = new("pack 3 is shorter than its rows", true, new VerifyReport(10, 100, 1, 0, [bad], []));
        Assert.Equal(VmCommands.Failed, await Run("store", "scrub"));
        Assert.Contains(["Index", "pack 3 is shorter than its rows (rebuilt from the packs)"], Rows(output));
        Assert.Contains(["corrupt", bad.ToString()], Rows(output));
        Assert.Equal(VmCommands.Failed, await Run("--json", "store", "scrub"));
        Assert.False(JsonOut().GetProperty("ok").GetBoolean());
        Assert.Equal(bad.ToString(), JsonOut().GetProperty("corrupt")[0].GetString());
    }

    [Fact]
    public async Task HistoryListsEveryPageOldestFirstAndFiltersByProduct()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        stores.Source.Time = time;
        stores.Source.AddReply("wow", "us", ObservationKind.Versions, Encoding.UTF8.GetBytes("wow versions"));
        stores.Source.AddReply("wowt", "eu", ObservationKind.Cdns, Encoding.UTF8.GetBytes("wowt cdns"));
        for (var i = 0; i < 1001; i++)
        {
            await stores.Source.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), default);
            time.Advance(TimeSpan.FromMinutes(1));
        }
        await stores.Source.ObserveAsync(new(ObservationKind.Cdns, "wowt", "eu"), default);

        Assert.Equal(VmCommands.Ok, await Run("--json", "history", "list"));
        var all = JsonOut();
        Assert.Equal(1002, all.GetProperty("count").GetInt32());
        var entries = all.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal(1002, entries.Length);
        Assert.Equal("wowt", entries[^1].GetProperty("product").GetString());
        Assert.Equal("cdns", entries[^1].GetProperty("kind").GetString());
        Assert.True(entries.Zip(entries.Skip(1)).All(p => p.First.GetProperty("observedAt").GetDateTimeOffset() <= p.Second.GetProperty("observedAt").GetDateTimeOffset()));

        Assert.Equal(VmCommands.Ok, await Run("history", "list", "wowt"));
        var rows = Rows(output);
        Assert.Equal(["Observed (UTC)", "Kind", "Product", "Region", "Document", "URL"], rows[0]);
        var row = Assert.Single(rows.Skip(1));
        Assert.Equal(["2026-01-01 16:41:00", "cdns", "wowt", "eu"], row[..4]);
        Assert.Contains("1 entry", output.ToString());
        Assert.All(stores.Opened, o => Assert.False(o.Writer));
    }

    [Fact]
    public async Task AHistoryReadThatFailsOnALaterPageStillClosesOneJsonDocument()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        stores.Source.Time = time;
        stores.Source.AddReply("wow", "us", ObservationKind.Versions, Encoding.UTF8.GetBytes("wow versions"));
        for (var i = 0; i < 1000; i++) { await stores.Source.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), default); time.Advance(TimeSpan.FromMinutes(1)); }
        stores.LaterHistoryPageThrows = new ObjectStoreException(ObjectStoreCodes.IndexUnreadable, "The history index has rows out of order.");

        Assert.Equal(VmCommands.Failed, await Run("--json", "history", "list"));
        var document = JsonOut();
        Assert.False(document.GetProperty("ok").GetBoolean());
        Assert.Equal(ObjectStoreCodes.IndexUnreadable, document.GetProperty("code").GetString());
        Assert.Equal(1000, document.GetProperty("count").GetInt32());
        Assert.Equal(1000, document.GetProperty("entries").GetArrayLength());
    }

    [Fact]
    public async Task AnUnexpectedErrorIsANamedFailureNotACrash()
    {
        stores.OpenThrows = new IOException("disk went away");
        Assert.Equal(VmCommands.Failed, await Run("--json", "store", "info"));
        Assert.Equal("CLI_FAILED", JsonOut().GetProperty("code").GetString());
        Assert.Contains("disk went away", JsonOut().GetProperty("message").GetString());
        Assert.Equal(VmCommands.Failed, await Run("store", "info"));
        Assert.Contains("CLI_FAILED", error.ToString());
    }

    [Fact]
    public async Task AnEmptyHistorySaysSo()
    {
        Assert.Equal(VmCommands.Ok, await Run("history", "list", "wow"));
        Assert.Equal("No history recorded for wow.", output.ToString().Trim());
        Assert.Equal(VmCommands.Ok, await Run("--json", "history", "list"));
    }

    [Theory]
    [InlineData()]
    [InlineData("store")]
    [InlineData("store", "wipe")]
    [InlineData("history", "list", "wow", "extra")]
    [InlineData("--frobnicate", "store", "info")]
    [InlineData("store", "info", "--data")]
    [InlineData("builds", "list", "agent")]
    [InlineData("builds", "list", "WOW")]
    [InlineData("builds", "refresh", "diablo3")]
    [InlineData("builds", "list", "wow", "extra")]
    public async Task BadUsageExitsWithTwoAndOpensNothing(params string[] args)
    {
        Assert.Equal(VmCommands.Usage, await Run(args));
        Assert.Contains("Usage: vm", error.ToString());
        Assert.Empty(stores.Opened);
    }

    [Fact]
    public async Task TheDataFlagBeatsTheEnvironment()
    {
        Assert.Equal(VmCommands.Ok, await Run("--data", "/elsewhere", "--json", "store", "info"));
        Assert.Equal([(Path.Combine("/elsewhere", ObjectStore.DefaultFolderName), false)], stores.Opened);
        Assert.Equal("--data", JsonOut().GetProperty("dataFolder").GetProperty("source").GetString());
    }

    [Fact]
    public async Task CancelingAScrubExitsWith130AndClosesTheStore()
    {
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        stores.BeforeScrub = async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); };
        var run = Run(cancel.Token, "store", "scrub");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        Assert.Equal(VmCommands.Canceled, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, stores.Open);
        Assert.Contains("CANCELED", error.ToString());
    }

    private const string VersionsHeader = "Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16";
    private static readonly string A = new('a', 32), B = new('b', 32), C = new('c', 32);
    private static readonly byte[] CdnsReply = "Name!STRING:0|Path!STRING:0|Hosts!STRING:0\nus|tpr/wow|cdn.example.invalid\n"u8.ToArray();

    /// <summary>The us version service answers for <paramref name="product"/> with one build in us and eu.</summary>
    private void Serve(string product, string name, string build)
    {
        var versions = Encoding.UTF8.GetBytes($"{VersionsHeader}\n## seqn = 1\nus|{build}|{B}|{C}|{name.Split('.')[^1]}|{name}|{C}\neu|{build}|{B}|{C}|{name.Split('.')[^1]}|{name}|{C}\n");
        stores.Source.AddReply(product, "us", ObservationKind.Versions, versions).AddReply(product, "us", ObservationKind.Cdns, CdnsReply);
    }

    private void ServeSummary(params string[] products) => stores.Source.AddReply("", "us", ObservationKind.Summary, Encoding.UTF8.GetBytes(
        "Product!STRING:0|Seqn!DEC:4|Flags!STRING:0\n## seqn = 9\n" + string.Concat(products.Select(p => $"{p}|1|\n{p}|2|cdn\n"))));

    private async Task<int> HistoryCount(HistoryKind? kind = null) => (await stores.Source.ReadHistoryAsync(new HistoryQuery(Kind: kind), default)).Count;

    [Fact]
    public async Task RefreshFetchesTheSummaryThenEveryWowProductItListsAsTheWriter()
    {
        Serve("wow", "12.0.1.60000", A);
        Serve("wowt", "12.0.5.61000", B);
        ServeSummary("wow", "wowt", "wowdev", "wow_classic_era_beta", "agent");
        Assert.Equal(VmCommands.Ok, await Run("builds", "refresh"));
        Assert.Equal([(StoreFolder, true)], stores.Opened);
        Assert.Equal([true], stores.WriterCreate);
        Assert.Equal(0, stores.Open);
        Assert.Contains("as the store's writer", error.ToString());
        var text = output.ToString();
        Assert.Contains("Retail (wow)", text);
        Assert.Contains("PTR (wowt)", text);
        Assert.Contains(Rows(output), r => r is ["12.0.1.60000", "yes", "eu us", _, _, var config] && config == A);
        // Every reviewed product is shown, grouped under its name; one the service doesn't answer for says so.
        Assert.All(Products.Reviewed, p => Assert.Contains($"{p.Label} ({p.Id})", text));
        var beta = text[text.IndexOf("Beta (wow_beta)", StringComparison.Ordinal)..];
        Assert.StartsWith("not available (CDN_NOT_FOUND)", beta[(beta.IndexOf('\n') + 1)..]);
        // D476: wow_classic_era_beta is reviewed; wowdev isn't, and is fetched and listed after the reviewed ones; agent isn't WoW.
        Assert.Contains("Classic Era Beta (wow_classic_era_beta)", text);
        Assert.Contains("Not reviewed, listed after the reviewed products: wowdev.", text);
        Assert.True(text.IndexOf("wowdev (not reviewed)", StringComparison.Ordinal) > text.IndexOf("Titan Reforged (China) (wow_classic_titan)", StringComparison.Ordinal));
        Assert.DoesNotContain("agent", text);
        Assert.Equal(1, await HistoryCount(HistoryKind.Summary));
        Assert.Equal(2, await HistoryCount(HistoryKind.Versions));
    }

    [Fact]
    public async Task RefreshAsJsonNamesEachProductAndTheNewOnes()
    {
        Serve("wow", "12.0.1.60000", A);
        ServeSummary("wow", "wowz");
        Assert.Equal(VmCommands.Ok, await Run("--json", "builds", "refresh", "wow"));
        var doc = JsonOut();
        Assert.True(doc.GetProperty("ok").GetBoolean());
        Assert.True(doc.GetProperty("store").GetProperty("writer").GetBoolean());
        Assert.Equal(["wow"], doc.GetProperty("fetched").EnumerateArray().Select(e => e.GetString()));
        var wow = Assert.Single(doc.GetProperty("products").EnumerateArray());
        Assert.Equal(("wow", "Retail", true), (wow.GetProperty("id").GetString(), wow.GetProperty("label").GetString(), wow.GetProperty("available").GetBoolean()));
        Assert.Equal("present", wow.GetProperty("versions").GetProperty("state").GetString());
        var build = Assert.Single(wow.GetProperty("builds").EnumerateArray());
        Assert.Equal(("12.0.1.60000", A), (build.GetProperty("versionsName").GetString(), build.GetProperty("buildConfig").GetString()));
        Assert.Equal(["eu", "us"], build.GetProperty("regions").EnumerateArray().Select(r => r.GetProperty("region").GetString()));
        var summary = doc.GetProperty("summary");
        Assert.Equal(["wowz"], summary.GetProperty("newProducts").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(2, summary.GetProperty("listed").GetInt32());
        Assert.Equal("https://us.version.battle.net/v2/summary", summary.GetProperty("observation").GetProperty("url").GetString());
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task ListFetchesOnFirstUseAsTheWriterThenReadsOnly()
    {
        Serve("wow", "12.0.1.60000", A);
        ServeSummary("wow");
        Assert.Equal(VmCommands.Ok, await Run("builds", "list", "wow"));
        Assert.Equal([(StoreFolder, false), (StoreFolder, true)], stores.Opened);
        Assert.Contains("No history for wow: fetching it from the version service", error.ToString());
        Assert.Contains("takes the writer lock", error.ToString());
        Assert.Contains("Retail (wow)", output.ToString());
        Assert.Contains("12.0.1.60000", output.ToString());
        var recorded = await HistoryCount();

        stores.Opened.Clear(); output.GetStringBuilder().Clear(); error.GetStringBuilder().Clear();
        Assert.Equal(VmCommands.Ok, await Run("builds", "list", "wow"));
        Assert.Equal([(StoreFolder, false)], stores.Opened);
        Assert.Equal(recorded, await HistoryCount());
        Assert.Equal("", error.ToString());
        Assert.Contains("12.0.1.60000", output.ToString());
        Assert.DoesNotContain("Product summary", output.ToString());
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task ListWithNoArgumentFetchesOnlyTheProductsWithNoHistory()
    {
        Serve("wow", "12.0.1.60000", A);
        Serve("wowt", "12.0.5.61000", B);
        await stores.Source.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), default);
        Assert.Equal(VmCommands.Ok, await Run("--json", "builds", "list"));
        var doc = JsonOut();
        var fetched = doc.GetProperty("fetched").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(Products.Ids.Where(id => id != "wow"), fetched);
        var products = doc.GetProperty("products").EnumerateArray().ToList();
        Assert.Equal(Products.Ids, products.Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(JsonValueKind.Null, products[0].GetProperty("versions").ValueKind);
        Assert.False(products[3].GetProperty("available").GetBoolean());
        // The summary wasn't served: not available, which is an answer, not a failure.
        Assert.Equal("CDN_NOT_FOUND", doc.GetProperty("summary").GetProperty("observation").GetProperty("code").GetString());
        Assert.True(doc.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ListOnAMissingStoreCreatesItForTheFirstFetch()
    {
        stores.ReadOnlyFailure = ObjectStoreCodes.Missing;
        Serve("wow", "12.0.1.60000", A);
        Assert.Equal(VmCommands.Ok, await Run("builds", "list", "wow"));
        Assert.Equal([(StoreFolder, false), (StoreFolder, true)], stores.Opened);
        Assert.Equal([true], stores.WriterCreate);
        Assert.Contains("12.0.1.60000", output.ToString());
    }

    [Fact]
    public async Task ABusyStoreIsAClearErrorForBothCommands()
    {
        stores.WriterFailure = ObjectStoreCodes.Busy;
        Assert.Equal(VmCommands.Failed, await Run("builds", "list", "wow"));
        Assert.Contains("OBJECT_STORE_BUSY", error.ToString());
        Assert.Contains("is VibeMiner running?", error.ToString());
        Assert.Contains("has no history for wow and needs the writer lock", error.ToString());
        Assert.Equal(VmCommands.Failed, await Run("--json", "builds", "refresh"));
        Assert.Equal(ObjectStoreCodes.Busy, JsonOut().GetProperty("code").GetString());
        Assert.Equal(0, await HistoryCount());
        Assert.Equal(0, stores.Open);
    }

    [Fact]
    public async Task AnOfflineRefreshFailsButStillListsWhatTheHistoryHas()
    {
        Serve("wow", "12.0.1.60000", A);
        await stores.Source.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), default);
        stores.ObservationFails = new(ObjectState.Unavailable, Code: "CDN_OFFLINE", Message: "The version service could not be reached.");
        Assert.Equal(VmCommands.Failed, await Run("builds", "refresh", "wow"));
        var text = output.ToString();
        Assert.Contains("not available (CDN_OFFLINE: The version service could not be reached.)", text);
        Assert.Contains("12.0.1.60000", text);
        Assert.Contains("(builds from earlier observations)", text);
        Assert.Contains("Product summary", text);
    }

    [Fact]
    public async Task AnUnreadableSummaryNeverClaimsNoNewProducts()
    {
        Serve("wow", "12.0.1.60000", A);
        stores.Source.AddReply("", "us", ObservationKind.Summary, "not a table\n"u8.ToArray());
        Assert.Equal(VmCommands.Ok, await Run("builds", "refresh", "wow"));
        Assert.Contains("new products are unknown", output.ToString());
        Assert.Contains("SUMMARY_UNREADABLE", output.ToString());
        Assert.DoesNotContain("No new WoW products", output.ToString());
    }

    [Fact]
    public async Task AFinishedScrubIsRecordedAndInfoShowsIt()
    {
        Assert.Equal(VmCommands.Ok, await Run("store", "scrub"));
        Assert.Contains(["Recorded", ScrubRecords.PathIn(Data)], Rows(output));
        var read = await ScrubRecords.ReadAsync(Data);
        Assert.Equal(ScrubRecordState.Present, read.State);
        Assert.Equal((ScrubOutcome.Clean, clock.GetUtcNow(), 1234L), (read.Record!.Outcome, read.Record.FinishedAt, read.Record.Checked));

        ClearOutput();
        Assert.Equal(VmCommands.Ok, await Run("store", "info"));
        Assert.Contains(["Last scrub", "2026-11-01 09:30 UTC: clean, 1,234 objects checked"], Rows(output));
        Assert.DoesNotContain(Rows(output), r => r[0] == "Scrub problem");

        // A later damaged scrub replaces it, and info names the problem.
        var bad = ObjectKey.Parse(ObjectKeyType.Config, new string('a', 32));
        stores.Scrub = new("pack 3 is shorter than its rows", true, new VerifyReport(10, 100, 1, 0, [bad], []));
        clock.Advance(TimeSpan.FromHours(1));
        ClearOutput();
        Assert.Equal(VmCommands.Failed, await Run("--json", "store", "scrub"));
        Assert.True(JsonOut().GetProperty("recorded").GetBoolean());
        ClearOutput();
        Assert.Equal(VmCommands.Ok, await Run("store", "info"));
        Assert.Contains(["Last scrub", "2026-11-01 10:30 UTC: problem, 10 objects checked"], Rows(output));
        Assert.Contains(["Scrub problem", $"index: pack 3 is shorter than its rows (rebuilt from the packs); 1 corrupt, 0 lost; first: corrupt {bad}"], Rows(output));

        ClearOutput();
        Assert.Equal(VmCommands.Ok, await Run("--json", "store", "info"));
        var last = JsonOut().GetProperty("lastScrub");
        Assert.Equal("present", last.GetProperty("state").GetString());
        Assert.Equal("problem", last.GetProperty("outcome").GetString());
        Assert.Equal(1, last.GetProperty("corruptCount").GetInt64());
        Assert.Equal($"corrupt {bad}", last.GetProperty("problems")[0].GetString());
        Assert.Equal(clock.GetUtcNow(), last.GetProperty("finishedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task ACanceledScrubRecordsNothing()
    {
        using var cancel = new CancellationTokenSource();
        stores.BeforeScrub = async ct => { await cancel.CancelAsync(); ct.ThrowIfCancellationRequested(); };
        Assert.Equal(VmCommands.Canceled, await Run(cancel.Token, "store", "scrub"));
        Assert.Equal(ScrubRecordState.Absent, (await ScrubRecords.ReadAsync(Data)).State);
    }

    [Fact]
    public async Task AScrubThatThrowsIsRecordedAsFailedAndABusyStoreIsNot()
    {
        stores.WriterFailure = ObjectStoreCodes.Busy;
        Assert.Equal(VmCommands.Failed, await Run("store", "scrub"));
        Assert.Equal(ScrubRecordState.Absent, (await ScrubRecords.ReadAsync(Data)).State);

        stores.WriterFailure = null;
        stores.BeforeScrub = _ => throw new ObjectStoreException("OBJECT_STORE_CORRUPT", "the index is unreadable");
        Assert.Equal(VmCommands.Failed, await Run("store", "scrub"));
        var read = await ScrubRecords.ReadAsync(Data);
        Assert.Equal(("OBJECT_STORE_CORRUPT", "the index is unreadable"), (read.Record!.FailureCode, read.Record.FailureMessage));
        ClearOutput();
        Assert.Equal(VmCommands.Ok, await Run("store", "info"));
        Assert.Contains(["Last scrub", "2026-11-01 09:30 UTC: failed, 0 objects checked"], Rows(output));
        Assert.Contains(["Scrub problem", "OBJECT_STORE_CORRUPT: the index is unreadable"], Rows(output));
    }

    [Fact]
    public async Task InfoSaysWhenTheScrubRecordCantBeUsed()
    {
        Directory.CreateDirectory(Data);
        await File.WriteAllTextAsync(ScrubRecords.PathIn(Data), """{"version":9}""");
        Assert.Equal(VmCommands.Ok, await Run("store", "info"));
        Assert.Contains(["Last scrub", "scrub-v1.json can't be used (newer): The scrub record is version 9, from a newer VibeMiner."], Rows(output));
        ClearOutput();
        Assert.Equal(VmCommands.Ok, await Run("--json", "store", "info"));
        Assert.Equal("newer", JsonOut().GetProperty("lastScrub").GetProperty("state").GetString());
        Assert.Contains("newer VibeMiner", JsonOut().GetProperty("lastScrub").GetProperty("reason").GetString());
    }
}
