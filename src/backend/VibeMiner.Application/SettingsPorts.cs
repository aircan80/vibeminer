using VibeMiner.Domain;

namespace VibeMiner.Application;

public interface ISettingsStore
{
    // Never throws for a missing, malformed or newer file; those are document states.
    Task<SettingsDocument> ReadAsync(CancellationToken token);

    // Replaces the whole file with these values (raw JSON, in order) and top-level fields. A pre-commit cancellation
    // may abandon the write; once the file is replaced the write is committed. Failures are SETTINGS_WRITE_FAILED.
    Task WriteAsync(IReadOnlyList<KeyValuePair<string, string>> values, DateTimeOffset changedAt,
        IReadOnlyList<KeyValuePair<string, string>> unknownFields, CancellationToken token);
}

/// <summary>Whether a table may fetch a newer definition from WoWDBDefs when no bundled one covers its layout (D081).</summary>
public interface ITableDefinitionFetchPolicy
{
    // Never throws for an unreadable settings file: the setting's default applies.
    Task<bool> FetchEnabledAsync(CancellationToken token);
}

public interface IStorageMeasurer
{
    string Location { get; }

    // Bounded walk of the data folder; checks cancellation between bounded batches.
    Task<StorageMeasurement> MeasureAsync(CancellationToken token);
}
