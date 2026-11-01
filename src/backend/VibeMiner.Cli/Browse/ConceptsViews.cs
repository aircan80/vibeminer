using System.Collections.Immutable;
using System.Globalization;
using VibeMiner.Builds;
using VibeMiner.Concepts.Creatures;
using VibeMiner.Concepts.ItemEffects;
using VibeMiner.Concepts.Items;
using VibeMiner.Files;
using VibeMiner.Formats.Models;
using VibeMiner.Formats.Tables;

namespace VibeMiner.Cli.Browse;

/// <summary>What the <c>:items</c> and <c>:creatures</c> views share (#204): opening layer 6 for a load, and the words they show.</summary>
internal static class ConceptsAccess
{
    public const string NotWired = "Items and creatures aren't available: layer 6 isn't wired into this session.";
    /// <summary>The most lines one long list in a detail shows (values, facts, textures, raw cells); the rest is one "+N more" line.</summary>
    public const int MaxListLines = 32;

    /// <summary>
    /// Runs <paramref name="load"/> on a hold on the session's layer 6 handles for the build and locale
    /// (<see cref="BrowseSession.Concepts"/>), released before returning; the handles stay for the next load.
    /// </summary>
    public static async Task<ViewContent> WithConceptsAsync(BrowseSession session, BuildRef build, string locale, Func<ConceptsHold, Task<ViewContent>> load,
        CancellationToken ct)
    {
        if (session.OpenConcepts is null) return new([], [], NotWired);
        await using var hold = await session.Concepts.HoldAsync(build, locale, ct).ConfigureAwait(false);
        return await load(hold).ConfigureAwait(false);
    }

    public static string NotOpen(string locale, FilesOpenState state, string? code, string? message) =>
        FilesView.Fit([$"Can't open the build's files for {locale}: {state} ({code ?? "no code"}){(message is { } m ? $": {m}" : "")}"]);

    public static string I(long value) => value.ToString(CultureInfo.InvariantCulture);
    public static string I(uint? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "-";

    /// <summary>A name as shown: a Valid name as stored (quoted when it is only whitespace), any other state by its name in brackets, never blank.</summary>
    public static string Name(ItemName name) => Shown(name.State == ItemNameState.Valid, name.Value, name.State.ToString());
    public static string Name(CreatureName name) => Shown(name.State == CreatureNameState.Valid, name.Value, name.State.ToString());

    private static string Shown(bool valid, string? value, string state) =>
        !valid || value is null ? $"({state})" : string.IsNullOrWhiteSpace(value) ? $"\"{value}\"" : value;

    /// <summary>A table entry in one phrase: name, FileDataID, state and reason code; "not looked at" when the call didn't reach it.</summary>
    public static string Table(TableEntry? entry) => entry is null ? "not looked at"
        : $"{TablesAccess.Title(entry)} ({TablesAccess.Fdid(entry.FileDataId)}) {entry.State}{(entry.Reason?.Code is { } code ? $" ({code})" : "")}";

    public static string Code(string? code, string? message) =>
        code is null && message is null ? "" : $" {code ?? "no code"}{(message is { } m ? $": {m}" : "")}";

    public static string Row(TableRow row) =>
        $"ID {I(row.Row.Id)}, ordinal {I(row.Ordinal)}{(row.Row.IsCopy ? $", copy of {I(row.Row.CopySourceId)}" : "")}: {string.Join(" | ", TablesAccess.Shown(row.Row))}";

    /// <summary>At most <paramref name="max"/> lines, then one "+N more" line.</summary>
    public static IEnumerable<string> Capped(IReadOnlyList<string> lines, string indent, int max = MaxListLines)
    {
        foreach (var line in lines.Take(max)) yield return line;
        if (lines.Count > max) yield return $"{indent}+{lines.Count - max:N0} more";
    }

    public static string Label(string label) => label.PadRight(16);
}

internal enum ConceptKind { Items, Creatures }

/// <summary><c>:items</c> and <c>:creatures</c>: the current builds of every reviewed product, as <c>:tables</c> lists them. Enter lists the build's items or creatures (enUS).</summary>
internal sealed class ConceptBuildsView(ConceptKind kind) : BrowseView
{
    public override string Title => kind == ConceptKind.Items ? "items" : "creatures";

    public override async Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct)
    {
        var (rows, missing) = await TablesView.CurrentBuildRowsAsync(session, (build, title) => kind == ConceptKind.Items
            ? new ItemListView(build, title, TablesAccess.Locale) : new CreatureListView(build, title, TablesAccess.Locale), ct).ConfigureAwait(false);
        if (missing is not null) return new(TablesView.Columns, [], missing);
        var note = rows.Count == 0 ? "No current builds in the history log. r in :builds fetches them."
            : $"{rows.Count:N0} current builds. Enter lists a build's {Title} ({TablesAccess.Locale}).";
        if (session.OpenConcepts is null) note += " " + ConceptsAccess.NotWired;
        return new(TablesView.Columns, rows, note);
    }
}

/// <summary>One page of a concept list as a view shows it: the rows, the cursor that continues, the note.</summary>
internal sealed record ConceptPage(IReadOnlyList<string> Columns, List<BrowseRow> Rows, string? Next, string Note);

