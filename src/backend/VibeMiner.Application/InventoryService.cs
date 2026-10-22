using System.Globalization;
using VibeMiner.Domain;
namespace VibeMiner.Application;

public sealed record InventoryReadRequest(string ProductId, string BuildIdentity, string SourceRegion,
    string Generation, int Limit = 50, string? After = null);
/// <summary>
/// Files filters. Each one tests a single membership; a file is listed when at least one of its memberships passes
/// all of them. FileDataId is an exact ROOT FileDataID, ContentKey a lowercase CKey or a prefix of at least four hex
/// digits, LocaleFlags a raw mask that must share a bit with the membership's locale flags, ContentFlags either
/// "none" (derived content flags are zero) or a raw mask whose bits must all be set. Family is "root" or "install".
/// </summary>
public sealed record InventoryFileFilter(string? FileDataId = null, string? ContentKey = null, string? LocaleFlags = null,
    string? ContentFlags = null, string? Family = null, bool StoredOnly = false);
public sealed record InventoryFilesRequest(string ProductId, string BuildIdentity, string SourceRegion,
    string Generation, InventoryFileFilter Filter, int Limit = 50, string? After = null);
public sealed record InventoryFileRequest(string ProductId, string BuildIdentity, string SourceRegion,
    string Generation, string File, InventoryFileFilter Filter, int Limit = 100, string? After = null);
