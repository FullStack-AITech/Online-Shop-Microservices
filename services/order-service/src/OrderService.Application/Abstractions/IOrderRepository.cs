using OrderService.Domain.Orders;

namespace OrderService.Application.Abstractions;

/// <summary>Persistence port for the order aggregate.</summary>
public interface IOrderRepository
{
    Task AddAsync(Order order, CancellationToken cancellationToken);

    Task<Order?> GetAsync(Guid orderId, CancellationToken cancellationToken);

    Task<(IReadOnlyList<Order> Orders, int Total)> ListByUserAsync(
        string userId, int limit, int offset, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
