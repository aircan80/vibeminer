using System.Globalization;
using VibeMiner.Domain;
namespace VibeMiner.Application;

public sealed record InventoryReadRequest(string ProductId, string BuildIdentity, string SourceRegion,
    string Generation, int Limit = 50, string? After = null);
public interface IInventoryCatalog
{
    Task<InventoryPage> ReadInventoryAsync(InventoryReadRequest request, CancellationToken token);
}
public sealed class InventoryService(IInventoryCatalog catalog)
{
    public Task<InventoryPage> ReadAsync(InventoryReadRequest request, CancellationToken token = default)
    {
        Validate(request); return catalog.ReadInventoryAsync(request, token);
    }
    public static void Validate(InventoryReadRequest request)
    {
        WorkspaceService.Validate(new(request.ProductId, request.BuildIdentity, request.SourceRegion));
        if (request.Limit is < 1 or > 100 || !Decimal(request.Generation, false) || request.After is not null && !Decimal(request.After, true))
            throw new DiscoveryException("INVALID_REQUEST", "Choose a valid inventory generation and bounded page.");
    }
    private static bool Decimal(string value, bool zero) => value.Length is > 0 and <= 19 &&
        value.All(c => c is >= '0' and <= '9') && (value.Length == 1 || value[0] != '0') &&
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed >= (zero ? 0 : 1);
}
