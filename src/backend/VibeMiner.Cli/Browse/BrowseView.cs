namespace VibeMiner.Cli.Browse;

/// <summary>
/// One table row: its cells, when Enter opens something the view it opens, and <see cref="Tag"/>, what the row stands for, for a
/// <see cref="ViewKey"/> that acts on the selected row.
/// </summary>
internal sealed record BrowseRow(IReadOnlyList<string> Cells, Func<BrowseView>? Drill = null, object? Tag = null)
{
    public bool Matches(string filter) => Cells.Any(c => c.Contains(filter, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// What a view shows after a load: a table (<see cref="Columns"/> and <see cref="Rows"/>) or, when <see cref="Text"/> is set, a
/// read-only text. <see cref="Note"/> is one line under the title (a missing store, truncation, problems).
/// </summary>
internal sealed record ViewContent(IReadOnlyList<string> Columns, IReadOnlyList<BrowseRow> Rows, string? Note = null, string? Text = null)
{
    public static ViewContent OfText(string text, string? note = null) => new([], [], note, text);
}

/// <summary>
/// A key a view adds, which runs as one background task (<see cref="BackgroundTasks"/>). <see cref="Run"/> reports progress lines
/// and returns the line the status keeps when it ends. The current view reloads after it.
/// </summary>
internal sealed record ViewAction(char Key, string Help, string Task, Func<BrowseSession, IProgress<string>, CancellationToken, Task<string>> Run);

/// <summary>
/// A quick key a view adds, which runs on the UI thread: <see cref="Press"/> gets the selected visible row (null in a text view or
/// an empty table) and says what to do. It must not do I/O; slow work goes in <see cref="KeyOutcome.Start"/>. It can't take a
/// global key (<see cref="Navigator.GlobalKeys"/>) or a key one of the view's <see cref="BrowseView.Actions"/> has; such a key is
/// ignored and not listed in the help.
/// </summary>
internal sealed record ViewKey(char Key, string Help, Func<BrowseRow?, KeyOutcome> Press);

/// <summary>
/// What a <see cref="ViewKey"/> (or a <see cref="KeyAsk"/> answer) does, any mix of: <see cref="Say"/> a message line,
/// <see cref="Start"/> a background task (the view showing when it ends reloads, as after a <see cref="ViewAction"/>),
/// <see cref="Reload"/> the current view, <see cref="Open"/> a view on top of it (like Enter on a row), and <see cref="Ask"/> a
/// question on the status line, and <see cref="Act"/> on the session on the UI thread, whose non-null return is said (it must
/// not block: the table preload's stop and resume start their work and return). Reload with Open reloads the current view when Esc comes back to it.
/// </summary>
internal sealed record KeyOutcome(BrowseView? Open = null, bool Reload = false, string? Say = null, KeyAsk? Ask = null, KeyTask? Start = null,
    Func<BrowseSession, string?>? Act = null)
{
    public static readonly KeyOutcome None = new();
}

/// <summary>A prompt on the status line: Enter passes the typed line to <see cref="Answer"/>, Esc cancels it.</summary>
internal sealed record KeyAsk(string Question, string Initial, Func<string, KeyOutcome> Answer);

/// <summary>A background task a key starts, run like a <see cref="ViewAction"/>'s.</summary>
internal sealed record KeyTask(string Name, Func<BrowseSession, IProgress<string>, CancellationToken, Task<string>> Run);

/// <summary>
/// A view's model: what it lists and what Enter opens. Views don't know Terminal.Gui; <see cref="Navigator"/> loads them off the UI
/// thread and the shell draws the result. A load opens what it needs and closes it before it returns, so nothing stays open
/// between keys.
/// </summary>
internal abstract class BrowseView
{
    /// <summary>The breadcrumb part: the registry name for a top view, the drilled item for the others.</summary>
    public abstract string Title { get; }
    public virtual IReadOnlyList<ViewAction> Actions => [];
    public virtual IReadOnlyList<ViewKey> Keys => [];

    /// <summary>
    /// True for a view that filters while it loads (a long list read a page at a time): <c>/text</c> and Esc reload it with
    /// <see cref="Filter"/> set instead of narrowing the loaded rows.
    /// </summary>
    public virtual bool FiltersOnLoad => false;
    /// <summary>The frame's filter, set by <see cref="Navigator"/> before every load.</summary>
    public string Filter { get; set; } = "";

    /// <summary>True when a text view can read further than its last load showed.</summary>
    public virtual bool HasMore => false;
    /// <summary>Down or PgDn on the last line of a text view with <see cref="HasMore"/>: widen what the next load reads. No I/O here.</summary>
    public virtual void More() { }

    public abstract Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct);

    /// <summary>
    /// The table preload line this view is about (its build's), which the status line shows instead of the session's latest;
    /// null for none. Called on the UI thread on every redraw: no I/O.
    /// </summary>
    public virtual PreloadLine? PreloadLine(SessionPreloads preloads) => null;
}

/// <summary>A top view the <c>:</c> line can jump to.</summary>
internal sealed record ViewEntry(string Name, string Help, Func<BrowseView> Create);

/// <summary>
/// The top views by name. <c>:name</c> takes an exact name or a unique prefix. Adding a view is one <see cref="ViewEntry"/> here
/// and one <see cref="BrowseView"/>.
/// </summary>
internal sealed class ViewRegistry
{
    private readonly List<ViewEntry> entries = [];

    public IReadOnlyList<ViewEntry> Entries => entries;

    public ViewRegistry Add(ViewEntry entry)
    {
        if (entries.Any(e => e.Name == entry.Name)) throw new ArgumentException($"View {entry.Name} is already registered.", nameof(entry));
        entries.Add(entry);
        return this;
    }

    public ViewEntry? Find(string name)
    {
        var matches = Matching(name);
        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>The exact match alone, else every view the prefix starts; more than one means the prefix is ambiguous.</summary>
    public IReadOnlyList<ViewEntry> Matching(string name)
    {
        name = name.Trim().TrimStart(':').ToLowerInvariant();
        if (name.Length == 0) return [];
        if (entries.FirstOrDefault(e => e.Name == name) is { } exact) return [exact];
        return entries.Where(e => e.Name.StartsWith(name, StringComparison.Ordinal)).ToList();
    }

    /// <summary>The views <c>vm browse</c> has today.</summary>
    public static ViewRegistry Default() => new ViewRegistry()
        .Add(new("store", "the object store: packs, objects, bytes", () => new StoreView()))
        .Add(new("history", "version-service replies per product; Enter shows the raw reply", () => new HistoryView()))
        .Add(new("builds", "builds per product from the history log; r refreshes", () => new BuildsView()))
        .Add(new("settings", "the data folder, its sizes, the last scrub and measured builds (read-only)", () => new SettingsView()))
        .Add(new("files", "a build's files; f in :builds opens one", () => new FilesView()))
        .Add(new("tables", "a current build's tables (enUS): state, schema, rows, references", () => new TablesView()))
        .Add(new("items", "a current build's items (enUS): names, icon, facts, labels, effects", () => new ConceptBuildsView(ConceptKind.Items)))
        .Add(new("creatures", "a current build's creatures (enUS): names, display chains, models", () => new ConceptBuildsView(ConceptKind.Creatures)));
}
