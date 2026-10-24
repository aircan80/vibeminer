using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>
/// App-wide settings saved by the engine. Writes are serialized and verified by reading the file back.
/// Storage measurement runs one walk at a time inside a time budget.
/// </summary>
public sealed class SettingsService(ISettingsStore store, IStorageMeasurer storage, DownloadLimitSettings limits,
    TimeProvider? clock = null, TimeSpan? measurementBudget = null) : ITableDefinitionFetchPolicy
{
    public const int FormatVersion = 1;
    public const string CommunityBuildHistory = "communityBuildHistory";
    public const string FetchTableDefinitions = "fetchTableDefinitions";
    /// <summary>The switches this version knows, in the order they are written.</summary>
    public static readonly IReadOnlyList<string> Switches = [CommunityBuildHistory, FetchTableDefinitions];
    public static readonly TimeSpan DefaultMeasurementBudget = TimeSpan.FromMinutes(5);
    private const bool CommunityBuildHistoryDefault = false;
    // Owner decision on #55: fetching newer definitions is on until the user turns it off.
    private const bool FetchTableDefinitionsDefault = true;
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly TimeSpan budget = measurementBudget ?? DefaultMeasurementBudget;
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly SemaphoreSlim measuring = new(1, 1);

    public async Task<SettingsSnapshot> ReadAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Project(await store.ReadAsync(token));
    }

    // Fetch only when the switch reads as on: saved on, or no value saved (a fresh install). An unreadable value or a
    // file from a newer VibeMiner counts as off, so a user who turned it off is never overridden (D081).
    public async Task<bool> FetchEnabledAsync(CancellationToken token) =>
        (await ReadAsync(token)).FetchTableDefinitions is { Source: not SettingSource.Unreadable, Value: true };

    public async Task<SettingsSnapshot> SetAsync(string name, bool value, CancellationToken token = default)
    {
        if (!Switches.Contains(name)) throw new DiscoveryException("INVALID_REQUEST", "Choose a known setting.");
        await mutations.WaitAsync(token);
        try
        {
            var current = await store.ReadAsync(token);
            if (current.File == SettingsFileState.Newer)
                throw new DiscoveryException("SETTINGS_VERSION", "These settings were saved by a newer VibeMiner. This version doesn’t change them.");
            // Only content that was read and found bad may be replaced; a file that couldn't be read is left alone.
            if (!current.Replaceable)
                throw new DiscoveryException("SETTINGS_NOT_READ", $"The settings file couldn’t be read, so it wasn’t changed. {current.Problem!.Message}", current.Problem!.Retryable);
            // A read file keeps every other value as it was: saved switches, switches with a value this version
            // doesn't understand, and unknown values. A damaged file keeps nothing, as before.
            // A damaged file is replaced with the values the user sees now, so switching one setting never turns another
            // back to a default the user isn't shown (fetching reads as Off while the file is damaged).
            var values = new List<KeyValuePair<string, string>>();
            var readable = current.File == SettingsFileState.Read;
            var shown = current.File == SettingsFileState.Unreadable ? Project(current) : null;
            foreach (var known in Switches)
            {
                var saved = known == CommunityBuildHistory ? current.CommunityBuildHistory : current.FetchTableDefinitions;
                if (known == name) values.Add(new(known, Json(value)));
                else if (readable && saved is { } kept) values.Add(new(known, Json(kept)));
                else if (readable && current.UnreadableValues.FirstOrDefault(pair => pair.Key == known) is { Key: not null } raw) values.Add(raw);
                else if (shown is not null) values.Add(new(known, Json((known == CommunityBuildHistory ? shown.CommunityBuildHistory : shown.FetchTableDefinitions).Value)));
            }
            if (readable) values.AddRange(current.UnknownValues);
            await store.WriteAsync(values, time.GetUtcNow(), readable ? current.UnknownFields : [], token);
            // The write is committed; request cancellation no longer withdraws it.
            var committed = Project(await store.ReadAsync(CancellationToken.None));
            var setting = name == CommunityBuildHistory ? committed.CommunityBuildHistory : committed.FetchTableDefinitions;
            if (setting.Source != SettingSource.Saved || setting.Value != value)
                throw new DiscoveryException("SETTINGS_WRITE_FAILED", "The engine couldn’t confirm the saved settings file. Read the settings again.", true);
            return committed;
        }
        finally { mutations.Release(); }
    }

    public async Task<StorageMeasurement> MeasureAsync(CancellationToken token = default)
    {
        await measuring.WaitAsync(token);
        try
        {
            using var limit = new CancellationTokenSource(budget, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, limit.Token);
            try { return await storage.MeasureAsync(linked.Token); }
            catch (OperationCanceledException) when (limit.IsCancellationRequested && !token.IsCancellationRequested)
            {
                throw new DiscoveryException("STORAGE_MEASURE_TIMEOUT", "Measuring the storage folder took too long. Nothing was changed.", true);
            }
        }
        finally { measuring.Release(); }
    }

    private static string Json(bool value) => value ? "true" : "false";

    private SettingsSnapshot Project(SettingsDocument document) =>
        new(FormatVersion, document.File, document.Problem, document.ChangedAt,
            Switch(document, document.CommunityBuildHistory, document.CommunityBuildHistoryProblem, CommunityBuildHistoryDefault, false),
            storage.Location, limits,
            Switch(document, document.FetchTableDefinitions, document.FetchTableDefinitionsProblem, FetchTableDefinitionsDefault, false));

    // An unreadable value is never used. It reads as the safe value, Off for both switches: nothing is fetched.
    private static BooleanSetting Switch(SettingsDocument document, bool? saved, SettingsProblem? problem, bool defaultValue, bool safeValue) => document.File switch
    {
        SettingsFileState.Unreadable or SettingsFileState.Newer => new(safeValue, defaultValue, SettingSource.Unreadable, document.Problem),
        _ when problem is not null => new(safeValue, defaultValue, SettingSource.Unreadable, problem),
        _ when saved is { } value => new(value, defaultValue, SettingSource.Saved, null),
        _ => new(defaultValue, defaultValue, SettingSource.Default, null),
    };
}
