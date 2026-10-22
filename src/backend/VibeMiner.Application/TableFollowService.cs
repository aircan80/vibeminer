using System.Globalization;
using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>
/// Follow one declared reference by value. The source names its exact artifact and flattened column; the engine
/// checks the declaration and the target table itself. The target must already be admitted (TargetArtifactId).
/// </summary>
public sealed record TableFollowRequest(string ProductId, string BuildIdentity, string SourceRegion, string TableId,
    string ResearchLocale, string ArtifactId, int ColumnIndex, string Value, string TargetTableId, string TargetArtifactId,
    int Limit = 50, string? After = null);

public static class TableFollowRules
{
    public const string PolicyRevision = "table-follow-v1";
    /// <summary>A non-ID target column is scanned only when the whole admitted target has at most this many rows (D072).</summary>
    public const int MaximumScanRows = TableReferenceTextRules.MaximumValueIndexRows;
    public const int MaximumColumns = 512;
}

public interface ITableFollowStore
{
    /// <summary>Local only: never acquires. An unprepared source or target is TABLE_NOT_PREPARED.</summary>
    Task<TableFollowPage> FollowAsync(TableFollowRequest request, CancellationToken token);
}

public sealed class TableFollowService(ITableFollowStore store)
{
    public Task<TableFollowPage> FollowAsync(TableFollowRequest request, CancellationToken token = default)
    {
        Validate(request); token.ThrowIfCancellationRequested();
        return store.FollowAsync(request, token);
    }

    public static void Validate(TableFollowRequest request)
    {
        if (request is null) throw Invalid();
        TableDataService.Validate(new TableRowsRequest(request.ProductId, request.BuildIdentity, request.SourceRegion, request.TableId,
            request.ResearchLocale, request.ArtifactId, request.Limit, request.After));
        TableDataService.Validate(new TableRowsRequest(request.ProductId, request.BuildIdentity, request.SourceRegion, request.TargetTableId,
            request.ResearchLocale, request.TargetArtifactId, request.Limit, request.After));
        if (request.ColumnIndex is < 0 or >= TableFollowRules.MaximumColumns || !TryParseValue(request.Value, out _)) throw Invalid();
    }

    /// <summary>Canonical decimal only: no sign on zero, no leading zeros, no plus, at most 20 digits.</summary>
    public static bool TryParseValue(string? value, out Int128 key)
    {
        key = default;
        if (value is null || value.Length is < 1 or > 21) return false;
        var digits = value[0] == '-' ? value.AsSpan(1) : value.AsSpan();
        if (digits.Length is < 1 or > 20 || digits[0] == '0' && (digits.Length > 1 || value[0] == '-')) return false;
        foreach (var ch in digits) if (ch is < '0' or > '9') return false;
        return Int128.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out key);
    }

    private static DiscoveryException Invalid() =>
        new("INVALID_REQUEST", "Choose an exact source column, canonical value, target table and bounded row page.");
}
