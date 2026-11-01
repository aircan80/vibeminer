using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;
using VibeMiner.Objects;

namespace VibeMiner.Cli;

/// <summary>
/// The <c>vm</c> commands (#170, D150). Read-only unless a command says it writes. Text goes through Spectre.Console (D151): tables,
/// a progress bar on stderr and red error panels, plain when NO_COLOR is set or output is redirected. <c>--json</c> writes one plain
/// JSON document to <c>json</c> instead. Exit codes: 0 done, 1 failed (including a scrub that found damage), 2 bad usage, 130 canceled.
/// </summary>
internal sealed class VmCommands(CliEnvironment environment, IStoreOpener stores, IAnsiConsole output, IAnsiConsole error, TextWriter jsonOutput)
{
    public const int Ok = 0, Failed = 1, Usage = 2, Canceled = 130;
    private const int HistoryPage = 1000, ListedDamage = 20;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public const string UsageText = """
        Usage: vm [--data <dir>] [--json] <command>

          store info              packs, objects and bytes in the object store (read-only)
          store scrub             full index check and verify of every object (writes: takes the writer lock)
          history list [product]  the dated version-service observations, oldest first (read-only)

          --data <dir>   data folder; otherwise VIBEMINER_DATA_DIR, the folder chosen in Settings, then the default
          --json         one JSON document on stdout instead of text
        """;

