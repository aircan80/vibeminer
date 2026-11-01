using VibeMiner.Cli.Browse;

namespace VibeMiner.Cli.Tests;

/// <summary>
/// A table view for the framework tests: rows <c>alpha</c>, <c>beta</c>, <c>gamma</c> tagged with their index, the keys and
/// actions a test gives it, and every load's filter recorded. With <see cref="FiltersOnLoad"/> it narrows the rows itself.
/// </summary>
internal sealed class KeysView(string title = "keys", bool filtersOnLoad = false) : BrowseView
{
    public static readonly string[] Names = ["alpha", "beta", "gamma"];

    public override string Title => title;
    public List<ViewKey> KeyList { get; } = [];
    public List<ViewAction> ActionList { get; } = [];
    public override IReadOnlyList<ViewKey> Keys => KeyList;
    public override IReadOnlyList<ViewAction> Actions => ActionList;
    public override bool FiltersOnLoad => filtersOnLoad;
    /// <summary>The filter each load saw, in order.</summary>
    public List<string> Loads { get; } = [];

    public override Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct)
    {
        Loads.Add(Filter);
        var rows = Names.Select((n, i) => new BrowseRow([n, $"{i}"], Tag: i))
            .Where(r => !filtersOnLoad || r.Cells[0].Contains(Filter, StringComparison.OrdinalIgnoreCase)).ToList();
        return Task.FromResult(new ViewContent(["Name", "Index"], rows));
    }
}

/// <summary>A text view that reads <see cref="PageLines"/> more lines each time <see cref="More"/> asks, up to <see cref="Pages"/> pages.</summary>
internal sealed class MoreView(int pageLines = 30, int pages = 3) : BrowseView
{
    public override string Title => "more";
    public int Read { get; private set; } = 1;
    public int PageLines => pageLines;
    public int Pages => pages;
    public override bool HasMore => Read < pages;
    public override void More() => Read++;

    public override Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct) =>
        Task.FromResult(ViewContent.OfText(string.Join('\n', Enumerable.Range(0, Read * pageLines).Select(i => $"line {i:D4}")),
            $"{Read} of {pages} pages"));
}
