namespace VibeMiner.Cli.Browse;

/// <summary>One table row: its cells and, when Enter opens something, the view it opens.</summary>
internal sealed record BrowseRow(IReadOnlyList<string> Cells, Func<BrowseView>? Drill = null)
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
/// A view's model: what it lists and what Enter opens. Views don't know Terminal.Gui; <see cref="Navigator"/> loads them off the UI
/// thread and the shell draws the result. A load opens what it needs and closes it before it returns, so nothing stays open
/// between keys.
/// </summary>
internal abstract class BrowseView
{
    /// <summary>The breadcrumb part: the registry name for a top view, the drilled item for the others.</summary>
    public abstract string Title { get; }
    public virtual IReadOnlyList<ViewAction> Actions => [];
    public abstract Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct);
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

    /// <summary>The views <c>vm browse</c> has today; files and tables come with their layers.</summary>
    public static ViewRegistry Default() => new ViewRegistry()
        .Add(new("store", "the object store: packs, objects, bytes", () => new StoreView()))
        .Add(new("history", "version-service replies per product; Enter shows the raw reply", () => new HistoryView()))
        .Add(new("builds", "builds per product from the history log; r refreshes", () => new BuildsView()))
        .Add(new("settings", "the data folder, its sizes, the last scrub and measured builds (read-only)", () => new SettingsView()));
}
