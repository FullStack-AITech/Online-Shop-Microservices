using OrderService.Application.Abstractions;
using OrderService.Application.Common;
using OrderService.Domain.Orders;

namespace OrderService.Application.Orders;

/// <summary>
/// Advances an order through its lifecycle.
/// </summary>
/// <remarks>
/// For now these are driven by explicit API calls. In week 4 the same transitions will be
/// driven by PaymentProcessed and PaymentFailed events instead — which is exactly why the
/// transition rules live on the aggregate and not in an endpoint.
/// </remarks>
public sealed class UpdateOrderStatusHandler(IOrderRepository repository)
{
    public async Task<Result<Order>> PayAsync(Guid orderId, CancellationToken cancellationToken)
        => await MutateAsync(orderId, order => order.MarkPaid(), cancellationToken);

    public async Task<Result<Order>> ShipAsync(Guid orderId, CancellationToken cancellationToken)
        => await MutateAsync(orderId, order => order.MarkShipped(), cancellationToken);

    public async Task<Result<Order>> CancelAsync(Guid orderId, string reason,
        CancellationToken cancellationToken)
        => await MutateAsync(orderId, order => order.Cancel(reason), cancellationToken);

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
