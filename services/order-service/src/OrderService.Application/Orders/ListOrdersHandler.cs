using OrderService.Application.Abstractions;
using OrderService.Domain.Orders;

namespace OrderService.Application.Orders;

public sealed record OrderPage(IReadOnlyList<Order> Orders, int Total, int Limit, int Offset);

public sealed class ListOrdersHandler(IOrderRepository repository)
{
    public const int MaxPageSize = 100;

    public async Task<OrderPage> HandleAsync(string userId, int limit, int offset,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, MaxPageSize);
        offset = Math.Max(offset, 0);

        var (orders, total) = await repository.ListByUserAsync(userId, limit, offset,
            cancellationToken);
        return new OrderPage(orders, total, limit, offset);
    }
}
