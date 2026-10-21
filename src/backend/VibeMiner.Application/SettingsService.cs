using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>
/// App-wide settings saved by the engine. Writes are serialized and verified by reading the file back.
/// Storage measurement runs one walk at a time inside a time budget.
/// </summary>
public sealed class SettingsService(ISettingsStore store, IStorageMeasurer storage, DownloadLimitSettings limits,
    TimeProvider? clock = null, TimeSpan? measurementBudget = null)
{
    public const int FormatVersion = 1;
    public const string CommunityBuildHistory = "communityBuildHistory";
    public static readonly TimeSpan DefaultMeasurementBudget = TimeSpan.FromMinutes(5);
    private const bool CommunityBuildHistoryDefault = false;
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly TimeSpan budget = measurementBudget ?? DefaultMeasurementBudget;
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly SemaphoreSlim measuring = new(1, 1);

    public async Task<SettingsSnapshot> ReadAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Project(await store.ReadAsync(token));
    }

    public async Task<SettingsSnapshot> SetAsync(string name, bool value, CancellationToken token = default)
    {
        if (name != CommunityBuildHistory) throw new DiscoveryException("INVALID_REQUEST", "Choose a known setting.");
        await mutations.WaitAsync(token);
        try
        {
            var current = await store.ReadAsync(token);
            if (current.File == SettingsFileState.Newer)
                throw new DiscoveryException("SETTINGS_VERSION", "These settings were saved by a newer VibeMiner. This version doesn’t change them.");
            // Only content that was read and found bad may be replaced; a file that couldn't be read is left alone.
            if (!current.Replaceable)
                throw new DiscoveryException("SETTINGS_NOT_READ", $"The settings file couldn’t be read, so it wasn’t changed. {current.Problem!.Message}", current.Problem!.Retryable);
            await store.WriteAsync(value, time.GetUtcNow(), current.UnknownValues, current.UnknownFields, token);
            // The write is committed; request cancellation no longer withdraws it.
            var committed = Project(await store.ReadAsync(CancellationToken.None));
            if (committed.CommunityBuildHistory.Source != SettingSource.Saved || committed.CommunityBuildHistory.Value != value)
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

    private SettingsSnapshot Project(SettingsDocument document)
    {
        var community = document.File switch
        {
            SettingsFileState.Unreadable or SettingsFileState.Newer => new BooleanSetting(CommunityBuildHistoryDefault, CommunityBuildHistoryDefault,
                SettingSource.Unreadable, document.Problem),
            _ when document.CommunityBuildHistoryProblem is not null => new BooleanSetting(CommunityBuildHistoryDefault, CommunityBuildHistoryDefault,
                SettingSource.Unreadable, document.CommunityBuildHistoryProblem),
            _ when document.CommunityBuildHistory is { } saved => new BooleanSetting(saved, CommunityBuildHistoryDefault, SettingSource.Saved, null),
            _ => new BooleanSetting(CommunityBuildHistoryDefault, CommunityBuildHistoryDefault, SettingSource.Default, null),
        };
        return new(FormatVersion, document.File, document.Problem, document.ChangedAt, community, storage.Location, limits);
    }
}
