using OrderService.Application.Abstractions;
using OrderService.Domain.Orders;

namespace OrderService.Application.Events;

/// <summary>
/// Payload of <c>OrderCreated</c> v1: <c>docs/events/schemas/order-created.v1.schema.json</c>.
/// </summary>
/// <remarks>
/// Carries everything a consumer needs — the email, the priced lines, the total — so the
/// Payment and Notification services never have to call back to read the order. A
/// consumer that calls the producer back has reintroduced the coupling the broker removed.
/// </remarks>
public sealed record OrderCreatedV1(
    Guid OrderId,
    string UserId,
    string UserEmail,
    string Currency,
    string TotalAmount,
    int TotalItems,
    IReadOnlyList<OrderCreatedLineV1> Lines,
    DateTimeOffset CreatedAt)
{
    public const string EventType = "OrderCreated";
    public const int EventVersion = 1;

    public static IntegrationEvent From(Order order, UserSummary user, string correlationId)
    {
        var payload = new OrderCreatedV1(
            order.Id,
            order.UserId,
            user.Email,
            order.Currency,
            Money.Format(order.TotalAmount),
            order.TotalItems,
            order.Lines.Select(line => new OrderCreatedLineV1(
                line.ProductId, line.Sku, line.ProductName,
                Money.Format(line.UnitPrice), line.Quantity, Money.Format(line.LineTotal)))
                .ToList(),
            order.CreatedAt);

        // The fact became true when the order was stored, not when it is published.
        return IntegrationEvent.Create(EventType, EventVersion, order.UpdatedAt, correlationId,
            payload);
    }
}

public sealed record OrderCreatedLineV1(
    string ProductId,
    string Sku,
    string ProductName,
    string UnitPrice,
    int Quantity,
    string LineTotal);
