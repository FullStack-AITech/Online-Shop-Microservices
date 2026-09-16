namespace OrderService.Domain.Orders;

/// <summary>
/// The single source of truth for which order transitions are legal.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="Order"/> so the rules can be read, tested and reasoned
/// about as a table. Anything not listed here is illegal by default — the safe direction
/// for a state machine to fail in.
/// </remarks>
public static class OrderStateMachine
{
    private static readonly IReadOnlyDictionary<OrderStatus, OrderStatus[]> Allowed =
        new Dictionary<OrderStatus, OrderStatus[]>
        {
            // Stock reserved, or it could not be.
            [OrderStatus.Pending] = [OrderStatus.AwaitingPayment, OrderStatus.Cancelled, OrderStatus.Failed],

            // Payment succeeded, failed, or the customer pulled out first.
            [OrderStatus.AwaitingPayment] = [OrderStatus.Paid, OrderStatus.Cancelled, OrderStatus.Failed],

            // Paid orders can still be cancelled — that is the refund path, and it is the
            // reason cancellation needs a compensating action rather than a status change.
            [OrderStatus.Paid] = [OrderStatus.Shipped, OrderStatus.Cancelled],

            [OrderStatus.Shipped] = [],
            [OrderStatus.Cancelled] = [],
            [OrderStatus.Failed] = []
        };

    public static bool CanTransition(OrderStatus from, OrderStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlyCollection<OrderStatus> NextStates(OrderStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : [];

    public static bool IsTerminal(OrderStatus status) => NextStates(status).Count == 0;
}
