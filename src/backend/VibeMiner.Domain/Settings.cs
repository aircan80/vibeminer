namespace VibeMiner.Domain;

/// <summary>What the engine found in its settings file. Absent and unreadable are different states.</summary>
public enum SettingsFileState { Absent, Read, Unreadable, Newer }

/// <summary>Where a setting's current value comes from.</summary>
public enum SettingSource { Default, Saved, Unreadable }

public sealed record SettingsProblem(string Code, string Message, bool Retryable);

/// <summary>A switch setting. An unreadable saved value is never used: Value is the safe value (Off), and the setting says so.</summary>
public sealed record BooleanSetting(bool Value, bool DefaultValue, SettingSource Source, SettingsProblem? Problem);

/// <summary>The engine's current download limits. They are fixed in this version.</summary>
public sealed record DownloadLimitSettings(int ParallelDownloads, int BackgroundDownloads, long? BandwidthCapBytesPerSecond, bool Adjustable);

/// <summary>One read of the settings, for every product and build.</summary>
public sealed record SettingsSnapshot(int FormatVersion, SettingsFileState File, SettingsProblem? Problem,
    DateTimeOffset? LastChangedAt, BooleanSetting CommunityBuildHistory, string DataDirectory, DownloadLimitSettings DownloadLimits,
    BooleanSetting FetchTableDefinitions, BooleanSetting CommunityListfile);

/// <summary>
/// The parsed settings file. A null value means the file has no value for that setting.
/// Unknown values are kept as raw JSON so a write never drops them.
/// </summary>
public sealed record SettingsDocument(SettingsFileState File, SettingsProblem? Problem, DateTimeOffset? ChangedAt,
    bool? CommunityBuildHistory, SettingsProblem? CommunityBuildHistoryProblem, IReadOnlyList<KeyValuePair<string, string>> UnknownValues)
{
    public static SettingsDocument Absent { get; } = new(SettingsFileState.Absent, null, null, null, null, []);
    /// <summary>
    /// False when a file exists but its bytes were never parsed: too large, changed while read, or not openable
    /// (a lock or a permission error). Only bytes that were read and found bad may be replaced by a switch.
    /// </summary>
    public bool Replaceable { get; init; } = true;
    /// <summary>Top-level fields other than version, changedAt and values, kept as raw JSON so a write never drops them.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> UnknownFields { get; init; } = [];
    /// <summary>Fetch newer table definitions from WoWDBDefs (#55). Null when the file has no value for it.</summary>
    public bool? FetchTableDefinitions { get; init; }
    public SettingsProblem? FetchTableDefinitionsProblem { get; init; }
    /// <summary>Community file names from wow-listfile (#21, D105). Null when the file has no value for it.</summary>
    public bool? CommunityListfile { get; init; }
    public SettingsProblem? CommunityListfileProblem { get; init; }
    /// <summary>Known settings whose saved value VibeMiner doesn't understand, as raw JSON. Changing another setting
    /// keeps them as written; only switching that setting replaces its value.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> UnreadableValues { get; init; } = [];
}

/// <summary>
/// One walk of the data folder. Bytes are summed file lengths; links are not followed.
/// Skipped counts folders and files that could not be read, so they are not in the total.
/// </summary>
public sealed record StorageMeasurement(long Bytes, long Files, long Skipped, DateTimeOffset StartedAt, DateTimeOffset MeasuredAt);
