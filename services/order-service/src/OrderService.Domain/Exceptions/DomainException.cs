using OrderService.Domain.Orders;

namespace OrderService.Domain.Exceptions;

/// <summary>
/// Base class for expected, business-rule failures.
/// </summary>
/// <remarks>
/// The domain raises these; only the API layer knows about HTTP status codes. That keeps
/// the domain reusable from a future event consumer or gRPC endpoint.
/// </remarks>
public abstract class DomainException(string message) : Exception(message)
{
    /// <summary>Stable machine-readable code, returned to clients as <c>error</c>.</summary>
    public abstract string Code { get; }
}

public sealed class InvalidOrderTransitionException(OrderStatus from, OrderStatus to)
    : DomainException($"An order cannot move from {from} to {to}")
{
    public override string Code => "invalid_order_transition";

    public OrderStatus From { get; } = from;

    public OrderStatus To { get; } = to;
}

public sealed class EmptyOrderException()
    : DomainException("An order must contain at least one line")
{
    public override string Code => "empty_order";
}

public sealed class MixedCurrencyException(string expected, string actual)
    : DomainException($"All lines must use the order currency {expected}, but a line used {actual}")
{
    public override string Code => "mixed_currency";
}

public sealed class InvalidQuantityException(int quantity)
    : DomainException($"Quantity must be greater than zero, but was {quantity}")
{
    public override string Code => "invalid_quantity";
}

public sealed class NegativePriceException(decimal price)
    : DomainException($"Unit price cannot be negative, but was {price}")
{
    public override string Code => "negative_price";
}

public sealed class DuplicateOrderLineException(string productId)
    : DomainException($"Product '{productId}' appears more than once; combine the quantities instead")
{
    public override string Code => "duplicate_order_line";
}
