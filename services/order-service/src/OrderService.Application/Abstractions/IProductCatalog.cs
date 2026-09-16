namespace OrderService.Application.Abstractions;

/// <summary>
/// The Order Service's view of the Product Service.
/// </summary>
/// <remarks>
/// An interface owned by this service, not a shared client library. The port describes
/// what this service needs; the adapter in Infrastructure deals with HTTP, timeouts and
/// retries. Tests substitute it without a network.
/// </remarks>
public interface IProductCatalog
{
    /// <summary>Looks a product up. Returns <c>null</c> when it does not exist.</summary>
    Task<CatalogProduct?> GetProductAsync(string productId, CancellationToken cancellationToken);

    /// <summary>
    /// Reserves stock for one product.
    /// </summary>
    /// <remarks>
    /// This is <b>not</b> idempotent — calling it twice reserves twice — so the adapter
    /// must never retry it. See <c>ProductCatalogClient</c>.
    /// </remarks>
    Task<StockReservationResult> ReserveStockAsync(string productId, int quantity,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns previously reserved stock. The compensating action for a failed order.
    /// </summary>
    Task<bool> ReleaseStockAsync(string productId, int quantity,
        CancellationToken cancellationToken);
}

public sealed record CatalogProduct(
    string Id,
    string Sku,
    string Name,
    decimal Price,
    string Currency,
    int StockQuantity,
    bool Active);

/// <summary>Outcome of a reservation attempt.</summary>
public sealed record StockReservationResult(bool Succeeded, string? FailureReason = null)
{
    public static StockReservationResult Success() => new(true);

    public static StockReservationResult Failure(string reason) => new(false, reason);
}