/// <summary>The file catalog status of one opened build (D071).</summary>
public sealed record FileCatalogRequest(string ProductId, string BuildIdentity, string SourceRegion);
/// <summary>Stop a running or queued preparation, or resume (retry) a stopped or failed one.</summary>
public sealed record FileCatalogSetRequest(string ProductId, string BuildIdentity, string Action);
public interface IInventoryCatalog
{
    Task<FileCatalogStatus> ReadFileCatalogAsync(FileCatalogRequest request, CancellationToken token);
    Task<FileCatalogList> ListFileCatalogsAsync(CancellationToken token);
    Task<FileCatalogStatus> SetFileCatalogAsync(FileCatalogSetRequest request, CancellationToken token);
    Task<InventoryPage> ReadInventoryAsync(InventoryReadRequest request, CancellationToken token);
    Task<InventoryFilesPage> ReadInventoryFilesAsync(InventoryFilesRequest request, CancellationToken token);
    Task<InventoryFileDetail> ReadInventoryFileAsync(InventoryFileRequest request, CancellationToken token);
}
/// <summary>A Files position: after a ROOT FileDataID, or after an install file (its first membership ID).</summary>
public readonly record struct InventoryFilePosition(bool Install, long Value)
{
    public override string ToString() => (Install ? "install:" : "root:") + Value.ToString(CultureInfo.InvariantCulture);
}
/// <summary>Parsed, validated Files filters with the one membership predicate both the list and the inspector use.</summary>
public sealed record InventoryFileQuery(long? FileDataId, string? ContentKey, uint? LocaleMask, bool NoContentFlags,
    uint? ContentMask, string? Family, bool StoredOnly)
{
    public bool IncludesRoot => Family is null or "root";
    public bool IncludesInstall => Family is null or "install";
    /// <summary>Install memberships carry no ROOT locale or content flags, so those filters can never match them.</summary>
    public bool InstallCanMatch => IncludesInstall && FileDataId is null && LocaleMask is null && !NoContentFlags && ContentMask is null;
    /// <summary>True when the filters narrow the files beyond the family choice, so no exact total is known.</summary>
    public bool Narrows => FileDataId is not null || ContentKey is not null || LocaleMask is not null || NoContentFlags || ContentMask is not null || StoredOnly;
    public bool VariantFilter => LocaleMask is not null || NoContentFlags || ContentMask is not null;
    public bool MatchesVariant(uint locale, uint flags) =>
        (LocaleMask is not { } mask || (locale & mask) != 0) && (!NoContentFlags || flags == 0) && (ContentMask is not { } bits || (flags & bits) == bits);
    public bool Matches(string family, long? fileDataId, string contentKey, uint? locale, uint? flags, bool stored)
    {
        if (Family is not null && family != Family) return false;
        if (StoredOnly && !stored) return false;
        if (ContentKey is not null && !contentKey.StartsWith(ContentKey, StringComparison.Ordinal)) return false;
        if (family == "install") return InstallCanMatch;
        if (FileDataId is not null && fileDataId != FileDataId) return false;
        return MatchesVariant(locale ?? 0, flags ?? 0);
    }
}
public sealed class InventoryService(IInventoryCatalog catalog)
{
    public const int MaxFilesPage = 100;
    public const int MaxMembershipsPage = 100;
    public Task<InventoryPage> ReadAsync(InventoryReadRequest request, CancellationToken token = default)
    {
        Validate(request); return catalog.ReadInventoryAsync(request, token);
    }
    public Task<InventoryFilesPage> ReadFilesAsync(InventoryFilesRequest request, CancellationToken token = default)
    {
        Validate(request); return catalog.ReadInventoryFilesAsync(request, token);
    }
    public Task<InventoryFileDetail> ReadFileAsync(InventoryFileRequest request, CancellationToken token = default)
    {
        Validate(request); return catalog.ReadInventoryFileAsync(request, token);
    }
    public Task<FileCatalogStatus> ReadCatalogAsync(FileCatalogRequest request, CancellationToken token = default)
    {
        Validate(request); return catalog.ReadFileCatalogAsync(request, token);
    }
    public Task<FileCatalogList> ListCatalogsAsync(CancellationToken token = default) => catalog.ListFileCatalogsAsync(token);
    public Task<FileCatalogStatus> SetCatalogAsync(FileCatalogSetRequest request, CancellationToken token = default)
    {
        Validate(request); return catalog.SetFileCatalogAsync(request, token);
    }
    public static void Validate(FileCatalogRequest request) =>
        WorkspaceService.Validate(new(request.ProductId, request.BuildIdentity, request.SourceRegion));
    public static void Validate(FileCatalogSetRequest request)
    {
        WorkspaceService.Validate(new(request.ProductId, request.BuildIdentity, "us"));
        if (request.Action is not ("stop" or "resume")) throw new DiscoveryException("INVALID_REQUEST", "Choose stop or resume.");
    }
    public static void Validate(InventoryReadRequest request)
    {
        WorkspaceService.Validate(new(request.ProductId, request.BuildIdentity, request.SourceRegion));
        if (request.Limit is < 1 or > 100 || !Decimal(request.Generation, false) || request.After is not null && !Decimal(request.After, true))
            throw new DiscoveryException("INVALID_REQUEST", "Choose a valid inventory generation and bounded page.");
    }
    public static (InventoryFileQuery Query, InventoryFilePosition? After) Validate(InventoryFilesRequest request)
    {
        WorkspaceService.Validate(new(request.ProductId, request.BuildIdentity, request.SourceRegion));
        if (request.Limit is < 1 or > MaxFilesPage || !Decimal(request.Generation, false))
            throw new DiscoveryException("INVALID_REQUEST", "Choose a valid inventory generation and bounded page.");
        InventoryFilePosition? after = null;
        if (request.After is not null)
            after = Position(request.After, true) ?? throw new DiscoveryException("INVALID_REQUEST", "The Files page cursor is not valid.");
        return (Parse(request.Filter), after);
    }
    public static (InventoryFileQuery Query, InventoryFilePosition File, long After) Validate(InventoryFileRequest request)
    {
        WorkspaceService.Validate(new(request.ProductId, request.BuildIdentity, request.SourceRegion));
        if (request.Limit is < 1 or > MaxMembershipsPage || !Decimal(request.Generation, false) || request.After is not null && !Decimal(request.After, false))
            throw new DiscoveryException("INVALID_REQUEST", "Choose a valid inventory generation and bounded membership page.");
        var file = Position(request.File, false) ?? throw new DiscoveryException("INVALID_REQUEST", "Choose a valid Files entry.");
        return (Parse(request.Filter), file, request.After is null ? 0 : long.Parse(request.After, CultureInfo.InvariantCulture));
    }
    public static InventoryFileQuery Parse(InventoryFileFilter filter)
    {
        static DiscoveryException Invalid(string message) => new("INVALID_REQUEST", message);
        long? fileDataId = null; uint? locale = null, mask = null; var none = false;
        if (filter.FileDataId is { } id)
            fileDataId = Unsigned32(id, true) ?? throw Invalid("A FileDataID filter is a decimal number from 0 to 4294967295.");
        if (filter.ContentKey is { } key && (key.Length is < 4 or > 32 || !key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw Invalid("A content key filter is 4 to 32 lowercase hex digits.");
        if (fileDataId is not null && filter.ContentKey is not null) throw Invalid("Search by a FileDataID or a content key, not both.");
        if (filter.LocaleFlags is { } localeText)
            locale = (uint?)Unsigned32(localeText, false) ?? throw Invalid("A locale flag filter is a nonzero raw mask.");
        if (filter.ContentFlags is { } flags)
        {
            if (flags == "none") none = true;
            else mask = (uint?)Unsigned32(flags, false) ?? throw Invalid("A content flag filter is \"none\" or a nonzero raw mask.");
        }
        if (filter.Family is not (null or "root" or "install")) throw Invalid("The source family filter is ROOT or Install.");
        return new(fileDataId, filter.ContentKey, locale, none, mask, filter.Family, filter.StoredOnly);
    }
    /// <summary>root:&lt;FileDataID&gt; (0..4294967295) or install:&lt;first membership ID&gt;; a cursor may also be install:0.</summary>
    public static InventoryFilePosition? Position(string text, bool cursor)
    {
        var colon = text.IndexOf(':');
        if (colon < 0) return null;
        var kind = text[..colon]; var value = text[(colon + 1)..];
        if (kind == "root") return Unsigned32(value, true) is { } fdid ? new InventoryFilePosition(false, fdid) : null;
        if (kind == "install" && Decimal(value, cursor)) return new InventoryFilePosition(true, long.Parse(value, CultureInfo.InvariantCulture));
        return null;
    }
    private static long? Unsigned32(string value, bool zero) =>
        Decimal(value, zero) && long.Parse(value, CultureInfo.InvariantCulture) is var parsed && parsed <= uint.MaxValue ? parsed : null;
    private static bool Decimal(string value, bool zero) => value.Length is > 0 and <= 19 &&
        value.All(c => c is >= '0' and <= '9') && (value.Length == 1 || value[0] != '0') &&
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= (zero ? 0 : 1);
}
