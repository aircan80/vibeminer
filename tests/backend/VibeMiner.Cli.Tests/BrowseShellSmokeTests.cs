using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using VibeMiner.Cli.Browse;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;
using Xunit;
using Xunit.Abstractions;

namespace VibeMiner.Cli.Tests;

/// <summary>
/// Drives the real <see cref="BrowseShell"/> headless at 80x24 (#182): keys go in through <see cref="IApplication.InjectKey"/>
/// from the main loop's iterations and the screen is read back from the driver. Loads finish on the thread pool, so each step
/// waits, iteration by iteration and bounded, until the screen shows what it needs.
/// </summary>
public sealed class BrowseShellSmokeTests(ITestOutputHelper output)
{
    private const string Data = "/data/vm";
    private static readonly string StoreFolder = Path.Combine(Data, ObjectStore.DefaultFolderName);
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private const int MaxIterations = 20_000;

    // Without this the ANSI driver opens /dev/tty even with testhost's output redirected, and draws in the developer's terminal.
    static BrowseShellSmokeTests() => Environment.SetEnvironmentVariable("DisableRealDriverIO", "1");

    private sealed record Step(string Name, Func<string, BrowseShell, bool> Ready, Action<IApplication> Act);

    [Fact]
    public async Task TheShellBrowsesHistoryFiltersShowsHelpAndQuits()
    {
        var stores = new FakeStores();
        var time = new ManualTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        stores.Source.Time = time;
        await Observe(stores, time, ObservationKind.Versions, "wow", "wow versions reply");
        await Observe(stores, time, ObservationKind.Summary, "", "summary reply");
        var session = new BrowseSession(stores, new DataFolder(Data, DataFolderSource.Override), StoreFolder);

        var shots = new Dictionary<string, string>();
        string? failure = null;
        var stopped = false;
        Navigator? navigator = null;

        RunWithTimeout(() =>
        {
            using var app = Application.Create(new VirtualTimeProvider()).Init(DriverRegistry.Names.ANSI);
            app.Driver!.SetScreenSize(80, 24);
            navigator = new Navigator(ViewRegistry.Default(), session, a => app.Invoke(a));
            using var shell = new BrowseShell(app, navigator);

            Step[] steps =
            [
                new("store", (s, _) => s.Contains("Packs") && s.Contains("1,234"), a => { Type(a, ":history"); a.InjectKey(Key.Enter); }),
                new("history", (s, _) => s.Contains("Retail") && s.Contains("(summary)"), a => { a.InjectKey(Key.CursorDown); a.InjectKey(Key.Enter); }),
                new("drilled", (s, _) => Line(s, 0).Contains("history > wow") && s.Contains("newest first"), a => a.InjectKey(Key.Esc)),
                new("back", (s, _) => !Line(s, 0).Contains("history > ") && s.Contains("Retail"), a => { Type(a, "/wow"); a.InjectKey(Key.Enter); }),
                new("filtered", (_, sh) => sh.StatusText.Contains("/wow (1 of 2)"), a => a.InjectKey(new Key('?'))),
                new("help", (s, _) => s.Contains("Keys (Esc closes)") && s.Contains("go to a view"), a => a.InjectKey(Key.Esc)),
                new("closed", (s, _) => !s.Contains("Keys (Esc closes)"), a => a.InjectKey(new Key('q'))),
            ];

            var step = 0;
            var iterations = 0;
            app.Iteration += (_, _) =>
            {
                if (step >= steps.Length) return;
                if (++iterations > MaxIterations)
                {
                    app.LayoutAndDraw(true);
                    failure = $"step {steps[step].Name} never became ready. Status: {shell.StatusText}\n{Screen(app)}";
                    step = steps.Length;
                    app.RequestStop();
                    return;
                }
                app.LayoutAndDraw(true);
                var screen = Screen(app);
                if (!steps[step].Ready(screen, shell)) return;
                shots[steps[step].Name] = screen;
                steps[step++].Act(app);
            };
            app.Run(shell); // returns only after q, or RequestStop above
            stopped = true;
        }, Guard);

        await navigator!.DisposeAsync().AsTask().WaitAsync(Guard);
        foreach (var (name, screen) in shots) output.WriteLine($"--- {name}\n{screen}");
        Assert.Null(failure);
        Assert.True(stopped);

        Assert.Contains("vm browse  store  [read-only]", Line(shots["store"], 0));
        Assert.Contains("Product", shots["history"]);
        Assert.Contains("wow", shots["history"]);
        Assert.Contains("versions", shots["drilled"]);
        var filtered = shots["filtered"];
        Assert.Contains("Retail", filtered);
        Assert.DoesNotContain("(summary)", filtered);
        Assert.Contains("/wow (1 of 2)", filtered);
        Assert.Contains("Esc      clear the filter, then go back", shots["help"]);
        Assert.Contains("/wow (1 of 2)", shots["closed"]);
        Assert.All(stores.Opened, o => Assert.False(o.Writer));
        Assert.Empty(stores.WriterCreate);
        Assert.Equal(0, stores.Open);
    }

    private static async Task Observe(FakeStores stores, ManualTime time, ObservationKind kind, string product, string reply)
    {
        stores.Source.AddReply(product, "us", kind, Encoding.UTF8.GetBytes(reply));
        await stores.Source.ObserveAsync(new(kind, product, "us"), default);
        time.Advance(TimeSpan.FromMinutes(1));
    }

    /// <summary>Runs <paramref name="body"/> on its own thread and fails instead of hanging.</summary>
    private static void RunWithTimeout(Action body, TimeSpan timeout)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { body(); } catch (Exception e) { error = e; } }) { IsBackground = true, Name = "tg-ui" };
        thread.Start();
        Assert.True(thread.Join(timeout), $"The shell did not finish within {timeout}.");
        if (error is not null) throw new AggregateException(error);
    }

    private static void Type(IApplication app, string text)
    {
        foreach (var c in text) app.InjectKey(new Key(c));
    }

    private static string Line(string screen, int row) => screen.Split('\n')[row];

    private static string Screen(IApplication app)
    {
        var contents = app.Driver!.Contents!;
        var text = new StringBuilder();
        for (var row = 0; row < contents.GetLength(0); row++)
        {
            for (var col = 0; col < contents.GetLength(1); col++) text.Append(contents[row, col].Grapheme);
            text.Append('\n');
        }
        return text.ToString();
    }
}