/// <summary>
/// One build and locale's items or creatures, a page at a time in the table's row order (D451). <c>/text</c> searches names
/// (ordinal, ignore case), <c>i</c> lists one exact ID, <c>n</c>/<c>p</c> page with the layer 6 cursor. A page stops at its limit or
/// its row scan budget, so a short page that continues says so. Enter opens the detail.
/// </summary>
internal abstract class ConceptListView(BuildRef build, string title, string locale) : BrowseView
{
    public const int PageSize = ItemQueries.MaxLimit;

    private sealed record State(uint? ExactId, ImmutableList<string> Pages, string PagesFor, int Version);
    private sealed record PageEnd(int Version, string? Next);

    private readonly Lock gate = new();
    private State state = new(null, ImmutableList<string>.Empty, "", 0);
    private PageEnd? end;

    protected BuildRef Build => build;
    protected string Locale => locale;
    protected string Label => title;

    public override string Title => $"{title} {locale}";
    public override bool FiltersOnLoad => true;

    /// <summary>The exact ID listed, if <c>i</c> set one.</summary>
    public uint? ExactId { get { lock (gate) return state.ExactId; } }

    public override IReadOnlyList<ViewKey> Keys =>
    [
        new('n', "next page (continues the scan)", _ => Next()),
        new('p', "previous page", _ => Previous()),
        new('i', "exact ID: list only that ID (empty clears it)", _ => AskId()),
    ];

    private KeyOutcome Next()
    {
        lock (gate)
        {
            if (end is null || end.Version != state.Version) return new(Say: "Still loading this page.");
            if (end.Next is not { } next) return new(Say: "This is the last page.");
            state = state with { Pages = state.Pages.Add(next), Version = state.Version + 1 };
        }
        return new(Reload: true);
    }

    private KeyOutcome Previous()
    {
        lock (gate)
        {
            if (state.Pages.IsEmpty) return new(Say: "This is the first page.");
            state = state with { Pages = state.Pages.RemoveAt(state.Pages.Count - 1), Version = state.Version + 1 };
        }
        return new(Reload: true);
    }

    private KeyOutcome AskId() => new(Ask: new("ID (empty for all):", ExactId is { } id ? ConceptsAccess.I(id) : "", typed =>
    {
        typed = typed.Trim();
        uint? exact = null;
        if (typed.Length > 0)
        {
            if (!uint.TryParse(typed, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) return new(Say: $"Not an ID: {FilesView.Fit([typed])}");
            exact = parsed;
        }
        lock (gate) state = state with { ExactId = exact, Pages = ImmutableList<string>.Empty, Version = state.Version + 1 };
        return new(Reload: true);
    }));

    /// <summary>One page of the request. Argument errors (a query layer 6 refuses) are the caller's to catch.</summary>
    protected abstract Task<ConceptPage> PageAsync(ConceptsHold hold, string query, uint? exactId, string? after, int page, CancellationToken ct);

    public override Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct)
    {
        var filter = Filter;
        State s;
        lock (gate)
        {
            if (state.PagesFor != filter) state = state with { Pages = ImmutableList<string>.Empty, PagesFor = filter, Version = state.Version + 1 };
            s = state;
        }
        if (s.ExactId is { } id && filter.Length > 0)
        {
            Finish(s, null);
            return Task.FromResult(new ViewContent([], [], FilesView.Fit([$"ID {ConceptsAccess.I(id)} and a name search don't combine:", "Esc clears the search, i the ID."])));
        }
        return ConceptsAccess.WithConceptsAsync(session, build, locale, async hold =>
        {
            ConceptPage page;
            try { page = await PageAsync(hold, filter, s.ExactId, s.Pages.IsEmpty ? null : s.Pages[^1], s.Pages.Count + 1, ct).ConfigureAwait(false); }
            catch (ArgumentException e)
            {
                Finish(s, null);
                return new ViewContent([], [], FilesView.Fit([$"Search refused: {e.Message}"]));
            }
            Finish(s, page.Next);
            return new ViewContent(page.Columns, page.Rows, page.Note);
        }, ct);
    }

    private void Finish(State read, string? next)
    {
        lock (gate) if (end is null || read.Version >= end.Version) end = new(read.Version, next);
    }

    /// <summary>
    /// The note of an Ok page: the page, how many matched in how many rows scanned and whether it continues (a short page that
    /// continues says the scan budget ran out), then the keys, the filter and the build, as many as fit.
    /// </summary>
    protected string OkNote(string noun, int page, int count, long scanned, string? next, string query, uint? exactId)
    {
        var head = $"Page {page}: {count:N0} {noun}, {scanned:N0} rows scanned";
        head += next is null ? " (last)." : count < PageSize ? "; scan budget spent, n scans on." : "; n next.";
        var parts = new List<string> { head };
        if (page > 1) parts.Add("p back.");
        if (exactId is { } id) parts.Add($"ID {ConceptsAccess.I(id)}.");
        else if (query.Length > 0) parts.Add($"Name contains \"{query}\".");
        parts.Add($"{title} {locale}.");
        return FilesView.Fit(parts);
    }

