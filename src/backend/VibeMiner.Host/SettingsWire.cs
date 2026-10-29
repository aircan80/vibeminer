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
        communityBuildHistory = Switch(value.CommunityBuildHistory),
        fetchTableDefinitions = Switch(value.FetchTableDefinitions),
        communityListfile = Switch(value.CommunityListfile),
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

    // The community listfile download (D105). Byte counts are exact decimal strings; times carry UTC offsets.
    internal static object Listfile(CommunityListfileStatus value) => new
    {
        value.Enabled,
        value.Started,
        activity = value.Activity switch
        {
            CommunityListfileActivity.Verifying => "verifying", CommunityListfileActivity.Checking => "checking",
            CommunityListfileActivity.Downloading => "downloading", CommunityListfileActivity.Importing => "importing", _ => "idle",
        },
        release = value.Release is not { } r ? null : new
        {
            r.Tag, r.PublishedAt, r.FetchedAt, r.Sha256, byteLength = N(r.ByteLength), nameCount = N(r.NameCount), r.SourceUrl,
        },
        value.PendingTag,
        completed = value.Completed is { } done ? N(done) : null,
        total = value.Total is { } all ? N(all) : null,
        value.CheckedAt,
        problem = Problem(value.Problem),
    };

    private static object Switch(BooleanSetting setting) => new
    {
        setting.Value, setting.DefaultValue,
        source = setting.Source switch { SettingSource.Saved => "saved", SettingSource.Default => "default", _ => "unreadable" },
        problem = Problem(setting.Problem),
    };

    private static object? Problem(SettingsProblem? problem) => problem is null ? null : new { problem.Code, problem.Message, problem.Retryable };
    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
}
