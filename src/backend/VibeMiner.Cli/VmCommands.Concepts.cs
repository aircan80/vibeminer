using Spectre.Console;
using VibeMiner.Builds;
using VibeMiner.Cli.Browse;
using VibeMiner.Concepts.Creatures;
using VibeMiner.Concepts.ItemEffects;
using VibeMiner.Concepts.Items;
using VibeMiner.Files;
using VibeMiner.Formats.Tables;
using VibeMiner.Objects;

namespace VibeMiner.Cli;

/// <summary>
/// <c>vm concepts states &lt;product&gt;</c> (#204, D473): the live check of layer 6. It lists a sample of items and creatures and reads
/// a few details through the layer 6 APIs, and counts what came back by state. The views are <c>:items</c> and <c>:creatures</c>.
/// </summary>
internal sealed partial class VmCommands
{
    internal const int DefaultSample = 2000, MaxSample = 100_000, DefaultDetails = 20, MaxDetails = 1000;

    /// <summary>
    /// Opens the product's current build (as <c>tables states</c> picks it) in enUS as the store's writer, lists the first
    /// <c>--sample</c> items and creatures a page at a time and reads the first <c>--details</c> of each (with each item's first
    /// effects page), all at background priority. It downloads only what those reads need. A state is a count in the report,
    /// never a failure; only a build that doesn't open fails.
    /// </summary>
    private async Task<int> ConceptsStates(string storeFolder, string product, CancellationToken ct)
    {
        IStoreHandle? store;
        try { store = await stores.OpenWriterAsync(storeFolder, create: false, ct).ConfigureAwait(false); }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Busy)
        {
            return await Fail(e.Code, "Another writer holds the object store (is VibeMiner running?). concepts states downloads tables, icons and models, so it needs the writer lock. Close it and try again.");
        }
        try
        {
            if (await CurrentBuildAsync(store, product, ct).ConfigureAwait(false) is not ({ } build, { } buildRef))
                return await Fail("CONCEPTS_NO_CURRENT_BUILD", $"The history log lists no current build for {product}. Run vm builds refresh {product} first.");
            var lease = await composeConcepts(store, folder.Path, ct).ConfigureAwait(false);
            var handle = store;
            store = null; // the lease closes it now
            await using (lease)
                return await ConceptsReport(storeFolder, handle.Writer, lease, build, buildRef, ct).ConfigureAwait(false);
        }
        finally { if (store is not null) await store.DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>Counts per state name, in ordinal order.</summary>
    private sealed class StateCounts : SortedDictionary<string, int>
    {
        public StateCounts() : base(StringComparer.Ordinal) { }
        public void Add(object state) { var key = state.ToString()!; this[key] = this.GetValueOrDefault(key) + 1; }
    }

    /// <summary>How a sample list ended: it reached the sample size, the table's end, a page state that isn't Ok, or the page bound.</summary>
    private enum SampleEnd { Sample, End, PageState, PageBound }

    private sealed record ItemLine(uint Id, string State, string? Name, string? NameState, uint? IconFileDataId, string? IconState, string? EffectsState, int Effects,
        IReadOnlyList<(string Source, uint? FileDataId, string State)>? Icons = null, IReadOnlyList<uint>? ModelFileDataIds = null);
    private sealed record CreatureLine(uint Id, string State, string? Name, string? NameState, IReadOnlyList<uint> ModelFileDataIds, IReadOnlyList<string> Chains);

    private async Task<int> ConceptsReport(string storeFolder, bool writer, ConceptsLease lease, CatalogBuild build, BuildRef buildRef, CancellationToken ct)
    {
        const string locale = TablesAccess.Locale;
        var n = sample ?? DefaultSample;
        var m = Math.Min(details ?? DefaultDetails, n);
        // One limit for every page: a cursor is bound to its request, limit included.
        var limit = Math.Min(ItemQueries.MaxLimit, n);
        if (!json) error.Write(new Text($"Opening {build.Product} {build.VersionsName} ({buildRef.Region}) in {locale} as the store's writer.\n"));
        var itemsOpened = await lease.Items.ForAsync(buildRef, locale, ct).ConfigureAwait(false);
        if (itemsOpened is not { State: FilesOpenState.Open, Items: { } items })
            return await Fail(itemsOpened.Code ?? "CONCEPTS_OPEN_FAILED", OpenFailure(build, locale, itemsOpened.State, itemsOpened.Message));
        await using (items)
        {
            var creaturesOpened = await lease.Creatures.ForAsync(buildRef, locale, ct).ConfigureAwait(false);
            if (creaturesOpened is not { State: FilesOpenState.Open, Creatures: { } creatures })
                return await Fail(creaturesOpened.Code ?? "CONCEPTS_OPEN_FAILED", OpenFailure(build, locale, creaturesOpened.State, creaturesOpened.Message));
            await using (creatures)
            {
                // Items: the list, then the details with each one's first effects page.
                var itemPages = new StateCounts();
                var listedItems = new List<ItemSummary>();
                long itemsScanned = 0;
                var itemsEnd = SampleEnd.PageBound;
                string? itemsCode = null, after = null;
                for (var page = 0; page < n; page++)
                {
                    var got = await items.ListAsync(new ItemQuery("", null, limit, after), TablePriority.Background, ct).ConfigureAwait(false);
                    itemPages.Add(got.State);
                    itemsScanned += got.Scanned;
                    if (got.State != ItemsPageState.Ok) { itemsEnd = SampleEnd.PageState; itemsCode = got.Code ?? got.Item?.Reason?.Code ?? got.Sparse?.Reason?.Code; break; }
                    listedItems.AddRange(got.Entries.Take(n - listedItems.Count));
                    if (listedItems.Count >= n) { itemsEnd = SampleEnd.Sample; break; }
                    if (got.Next is null) { itemsEnd = SampleEnd.End; break; }
                    after = got.Next;
                }
                var itemNames = new StateCounts(); var itemRows = new StateCounts(); var sparse = new StateCounts();
                foreach (var item in listedItems)
                {
                    itemNames.Add(item.Name.State);
                    itemRows.Add(item.Item);
                    sparse.Add(ItemFacts.SparseState(item.Name.State));
                }

                if (!json) error.Write(new Text($"Listed {listedItems.Count:N0} items; reading {Math.Min(m, listedItems.Count):N0} details.\n"));
                var itemResults = new StateCounts(); var icons = new StateCounts(); var facts = new StateCounts(); var factSparse = new StateCounts();
                var classLabels = new StateCounts(); var subclassLabels = new StateCounts();
                var effectPages = new StateCounts(); var effectStates = new StateCounts(); var effectNames = new StateCounts();
                var iconSources = new StateCounts(); var appearanceSets = new StateCounts(); var appearanceDefaults = new StateCounts();
                var resourceStates = new StateCounts(); var itemModels = new StateCounts(); var itemModelReads = new StateCounts();
                var itemLines = new List<ItemLine>();
                var effectsOpened = await lease.Effects.ForAsync(buildRef, locale, ct).ConfigureAwait(false);
                await using (effectsOpened.Effects)
                    foreach (var summary in listedItems.Take(m))
                    {
                        var result = await items.GetAsync(summary.Id, TablePriority.Background, ct).ConfigureAwait(false);
                        itemResults.Add(result.State);
                        if (result.Detail is not { } d) { itemLines.Add(new(summary.Id, result.State.ToString(), null, null, null, null, null, 0)); continue; }
                        icons.Add(d.Icon.State);
                        facts.Add(d.Facts.State);
                        factSparse.Add(d.Facts.Sparse);
                        classLabels.Add(d.Labels.Class.State);
                        subclassLabels.Add(d.Labels.Subclass.State);
                        string effectsState;
                        var effectCount = 0;
                        if (effectsOpened.Effects is { } effects)
                        {
                            var page = await effects.GetAsync(summary.Id, new ItemEffectsQuery(), TablePriority.Background, ct).ConfigureAwait(false);
                            effectsState = page.State.ToString();
                            effectCount = page.Entries.Count;
                            foreach (var entry in page.Entries) { effectStates.Add(entry.Effect.State); effectNames.Add(entry.Name.State); }
                        }
                        else effectsState = $"Open{effectsOpened.State}";
                        effectPages.Add(effectsState);
                        foreach (var sourced in d.Icons) iconSources.Add($"{sourced.Table}.{sourced.Column}:{sourced.Icon.State}");
                        var set = d.Appearances;
                        appearanceSets.Add(set.State);
                        appearanceDefaults.Add(set.Default);
                        foreach (var slot in set.Rows.Select(r => r.Appearance.Display).OfType<ItemDisplayRef>().SelectMany(display => display.Slots))
                        {
                            resourceStates.Add($"Model:{slot.Model.State}");
                            resourceStates.Add($"Material:{slot.Material.State}");
                        }
                        foreach (var model in set.Models.Models)
                        {
                            itemModels.Add(model.State);
                            if (model.Read is { } read) itemModelReads.Add(read);
                        }
                        itemLines.Add(new(summary.Id, result.State.ToString(), ConceptsAccess.Name(d.Summary.Name), d.Summary.Name.State.ToString(), d.Icon.FileDataId,
                            d.Icon.State.ToString(), effectsState, effectCount,
                            [.. d.Icons.Select(i => ($"{i.Table}.{i.Column}", i.Icon.FileDataId, i.Icon.State.ToString()))],
                            [.. set.Models.Models.Select(m => m.FileDataId)]));
                    }

                // Creatures: the list, then the details with their display chains and models.
                var creaturePages = new StateCounts();
                var listedCreatures = new List<CreatureSummary>();
                long creaturesScanned = 0;
                var creaturesEnd = SampleEnd.PageBound;
                string? creaturesCode = null;
                after = null;
                for (var page = 0; page < n; page++)
                {
                    var got = await creatures.ListAsync(new CreatureQuery("", null, limit, after), TablePriority.Background, ct).ConfigureAwait(false);
                    creaturePages.Add(got.State);
                    creaturesScanned += got.Scanned;
                    if (got.State != CreaturesPageState.Ok) { creaturesEnd = SampleEnd.PageState; creaturesCode = got.Code ?? got.Creature?.Reason?.Code; break; }
                    listedCreatures.AddRange(got.Entries.Take(n - listedCreatures.Count));
                    if (listedCreatures.Count >= n) { creaturesEnd = SampleEnd.Sample; break; }
                    if (got.Next is null) { creaturesEnd = SampleEnd.End; break; }
                    after = got.Next;
                }
                var creatureNames = new StateCounts(); var slots = new StateCounts();
                foreach (var creature in listedCreatures)
                {
                    creatureNames.Add(creature.Name.State);
                    foreach (var slot in creature.Slots) slots.Add(slot.State);
                }

                if (!json) error.Write(new Text($"Listed {listedCreatures.Count:N0} creatures; reading {Math.Min(m, listedCreatures.Count):N0} details.\n"));
                var creatureResults = new StateCounts(); var chains = new StateCounts(); var models = new StateCounts();
                var modelReads = new StateCounts(); var appearances = new StateCounts();
                var creatureLines = new List<CreatureLine>();
                foreach (var summary in listedCreatures.Take(m))
                {
                    var result = await creatures.GetAsync(summary.Id, TablePriority.Background, ct).ConfigureAwait(false);
                    creatureResults.Add(result.State);
                    if (result.Detail is not { } d) { creatureLines.Add(new(summary.Id, result.State.ToString(), null, null, [], [])); continue; }
                    foreach (var chain in d.Displays)
                    {
                        chains.Add(chain.State);
                        if (chain.Model is not { } model) continue;
                        models.Add(model.State);
                        modelReads.Add(model.ReadState?.ToString() ?? "NotRead");
                        appearances.Add(model.Appearance.State);
                    }
                    creatureLines.Add(new(summary.Id, result.State.ToString(), ConceptsAccess.Name(d.Summary.Name), d.Summary.Name.State.ToString(),
                        [.. d.Displays.Where(c => c.ModelFileDataId is > 0).Select(c => c.ModelFileDataId!.Value).Distinct()],
                        [.. d.Displays.Select(c => c.State.ToString())]));
                }

                var itemCounts = new SortedDictionary<string, StateCounts>(StringComparer.Ordinal)
                {
                    ["pageStates"] = itemPages, ["nameStates"] = itemNames, ["itemRowStates"] = itemRows, ["sparseStates"] = sparse,
                    ["resultStates"] = itemResults, ["iconStates"] = icons, ["factStates"] = facts, ["factSparseStates"] = factSparse,
                    ["classLabelStates"] = classLabels, ["subclassLabelStates"] = subclassLabels, ["effectPageStates"] = effectPages,
                    ["effectStates"] = effectStates, ["effectNameStates"] = effectNames, ["iconSourceStates"] = iconSources,
                    ["appearanceSetStates"] = appearanceSets, ["appearanceDefaultStates"] = appearanceDefaults, ["resourceStates"] = resourceStates,
                    ["modelStates"] = itemModels, ["modelReadStates"] = itemModelReads,
                };
                var creatureCounts = new SortedDictionary<string, StateCounts>(StringComparer.Ordinal)
                {
                    ["pageStates"] = creaturePages, ["nameStates"] = creatureNames, ["slotStates"] = slots, ["resultStates"] = creatureResults,
                    ["chainStates"] = chains, ["modelStates"] = models, ["modelReadStates"] = modelReads, ["appearanceStates"] = appearances,
                };

                if (json)
                {
                    await WriteJson(new
                    {
                        ok = true, dataFolder = DataFolderJson(), store = new { path = storeFolder, writer },
                        build = new { product = build.Product, version = build.VersionsName, buildConfig = buildRef.BuildConfig, cdnConfig = buildRef.CdnConfig, region = buildRef.Region },
                        locale, sample = n, details = m, fileReads = FileReads,
                        items = new
                        {
                            listed = listedItems.Count, scanned = itemsScanned, ended = itemsEnd.ToString(), code = itemsCode, counts = itemCounts,
                            entries = itemLines.Select(l => new
                            {
                                id = l.Id, state = l.State, name = l.Name, nameState = l.NameState, iconFileDataId = l.IconFileDataId, iconState = l.IconState,
                                icons = (l.Icons ?? []).Select(i => new { source = i.Source, fileDataId = i.FileDataId, state = i.State }),
                                modelFileDataIds = l.ModelFileDataIds ?? [], effectsState = l.EffectsState, effects = l.Effects,
                            }),
                        },
                        creatures = new
                        {
                            listed = listedCreatures.Count, scanned = creaturesScanned, ended = creaturesEnd.ToString(), code = creaturesCode, counts = creatureCounts,
                            entries = creatureLines.Select(l => new { id = l.Id, state = l.State, name = l.Name, nameState = l.NameState, modelFileDataIds = l.ModelFileDataIds, chains = l.Chains }),
                        },
                    }).ConfigureAwait(false);
                    return Ok;
                }

                output.Write(new Rule(Markup.Escape($"{build.Product} {build.VersionsName} ({buildRef.Region}) {locale}")).LeftJustified());
                output.Write(new Text($"Icons and models: {FileReads}.\n"));
                output.Write(new Text($"Items: {listedItems.Count:N0} listed in {itemsScanned:N0} rows scanned ({itemsEnd}{(itemsCode is null ? "" : $", {itemsCode}")}), {itemLines.Count:N0} details.\n"
                    + $"Creatures: {listedCreatures.Count:N0} listed in {creaturesScanned:N0} rows scanned ({creaturesEnd}{(creaturesCode is null ? "" : $", {creaturesCode}")}), {creatureLines.Count:N0} details.\n"));
                var counts = new Table().Border(TableBorder.Rounded).AddColumn("Kind").AddColumn("Counted").AddColumn("State").AddColumn(new TableColumn("Count").RightAligned());
                foreach (var (kind, groups) in new[] { ("items", itemCounts), ("creatures", creatureCounts) })
                    foreach (var (group, states) in groups)
                        foreach (var (state, count) in states) counts.AddRow(Cells(kind, group, state, $"{count:N0}"));
                output.Write(counts);
                var lines = new Table().Border(TableBorder.Rounded).AddColumn("Kind").AddColumn(new TableColumn("ID").RightAligned()).AddColumn("Name")
                    .AddColumn("State").AddColumn("Icon / models");
                foreach (var l in itemLines)
                    lines.AddRow(Cells("item", ConceptsAccess.I(l.Id), l.Name ?? "-", l.State, l.IconState is null ? "-"
                        : $"icons {string.Join(", ", (l.Icons ?? []).Select(i => $"{ConceptsAccess.I(i.FileDataId)} {i.State} ({i.Source})"))}; "
                          + $"models {((l.ModelFileDataIds ?? []).Count == 0 ? "none" : string.Join(' ', l.ModelFileDataIds!.Select(id => ConceptsAccess.I(id))))}; effects {l.EffectsState} ({l.Effects})"));
                foreach (var l in creatureLines)
                    lines.AddRow(Cells("creature", ConceptsAccess.I(l.Id), l.Name ?? "-", l.State, l.ModelFileDataIds.Count == 0 ? $"none ({string.Join(", ", l.Chains.Distinct())})"
                        : string.Join(' ', l.ModelFileDataIds.Select(id => ConceptsAccess.I(id)))));
                output.Write(lines);
                return Ok;
            }
        }
    }

    /// <summary>How the command's icon and model reads get bytes: through the writer, unlike browse's read-only session (D472).</summary>
    internal const string FileReads = "read through the writer, fetching what isn't stored (keyless)";

    private static string OpenFailure(CatalogBuild build, string locale, FilesOpenState state, string? message) =>
        $"Can't open {build.Product} {build.VersionsName} in {locale}: {state}{(message is { } m ? $": {m}" : "")}";
}