    /// <summary>The note of a page that isn't Ok: its state and code first.</summary>
    protected static string StateNote(string state, string? code, string? message, string? what = null) => state switch
    {
        "Stale" => "Stale: the table changed since the first page; p back or reopen it.",
        "InvalidCursor" => "InvalidCursor: no rows past this point.",
        _ => FilesView.Fit([$"{state}{(what is null ? "" : $": {what}")}{(code is null ? "" : $" ({code})")}{(message is { } m ? $": {m}" : "")}"]),
    };
}

/// <summary>A build's items (#204): ID, name and its state, the Item row state, the icon FileDataID and copy source. Enter opens the item.</summary>
internal sealed class ItemListView(BuildRef build, string title, string locale) : ConceptListView(build, title, locale)
{
    private static readonly string[] Columns = ["Id", "Name", "Name state", "Item row", "Icon FDID", "Copy of"];

    protected override async Task<ConceptPage> PageAsync(ConceptsHold hold, string query, uint? exactId, string? after, int page, CancellationToken ct)
    {
        var request = new ItemQuery(query, exactId, PageSize, after);
        ItemQueries.Validate(request);
        var opened = await hold.ItemsAsync(ct).ConfigureAwait(false);
        if (opened is not { State: FilesOpenState.Open, Items: { } items })
            return new(Columns, [], null, ConceptsAccess.NotOpen(Locale, opened.State, opened.Code, opened.Message));
        // The handle belongs to the session's cache.
        {
            var result = await items.ListAsync(request, TablePriority.Interactive, ct).ConfigureAwait(false);
            if (result.State != ItemsPageState.Ok)
            {
                var entry = result.State == ItemsPageState.SparseNotReadable ? result.Sparse : result.State == ItemsPageState.ItemNotReadable ? result.Item : null;
                return new(TablesAccess.FieldValue, entry is null ? [] : TablesAccess.StateRows(entry), null,
                    StateNote(result.State.ToString(), result.Code ?? entry?.Reason?.Code, result.Message, entry is null ? null : $"{TablesAccess.Title(entry)} is {entry.State}"));
            }
            var rows = result.Entries.Select(e => new BrowseRow([
                ConceptsAccess.I(e.Id), ConceptsAccess.Name(e.Name), e.Name.State.ToString(),
                e.Item == ItemRowState.Protected ? $"Protected ({e.ItemKey})" : e.Item.ToString(),
                e.IconFileDataId is { } icon ? ConceptsAccess.I(icon) : "(unavailable)",
                e.IsCopy ? ConceptsAccess.I(e.CopySourceId) : ""], () => new ItemDetailView(Build, Locale, e.Id), e)).ToList();
            var note = exactId is { } id && rows.Count == 0 ? $"No item {ConceptsAccess.I(id)}: Item is complete and has no such row."
                : OkNote("items", page, rows.Count, result.Scanned, result.Next, query, exactId);
            return new(Columns, rows, result.Next, note);
        }
    }
}

/// <summary>A build's creatures (#204): ID, name and its state, the declared display slots and copy source. Enter opens the creature.</summary>
internal sealed class CreatureListView(BuildRef build, string title, string locale) : ConceptListView(build, title, locale)
{
    private static readonly string[] Columns = ["Id", "Name", "Name state", "Displays", "Copy of"];

    /// <summary>The slots in one cell: a declared DisplayID, <c>-</c> for none, <c>(Invalid n)</c> for a negative one.</summary>
    internal static string Slots(IReadOnlyList<CreatureDisplaySlot> slots) => slots.Count == 0 ? "(none)" : string.Join(" ", slots.Select(s => s.State switch
    {
        CreatureSlotState.Declared => ConceptsAccess.I(s.DisplayId),
        CreatureSlotState.NoDeclaredDisplay => "-",
        _ => $"(Invalid {ConceptsAccess.I(s.DisplayId)})",
    }));

    protected override async Task<ConceptPage> PageAsync(ConceptsHold hold, string query, uint? exactId, string? after, int page, CancellationToken ct)
    {
        var request = new CreatureQuery(query, exactId, PageSize, after);
        CreatureQueries.Validate(request);
        var opened = await hold.CreaturesAsync(ct).ConfigureAwait(false);
        if (opened is not { State: FilesOpenState.Open, Creatures: { } creatures })
            return new(Columns, [], null, ConceptsAccess.NotOpen(Locale, opened.State, opened.Code, opened.Message));
        // The handle belongs to the session's cache.
        {
            var result = await creatures.ListAsync(request, TablePriority.Interactive, ct).ConfigureAwait(false);
            if (result.State != CreaturesPageState.Ok)
            {
                var entry = result.State == CreaturesPageState.CreatureNotReadable ? result.Creature : null;
                return new(TablesAccess.FieldValue, entry is null ? [] : TablesAccess.StateRows(entry), null,
                    StateNote(result.State.ToString(), result.Code ?? entry?.Reason?.Code, result.Message, entry is null ? null : $"{TablesAccess.Title(entry)} is {entry.State}"));
            }
            var rows = result.Entries.Select(e => new BrowseRow([
                ConceptsAccess.I(e.Id), ConceptsAccess.Name(e.Name), e.Name.State.ToString(), Slots(e.Slots),
                e.IsCopy ? ConceptsAccess.I(e.CopySourceId) : ""], () => new CreatureDetailView(Build, Locale, e.Id), e)).ToList();
            string note;
            if (exactId is { } id && rows.Count == 0)
                note = result.Exact switch
                {
                    TableRowOutcome.Protected => $"Creature {ConceptsAccess.I(id)} is Protected (key {result.ProtectedKey}): not proof it's absent.",
                    TableRowOutcome.Absent or null => $"No creature {ConceptsAccess.I(id)}: Creature is complete and has no such row.",
                    { } other => $"Creature {ConceptsAccess.I(id)} is {other}: not in the readable rows, not proof it's absent.",
                };
            else note = OkNote("creatures", page, rows.Count, result.Scanned, result.Next, query, exactId);
            return new(Columns, rows, result.Next, note);
        }
    }
}

