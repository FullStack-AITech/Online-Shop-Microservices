using Microsoft.EntityFrameworkCore;
using OrderService.Application.Abstractions;
using OrderService.Domain.Orders;

namespace OrderService.Infrastructure.Persistence.Repositories;

public sealed class OrderRepository(OrderDbContext context) : IOrderRepository
{
    public async Task AddAsync(Order order, CancellationToken cancellationToken) =>
        await context.Orders.AddAsync(order, cancellationToken);

    public async Task<Order?> GetAsync(Guid orderId, CancellationToken cancellationToken) =>
        await context.Orders.FirstOrDefaultAsync(order => order.Id == orderId, cancellationToken);

    public async Task<(IReadOnlyList<Order> Orders, int Total)> ListByUserAsync(
        string userId, int limit, int offset, CancellationToken cancellationToken)
    {
        var query = context.Orders.Where(order => order.UserId == userId);

        var total = await query.CountAsync(cancellationToken);
        var orders = await query
            .OrderByDescending(order => order.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return (orders, total);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken) =>
        await context.SaveChangesAsync(cancellationToken);
}
