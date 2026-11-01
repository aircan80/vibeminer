using System.Text;
using VibeMiner.Builds;
using VibeMiner.ObjectAccess;
using VibeMiner.Objects;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// <c>:history</c>: one row per product in the history log (the summary's empty product as "(summary)"), with counts per kind.
/// Reads every entry a page at a time; memory is one page plus a row per product.
/// </summary>
internal sealed class HistoryView : BrowseView
{
    public const int Page = 1000;
    public const string SummaryProduct = "(summary)";

    public override string Title => "history";

    public override async Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct)
    {
        await using var store = await session.OpenReadAsync(ct).ConfigureAwait(false);
        var products = new SortedDictionary<string, (long Versions, long Cdns, long Other, DateTimeOffset Last)>(StringComparer.Ordinal);
        long total = 0;
        HistoryEntry? after = null;
        while (true)
        {
            var page = await store.Objects.ReadHistoryAsync(new HistoryQuery(After: after, Limit: Page), ct).ConfigureAwait(false);
            foreach (var e in page)
            {
                products.TryGetValue(e.Product, out var p);
                products[e.Product] = (p.Versions + (e.Kind == HistoryKind.Versions ? 1 : 0), p.Cdns + (e.Kind == HistoryKind.Cdns ? 1 : 0),
                    p.Other + (e.Kind is HistoryKind.Versions or HistoryKind.Cdns ? 0 : 1), e.ObservedAt > p.Last ? e.ObservedAt : p.Last);
            }
            total += page.Count;
            if (page.Count < Page) break;
            after = page[^1];
        }
        var rows = products.Select(p => new BrowseRow(
            [p.Key.Length == 0 ? SummaryProduct : p.Key, Name(p.Key), $"{p.Value.Versions:N0}", $"{p.Value.Cdns:N0}", $"{p.Value.Other:N0}", BrowseSession.Time(p.Value.Last)],
            () => new HistoryEntriesView(p.Key))).ToList();
        return new(["Product", "Name", "Versions", "CDNs", "Other", "Last (UTC)"], rows,
            total == 0 ? "No history recorded." : $"{total:N0} entries. Enter lists a product's replies.");
    }

    public static string Name(string product) =>
        product.Length == 0 ? "version-service summary" : Products.IsKnown(product) ? Products.Label(product) : "not reviewed";
}

/// <summary>One product's replies, newest first. Keeps the newest <see cref="Kept"/>; the note says when there were more.</summary>
internal sealed class HistoryEntriesView(string product) : BrowseView
{
    public const int Kept = 2000;

    public override string Title => product.Length == 0 ? HistoryView.SummaryProduct : product;

    public override async Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct)
    {
        await using var store = await session.OpenReadAsync(ct).ConfigureAwait(false);
        var newest = new Queue<HistoryEntry>();
        long total = 0;
        HistoryEntry? after = null;
        while (true)
        {
            var page = await store.Objects.ReadHistoryAsync(new HistoryQuery(Product: product, After: after, Limit: HistoryView.Page), ct).ConfigureAwait(false);
            foreach (var e in page)
            {
                newest.Enqueue(e);
                if (newest.Count > Kept) newest.Dequeue();
            }
            total += page.Count;
            if (page.Count < HistoryView.Page) break;
            after = page[^1];
        }
        var rows = newest.Reverse().Select(e => new BrowseRow(
            [BrowseSession.Time(e.ObservedAt), e.Kind.ToString().ToLowerInvariant(), e.Region, e.Document.KeyHex, e.Url],
            () => new ReplyView(e))).ToList();
        var note = total == 0 ? "No history for this product."
            : total > Kept ? $"Newest {Kept:N0} of {total:N0} replies. Enter shows the raw reply."
            : $"{total:N0} replies, newest first. Enter shows the raw reply.";
        return new(["Observed (UTC)", "Kind", "Region", "Document", "URL"], rows, note);
    }
}

/// <summary>A reply's exact bytes as text, read from the store (read-only: never the network). Shows the first <see cref="MaxBytes"/>.</summary>
internal sealed class ReplyView(HistoryEntry entry) : BrowseView
{
    public const int MaxBytes = 1 << 20;

    public override string Title => $"{entry.Kind.ToString().ToLowerInvariant()} {BrowseSession.Time(entry.ObservedAt)}";

    public override async Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct)
    {
        await using var store = await session.OpenReadAsync(ct).ConfigureAwait(false);
        var got = await store.Objects.GetKeyAsync(new ObjectRequest(entry.Document), cdn: null, FetchPriority.Interactive, ct).ConfigureAwait(false);
        var source = $"{entry.Url} ({entry.Region}), document {entry.Document.KeyHex}";
        if (!got.IsPresent || got.Bytes is not { } bytes)
            return ViewContent.OfText("", $"The reply isn't readable: {got.State}{(got.Code is null ? "" : ", " + got.Code)}{(got.Message is null ? "" : ": " + got.Message)}. {source}");
        var shown = bytes.AsSpan(0, Math.Min(bytes.Length, MaxBytes));
        return ViewContent.OfText(Printable(shown),
            bytes.Length > MaxBytes ? $"First {VmCommands.Size(MaxBytes)} of {VmCommands.Size(bytes.Length)}. {source}" : $"{VmCommands.Size(bytes.Length)}. {source}");
    }

    /// <summary>UTF-8 text with CR dropped and other control characters but tab and newline shown as '.', so a bad reply can't drive the terminal.</summary>
    public static string Printable(ReadOnlySpan<byte> bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            if (c != '\r') builder.Append(c is '\n' or '\t' || !char.IsControl(c) ? c : '.');
        return builder.ToString();
    }
}