    private bool json;
    /// <summary>Entries written so far while a <c>history list --json</c> document is open, so a failure can close it.</summary>
    private long? openHistory;
    private DataFolder folder = new("", DataFolderSource.Default);

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        // Numbers and dates read the same everywhere, so scripts can parse the text too.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        string? data = null;
        var words = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--json") json = true;
            else if (arg == "--data")
            {
                if (i + 1 >= args.Count || args[i + 1].Length == 0) return UsageError("--data needs a folder.");
                data = args[++i];
            }
            else if (arg.StartsWith("--data=", StringComparison.Ordinal) && arg.Length > "--data=".Length) data = arg["--data=".Length..];
            else if (arg is "--help" or "-h" or "help") { output.Write(new Text(UsageText + "\n")); return Ok; }
            else if (arg.StartsWith('-')) return UsageError($"Unknown option {arg}.");
            else words.Add(arg);
        }

        Func<string, CancellationToken, Task<int>>? command = words switch
        {
            ["store", "info"] => Info,
            ["store", "scrub"] => Scrub,
            ["history", "list"] => (folder, token) => History(folder, null, token),
            ["history", "list", var product] => (folder, token) => History(folder, product, token),
            _ => null,
        };
        if (command is null) return UsageError(words.Count == 0 ? "No command given." : $"Unknown command: {string.Join(' ', words)}.");

        try
        {
            var resolved = await DataFolders.ResolveAsync(environment, data, ct).ConfigureAwait(false);
            if (resolved is DataFolderProblem problem) return await Fail("DATA_FOLDER_SETTING_INVALID", $"{problem.Problem} ({problem.File}). {problem.Remedy}");
            folder = (DataFolder)resolved;
            return await command(Path.Combine(folder.Path, ObjectStore.DefaultFolderName), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await Fail("CANCELED", "Canceled."); return Canceled; }
        catch (ObjectStoreException e) { return await Fail(e.Code, Explain(e)); }
        // Anything else (a file system or SQLite error, a bad path) is still one named failure with exit code 1, never a stack trace.
        catch (Exception e) when (e is not OperationCanceledException) { return await Fail("CLI_FAILED", $"{e.GetType().Name}: {e.Message}"); }
    }

    private async Task<int> Info(string storeFolder, CancellationToken ct)
    {
        await using var store = await stores.OpenReadOnlyAsync(storeFolder, ct).ConfigureAwait(false);
        var stats = await store.StatsAsync(ct).ConfigureAwait(false);
        if (json)
        {
            await WriteJson(new
            {
                ok = true, dataFolder = DataFolderJson(), store = new { path = storeFolder, writer = store.Writer },
                stats.Packs, stats.Objects, stats.HistoryRecords, stats.PackBytes, stats.LiveBytes, createdAt = stats.CreatedAt,
                // The store keeps no record of scrubs yet (D150).
                lastScrub = (DateTimeOffset?)null,
            });
            return Ok;
        }
        var table = new Table().Border(TableBorder.Rounded).HideHeaders().AddColumn("").AddColumn("");
        void Row(string name, string value) => table.AddRow(new Text(name, new Style(decoration: Decoration.Bold)), new Text(value));
        Row("Data folder", $"{folder.Path} ({SourceName(folder.Source)})");
        Row("Store", $"{storeFolder} (read-only)");
        Row("Packs", $"{stats.Packs:N0}");
        Row("Objects", $"{stats.Objects:N0} ({stats.HistoryRecords:N0} history records)");
        Row("Bytes", $"{Size(stats.PackBytes)} in packs, {Size(stats.LiveBytes)} live");
        Row("Created", $"{stats.CreatedAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC");
        Row("Last scrub", "not recorded");
        output.Write(table);
        return Ok;
    }

    private async Task<int> Scrub(string storeFolder, CancellationToken ct)
    {
        await using var store = await stores.OpenWriterAsync(storeFolder, ct).ConfigureAwait(false);
        ScrubReport report;
        if (json) report = await store.ScrubAsync(null, ct).ConfigureAwait(false);
        else
        {
            error.Write(new Text($"Scrubbing {storeFolder} as its writer.\n"));
            // The bar's total is the index's object count; verify reports objects checked against it.
            var total = Math.Max(1, (await store.StatsAsync(ct).ConfigureAwait(false)).Objects);
            report = await error.Progress().AutoClear(false)
                .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new ElapsedTimeColumn())
                .StartAsync(async context =>
                {
                    var task = context.AddTask("Verifying objects", maxValue: total);
                    var result = await store.ScrubAsync(new Progress(task), ct).ConfigureAwait(false);
                    task.Value = total;
                    return result;
                }).ConfigureAwait(false);
        }
        var verify = report.Verify;
        var damaged = report.IndexProblem is not null || verify.CorruptCount > 0 || verify.LostCount > 0 || !report.IndexChecked;
        if (json)
        {
            await WriteJson(new
            {
                ok = !damaged, dataFolder = DataFolderJson(), store = new { path = storeFolder, writer = store.Writer },
                index = new { @checked = report.IndexChecked, problem = report.IndexProblem, rebuilt = report.IndexRebuilt },
                @checked = verify.Checked, verify.Bytes, verify.CorruptCount, verify.LostCount,
                corrupt = verify.Corrupt.Select(k => k.ToString()), lost = verify.Lost.Select(k => k.ToString()),
            });
            return damaged ? Failed : Ok;
        }
        var index = !report.IndexChecked ? "not checked (no stable view)"
            : report.IndexProblem is null ? "sound"
            : $"{report.IndexProblem}{(report.IndexRebuilt ? " (rebuilt from the packs)" : "")}";
        var table = new Table().Border(TableBorder.Rounded).HideHeaders().AddColumn("").AddColumn("");
        void Row(string name, string value, bool bad = false) =>
            table.AddRow(new Text(name, new Style(decoration: Decoration.Bold)), new Text(value, bad ? new Style(Color.Red) : Style.Plain));
        Row("Store", $"{storeFolder} (writer)");
        Row("Index", index, report.IndexProblem is not null || !report.IndexChecked);
        Row("Checked", $"{verify.Checked:N0} objects, {Size(verify.Bytes)}");
        Row("Corrupt", $"{verify.CorruptCount:N0}", verify.CorruptCount > 0);
        Row("Lost", $"{verify.LostCount:N0}", verify.LostCount > 0);
        foreach (var key in verify.Corrupt.Take(ListedDamage)) Row("corrupt", key.ToString(), true);
        foreach (var key in verify.Lost.Take(ListedDamage)) Row("lost", key.ToString(), true);
        output.Write(table);
        return damaged ? Failed : Ok;
    }

    private async Task<int> History(string storeFolder, string? product, CancellationToken ct)
    {
        await using var store = await stores.OpenReadOnlyAsync(storeFolder, ct).ConfigureAwait(false);
        // "ok" comes last, so a failure on a later page still closes one valid document (see Fail).
        if (json) { await jsonOutput.WriteAsync($"{{\"dataFolder\":{JsonSerializer.Serialize(DataFolderJson(), Json)},\"product\":{JsonSerializer.Serialize(product)},\"entries\":["); openHistory = 0; }
        HistoryEntry? after = null;
        long count = 0;
        // Pages of HistoryPage entries, written as they arrive (one table per page in text): memory stays one page however long the log is.
        while (true)
        {
            var page = await store.Objects.ReadHistoryAsync(new HistoryQuery(Product: product, After: after, Limit: HistoryPage), ct).ConfigureAwait(false);
            if (json)
                foreach (var e in page)
                {
                    await jsonOutput.WriteAsync((count++ > 0 ? "," : "") + JsonSerializer.Serialize(new
                    {
                        observedAt = e.ObservedAt, kind = KindName(e.Kind), e.Product, e.Region, e.Url,
                        document = e.Document.KeyHex, record = e.Record.KeyHex,
                    }, Json));
                    openHistory = count;
                }
            else if (page.Count > 0)
            {
                var table = new Table().Border(TableBorder.Rounded)
                    .AddColumn("Observed (UTC)").AddColumn("Kind").AddColumn("Product").AddColumn("Region").AddColumn("Document").AddColumn("URL");
                foreach (var e in page)
                    table.AddRow(Cells($"{e.ObservedAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss}", KindName(e.Kind), e.Product, e.Region, e.Document.KeyHex, e.Url));
                output.Write(table);
                count += page.Count;
            }
            if (page.Count < HistoryPage) break;
            after = page[^1];
        }
        if (json) { await jsonOutput.WriteLineAsync($"],\"count\":{count},\"ok\":true}}"); openHistory = null; }
        else if (count == 0) output.Write(new Text(product is null ? "No history recorded.\n" : $"No history recorded for {product}.\n"));
        else output.Write(new Text(count == 1 ? "1 entry\n" : $"{count:N0} entries\n"));
        return Ok;
    }

    private int UsageError(string message)
    {
        ErrorPanel("Usage", message);
        error.Write(new Text(UsageText + "\n"));
        return Usage;
    }

    private async Task<int> Fail(string code, string message)
    {
        if (json && openHistory is { } written)
        {
            openHistory = null;
            await jsonOutput.WriteLineAsync($"],\"count\":{written},\"ok\":false,\"code\":{JsonSerializer.Serialize(code)},\"message\":{JsonSerializer.Serialize(message)}}}");
        }
        else if (json) await WriteJson(new { ok = false, code, message });
        else ErrorPanel(code, message);
        return Failed;
    }

    private void ErrorPanel(string title, string message) =>
        error.Write(new Panel(new Text(message)).Header(new PanelHeader(Markup.Escape(title))).Border(BoxBorder.Rounded).BorderColor(Color.Red));

    private static IRenderable[] Cells(params string[] values) => values.Select(v => (IRenderable)new Text(v)).ToArray();

    private static string Explain(ObjectStoreException e) => e.Code switch
    {
        ObjectStoreCodes.Missing => $"{e.Message} Nothing was created.",
        ObjectStoreCodes.Busy => "Another writer holds the object store (is VibeMiner running?). Close it and try again.",
        _ => e.Message,
    };

    private object DataFolderJson() => new { path = folder.Path, source = SourceName(folder.Source) };

    private static string SourceName(DataFolderSource source) => source switch
    {
        DataFolderSource.Flag => "--data",
        DataFolderSource.Override => "VIBEMINER_DATA_DIR",
        DataFolderSource.Chosen => "chosen in Settings",
        _ => "default",
    };

    private static string KindName(HistoryKind kind) => kind.ToString().ToLowerInvariant();

    internal static string Size(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{bytes:N0} B" : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
    }

    private Task WriteJson(object value) => jsonOutput.WriteLineAsync(JsonSerializer.Serialize(value, Json));

    /// <summary>Verify's count of checked objects moves the bar; Spectre's task is safe to update from the scrub's thread.</summary>
    private sealed class Progress(ProgressTask task) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => task.Value = Math.Min(value.Entries, task.MaxValue);
    }
}