/// <summary>
/// One item as text (#204): the summary with every state named, the icon, facts, labels, its effects a page at a time (Down on
/// the last line, or <c>n</c>, reads the next page) and the source rows.
/// </summary>
internal sealed class ItemDetailView(BuildRef build, string locale, uint id) : BrowseView
{
    /// <summary>The most effect pages a detail reads (16 effects each).</summary>
    public const int MaxEffectPages = 8;

    private readonly Lock gate = new();
    private int wanted = 1, read;
    private bool more;

    public override string Title => $"item {ConceptsAccess.I(id)}";

    public override IReadOnlyList<ViewKey> Keys => [new('n', "next effects page (also Down on the last line)", _ => NextEffects())];

    public override bool HasMore { get { lock (gate) return more && read >= wanted && wanted < MaxEffectPages; } }

    public override void More() { lock (gate) if (more && wanted < MaxEffectPages) wanted = Math.Max(wanted, read + 1); }

    private KeyOutcome NextEffects()
    {
        if (!HasMore) return new(Say: "No more effects to read here.");
        More();
        return new(Reload: true);
    }

    public override Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct)
    {
        int pages;
        lock (gate) pages = wanted;
        return ConceptsAccess.WithConceptsAsync(session, build, locale, async hold =>
        {
            var opened = await hold.ItemsAsync(ct).ConfigureAwait(false);
            if (opened is not { State: FilesOpenState.Open, Items: { } items })
                return ViewContent.OfText(ConceptsAccess.NotOpen(locale, opened.State, opened.Code, opened.Message));
            ItemResult result;
            result = await items.GetAsync(id, TablePriority.Interactive, ct).ConfigureAwait(false);
            var lines = new List<string> { $"Item {ConceptsAccess.I(id)} ({locale})" };
            lines.AddRange(ItemText.Result(result));
            if (result.Detail is null)
            {
                lock (gate) { read = 0; more = false; }
                return ViewContent.OfText(string.Join('\n', lines), $"Item {ConceptsAccess.I(id)}: {result.State}.");
            }
            var effectsOpened = await hold.EffectsAsync(ct).ConfigureAwait(false);
            var effectPages = new List<ItemEffectsPage>();
            if (effectsOpened is { State: FilesOpenState.Open, Effects: { } effects })
                {
                    string? after = null;
                    for (var p = 0; p < pages; p++)
                    {
                        var page = await effects.GetAsync(id, new ItemEffectsQuery(ItemEffectQueries.MaxLimit, after), TablePriority.Interactive, ct).ConfigureAwait(false);
                        effectPages.Add(page);
                        if (page.State != ItemEffectsPageState.Ok || page.Next is null) break;
                        after = page.Next;
                    }
                }
            lines.AddRange(ItemText.Effects(effectsOpened, effectPages));
            lines.AddRange(ItemText.SourceRows(result.Detail));
            var continues = effectPages.Count > 0 && effectPages[^1].Next is not null;
            lock (gate) { read = effectPages.Count; more = continues; }
            var note = $"Item {ConceptsAccess.I(id)}: {ConceptsAccess.Name(result.Detail.Summary.Name)}."
                + (continues ? effectPages.Count < MaxEffectPages ? " More effects: n reads the next page." : $" Only the first {MaxEffectPages} effect pages are read." : "");
            return ViewContent.OfText(string.Join('\n', lines), FilesView.Fit([note]));
        }, ct);
    }
}

/// <summary>The item detail's text, one section per part of <see cref="ItemDetail"/>; a later part is one more section.</summary>
internal static class ItemText
{
    private static string L(string label) => "  " + ConceptsAccess.Label(label);

    public static IEnumerable<string> Result(ItemResult result)
    {
        if (result.Detail is not { } d)
        {
            yield return $"Result          {result.State}";
            if (result.ProtectedKey is { } key) yield return $"Key needed      {key}";
            yield return $"Item table      {ConceptsAccess.Table(result.Item)}";
            yield return $"ItemSparse      {ConceptsAccess.Table(result.Sparse)}";
            if (result.Code is not null || result.Message is not null) yield return $"Code           {ConceptsAccess.Code(result.Code, result.Message)}";
            yield break;
        }
        foreach (var section in Sections(d)) foreach (var line in section) yield return line;
    }

