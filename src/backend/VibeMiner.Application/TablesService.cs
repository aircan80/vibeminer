using System.Globalization;
using VibeMiner.Domain;

namespace VibeMiner.Application;

public sealed record TableCatalogRequest(string ProductId, string BuildIdentity, string SourceRegion,
    string Query = "", int Limit = 50, string? After = null);
public sealed record TableDefinitionRequest(string ProductId, string BuildIdentity, string SourceRegion, string TableId);
public interface ITablesCatalog
{
    Task<TableCatalogPage> ReadCatalogAsync(TableCatalogRequest request, CancellationToken token);
    Task<TableDefinitionSnapshot> ReadDefinitionAsync(TableDefinitionRequest request, CancellationToken token);
}
public sealed class TablesService(ITablesCatalog catalog)
{
    public Task<TableCatalogPage> ReadCatalogAsync(TableCatalogRequest request, CancellationToken token = default)
    { Validate(request); return catalog.ReadCatalogAsync(request, token); }
    public Task<TableDefinitionSnapshot> ReadDefinitionAsync(TableDefinitionRequest request, CancellationToken token = default)
    { Validate(request); return catalog.ReadDefinitionAsync(request, token); }
    public static void Validate(TableCatalogRequest request)
    {
        WorkspaceService.Validate(new(request.ProductId, request.BuildIdentity, request.SourceRegion));
        if (request.Query is null || request.Query.Length > 80 || request.Query.Any(char.IsControl) ||
            request.Limit is < 1 or > 100 || request.After is not null &&
            (request.After.Length is < 1 or > 10 || request.After.Any(c => c is < '0' or > '9') ||
             request.After.Length > 1 && request.After[0] == '0' ||
             !int.TryParse(request.After, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw new DiscoveryException("INVALID_REQUEST", "Choose a bounded table-definition catalogue page.");
    }
    public static void Validate(TableDefinitionRequest request)
    {
        WorkspaceService.Validate(new(request.ProductId, request.BuildIdentity, request.SourceRegion));
        if (request.TableId is null || request.TableId.Length != 8 ||
            request.TableId.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new DiscoveryException("INVALID_REQUEST", "Choose one exact provider table identity.");
    }
}
