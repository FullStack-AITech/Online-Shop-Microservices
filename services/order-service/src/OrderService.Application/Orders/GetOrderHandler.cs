using OrderService.Application.Abstractions;
using OrderService.Application.Common;
using OrderService.Domain.Orders;

namespace OrderService.Application.Orders;

public sealed class GetOrderHandler(IOrderRepository repository)
{
    public async Task<Result<Order>> HandleAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var order = await repository.GetAsync(orderId, cancellationToken);
        return order is null ? OrderError.OrderNotFound(orderId) : order;
    }
}
