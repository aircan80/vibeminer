using VibeMiner.Cli.Profile;

namespace VibeMiner.Cli.Browse;

/// <summary><c>:store</c>: what <c>vm store info</c> shows, from layer 1's StatsAsync, opened read-only, and the last scrub record.</summary>
internal sealed class StoreView : BrowseView
{
    public override string Title => "store";

    public static string SourceName(DataFolderSource source) => source switch
    {
        DataFolderSource.Flag => "--data",
        DataFolderSource.Override => "VIBEMINER_DATA_DIR",
        DataFolderSource.Chosen => "chosen in Settings",
        _ => "default",
    };

    public override async Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct)
    {
        await using var store = await session.OpenReadAsync(ct).ConfigureAwait(false);
        var stats = await store.StatsAsync(ct).ConfigureAwait(false);
        var scrub = await ScrubRecords.ReadAsync(session.Folder.Path, ct).ConfigureAwait(false);
        var source = SourceName(session.Folder.Source);
        static BrowseRow Row(string name, string value) => new([name, value]);
        return new(["Field", "Value"],
        [
            Row("Data folder", $"{session.Folder.Path} ({source})"),
            Row("Store", $"{session.StoreFolder} ({(store.Writer ? "writer" : "read-only")})"),
            Row("Packs", $"{stats.Packs:N0}"),
            Row("Objects", $"{stats.Objects:N0}"),
            Row("History records", $"{stats.HistoryRecords:N0}"),
            Row("Pack bytes", VmCommands.Size(stats.PackBytes)),
            Row("Live bytes", VmCommands.Size(stats.LiveBytes)),
            Row("Created", $"{BrowseSession.Time(stats.CreatedAt)} UTC"),
            Row("Last scrub", ScrubSummary.Line(scrub)),
            .. ScrubSummary.Problem(scrub) is { } problem ? [Row("Scrub problem", problem)] : Array.Empty<BrowseRow>(),
        ]);
    }
}
