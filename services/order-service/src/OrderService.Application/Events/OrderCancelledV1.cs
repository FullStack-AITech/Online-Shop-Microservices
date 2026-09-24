using OrderService.Domain.Orders;

namespace OrderService.Application.Events;

/// <summary>
/// Payload of <c>OrderCancelled</c> v1: <c>docs/events/schemas/order-cancelled.v1.schema.json</c>.
/// </summary>
/// <remarks>
/// No email: the order never stores one. The lines are there so the week 7 saga can give
/// the stock back, and <see cref="PreviousStatus"/> so it knows whether a refund is due.
/// </remarks>
public sealed record OrderCancelledV1(
    Guid OrderId,
    string UserId,
    string Reason,
    string PreviousStatus,
    IReadOnlyList<OrderCancelledLineV1> Lines,
    DateTimeOffset CancelledAt)
{
    public const string EventType = "OrderCancelled";
    public const int EventVersion = 1;

    public static IntegrationEvent From(Order order, OrderStatus previousStatus,
        string correlationId)
    {
        var payload = new OrderCancelledV1(
            order.Id,
            order.UserId,
            // The schema requires a reason; an empty one from the API must not make the
            // event invalid.
            string.IsNullOrWhiteSpace(order.StatusReason) ? "unspecified" : order.StatusReason,
            previousStatus.ToString(),
            order.Lines.Select(line => new OrderCancelledLineV1(line.ProductId, line.Quantity))
                .ToList(),
            order.UpdatedAt);

        return IntegrationEvent.Create(EventType, EventVersion, order.UpdatedAt, correlationId,
            payload);
    }
}

public sealed record OrderCancelledLineV1(string ProductId, int Quantity);