    /// <summary>Summary, icons (each with its source field), facts, labels, appearances: each its own section.</summary>
    public static IEnumerable<IEnumerable<string>> Sections(ItemDetail d) => [Summary(d.Summary), Icons(d.Icons), Facts(d.Facts), Labels(d.Labels), Appearances(d.Appearances)];

    /// <summary>Every icon with the field it came from (D469); none is picked as "the" icon.</summary>
    public static IEnumerable<string> Icons(IReadOnlyList<ItemIconSourced> icons)
    {
        if (icons.Count == 0) yield return "Icons           (none)";
        foreach (var sourced in icons)
            foreach (var line in Icon(sourced.Icon, $"Icon from {sourced.Table}.{sourced.Column} of row {ConceptsAccess.I(sourced.RowId)}")) yield return line;
    }

    public static IEnumerable<string> Summary(ItemSummary s)
    {
        yield return "Summary";
        yield return L("Name") + ConceptsAccess.Name(s.Name);
        yield return L("Name state") + s.Name.State + (s.SparseKey is { } key ? $" (key {key})" : "");
        yield return L("Item row") + s.Item + (s.ItemKey is { } itemKey ? $" (key {itemKey})" : "");
        yield return L("Copy") + (s.IsCopy ? $"copy of {ConceptsAccess.I(s.CopySourceId)}" : "no");
        yield return L("Icon FDID") + (s.IconFileDataId is { } icon ? ConceptsAccess.I(icon) : "(unavailable)");
        if (s.Values.Count == 0) yield return L("Values") + "(none)";
        foreach (var line in ConceptsAccess.Capped([.. s.Values.Select(v => L("Value") + $"{v.Table}.{v.Column} [{v.ColumnIndex}] = {v.Value}")], "    ")) yield return line;
    }

    public static IEnumerable<string> Icon(ItemIcon icon, string title = "Icon")
    {
        yield return title;
        yield return L("State") + icon.State;
        yield return L("FileDataID") + ConceptsAccess.I(icon.FileDataId);
        yield return L("CKey") + (icon.ContentKey ?? "-");
        yield return L("Image") + (icon.Image?.ToString() ?? "not inspected");
        if (icon.Header is { } h)
            yield return L("Header") + $"{h.Width} x {h.Height}, color encoding {h.ColorEncoding}, alpha depth {h.AlphaDepth}, alpha encoding {h.AlphaEncoding}, {h.DeclaredMips} declared mips";
        if (icon.Encoding is { } encoding) yield return L("Encoding") + $"{encoding}, {icon.MipCount ?? 0} mips";
        if (icon.FileState is { } file) yield return L("File state") + file;
        if (icon.HoleKeys is { Count: > 0 } keys) yield return L("Keys needed") + string.Join(' ', keys);
        if (icon.Code is not null || icon.Message is not null) yield return L("Code") + ConceptsAccess.Code(icon.Code, icon.Message).TrimStart();
        if (icon.ObservedAt is { } at) yield return L("Observed") + $"{BrowseSession.Time(at)} UTC";
    }

    public static IEnumerable<string> Facts(ItemFactSet facts)
    {
        yield return $"Facts           {facts.State}, ItemSparse {facts.Sparse}{ConceptsAccess.Code(facts.Code, facts.Message)}";
        var lines = facts.Facts.Select(f => L(f.Key) + $"{f.State}, {f.Agreement}"
            + (f.Values.Count == 0 ? "" : ": " + string.Join("; ", f.Values.Select(v => $"{v.Table}.{v.Column} = {v.Value}{(v.Label is { } label ? $" ({label})" : "")}")))).ToList();
        foreach (var line in ConceptsAccess.Capped(lines, "    ")) yield return line;
    }

    public static IEnumerable<string> Labels(ItemClassLabels labels)
    {
        yield return "Labels";
        yield return Label("Class", labels.Class);
        yield return Label("Subclass", labels.Subclass);
    }

    private static string Label(string name, ItemClassLabel label)
    {
        var parts = new List<string> { label.State.ToString() };
        if (label.Text is { } text) parts.Add($"\"{text}\"");
        if (label.SourceKeys.Count > 0) parts.Add("keys " + string.Join(", ", label.SourceKeys.Select(k => $"{k.Column}={k.Value}")));
        if (label.Candidates is { } candidates) parts.Add($"{candidates:N0} candidates in {label.Scanned:N0} rows");
        if (label.Table is { } table) parts.Add(ConceptsAccess.Table(table));
        if (label.Code is not null) parts.Add(label.Code);
        return L(name) + string.Join(", ", parts);
    }

