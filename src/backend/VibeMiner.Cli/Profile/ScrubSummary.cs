using System.Globalization;

namespace VibeMiner.Cli.Profile;

/// <summary>How <c>vm store info</c>, <c>:store</c> and <c>:settings</c> show the last scrub record: one line, the problem, and JSON.</summary>
internal static class ScrubSummary
{
    /// <summary>When and how it ended, or why there's nothing to show.</summary>
    public static string Line(ScrubRecordRead read) => read switch
    {
        { State: ScrubRecordState.Absent } => "none yet (vm store scrub runs one)",
        { State: ScrubRecordState.Present, Record: { } r } => string.Create(CultureInfo.InvariantCulture,
            $"{Time(r.FinishedAt)} UTC: {(r.FailureCode is not null ? "failed" : r.Outcome == ScrubOutcome.Clean ? "clean" : "problem")}, {r.Checked:N0} objects checked"),
        _ => $"{ScrubRecords.FileName} can't be used ({StateName(read.State)}): {read.Reason ?? "no reason given"}",
    };

    /// <summary>What went wrong, in one line; null for a clean record or when there is no record.</summary>
    public static string? Problem(ScrubRecordRead read)
    {
        if (read is not { State: ScrubRecordState.Present, Record: { } r }) return null;
        if (r.FailureCode is not null) return $"{r.FailureCode}: {r.FailureMessage}";
        if (r.Outcome == ScrubOutcome.Clean) return null;
        var parts = new List<string>();
        if (r.IndexProblem is not null) parts.Add($"index: {r.IndexProblem}{(r.IndexRebuilt ? " (rebuilt from the packs)" : "")}");
        else if (r.IndexRebuilt) parts.Add("index rebuilt from the packs");
        if (r.CorruptCount > 0 || r.LostCount > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{r.CorruptCount:N0} corrupt, {r.LostCount:N0} lost"));
        if (r.Problems.Count > 0) parts.Add($"first: {r.Problems[0]}");
        return parts.Count == 0 ? "problem" : string.Join("; ", parts);
    }

    /// <summary>The <c>lastScrub</c> object: always a <c>state</c>, then the record's fields or the reason it can't be used.</summary>
    public static object Json(ScrubRecordRead read) => read is { State: ScrubRecordState.Present, Record: { } r }
        ? new
        {
            state = StateName(read.State), finishedAt = r.FinishedAt, outcome = r.Outcome == ScrubOutcome.Clean ? "clean" : "problem",
            r.IndexChecked, r.IndexProblem, r.IndexRebuilt, @checked = r.Checked, r.Bytes, r.CorruptCount, r.LostCount, r.Problems,
            r.FailureCode, r.FailureMessage,
        }
        : new { state = StateName(read.State), reason = read.Reason };

    public static string StateName(ScrubRecordState state) => state switch
    {
        ScrubRecordState.BadJson => "badJson",
        ScrubRecordState.TooLarge => "tooLarge",
        _ => state.ToString().ToLowerInvariant(),
    };

    private static string Time(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
