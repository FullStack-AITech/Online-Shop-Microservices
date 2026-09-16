using OrderService.Domain.Exceptions;

namespace OrderService.Domain.Orders;

/// <summary>
/// One product on an order, priced at the moment the order was placed.
/// </summary>
/// <remarks>
/// The price is copied onto the line rather than read from the Product Service later.
/// A catalogue price change must never alter what a customer already agreed to pay, and
/// there are no cross-service joins to fall back on anyway.
/// </remarks>
public sealed class OrderLine
{
    private OrderLine()
    {
        // Required by EF Core.
        ProductId = string.Empty;
        Sku = string.Empty;
        Currency = string.Empty;
        ProductName = string.Empty;
    }

    internal OrderLine(string productId, string sku, string productName, decimal unitPrice,
        string currency, int quantity)
    {
        if (quantity <= 0)
        {
            throw new InvalidQuantityException(quantity);
        }

        if (unitPrice < 0)
        {
            throw new NegativePriceException(unitPrice);
        }

        Id = Guid.NewGuid();
        ProductId = productId;
        Sku = sku;
        ProductName = productName;
        UnitPrice = unitPrice;
        Currency = currency.ToUpperInvariant();
        Quantity = quantity;
    }

    public Guid Id { get; private set; }

    public Guid OrderId { get; private set; }

    public string ProductId { get; private set; }

    public string Sku { get; private set; }

    /// <summary>Name at the time of ordering, so an old order still reads correctly.</summary>
    public string ProductName { get; private set; }

    public decimal UnitPrice { get; private set; }

    public string Currency { get; private set; }

    public int Quantity { get; private set; }

    public decimal LineTotal => UnitPrice * Quantity;
}