    /// <summary>
    /// The appearances section (D467-D470): the set's state and default, each ItemModifiedAppearance row down to its display
    /// slots and resource files, then the distinct model files and their reads.
    /// </summary>
    public static IEnumerable<string> Appearances(ItemAppearanceSet set)
    {
        yield return $"Appearances     {set.State}, default {set.Default}{(set.DefaultIndex is { } at ? $" (row #{at + 1})" : "")}, {set.Rows.Count} of {set.Matches:N0} rows"
            + ConceptsAccess.Code(set.Code, set.Message);
        yield return L("Table") + ConceptsAccess.Table(set.Table);
        var rows = new List<string>();
        for (var i = 0; i < set.Rows.Count; i++)
        {
            var r = set.Rows[i];
            rows.Add($"    #{i + 1} row {ConceptsAccess.I(r.Row.Row.Id)}: modifier {Long(r.ItemAppearanceModifierId)}, order {Long(r.OrderIndex)}, ItemAppearanceID {Long(r.ItemAppearanceId)}");
            var a = r.Appearance;
            rows.Add($"      ItemAppearance {Ref(a.State, a.ProtectedKey, a.Table, a.Code)}"
                + (a.State == ItemRefState.Found ? $", DefaultIconFileDataID {ConceptsAccess.I(a.DefaultIconFileDataId)}, ItemDisplayInfoID {ConceptsAccess.I(a.ItemDisplayInfoId)}" : ""));
            if (a.Display is not { } d) continue;
            rows.Add($"      ItemDisplayInfo {ConceptsAccess.I(d.Id)} {Ref(d.State, d.ProtectedKey, d.Table, d.Code)}");
            foreach (var slot in d.Slots)
                foreach (var resource in new[] { slot.Model, slot.Material })
                {
                    rows.Add($"        Slot {slot.Slot} {resource.Kind} {Long(resource.ResourceId)}: {resource.State}{(resource.Complete ? "" : ", not complete")}, {resource.Files.Count} of {resource.Matches} files"
                        + (resource.KeyNames is { Count: > 0 } keys ? $", hidden keys {string.Join(' ', keys)}" : "") + ConceptsAccess.Code(resource.Code, null));
                    foreach (var file in resource.Files)
                    {
                        var c = file.Component;
                        rows.Add($"          FDID {ConceptsAccess.I(file.FileDataId)}: component {c.State}"
                            + (c.State == ItemRefState.Found ? $" race {Long(c.RaceId)} gender {Long(c.GenderIndex)} class {Long(c.ClassId)}{(resource.Kind == ItemResourceKind.Model ? $" position {Long(c.PositionIndex)}" : "")}" : "")
                            + (c.ProtectedKey is { } key ? $" (key {key})" : "") + ConceptsAccess.Code(c.Code, null));
                    }
                }
        }
        foreach (var line in ConceptsAccess.Capped(rows, "    ", ConceptsAccess.MaxListLines * 2)) yield return line;
        var models = set.Models;
        yield return L("Models") + $"{models.State}, {models.Models.Count} distinct" + ConceptsAccess.Code(models.Code, models.Message);
        var modelLines = new List<string>();
        foreach (var m in models.Models)
        {
            modelLines.Add($"    FDID {ConceptsAccess.I(m.FileDataId)}: {m.State}{(m.Read is { } read ? $", read {read}" : "")}"
                + (m.State == ItemModelRefState.Read && m.Read == ModelReadState.Read ? $", {m.SkinFileDataIds} skins, {m.Textures?.Count ?? 0} textures" : "")
                + (m.KeyNames is { Count: > 0 } keys ? $", keys {string.Join(' ', keys)}" : "") + ConceptsAccess.Code(m.Code, m.Message));
            foreach (var t in m.Textures ?? [])
                modelLines.Add($"      #{t.Ordinal} type {(t.Type is { } type ? type.ToString(CultureInfo.InvariantCulture) : "-")} FDID {ConceptsAccess.I(t.FileDataId)} {t.State}");
        }
        foreach (var line in ConceptsAccess.Capped(modelLines, "    ")) yield return line;
    }

    private static string Long(long? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : "(invalid)";

    private static string Ref(ItemRefState state, string? key, TableEntry? table, string? code) =>
        state + (key is { } k ? $" (key {k})" : "") + (state == ItemRefState.TableNotReadable ? $": {ConceptsAccess.Table(table)}" : "") + (code is null ? "" : $" ({code})");

    /// <summary>The effects section: the open, each page read (state, link, sources, scanned) and its entries.</summary>
    public static IEnumerable<string> Effects(ItemEffectsOpenResult opened, IReadOnlyList<ItemEffectsPage> pages)
    {
        if (opened.State != FilesOpenState.Open)
        {
            yield return $"Effects         not opened: {opened.State}{ConceptsAccess.Code(opened.Code, opened.Message)}";
            yield break;
        }
        var first = pages.Count > 0 ? pages[0] : null;
        yield return $"Effects         link {first?.Link?.ToString() ?? "(none)"}, {pages.Count} page(s) read";
        if (first is not null) yield return L("Sources") + string.Join(", ", new[] { first.Bridge, first.Effect, first.Name }.Select(Source));
        for (var p = 0; p < pages.Count; p++)
        {
            var page = pages[p];
            yield return L($"Page {p + 1}") + $"{page.State}, {page.Entries.Count} entries of {page.Matches:N0} link rows"
                + (page.ProtectedKey is { } key ? $", key {key}" : "") + ConceptsAccess.Code(page.Code, page.Message)
                + (page.Next is null ? "" : ", continues");
            foreach (var entry in page.Entries) yield return "    " + Entry(entry);
        }
    }

    private static string Source(ItemEffectsSource source) =>
        $"{source.Role} {source.State}{(source.Problem is { } problem ? $" ({problem.Code})" : "")}";

    internal static string Entry(ItemEffectEntry e)
    {
        var effect = e.Effect;
        var line = $"link {ConceptsAccess.I(e.Link.Row.Id)}: effect {e.ItemEffectId} {effect.State}";
        if (effect.Absence is { } absence) line += $" ({absence})";
        if (effect.ProtectedKey is { } key) line += $" (key {key})";
        line += $", spell {effect.SpellId ?? "-"}, name {e.Name.State}";
        if (e.Name.Text is { } text) line += $" \"{text}\"";
        if (e.Name.TextUnits is { } units) line += $" ({units} units)";
        if (e.Name.IssueCode is { } issue) line += $" ({issue})";
        if (e.Name.Absence is { } nameAbsence) line += $" ({nameAbsence})";
        if (e.Name.ProtectedKey is { } nameKey) line += $" (key {nameKey})";
        return line;
    }

    public static IEnumerable<string> SourceRows(ItemDetail d)
    {
        yield return "Source rows";
        yield return L("Item") + ConceptsAccess.Row(d.ItemRow);
        // No row is only proven absent when ItemSparse is complete (Missing); Unknown and Protected never are.
        yield return L("ItemSparse") + (d.SparseRow is { } sparse ? ConceptsAccess.Row(sparse) : ItemFacts.SparseState(d.Summary.Name.State) switch
        {
            ItemSparseState.Missing => "Missing: ItemSparse is complete and has no row",
            ItemSparseState.Protected => $"Protected: in a keyed section (key {d.Summary.SparseKey ?? "unknown"})",
            ItemSparseState.Unknown => "Unknown: not in ItemSparse's readable rows, not proof it's absent",
            var other => $"{other}: no row",
        });
    }
}

/// <summary>One creature as text (#204): the summary with every state named, the display slots, each slot's chain and its model.</summary>
internal sealed class CreatureDetailView(BuildRef build, string locale, uint id) : BrowseView
{
    public override string Title => $"creature {ConceptsAccess.I(id)}";

    public override Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct) =>
        ConceptsAccess.WithConceptsAsync(session, build, locale, async hold =>
        {
            var opened = await hold.CreaturesAsync(ct).ConfigureAwait(false);
            if (opened is not { State: FilesOpenState.Open, Creatures: { } creatures })
                return ViewContent.OfText(ConceptsAccess.NotOpen(locale, opened.State, opened.Code, opened.Message));
            CreatureResult result;
            result = await creatures.GetAsync(id, TablePriority.Interactive, ct).ConfigureAwait(false);
            var lines = new List<string> { $"Creature {ConceptsAccess.I(id)} ({locale})" };
            lines.AddRange(CreatureText.Result(result));
            var note = result.Detail is { } d ? $"Creature {ConceptsAccess.I(id)}: {ConceptsAccess.Name(d.Summary.Name)}." : $"Creature {ConceptsAccess.I(id)}: {result.State}.";
            return ViewContent.OfText(string.Join('\n', lines), FilesView.Fit([note]));
        }, ct);
}

internal static class CreatureText
{
    private static string L(string label, int depth = 1) => new string(' ', depth * 2) + ConceptsAccess.Label(label);

