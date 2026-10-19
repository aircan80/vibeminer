using System.Globalization;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Host;

/// <summary>Exact numeric wire strings and explicit public state names; no SQL/driver details.</summary>
internal static class RetentionWire
{
    internal static object Snapshot(RetentionSnapshot value) => new
    {
        value.Target, value.JobId, revision = N(value.Revision), mode = Mode(value.Mode), value.Paused,
        state = State(value.State), value.Scope, objectCount = N(value.ObjectCount),
        totalEncodedBytes = N(value.TotalEncodedBytes),
        coverage = new
        {
            receiptObjects = N(value.Coverage.ReceiptObjects), receiptBytes = N(value.Coverage.ReceiptBytes),
            value.Coverage.ReceiptedAt, value.Coverage.PresenceState, value.Coverage.PresenceAssessedAt,
            observedObjects = N(value.Coverage.ObservedObjects), presentObjects = N(value.Coverage.PresentObjects),
            presentBytes = N(value.Coverage.PresentBytes), missingObjects = N(value.Coverage.MissingObjects),
            corruptObjects = N(value.Coverage.CorruptObjects)
        },
        progress = value.Progress is { } progress ? new
        {
            progress.Stage, progress.Unit, completed = progress.Completed is { } completed ? N(completed) : null,
            total = progress.Total is { } total ? N(total) : null
        } : null,
        value.PendingObjects, wireBytesThisSession = N(value.WireBytesThisSession),
        unresolvedObjects = N(value.UnresolvedObjects), failedObjects = N(value.FailedObjects),
        value.QueuePosition, value.Problem
    };

    internal static RetentionAction Action(string value) => value switch
    {
        "downloadAll" => RetentionAction.DownloadAll, "pause" => RetentionAction.Pause,
        "resume" => RetentionAction.Resume, "onDemand" => RetentionAction.OnDemand,
        _ => throw new DiscoveryException("INVALID_REQUEST", "Choose downloadAll, pause, resume or onDemand.")
    };

    internal static long Revision(string value)
    {
        if (value.Length is < 1 or > 19 || value.Length > 1 && value[0] == '0' ||
            value.Any(ch => ch is < '0' or > '9') ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
            throw new DiscoveryException("INVALID_REQUEST", "The expected intent revision must be a canonical nonnegative decimal string.");
        return revision;
    }

    private static string Mode(RetentionMode value) => value switch
    {
        RetentionMode.OnDemand => "onDemand", RetentionMode.DownloadAll => "downloadAll", _ => throw Invalid()
    };
    private static string State(RetentionState value) => value switch
    {
        RetentionState.OnDemand => "onDemand", RetentionState.Queued => "queued",
        RetentionState.Preparing => "preparing", RetentionState.Running => "running",
        RetentionState.Waiting => "waiting", RetentionState.Stopping => "stopping",
        RetentionState.Paused => "paused", RetentionState.Completed => "completed",
        RetentionState.Failed => "failed", _ => throw Invalid()
    };
    private static string N(long value) => value < 0 ? throw Invalid() : value.ToString(CultureInfo.InvariantCulture);
    private static DiscoveryException Invalid() => new("RETENTION_STATE_INVALID", "The retained job snapshot is inconsistent.");
}
