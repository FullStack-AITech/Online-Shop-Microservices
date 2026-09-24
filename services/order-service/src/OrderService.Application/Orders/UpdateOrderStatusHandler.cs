using Microsoft.Extensions.Logging;
using OrderService.Application.Abstractions;
using OrderService.Application.Common;
using OrderService.Application.Events;
using OrderService.Domain.Orders;

namespace OrderService.Application.Orders;

/// <summary>
/// Advances an order through its lifecycle on an explicit API call.
/// </summary>
/// <remarks>
/// Payment outcomes normally arrive as events instead — see
/// <see cref="HandlePaymentOutcomeHandler"/>. Both drive the same methods on the aggregate,
/// which is exactly why the transition rules live there and not in an endpoint or a
/// consumer.
/// </remarks>
public sealed class UpdateOrderStatusHandler(
    IOrderRepository repository,
    IEventPublisher publisher,
    ILogger<UpdateOrderStatusHandler> logger)
{
    public async Task<Result<Order>> PayAsync(Guid orderId, CancellationToken cancellationToken)
        => await MutateAsync(orderId, order => order.MarkPaid(), cancellationToken);

    public async Task<Result<Order>> ShipAsync(Guid orderId, CancellationToken cancellationToken)
        => await MutateAsync(orderId, order => order.MarkShipped(), cancellationToken);

    public async Task<Result<Order>> CancelAsync(Guid orderId, string reason,
        string correlationId, CancellationToken cancellationToken)
    {
        var previousStatus = default(OrderStatus);
        var result = await MutateAsync(orderId, order =>
        {
            previousStatus = order.Status;
            order.Cancel(reason);
        }, cancellationToken);

        if (!result.IsSuccess)
        {
            return result;
        }

        var cancelled = result.Value!;

        // The same dual-write gap as OrderCreated, and the same interim answer until the
        // outbox (#34): publish after the commit, and log loudly if that fails. The order
        // is cancelled either way.
        try
        {
            await publisher.PublishAsync(
                OrderCancelledV1.From(cancelled, previousStatus, correlationId),
                cancelled.Id.ToString(), cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "OrderCancelled for order {OrderId} was NOT published",
                cancelled.Id);
        }

        return result;
    }

    public async Task<Result<Order>> FailAsync(Guid orderId, string reason,
        CancellationToken cancellationToken)
        => await MutateAsync(orderId, order => order.Fail(reason), cancellationToken);

    private async Task<Result<Order>> MutateAsync(Guid orderId, Action<Order> mutate,
        CancellationToken cancellationToken)
    {
        var order = await repository.GetAsync(orderId, cancellationToken);
        if (order is null)
        {
            return OrderError.OrderNotFound(orderId);
        }

        // An illegal transition throws from the aggregate; the API maps it to 409.
        mutate(order);
        await repository.SaveChangesAsync(cancellationToken);
        return order;
    }
}
