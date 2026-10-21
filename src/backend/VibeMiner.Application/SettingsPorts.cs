using VibeMiner.Domain;

namespace VibeMiner.Application;

public interface ISettingsStore
{
    // Never throws for a missing, malformed or newer file; those are document states.
    Task<SettingsDocument> ReadAsync(CancellationToken token);

    // Replaces the whole file. A pre-commit cancellation may abandon the write;
    // once the file is replaced the write is committed. Failures are SETTINGS_WRITE_FAILED.
    Task WriteAsync(bool communityBuildHistory, DateTimeOffset changedAt, IReadOnlyList<KeyValuePair<string, string>> unknownValues,
        IReadOnlyList<KeyValuePair<string, string>> unknownFields, CancellationToken token);
}

public interface IStorageMeasurer
{
    string Location { get; }

    // Bounded walk of the data folder; checks cancellation between bounded batches.
    Task<StorageMeasurement> MeasureAsync(CancellationToken token);
}
