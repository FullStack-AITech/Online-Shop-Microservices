using Microsoft.Extensions.Logging;
using OrderService.Application.Abstractions;
using OrderService.Application.Common;
using OrderService.Domain.Orders;

namespace OrderService.Application.Orders;

public sealed record CreateOrderLine(string ProductId, int Quantity);

public sealed record CreateOrderCommand(string UserId, IReadOnlyList<CreateOrderLine> Lines);

/// <summary>
/// Places an order: validate the user, price the lines from the catalogue, reserve stock,
/// then persist.
/// </summary>
/// <remarks>
/// <para>
/// This is the first place in the platform where one service's work spans another's
/// database, and there is no transaction that covers both. Stock is reserved one product
/// at a time, and if any reservation fails the ones already taken are released again.
/// </para>
/// <para>
/// That compensation is best-effort and deliberately so: the release call can itself fail,
/// which would leak stock until the reservation expires. Making this reliable is what the
/// Saga and outbox work in week 7 is for. Until then the failure is logged loudly rather
/// than hidden.
/// </para>
/// </remarks>
public sealed class CreateOrderHandler(
    IOrderRepository repository,
    IProductCatalog catalog,
    IUserDirectory users,
    ILogger<CreateOrderHandler> logger)
{
    public async Task<Result<Order>> HandleAsync(CreateOrderCommand command,
        CancellationToken cancellationToken)
    {
        var user = await users.GetUserAsync(command.UserId, cancellationToken);
        if (user is null)
        {
            return OrderError.UserNotFound(command.UserId);
        }

        if (!user.IsActive)
        {
            return OrderError.UserInactive(command.UserId);
        }

        // Price every line before reserving anything, so a bad request fails without
        // touching the catalogue's stock at all.
        var drafts = new List<OrderLineDraft>(command.Lines.Count);
        foreach (var line in command.Lines)
        {
            var product = await catalog.GetProductAsync(line.ProductId, cancellationToken);
            if (product is null)
            {
                return OrderError.ProductNotFound(line.ProductId);
            }

            if (!product.Active)
            {
                return OrderError.ProductInactive(line.ProductId);
            }

            drafts.Add(new OrderLineDraft(product.Id, product.Sku, product.Name,
                product.Price, product.Currency, line.Quantity));
        }

        // Domain rules — empty order, duplicate product, mixed currency — are enforced here
        // and surface as exceptions the API layer maps to 400.
        var order = Order.Create(command.UserId, drafts);

        var reserved = new List<(string ProductId, int Quantity)>();
        foreach (var draft in drafts)
        {
            var result = await catalog.ReserveStockAsync(draft.ProductId, draft.Quantity,
                cancellationToken);
            if (result.Succeeded)
            {
                reserved.Add((draft.ProductId, draft.Quantity));
                continue;
            }

            await ReleaseAsync(reserved, order.Id, cancellationToken);
            return OrderError.InsufficientStock(draft.ProductId,
                result.FailureReason ?? "not enough stock");
        }

        order.MarkAwaitingPayment();

        try
        {
            await repository.AddAsync(order, cancellationToken);
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            // Stock is reserved but the order was never stored. Give the units back rather
            // than stranding them.
            logger.LogError(exception,
                "Failed to persist order {OrderId}; releasing reserved stock", order.Id);
            await ReleaseAsync(reserved, order.Id, cancellationToken);
            throw;
        }

        logger.LogInformation(
            "Created order {OrderId} for user {UserId} with {LineCount} lines totalling {Total} {Currency}",
            order.Id, order.UserId, order.Lines.Count, order.TotalAmount, order.Currency);

        return order;
    }

    private async Task ReleaseAsync(IReadOnlyCollection<(string ProductId, int Quantity)> reserved,
        Guid orderId, CancellationToken cancellationToken)
    {
        foreach (var (productId, quantity) in reserved)
        {
            try
            {
                var released = await catalog.ReleaseStockAsync(productId, quantity,
                    cancellationToken);
                if (!released)
                {
                    logger.LogError(
                        "Compensation failed for order {OrderId}: {Quantity} units of product "
                        + "{ProductId} remain reserved and will need reconciling",
                        orderId, quantity, productId);
                }
            }
            catch (Exception exception)
            {
                // Never let compensation throw over the original failure — the caller needs
                // to hear why the order failed, not why the cleanup did.
                logger.LogError(exception,
                    "Compensation threw for order {OrderId}, product {ProductId}, {Quantity} units",
                    orderId, productId, quantity);
            }
        }
    }
}
