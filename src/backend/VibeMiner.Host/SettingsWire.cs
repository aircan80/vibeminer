using System.Globalization;
using VibeMiner.Domain;

namespace VibeMiner.Host;

/// <summary>Explicit camel-case state names and exact decimal-string sizes; no file contents cross the wire.</summary>
internal static class SettingsWire
{
    internal static object Snapshot(SettingsSnapshot value) => new
    {
        formatVersion = value.FormatVersion,
        file = value.File switch
        {
            SettingsFileState.Absent => "absent", SettingsFileState.Read => "read",
            SettingsFileState.Unreadable => "unreadable", _ => "newer",
        },
        problem = Problem(value.Problem),
        value.LastChangedAt,
        communityBuildHistory = new
        {
            value.CommunityBuildHistory.Value, value.CommunityBuildHistory.DefaultValue,
            source = value.CommunityBuildHistory.Source switch
            {
                SettingSource.Saved => "saved", SettingSource.Default => "default", _ => "unreadable",
            },
            problem = Problem(value.CommunityBuildHistory.Problem),
        },
        storage = new { value.DataDirectory },
        downloadLimits = new
        {
            value.DownloadLimits.ParallelDownloads, value.DownloadLimits.BackgroundDownloads,
            bandwidthCapBytesPerSecond = value.DownloadLimits.BandwidthCapBytesPerSecond is { } cap ? N(cap) : null,
            value.DownloadLimits.Adjustable,
        },
    };

    internal static object Measurement(StorageMeasurement value) => new
    {
        bytes = N(value.Bytes), files = N(value.Files), skipped = N(value.Skipped), value.StartedAt, value.MeasuredAt,
    };

    private static object? Problem(SettingsProblem? problem) => problem is null ? null : new { problem.Code, problem.Message, problem.Retryable };
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
