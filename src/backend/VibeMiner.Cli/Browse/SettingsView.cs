using System.Globalization;
using VibeMiner.Cli.Profile;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// <c>:settings</c>: where the data folder is and why, the per-OS default and the choice file, what the data folder holds (object
/// store, generated data, last scrub) and the Downloaded share of the builds this session measured. Read-only and holds no lock:
/// the object store is opened read-only for its stats and closed.
/// </summary>
internal sealed class SettingsView : BrowseView
{
    public override string Title => "settings";

    public const string NoneMeasured = "none measured yet: d on a build in :builds";

    public override async Task<ViewContent> LoadAsync(BrowseSession session, CancellationToken ct)
    {
        var env = session.Environment;
        var choice = DataFolders.Join(env.Platform, DataFolders.UserData(env), DataFolders.ChoiceFile);
        var store = await StorageSizes.ObjectStoreAsync(session.StoreFolder, session.Stores, ct).ConfigureAwait(false);
        var generated = await StorageSizes.GeneratedAsync(session.Folder.Path, ct).ConfigureAwait(false);
        var scrub = await ScrubRecords.ReadAsync(session.Folder.Path, ct).ConfigureAwait(false);

        static BrowseRow Row(string name, string value) => new([name, value]);
        var rows = new List<BrowseRow>
        {
            Row("Data folder", $"{session.Folder.Path} ({StoreView.SourceName(session.Folder.Source)})"),
            Row("Default folder", DataFolders.Default(env)),
            Row("Choice file", $"{choice} ({(File.Exists(choice) ? "present" : "none")})"),
            Row("Object store", StoreLine(store)),
            Row("Generated data", GeneratedLine(generated)),
            Row("Last scrub", ScrubSummary.Line(scrub)),
        };
        if (ScrubSummary.Problem(scrub) is { } problem) rows.Add(Row("Scrub problem", problem));
        var measured = session.Measured;
        if (measured.Count == 0) rows.Add(Row("Downloaded", NoneMeasured));
        foreach (var m in measured)
            rows.Add(Row($"Downloaded {m.Build.Product} {m.VersionsName} ({m.Build.Region})", MeasuredLine(m)));
        return new(["Field", "Value"], rows, "Read-only. Downloaded rows are the builds measured in this session (d, or the end of a).");
    }

    internal static string StoreLine(ObjectStoreSize s) => s.State switch
    {
        SizeState.Measured when s.Stats is { } st => string.Create(CultureInfo.InvariantCulture,
            $"{VmCommands.Size(st.PackBytes)} in packs, {VmCommands.Size(st.LiveBytes)} live, {st.Objects:N0} objects"),
        SizeState.Missing => $"missing ({s.Code})",
        _ => $"failed ({s.Code}: {s.Message})",
    };

    internal static string GeneratedLine(GeneratedSize g) => g.State switch
    {
        SizeState.Measured => string.Create(CultureInfo.InvariantCulture, $"{VmCommands.Size(g.Bytes)} in {g.Files:N0} files"),
        SizeState.Missing => "absent",
        _ => $"failed ({g.Code}: {g.Message})",
    };

    internal static string MeasuredLine(MeasuredBuild m) => string.Create(CultureInfo.InvariantCulture,
        $"{m.Figure.Share * 100:0.0}% of {VmCommands.Size(m.Figure.TotalBytes)}, {m.Figure.StoredObjects:N0} of {m.Figure.TotalObjects:N0} objects, measured {BrowseSession.Time(m.At)} UTC");
}
