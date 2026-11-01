using System.Text;
using System.Text.Json;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using Spectre.Console;
using Xunit;

namespace VibeMiner.Cli.Tests;

public sealed class CommandTests
{
    private const string Data = "/data/vm";
    private static readonly string StoreFolder = Path.Combine(Data, ObjectStore.DefaultFolderName);

    private readonly FakeStores stores = new();
    private readonly StringWriter output = new(), error = new(), json = new();

    /// <summary>A plain, non-interactive console over a writer: no ANSI codes, wide enough that no cell wraps.</summary>
    private static IAnsiConsole Plain(TextWriter writer)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No,
        });
        console.Profile.Width = 250;
        return console;
    }

    private static CliEnvironment Env(params (string, string)[] variables) =>
        new("linux", name => variables.FirstOrDefault(v => v.Item1 == name).Item2, "/home/nobody-cli-test");

    private Task<int> Run(params string[] args) => Run(CancellationToken.None, args);
    private Task<int> Run(CancellationToken ct, params string[] args) =>
        new VmCommands(Env(("VIBEMINER_DATA_DIR", Data)), stores, Plain(output), Plain(error), json).RunAsync(args, ct);

    private JsonElement JsonOut() => JsonDocument.Parse(json.ToString()).RootElement;

    /// <summary>The rows of the tables a command printed, as trimmed cell texts (borders and rules dropped).</summary>
    private static List<string[]> Rows(StringWriter writer) => writer.ToString().Split('\n')
        .Select(line => line.Split('│').Select(cell => cell.Trim()).ToArray())
        .Where(cells => cells.Length > 2).Select(cells => cells[1..^1]).ToList();

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
        Assert.Contains(["Last scrub", "not recorded"], rows);
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
        Assert.Equal(JsonValueKind.Null, info.GetProperty("lastScrub").ValueKind);
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
}
