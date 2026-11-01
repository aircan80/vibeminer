using System.Diagnostics;
using Spectre.Console;
using VibeMiner.Builds;
using VibeMiner.Cli.Browse;
using VibeMiner.Files;
using VibeMiner.Formats.Tables;
using VibeMiner.Objects;

namespace VibeMiner.Cli;

/// <summary>
/// <c>vm tables states &lt;product&gt;</c> (#180): the live state check. The other table commands are the <c>:tables</c> views in
/// <c>vm browse</c>; this one stays for scripts and its JSON.
/// </summary>
internal sealed partial class VmCommands
{
    /// <summary>Tables checked at once.</summary>
    internal const int CheckConcurrency = 4;
    /// <summary>Rows per timed page.</summary>
    internal const int TimedPageRows = 50;
    /// <summary>The big tables whose first page is timed twice: the first may build it, the second reads the generated store.</summary>
    internal static readonly string[] TimedTables = ["ItemSparse", "SpellName", "Creature"];

    /// <summary>
    /// Opens the product's current build (its <c>us</c> region when current, else the first current one) in enUS as the store's
    /// writer, lists every table, times the first page of <see cref="TimedTables"/> twice, then checks every table at
    /// background priority, <see cref="CheckConcurrency"/> at a time. It downloads table bytes and the build metadata they need,
    /// nothing else. A table that can't be read is a state in the report, not a failure.
    /// </summary>
    private async Task<int> TablesStates(string storeFolder, string product, CancellationToken ct)
    {
        IStoreHandle? store;
        try { store = await stores.OpenWriterAsync(storeFolder, create: false, ct).ConfigureAwait(false); }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Busy)
        {
            return await Fail(e.Code, "Another writer holds the object store (is VibeMiner running?). tables states downloads table bytes, so it needs the writer lock. Close it and try again.");
        }
        try
        {
            var catalog = await store.CatalogAsync(ct).ConfigureAwait(false);
            var list = await catalog.ListBuildsAsync(product, ct).ConfigureAwait(false);
            var current = list.Builds.Where(b => b.Current).ToList();
            var build = current.FirstOrDefault(b => b.Regions.Any(g => g.Current && g.Region == "us")) ?? current.FirstOrDefault();
            var region = build is null ? null
                : build.Regions.FirstOrDefault(g => g.Current && g.Region == "us") ?? build.Regions.FirstOrDefault(g => g.Current) ?? build.Regions.FirstOrDefault();
            if (build is null || region is null)
                return await Fail("TABLES_NO_CURRENT_BUILD", $"The history log lists no current build for {product}. Run vm builds refresh {product} first.");
            var buildRef = new BuildRef(build.Product, build.BuildConfig, region.CdnConfig, region.Region);

            var lease = await composeTables(store, folder.Path, fetchDefinitions, ct).ConfigureAwait(false);
            var handle = store;
            store = null; // the lease closes it now
            await using (lease)
                return await CheckAll(storeFolder, handle.Writer, lease.Tables, build, buildRef, ct).ConfigureAwait(false);
        }
        finally { if (store is not null) await store.DisposeAsync().ConfigureAwait(false); }
    }

    private sealed record PageTiming(string Name, uint FileDataId, TableState State, TablePageState PageState, int Rows, double FirstMs, double SecondMs);

    private async Task<int> CheckAll(string storeFolder, bool writer, ITables tables, CatalogBuild build, BuildRef buildRef, CancellationToken ct)
    {
        const string locale = TablesAccess.Locale;
        if (!json) error.Write(new Text($"Opening {build.Product} {build.VersionsName} ({buildRef.Region}) in {locale} as the store's writer.\n"));
        var opened = await tables.ForAsync(buildRef, locale, ct).ConfigureAwait(false);
        if (opened is not { State: FilesOpenState.Open, Tables: { } handle })
            return await Fail(opened.Code ?? "TABLES_OPEN_FAILED",
                $"Can't open {build.Product} {build.VersionsName} in {locale}: {opened.State}{(opened.Message is { } m ? $": {m}" : "")}");
        await using (handle)
        {
            var listed = new List<TableEntry>();
            await foreach (var entry in handle.ListAsync(TableListFilter.All, TablePriority.Background, ct).ConfigureAwait(false)) listed.Add(entry);

            // The pages run before the full check, so the first one builds its table when it isn't in the generated store yet.
            var pages = new List<PageTiming>();
            foreach (var name in TimedTables)
            {
                var request = new TablePageRequest(TableRef.ForName(name), -1, TimedPageRows);
                var clock = Stopwatch.StartNew();
                await handle.PageAsync(request, TablePriority.Interactive, ct).ConfigureAwait(false);
                var firstMs = clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                var second = await handle.PageAsync(request, TablePriority.Interactive, ct).ConfigureAwait(false);
                pages.Add(new(name, second.Table.FileDataId, second.Table.State, second.State, second.Rows.Count, Math.Round(firstMs, 1),
                    Math.Round(clock.Elapsed.TotalMilliseconds, 1)));
            }

            var results = new TableEntry[listed.Count];
            var checking = Stopwatch.StartNew();
            async Task Check(Action? step)
            {
                await Parallel.ForEachAsync(Enumerable.Range(0, listed.Count), new ParallelOptions { MaxDegreeOfParallelism = CheckConcurrency, CancellationToken = ct },
                    async (i, token) =>
                    {
                        results[i] = await handle.CheckAsync(TableRef.Id(listed[i].FileDataId), TablePriority.Background, token).ConfigureAwait(false);
                        step?.Invoke();
                    }).ConfigureAwait(false);
            }
            if (json) await Check(null).ConfigureAwait(false);
            else
                await error.Progress().AutoClear(false)
                    .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new ElapsedTimeColumn())
                    .StartAsync(async context =>
                    {
                        var task = context.AddTask($"Checking {listed.Count:N0} tables", maxValue: Math.Max(1, listed.Count));
                        await Check(() => task.Increment(1)).ConfigureAwait(false);
                        task.Value = task.MaxValue;
                    }).ConfigureAwait(false);
            var checkMs = Math.Round(checking.Elapsed.TotalMilliseconds, 1);

            var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in results) counts[entry.State.ToString()] = counts.GetValueOrDefault(entry.State.ToString()) + 1;

            if (json)
            {
                await WriteJson(new
                {
                    ok = true, dataFolder = DataFolderJson(), store = new { path = storeFolder, writer },
                    build = new { product = build.Product, version = build.VersionsName, buildConfig = buildRef.BuildConfig, cdnConfig = buildRef.CdnConfig, region = buildRef.Region },
                    // Tables the generated store already held when listed, from any build that shares them (D350).
                    locale, fetchDefinitions, tableCount = results.Length, storedBeforeCheck = listed.Count(e => e.Checked), checkMs, counts,
                    pages = pages.Select(p => new { name = p.Name, fileDataId = p.FileDataId, state = p.State.ToString(), pageState = p.PageState.ToString(), rows = p.Rows, firstMs = p.FirstMs, secondMs = p.SecondMs }),
                    tables = results.Select(e => new
                    {
                        fileDataId = e.FileDataId, name = e.Name, state = e.State.ToString(), @checked = e.Checked, reasonCode = e.Reason?.Code,
                        readableRows = e.Counts?.ReadableRows, declaredRows = e.Counts?.DeclaredRows,
                    }),
                }).ConfigureAwait(false);
                return Ok;
            }

            output.Write(new Rule(Markup.Escape($"{build.Product} {build.VersionsName} ({buildRef.Region}) {locale}")).LeftJustified());
            var summary = new Table().Border(TableBorder.Rounded).AddColumn("State").AddColumn(new TableColumn("Tables").RightAligned());
            foreach (var (state, count) in counts) summary.AddRow(Cells(state, $"{count:N0}"));
            output.Write(summary);
            output.Write(new Text($"{results.Length:N0} tables checked in {checkMs / 1000:N1} s ({CheckConcurrency} at a time).\n"));
            var timing = new Table().Border(TableBorder.Rounded).AddColumn("Table").AddColumn("State").AddColumn("Page")
                .AddColumn(new TableColumn("Rows").RightAligned()).AddColumn(new TableColumn("First (ms)").RightAligned()).AddColumn(new TableColumn("Second (ms)").RightAligned());
            foreach (var p in pages)
                timing.AddRow(Cells(p.Name, p.State.ToString(), p.PageState.ToString(), $"{p.Rows:N0}", $"{p.FirstMs:N1}", $"{p.SecondMs:N1}"));
            output.Write(timing);
            return Ok;
        }
    }
}
