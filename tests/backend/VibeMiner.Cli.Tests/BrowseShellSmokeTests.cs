using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Terminal.Gui.Time;
using VibeMiner.Builds;
using VibeMiner.Cli.Browse;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
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
        var (shots, failure, stopped, navigator) = Drive(session, ViewRegistry.Default(), "store", steps);

        await navigator.DisposeAsync().AsTask().WaitAsync(Guard);
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

    /// <summary>
    /// Runs the shell at 80x24 over <paramref name="registry"/> from <paramref name="start"/>, taking each step once its screen is
    /// ready, until the last step quits it or one never becomes ready.
    /// </summary>
    private static (Dictionary<string, string> Shots, string? Failure, bool Stopped, Navigator Navigator) Drive(
        BrowseSession session, ViewRegistry registry, string start, Step[] steps)
    {
        var shots = new Dictionary<string, string>();
        string? failure = null;
        var stopped = false;
        Navigator? navigator = null;

        RunWithTimeout(() =>
        {
            using var app = Application.Create(new VirtualTimeProvider()).Init(DriverRegistry.Names.ANSI);
            app.Driver!.SetScreenSize(80, 24);
            navigator = new Navigator(registry, session, a => app.Invoke(a));
            using var shell = new BrowseShell(app, navigator, start);

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
        return (shots, failure, stopped, navigator!);
    }

    [Fact]
    public async Task AKeysQuestionIsAnsweredOrCanceledAndATextViewReadsFurther()
    {
        var stores = new FakeStores();
        var session = new BrowseSession(stores, new DataFolder(Data, DataFolderSource.Override), StoreFolder);
        var keys = new KeysView();
        var answers = new List<string>();
        keys.KeyList.Add(new('e', "export", row => new(Ask: new("Export to:", $"/tmp/{row!.Cells[0]}", typed =>
        {
            answers.Add(typed);
            return new(Say: $"would write {typed}");
        }))));
        var more = new MoreView(pageLines: 30, pages: 2);
        var registry = new ViewRegistry().Add(new("keys", "", () => keys)).Add(new("more", "", () => more));

        Step[] steps =
        [
            new("keys", (s, _) => s.Contains("alpha") && s.Contains("gamma"), a => a.InjectKey(new Key('e'))),
            new("asked", (s, _) => Line(s, 23).StartsWith("Export to: /tmp/alpha"), a => { a.InjectKey(Key.End); Type(a, "-x"); a.InjectKey(Key.Enter); }),
            new("answered", (_, sh) => sh.StatusText.Contains("would write /tmp/alpha-x"), a => a.InjectKey(new Key('e'))),
            new("asked again", (s, _) => Line(s, 23).StartsWith("Export to: /tmp/alpha "), a => { Type(a, "zz"); a.InjectKey(Key.Esc); }),
            new("canceled", (_, sh) => sh.StatusText.Contains("? keys"), a => { Type(a, ":more"); a.InjectKey(Key.Enter); }),
            new("text", (s, _) => s.Contains("line 0000") && s.Contains("1 of 2 pages"), a => { a.InjectKey(Key.End); a.InjectKey(Key.CursorDown); }),
            new("more", (s, _) => s.Contains("2 of 2 pages") && s.Contains("line 0029"), a => a.InjectKey(new Key('q'))),
        ];
        var (shots, failure, stopped, navigator) = Drive(session, registry, "keys", steps);

        await navigator.DisposeAsync().AsTask().WaitAsync(Guard);
        foreach (var (name, screen) in shots) output.WriteLine($"--- {name}\n{screen}");
        Assert.Null(failure);
        Assert.True(stopped);
        Assert.Equal(["/tmp/alpha-x"], answers);
        Assert.Equal(80, Line(shots["asked"], 23).Length);
        Assert.Contains("would write /tmp/alpha-x", shots["answered"]);
        Assert.DoesNotContain("zz", shots["canceled"]);
        Assert.Equal(2, more.Read);
        Assert.Equal(29, navigator.Frames[^1].Selected);
        Assert.Contains("line 0029", shots["more"]);
    }

    [Fact]
    public async Task AFailingKeyIsAStatusLineAndGoingToAViewAfterATaskEndsKeepsTheShellUp()
    {
        // #175: after m ended, :tables or :settings crashed the shell (ArgumentOutOfRangeException, CLI_FAILED).
        var stores = new FakeStores();
        var session = new BrowseSession(stores, new DataFolder(Data, DataFolderSource.Override), StoreFolder);
        var keys = new KeysView();
        keys.ActionList.Add(new('m', "metadata", "metadata", (_, _, _) => Task.FromResult("metadata: stored")));
        keys.KeyList.Add(new('b', "breaks", _ => throw new InvalidOperationException("boom")));
        var registry = new ViewRegistry().Add(new("keys", "", () => keys)).Add(new("other", "", () => new KeysView("other")));

        Step[] steps =
        [
            new("keys", (s, _) => s.Contains("alpha"), a => a.InjectKey(new Key('m'))),
            new("ended", (_, sh) => sh.StatusText.Contains("metadata: stored"), a => { Type(a, ":other"); a.InjectKey(Key.Enter); }),
            new("other", (s, sh) => Line(s, 0).Contains("vm browse  other") && s.Contains("alpha"), a => { Type(a, ":keys"); a.InjectKey(Key.Enter); }),
            new("back", (s, sh) => Line(s, 0).Contains("vm browse  keys") && s.Contains("alpha"), a => a.InjectKey(new Key('b'))),
            new("failed", (_, sh) => sh.StatusText.Contains("error: InvalidOperationException: boom"), a => a.InjectKey(new Key('q'))),
        ];
        var (shots, failure, stopped, navigator) = Drive(session, registry, "keys", steps);

        await navigator.DisposeAsync().AsTask().WaitAsync(Guard);
        foreach (var (name, screen) in shots) output.WriteLine($"--- {name}\n{screen}");
        Assert.Null(failure);
        Assert.True(stopped);
        Assert.DoesNotContain("metadata: stored", Line(shots["other"], 23));
        Assert.Contains("alpha", shots["failed"]);
    }

    private const string VersionsHeader = "Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!String:0|ProductConfig!HEX:16";

    [Fact]
    public async Task TheFilesViewsListFilterShowAFileAndItsHoleAndGAsksWithItsChoiceShown()
    {
        string a = new('a', 32), b = new('b', 32), c = new('c', 32);
        var stores = new FakeStores();
        var versions = Encoding.UTF8.GetBytes($"{VersionsHeader}\n## seqn = 1\nus|{a}|{b}|{c}|60000|12.0.1.60000|{c}\n");
        stores.Source.AddReply("wow", "us", ObservationKind.Versions, versions);
        await stores.Source.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), default);
        var memory = new InMemoryFileSystem();
        byte[] blp = [.. "BLP2"u8, .. new byte[12]];
        memory.Add(new BuildRef("wow", a, b, "us"), ["enUS"],
        [
            new FakeFile(10, Storage: FileStorage.Stored, Content: blp),
            new FakeFile(11, Storage: FileStorage.Stored, Content: new byte[64], Holes: [new FileHole(16, 16, "0123456789ABCDEF")]),
            new FakeFile(12, Size: 9),
        ]);
        memory.Names.Use(new Dictionary<uint, string> { [10] = "interface/icons/a.blp", [11] = "interface/x.bin", [12] = "sound/y.ogg" });
        await using var session = new BrowseSession(stores, new DataFolder(Data, DataFolderSource.Override), StoreFolder)
        {
            FileSystem = (_, _, _, _) => Task.FromResult<IFileSystem>(memory),
        };

        static bool Idle(BrowseShell sh) => !sh.StatusText.Contains("loading...");
        Step[] steps =
        [
            new("store", (s, _) => s.Contains("Packs"), a => { Type(a, ":builds"); a.InjectKey(Key.Enter); }),
            new("builds", (s, sh) => Idle(sh) && Line(s, 0).Contains("builds") && s.Contains("wow"), a => a.InjectKey(Key.Enter)),
            new("product", (s, sh) => Idle(sh) && Line(s, 0).Contains("builds > wow") && s.Contains("12.0.1.60000"), a => a.InjectKey(new Key('f'))),
            new("files", (s, sh) => Idle(sh) && Line(s, 0).Contains("files enUS") && s.Contains("sound/y.ogg"), a => { Type(a, "/interface"); a.InjectKey(Key.Enter); }),
            new("filtered", (s, sh) => Idle(sh) && sh.StatusText.Contains("/interface (2 rows)") && !s.Contains("sound/y.ogg"), a => a.InjectKey(Key.Enter)),
            new("file", (s, sh) => Idle(sh) && Line(s, 0).Contains("> a.blp") && s.Contains("|BLP2"), a => a.InjectKey(Key.Esc)),
            new("back", (s, sh) => Idle(sh) && !Line(s, 0).Contains("> a.blp") && s.Contains("interface/x.bin"), a => { a.InjectKey(Key.CursorDown); a.InjectKey(Key.Enter); }),
            new("hole", (s, sh) => Idle(sh) && Line(s, 0).Contains("> x.bin") && s.Contains("encrypted, needs TACT key"), a => a.InjectKey(Key.Esc)),
            new("back again", (s, sh) => Idle(sh) && !Line(s, 0).Contains("> x.bin") && s.Contains("interface/x.bin"), a => a.InjectKey(new Key('G'))),
            new("asked", (s, _) => Line(s, 23).StartsWith("ensure all ", StringComparison.Ordinal), a => a.InjectKey(Key.Esc)),
            new("not asked", (s, sh) => sh.StatusText.Contains("? keys") && !Line(s, 23).Contains("y/N"), a => a.InjectKey(new Key('q'))),
        ];
        // Every line the shell built (header, note, status, a text view's lines) fits 80 columns at every step.
        var wide = new List<string>();
        steps = [.. steps.Select(st => st with
        {
            Ready = (s, sh) =>
            {
                if (!st.Ready(s, sh)) return false;
                wide.AddRange(sh.ShownLines.Where(l => l.Length > 80).Select(l => $"{st.Name}: {l}"));
                return true;
            },
        })];
        var (shots, failure, stopped, navigator) = Drive(session, ViewRegistry.Default(), "store", steps);

        await navigator.DisposeAsync().AsTask().WaitAsync(Guard);
        foreach (var (name, screen) in shots) output.WriteLine($"--- {name}\n{screen}");
        Assert.Null(failure);
        Assert.True(stopped);
        Assert.Empty(wide);

        var files = shots["files"];
        foreach (var column in new[] { "Id", "Path", "Format", "Size", "Stored", "Enc" }) Assert.Contains(column, Line(files, 3));
        Assert.Contains("interface/icons/a.blp", files);
        Assert.Contains("interface/x.bin", shots["filtered"]);
        var file = shots["file"];
        Assert.Contains("FileDataID   10", file);
        Assert.Contains("Stored       yes", file);
        Assert.Contains("00000000  42 4c 50 32 00 00 00 00  00 00 00 00 00 00 00 00  |BLP2............|", file);
        Assert.Contains("00000010-0000001f  encrypted, needs TACT key 0123456789ABCDEF (16 bytes)", shots["hole"]);
        // The question shows its y/N choice at 80 columns; Esc starts nothing.
        Assert.StartsWith("ensure all enUS, path inter... (writer lock)? y/N", Line(shots["asked"], 23), StringComparison.Ordinal);
        Assert.Null(navigator.Tasks.Running);
        Assert.All(stores.Opened, o => Assert.False(o.Writer));
        Assert.Empty(memory.EnsureRequests);
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