    public static IEnumerable<string> Result(CreatureResult result)
    {
        if (result.Detail is not { } d)
        {
            yield return $"Result          {result.State}";
            if (result.ProtectedKey is { } key) yield return $"Key needed      {key}";
            yield return $"Creature table  {ConceptsAccess.Table(result.Creature)}";
            if (result.Code is not null || result.Message is not null) yield return $"Code           {ConceptsAccess.Code(result.Code, result.Message)}";
            yield break;
        }
        foreach (var section in Sections(d)) foreach (var line in section) yield return line;
    }

    public static IEnumerable<IEnumerable<string>> Sections(CreatureDetail d) => [Summary(d), Slots(d.Summary.Slots), Chains(d.Displays), Row(d.Row)];

    public static IEnumerable<string> Summary(CreatureDetail d)
    {
        var s = d.Summary;
        yield return "Summary";
        yield return L("Name") + ConceptsAccess.Name(s.Name);
        yield return L("Name state") + s.Name.State + (s.Name.Issue is { } issue ? $" ({issue})" : "");
        yield return L("Copy") + (s.IsCopy ? $"copy of {ConceptsAccess.I(s.CopySourceId)}" : "no");
        yield return L("Classification") + (d.ClassificationLabel ?? "(no reviewed label)");
        if (s.Facts.Count == 0) yield return L("Facts") + "(none)";
        foreach (var line in ConceptsAccess.Capped([.. s.Facts.Select(f => L(f.Column) + $"[{f.ColumnIndex}] {f.Value}")], "    ")) yield return line;
    }

    public static IEnumerable<string> Slots(IReadOnlyList<CreatureDisplaySlot> slots)
    {
        yield return "Display slots";
        foreach (var slot in slots) yield return L($"Slot {slot.Slot}") + Slot(slot);
    }

