using System.Globalization;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Rendering;
using VibeMiner.Builds;
using VibeMiner.Formats.Tables;
using VibeMiner.Cli.Browse;
using VibeMiner.Cli.Profile;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Cli;

/// <summary>
/// The <c>vm</c> commands (#170, D150). Read-only unless a command says it writes. Text goes through Spectre.Console (D151): tables,
/// a progress bar on stderr and red error panels, plain when NO_COLOR is set or output is redirected. <c>--json</c> writes one plain
/// JSON document to <c>json</c> instead. Exit codes: 0 done, 1 failed (including a scrub that found damage), 2 bad usage, 130 canceled.
/// <c>browse</c> hands the session to <c>browse</c>, the full-screen browser (#182), which is null when there's no terminal.
/// <c>tables</c> composes layer 5 over an open store handle (data folder, fetch definitions); by default
/// <see cref="TablesComposition.LeaseAsync"/>, a test seam otherwise. Its lease closes the handle too.
/// </summary>
internal sealed partial class VmCommands(CliEnvironment environment, IStoreOpener stores, IAnsiConsole output, IAnsiConsole error, TextWriter jsonOutput,
    Func<BrowseSession, CancellationToken, Task<int>>? browse = null, TimeProvider? time = null,
    Func<IStoreHandle, string, bool, CancellationToken, Task<TablesLease>>? tables = null)
{
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly Func<IStoreHandle, string, bool, CancellationToken, Task<TablesLease>> composeTables = tables ?? TablesComposition.LeaseAsync;

    public const int Ok = 0, Failed = 1, Usage = 2, Canceled = 130;
    private const int HistoryPage = 1000, ListedDamage = 20;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public const string UsageText = """
        Usage: vm [--data <dir>] [--json] [--yes] <command>

          browse                  full-screen browser: :store, :history, :builds, :files, :tables, :settings; ? lists the keys
                                  (read-only, except: r and a in :builds and m, g and G in :files take the writer lock, and
                                  the first :tables load holds it until browse exits, or reads only what is stored when the
                                  app holds it)
          store info              packs, objects and bytes in the object store, and the last scrub (read-only)
          store scrub             full index check and verify of every object; records the outcome in scrub-v1.json
                                  (writes: takes the writer lock)
          reset --yes             wipe catalog-v1/, generated-v1/ and settings-v1.json from the data folder; keeps
                                  every object and history record in cdn-store-v1/ (writes: takes the writer lock, the
                                  store's only write). Without --yes it only says what it would wipe
          history list [product]  the dated version-service observations, oldest first (read-only)
          builds list [product]   the builds of every reviewed product, or one, from the history log (read-only; the first
                                  use for a product fetches it, which writes and takes the writer lock)
          builds refresh [product]
                                  fetch versions and cdns for every reviewed product, or one, and the product summary,
                                  then list (writes: takes the writer lock)
          tables states <product> [--fetch-definitions]
                                  open the product's current build in enUS and check every table, with timings (writes: takes
                                  the writer lock and downloads table bytes and the metadata they need; --fetch-definitions
                                  also fetches newer WoWDBDefs definitions, D081)

          --data <dir>   data folder; otherwise VIBEMINER_DATA_DIR, the folder chosen in Settings, then the default
          --json         one JSON document on stdout instead of text
          --yes          confirm reset
        """;

    private bool json, yes, fetchDefinitions;
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
            else if (arg == "--yes") yes = true;
            else if (arg == "--fetch-definitions") fetchDefinitions = true;
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
            ["browse"] => Browse,
            ["store", "info"] => Info,
            ["store", "scrub"] => Scrub,
            ["reset"] => Reset,
            ["history", "list"] => (folder, token) => History(folder, null, token),
            ["history", "list", var product] => (folder, token) => History(folder, product, token),
            ["builds", "list"] => (folder, token) => BuildsList(folder, null, token),
            ["builds", "list", var product] => (folder, token) => BuildsList(folder, product, token),
            ["builds", "refresh"] => (folder, token) => BuildsRefresh(folder, null, token),
            ["builds", "refresh", var product] => (folder, token) => BuildsRefresh(folder, product, token),
            ["tables", "states", var product] => (folder, token) => TablesStates(folder, product, token),
            _ => null,
        };
        if (command is null) return UsageError(words.Count == 0 ? "No command given." : $"Unknown command: {string.Join(' ', words)}.");
        if (yes && words is not ["reset"]) return UsageError("--yes only applies to reset.");
        if (fetchDefinitions && words is not ["tables", "states", _]) return UsageError("--fetch-definitions applies only to tables states.");
        if (words is ["builds" or "tables", _, var named] && !Products.IsKnown(named))
            return UsageError($"{named} isn't a reviewed product. Reviewed: {string.Join(", ", Products.Ids)}.");

        try
        {
            var resolved = await DataFolders.ResolveAsync(environment, data, ct).ConfigureAwait(false);
            if (resolved is DataFolderProblem problem) return await Fail("DATA_FOLDER_SETTING_INVALID", $"{problem.Problem} ({problem.File}). {problem.Remedy}");
            folder = (DataFolder)resolved;
            return await command(Path.Combine(folder.Path, ObjectStore.DefaultFolderName), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await Fail("CANCELED", "Canceled."); return Canceled; }
        catch (ObjectStoreException e) { return await Fail(e.Code, Explain(e)); }
        catch (VibeMiner.Generated.GeneratedStoreException e) { return await Fail(e.Code, e.Code == VibeMiner.Generated.GeneratedStoreCodes.Busy
            ? "Another writer holds the generated store (is VibeMiner running?). Close it and try again." : e.Message); }
        catch (TablesBusyException e) { return await Fail(e.Code, $"{e.Message} Wait for it to finish and try again."); }
        // Anything else (a file system or SQLite error, a bad path) is still one named failure with exit code 1, never a stack trace.
        catch (Exception e) when (e is not OperationCanceledException) { return await Fail("CLI_FAILED", $"{e.GetType().Name}: {e.Message}"); }
    }

    private async Task<int> Browse(string storeFolder, CancellationToken ct)
    {
        if (json) return UsageError("browse is interactive; --json doesn't apply to it.");
        if (browse is null) return await Fail("BROWSE_NEEDS_TERMINAL", "vm browse needs a terminal on stdin and stdout. For scripts, use the other commands.");
        // Disposed after the browser returns, so after its navigator has stopped every load: closes the session's files.
        await using var session = new BrowseSession(stores, folder, storeFolder, environment);
        // The real composition shares the session's generated store with the names index (#186).
        session.UseTables(tables ?? session.ComposeTablesAsync);
        return await browse(session, ct).ConfigureAwait(false);
    }

    private async Task<int> Info(string storeFolder, CancellationToken ct)
    {
        await using var store = await stores.OpenReadOnlyAsync(storeFolder, ct).ConfigureAwait(false);
        var stats = await store.StatsAsync(ct).ConfigureAwait(false);
        var lastScrub = await ScrubRecords.ReadAsync(folder.Path, ct).ConfigureAwait(false);
        if (json)
        {
            await WriteJson(new
            {
                ok = true, dataFolder = DataFolderJson(), store = new { path = storeFolder, writer = store.Writer },
                stats.Packs, stats.Objects, stats.HistoryRecords, stats.PackBytes, stats.LiveBytes, createdAt = stats.CreatedAt,
                lastScrub = ScrubSummary.Json(lastScrub),
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
        Row("Last scrub", ScrubSummary.Line(lastScrub));
        if (ScrubSummary.Problem(lastScrub) is { } scrubProblem) Row("Scrub problem", scrubProblem);
        output.Write(table);
        return Ok;
    }

    private async Task<int> Scrub(string storeFolder, CancellationToken ct)
    {
        await using var store = await stores.OpenWriterAsync(storeFolder, create: false, ct).ConfigureAwait(false);
        ScrubReport report;
        try { report = await RunScrub(store, storeFolder, ct).ConfigureAwait(false); }
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            // A scrub that threw still ended, as a failure (the background schedule records those too); a canceled one records nothing.
            await RecordScrub(ScrubRecord.FromFailure(e, clock.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        // The scrub finished: record it even if cancellation arrives now, so the work isn't lost.
        var recordProblem = await RecordScrub(ScrubRecord.FromReport(report, clock.GetUtcNow()), CancellationToken.None).ConfigureAwait(false);
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
                recorded = recordProblem is null, recordProblem,
            });
            return damaged ? Failed : Ok;
        }
        var index = !report.IndexChecked ? "not checked (no stable view)"
            : report.IndexProblem is null ? "sound"
            : $"{report.IndexProblem}{(report.IndexRebuilt ? " (rebuilt from the packs)" : "")}";
        var table = KeyValueTable(out var row);
        void Row(string name, string value, bool bad = false) => row(name, value, bad);
        Row("Store", $"{storeFolder} (writer)");
        Row("Index", index, report.IndexProblem is not null || !report.IndexChecked);
        Row("Checked", $"{verify.Checked:N0} objects, {Size(verify.Bytes)}");
        Row("Corrupt", $"{verify.CorruptCount:N0}", verify.CorruptCount > 0);
        Row("Lost", $"{verify.LostCount:N0}", verify.LostCount > 0);
        foreach (var key in verify.Corrupt.Take(ListedDamage)) Row("corrupt", key.ToString(), true);
        foreach (var key in verify.Lost.Take(ListedDamage)) Row("lost", key.ToString(), true);
        Row("Recorded", recordProblem is null ? ScrubRecords.PathIn(folder.Path) : $"not recorded: {recordProblem}", recordProblem is not null);
        output.Write(table);
        return damaged ? Failed : Ok;
    }

    private async Task<ScrubReport> RunScrub(IStoreHandle store, string storeFolder, CancellationToken ct)
    {
        if (json) return await store.ScrubAsync(null, ct).ConfigureAwait(false);
        error.Write(new Text($"Scrubbing {storeFolder} as its writer.\n"));
        // The bar's total is the index's object count; verify reports objects checked against it.
        var total = Math.Max(1, (await store.StatsAsync(ct).ConfigureAwait(false)).Objects);
        return await error.Progress().AutoClear(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new ElapsedTimeColumn())
            .StartAsync(async context =>
            {
                var task = context.AddTask("Verifying objects", maxValue: total);
                var result = await store.ScrubAsync(new Progress(task), ct).ConfigureAwait(false);
                task.Value = total;
                return result;
            }).ConfigureAwait(false);
    }

    /// <summary>Writes the scrub record to the data folder; returns why it couldn't, or null. The scrub's outcome stands either way.</summary>
    private async Task<string?> RecordScrub(ScrubRecord record, CancellationToken ct)
    {
        try { await ScrubRecords.WriteAsync(folder.Path, record, ct).ConfigureAwait(false); return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { return $"{e.GetType().Name}: {e.Message}"; }
    }

    private const string KeptWhat = "every object and the history log";

    /// <summary>
    /// <c>reset</c>: wipes what a fresh profile rebuilds (<see cref="ProfileReset"/>'s targets) while holding the store's writer
    /// lock, so no writer runs during it; the object store itself is never touched and no catalog is opened. Without
    /// <c>--yes</c> it only says what it would wipe and keep, changes nothing and exits 2. Exit 0 when every target is wiped or
    /// absent, 1 when one failed, 130 when cancellation stopped it part way.
    /// </summary>
    private async Task<int> Reset(string storeFolder, CancellationToken ct)
    {
        var reset = new ProfileReset(folder.Path);
        var choice = DataFolders.Join(environment.Platform, DataFolders.UserData(environment), DataFolders.ChoiceFile);
        string[] notTouched = [$"Electron's {DataFolders.ChoiceFile} ({choice})", "the legacy discovery-v1.sqlite3 and objects-v1/"];
        static string Named(ResetTargetKind kind, string name) => kind == ResetTargetKind.Folder ? name + "/" : name;

        if (!yes)
        {
            const string needsYes = "reset deletes files. Nothing was changed; run vm reset --yes to wipe them.";
            if (json)
            {
                await WriteJson(new
                {
                    ok = false, code = "RESET_NEEDS_YES", message = needsYes, dataFolder = DataFolderJson(),
                    wouldWipe = reset.Targets.Select(t => Named(t.Kind, t.Name)), kept = new[] { ObjectStore.DefaultFolderName }, notTouched,
                });
                return Usage;
            }
            var plan = KeyValueTable(out var planRow);
            planRow("Data folder", $"{folder.Path} ({SourceName(folder.Source)})", false);
            foreach (var t in reset.Targets) planRow("Would wipe", Named(t.Kind, t.Name), false);
            planRow("Kept", $"{ObjectStore.DefaultFolderName} ({KeptWhat})", false);
            foreach (var n in notTouched) planRow("Not touched", n, false);
            output.Write(plan);
            ErrorPanel("Usage", needsYes);
            return Usage;
        }

        IStoreHandle? store = null;
        bool locked;
        try { store = await stores.OpenWriterAsync(storeFolder, create: false, ct).ConfigureAwait(false); locked = true; }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Busy)
        {
            return await Fail(e.Code, "Another writer holds the object store (is VibeMiner running?). Nothing was reset. Close it and try again.");
        }
        // No store, no lock to take: nothing can be writing to it.
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Missing) { locked = false; }
        ResetReport report;
        try { report = await reset.ResetAsync([], ct).ConfigureAwait(false); }
        finally { if (store is not null) await store.DisposeAsync().ConfigureAwait(false); }

        var code = report.Failed ? Failed : report.Cancelled ? Canceled : Ok;
        if (json)
        {
            static object Result(ResetResult r) => new
            {
                r.Name, kind = r.Kind.ToString().ToLowerInvariant(), state = r.State.ToString().ToLowerInvariant(), bytes = r.BytesFreed, r.ErrorType, r.ErrorMessage,
            };
            await WriteJson(new
            {
                ok = code == Ok, dataFolder = DataFolderJson(), store = new { path = storeFolder, @lock = locked ? "held" : "noStore" },
                targets = report.Targets.Select(Result), leftovers = report.Leftovers.Select(Result), kept = report.Kept, notTouched,
                bytesFreed = report.BytesFreed, report.BytesUnknown, cancelled = report.Cancelled,
            });
            return code;
        }
        static string State(ResetResult r) => r.State switch
        {
            ResetState.Wiped => r.BytesFreed is { } b ? $"wiped, {Size(b)}" : "wiped, size unknown",
            ResetState.Absent => "absent",
            ResetState.Skipped => "skipped (canceled)",
            _ => $"failed: {r.ErrorType}: {r.ErrorMessage}",
        };
        var table = KeyValueTable(out var row);
        row("Data folder", $"{folder.Path} ({SourceName(folder.Source)})", false);
        row("Writer lock", locked ? $"held on {storeFolder} for the reset" : $"no object store at {storeFolder}, so no lock to take", false);
        foreach (var t in report.Targets) row(Named(t.Kind, t.Name), State(t), t.State == ResetState.Failed);
        foreach (var t in report.Leftovers) row(t.Name, $"leftover of an interrupted reset: {State(t)}", t.State == ResetState.Failed);
        foreach (var k in report.Kept) row("Kept", $"{k} ({KeptWhat})", false);
        foreach (var n in notTouched) row("Not touched", n, false);
        row("Freed", report.BytesUnknown ? $"at least {Size(report.BytesFreed)}" : Size(report.BytesFreed), false);
        output.Write(table);
        if (report.Cancelled) error.Write(new Text("Canceled part way: the targets marked skipped are still there.\n"));
        return code;
    }

    /// <summary>A borderless-header two-column table and its row adder (a bad row is red).</summary>
    private static Table KeyValueTable(out Action<string, string, bool> row)
    {
        var table = new Table().Border(TableBorder.Rounded).HideHeaders().AddColumn("").AddColumn("");
        row = (name, value, bad) => table.AddRow(new Text(name, new Style(decoration: Decoration.Bold)), new Text(value, bad ? new Style(Color.Red) : Style.Plain));
        return table;
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

    /// <summary>
    /// Lists from the history log, read-only. A product with no versions history is fetched first (with the summary): that
    /// writes, so this run takes the writer lock and says so, creating the store if there is none.
    /// </summary>
    private async Task<int> BuildsList(string storeFolder, string? product, CancellationToken ct)
    {
        IReadOnlyList<string> products = product is null ? Products.Ids : [product];
        var missing = new List<string>();
        IStoreHandle? store = null;
        try
        {
            try { store = await stores.OpenReadOnlyAsync(storeFolder, ct).ConfigureAwait(false); }
            catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Missing) { missing.AddRange(products); }
            if (store is not null)
                foreach (var id in products)
                    if ((await store.Objects.ReadHistoryAsync(new HistoryQuery(Product: id, Kind: HistoryKind.Versions, Limit: 1), ct).ConfigureAwait(false)).Count == 0)
                        missing.Add(id);
            if (missing.Count == 0)
            {
                var catalog = await store!.CatalogAsync(ct).ConfigureAwait(false);
                var lists = new List<ProductReport>();
                foreach (var id in products) lists.Add(new(id, await catalog.ListBuildsAsync(id, ct).ConfigureAwait(false), null));
                return await ReportBuilds(storeFolder, store, lists, null);
            }
            if (store is not null) { var open = store; store = null; await open.DisposeAsync().ConfigureAwait(false); }

            var names = string.Join(", ", missing);
            if (!json) error.Write(new Text($"No history for {names}: fetching it from the version service. That writes to the store, so this run takes the writer lock.\n"));
            try { store = await stores.OpenWriterAsync(storeFolder, create: true, ct).ConfigureAwait(false); }
            catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Busy)
            {
                return await Fail(e.Code, $"Another writer holds the object store (is VibeMiner running?). builds list has no history for {names} and needs the writer lock to fetch it. Close it and try again, or run it while VibeMiner is closed.");
            }
            var writer = await store.CatalogAsync(ct).ConfigureAwait(false);
            var reports = new List<ProductReport>();
            foreach (var id in products)
                reports.Add(missing.Contains(id) ? Refreshed(await writer.RefreshAsync(id, ct).ConfigureAwait(false))
                    : new(id, await writer.ListBuildsAsync(id, ct).ConfigureAwait(false), null));
            var summary = await writer.RefreshSummaryAsync(ct).ConfigureAwait(false);
            return await ReportBuilds(storeFolder, store, reports, summary);
        }
        finally { if (store is not null) await store.DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>Fetches versions and cdns for each product and the summary, as the store's writer (creating the store if needed), then lists.</summary>
    private async Task<int> BuildsRefresh(string storeFolder, string? product, CancellationToken ct)
    {
        IReadOnlyList<string> products = product is null ? Products.Ids : [product];
        await using var store = await stores.OpenWriterAsync(storeFolder, create: true, ct).ConfigureAwait(false);
        if (!json) error.Write(new Text($"Fetching versions and cdns for {(product is null ? $"{products.Count} products" : product)} and the product summary, as the store's writer.\n"));
        var catalog = await store.CatalogAsync(ct).ConfigureAwait(false);
        var reports = new List<ProductReport>();
        foreach (var id in products) reports.Add(Refreshed(await catalog.RefreshAsync(id, ct).ConfigureAwait(false)));
        var summary = await catalog.RefreshSummaryAsync(ct).ConfigureAwait(false);
        return await ReportBuilds(storeFolder, store, reports, summary);
    }

    /// <summary>One product's list, and the refresh that came before it when this run fetched.</summary>
    private sealed record ProductReport(string Product, BuildList List, CatalogRefresh? Refresh)
    {
        /// <summary>False when this run asked the version service and its versions reply didn't come.</summary>
        public bool Available => Refresh is null || Refresh.Versions.State == ObjectState.Present;
    }

    private static ProductReport Refreshed(CatalogRefresh refresh) => new(refresh.Product, refresh.List, refresh);

    /// <summary>
    /// An observation that failed for a reason other than "the service has no such document" (offline, a bad reply, a store
    /// that couldn't take it) makes the run fail; a product the service doesn't answer for is an answer.
    /// </summary>
    private static bool FailedObservation(ObservationResult r) => r.State is not (ObjectState.Present or ObjectState.NotFound);

    private async Task<int> ReportBuilds(string storeFolder, IStoreHandle store, IReadOnlyList<ProductReport> reports, SummaryRefresh? summary)
    {
        var failed = reports.Any(r => r.Refresh is { } x && (FailedObservation(x.Versions) || FailedObservation(x.Cdns))) || summary is not null && FailedObservation(summary.Summary);
        if (json)
        {
            static object? Observed(ObservationResult? r) => r is null ? null : new
            {
                state = StateName(r.State), r.Recorded, r.Code, r.Message, url = r.Observation?.Url.AbsoluteUri, observedAt = r.Observation?.ObservedAt ?? r.ObservedAt,
            };
            static object Problem(CatalogProblem p) => new { p.Code, p.Message, record = p.Record?.KeyHex, p.Row };
            await WriteJson(new
            {
                ok = !failed, dataFolder = DataFolderJson(), store = new { path = storeFolder, writer = store.Writer },
                fetched = reports.Where(r => r.Refresh is not null).Select(r => r.Product),
                products = reports.Select(r => new
                {
                    id = r.Product, label = Products.Label(r.Product), available = r.Available,
                    versions = Observed(r.Refresh?.Versions), cdns = Observed(r.Refresh?.Cdns), lastObservedAt = r.List.LastObservedAt,
                    builds = r.List.Builds.Select(b => new
                    {
                        b.Id, b.VersionsName, b.BuildConfig, b.ProductConfig, b.Current, b.FirstSeen, b.LastSeen, b.Fields,
                        regions = b.Regions.Select(g => new { g.Region, g.CdnConfig, g.Current, g.FirstSeen, g.LastSeen, lastRecord = g.LastRecord.KeyHex }),
                    }),
                    problems = r.List.Problems.Select(Problem),
                }),
                summary = summary is null ? null : new
                {
                    observation = Observed(summary.Summary), summary.Sequence, listed = summary.Listed.Count, newProducts = summary.NewProducts,
                    problems = summary.Problems.Select(Problem),
                },
            });
            return failed ? Failed : Ok;
        }

        foreach (var r in reports)
        {
            output.Write(new Rule(Markup.Escape($"{Products.Label(r.Product)} ({r.Product})")).LeftJustified());
            if (!r.Available)
            {
                var v = r.Refresh!.Versions;
                output.Write(new Text($"not available ({v.Code ?? StateName(v.State)}{(v.Message is null ? "" : ": " + v.Message)})\n",
                    FailedObservation(v) ? new Style(Color.Red) : Style.Plain));
            }
            if (r.List.Builds.Count == 0)
            {
                if (r.Available) output.Write(new Text("no builds listed\n"));
            }
            else
            {
                var table = new Table().Border(TableBorder.Rounded).AddColumn("Version").AddColumn("Current").AddColumn("Regions")
                    .AddColumn("First seen (UTC)").AddColumn("Last seen (UTC)").AddColumn("Build config");
                foreach (var b in r.List.Builds)
                    table.AddRow(Cells(b.VersionsName, b.Current ? "yes" : "", string.Join(' ', b.Regions.Where(g => !b.Current || g.Current).Select(g => g.Region).Distinct()),
                        $"{b.FirstSeen.ToUniversalTime():yyyy-MM-dd HH:mm}", $"{b.LastSeen.ToUniversalTime():yyyy-MM-dd HH:mm}", b.BuildConfig));
                output.Write(table);
                if (!r.Available) output.Write(new Text("(builds from earlier observations)\n"));
            }
            if (r.Refresh is { Cdns: var cdns } && r.Available && FailedObservation(cdns))
                output.Write(new Text($"cdns not recorded ({cdns.Code ?? StateName(cdns.State)})\n", new Style(Color.Red)));
            foreach (var problem in r.List.Problems.Take(5)) output.Write(new Text($"problem: {problem.Code}: {problem.Message}\n", new Style(Color.Yellow)));
            if (r.List.Problems.Count > 5) output.Write(new Text($"{r.List.Problems.Count - 5} more problems (see --json)\n", new Style(Color.Yellow)));
        }
        if (summary is not null)
        {
            output.Write(new Rule("Product summary").LeftJustified());
            var s = summary.Summary;
            if (s.Observation is null)
                output.Write(new Text($"not available ({s.Code ?? StateName(s.State)}{(s.Message is null ? "" : ": " + s.Message)})\n", FailedObservation(s) ? new Style(Color.Red) : Style.Plain));
            else
            {
                output.Write(new Text($"{summary.Listed.Count:N0} products listed{(summary.Sequence is null ? "" : $", seqn {summary.Sequence}")}{(s.Recorded ? "" : $"; not recorded ({s.Code})")}\n",
                    s.Recorded ? Style.Plain : new Style(Color.Red)));
                // An unreadable reply lists nothing, which says nothing about new products.
                var readable = !summary.Problems.Any(p => p.Code is "SUMMARY_UNREADABLE" or "SUMMARY_COLUMNS_MISSING");
                if (!readable) output.Write(new Text("The summary couldn't be read, so new products are unknown.\n", new Style(Color.Red)));
                else if (summary.NewProducts.Count == 0) output.Write(new Text("No new WoW products.\n"));
                else output.Write(new Text($"New product found: {string.Join(", ", summary.NewProducts)}. Not in the reviewed list, so their builds aren't listed; adding one is a reviewed change (D017).\n"));
                foreach (var problem in summary.Problems.Take(5)) output.Write(new Text($"problem: {problem.Code}: {problem.Message}\n", new Style(Color.Yellow)));
            }
        }
        return failed ? Failed : Ok;
    }

    private static string StateName(ObjectState state) => state switch
    {
        ObjectState.NotFound => "not found", ObjectState.NeedsNetwork => "needs network", ObjectState.StoreFailed => "store failed",
        _ => state.ToString().ToLowerInvariant(),
    };

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
