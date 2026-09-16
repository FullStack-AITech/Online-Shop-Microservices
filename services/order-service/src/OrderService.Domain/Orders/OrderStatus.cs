namespace OrderService.Domain.Orders;

/// <summary>
/// The states an order can be in.
/// </summary>
/// <remarks>
/// Modelled as an explicit enum with an explicit transition table rather than a free-text
/// status column, so an illegal transition is a compile-or-runtime failure here instead of
/// a bug discovered later in a report.
/// </remarks>
public enum OrderStatus
{
    /// <summary>Created, but stock has not been reserved yet.</summary>
    Pending = 0,

    /// <summary>Stock is reserved; waiting for the Payment Service.</summary>
    AwaitingPayment = 1,

    /// <summary>Payment succeeded.</summary>
    Paid = 2,

    /// <summary>Dispatched to the customer. Terminal.</summary>
    Shipped = 3,

    /// <summary>Cancelled by the customer or by an operator. Terminal.</summary>
    Cancelled = 4,

    /// <summary>Could not be fulfilled — no stock, or payment failed. Terminal.</summary>
    Failed = 5
}