    private static string Slot(CreatureDisplaySlot slot) =>
        $"DisplayID {ConceptsAccess.I(slot.DisplayId)}, probability {slot.Probability.ToString("R", CultureInfo.InvariantCulture)}, {slot.State}";

    public static IEnumerable<string> Chains(IReadOnlyList<CreatureDisplayChain> chains)
    {
        yield return "Display chains";
        foreach (var chain in chains)
            foreach (var line in Chain(chain)) yield return line;
    }

    public static IEnumerable<string> Chain(CreatureDisplayChain c)
    {
        yield return L($"Slot {c.Slot.Slot}") + c.State;
        if (c.Display is not null) yield return L("Display table", 2) + ConceptsAccess.Table(c.Display);
        if (c.DisplayRow is { } displayRow) yield return L("Display row", 2) + ConceptsAccess.Row(displayRow);
        if (c.ModelId is { } modelId) yield return L("ModelID", 2) + ConceptsAccess.I(modelId);
        if (c.ModelData is not null) yield return L("Model table", 2) + ConceptsAccess.Table(c.ModelData);
        if (c.ModelRow is { } modelRow) yield return L("Model row", 2) + ConceptsAccess.Row(modelRow);
        if (c.ModelFileDataId is { } fdid) yield return L("Model FDID", 2) + ConceptsAccess.I(fdid);
        if (c.ProtectedKey is { } key) yield return L("Key needed", 2) + key;
        if (c.Code is not null || c.Message is not null) yield return L("Code", 2) + ConceptsAccess.Code(c.Code, c.Message).TrimStart();
        if (c.DisplayFacts is { } displayFacts) foreach (var line in Facts("Display facts", displayFacts)) yield return line;
        if (c.ModelFacts is { } modelFacts) foreach (var line in Facts("Model facts", modelFacts)) yield return line;
        if (c.Textures is { } t)
            yield return L("Variations", 2) + $"ExtendedDisplayInfoID {ConceptsAccess.I(t.ExtendedDisplayInfoId)}, TextureVariationFileDataID {string.Join(' ', t.TextureVariations.Select(v => ConceptsAccess.I(v)))}";
        if (c.Model is { } model) foreach (var line in Model(model)) yield return line;
    }

    private static IEnumerable<string> Facts(string name, CreatureRecordFacts facts)
    {
        yield return L(name, 2) + facts.State + (facts.Code is { } code ? $" ({code})" : "")
            + (facts.Raw.Count > 0 ? $", {facts.Raw.Count} other cells" : "");
        var lines = facts.Facts.Select(f => L(f.Key, 3) + f.State
            + (f.Cells.Count == 0 ? "" : ": " + string.Join(", ", f.Cells.Select(cell => $"{cell.Column} = {cell.Value}")))).ToList();
        foreach (var line in ConceptsAccess.Capped(lines, "        ")) yield return line;
    }

    public static IEnumerable<string> Model(CreatureModel m)
    {
        yield return L("Model", 2) + $"{m.State}, FileDataID {ConceptsAccess.I(m.FileDataId)}, read {m.ReadState?.ToString() ?? "-"}"
            + (m.KeyNames is { Count: > 0 } keys ? $", keys {string.Join(' ', keys)}" : "") + ConceptsAccess.Code(m.Code, m.Message);
        yield return L("Main skin", 3) + $"{ConceptsAccess.I(m.MainSkinFileDataId)}, {m.SkinState?.ToString() ?? "not read"}"
            + (m.SkinKeyNames is { Count: > 0 } skinKeys ? $", keys {string.Join(' ', skinKeys)}" : "");
        var a = m.Appearance;
        yield return L("Appearance", 3) + a.State + ConceptsAccess.Code(a.Code, a.Message)
            + (a.TextureOrdinal is { } texture ? $", texture #{texture}" : "") + (a.BatchOrdinal is { } batch ? $", batch #{batch}" : "")
            + (a.BlendedBatches is { } blended ? $", {blended} blended batches" : "");
        yield return L("Textures", 3) + $"{m.Textures.Count} declared, {m.Textures.Count(t => t.Drawn)} drawn";
        foreach (var line in ConceptsAccess.Capped([.. m.Textures.Select(t => "        " + Texture(t))], "        ")) yield return line;
    }

    internal static string Texture(CreatureTexture t)
    {
        var line = $"#{t.Ordinal} type {t.Type} flags {t.Flags} {t.Source}{(t.Variation is { } v ? $"[{v}]" : "")} FDID {ConceptsAccess.I(t.FileDataId)} {t.State}, drawn {(t.Drawn ? "yes" : "no")}";
        if (t.Reference is { } r) line += $", reference {r.Resolution}";
        if (t.Image is { } image) line += $", image {image}";
        if (t.FileState is { } file) line += $", file {file}";
        if (t.HoleKeys is { Count: > 0 } keys) line += $", keys {string.Join(' ', keys)}";
        if (t.Code is { } code) line += $" ({code})";
        if (t.Filename.Length > 0) line += $", name {t.Filename}";
        return line;
    }

    public static IEnumerable<string> Row(TableRow row)
    {
        yield return "Source row";
        yield return L("Creature") + ConceptsAccess.Row(row);
    }
}
