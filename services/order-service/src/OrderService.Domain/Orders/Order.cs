using OrderService.Domain.Exceptions;

namespace OrderService.Domain.Orders;

/// <summary>
/// The order aggregate. Every rule that must always hold about an order lives here.
/// </summary>
/// <remarks>
/// <para>
/// The aggregate protects itself: there is no public setter and no way to reach the lines
/// collection directly, so a rule cannot be bypassed by a caller that forgot to check it.
/// </para>
/// <para>
/// The order holds a <c>UserId</c> and a <c>ProductId</c> per line, but never reads the
/// user or product tables — those belong to other services. Everything needed to display
/// or fulfil the order is copied onto it at creation time.
/// </para>
/// </remarks>
public sealed class Order
{
    private readonly List<OrderLine> _lines = [];

    private Order()
    {
        // Required by EF Core.
        UserId = string.Empty;
        Currency = string.Empty;
    }

    private Order(string userId, string currency)
    {
        Id = Guid.NewGuid();
        UserId = userId;
        Currency = currency.ToUpperInvariant();
        Status = OrderStatus.Pending;
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; }

    public string UserId { get; private set; }

    public OrderStatus Status { get; private set; }

    public string Currency { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Set when the order reaches a terminal state, for reporting and support.</summary>
    public string? StatusReason { get; private set; }

    /// <summary>
    /// Optimistic concurrency token. Two operations racing on the same order — a payment
    /// callback and a cancellation, say — must not silently overwrite each other.
    /// </summary>
    public uint Version { get; private set; }

    public IReadOnlyCollection<OrderLine> Lines => _lines.AsReadOnly();

    public decimal TotalAmount => _lines.Sum(line => line.LineTotal);

    public int TotalItems => _lines.Sum(line => line.Quantity);

    /// <summary>
    /// Creates a new order in <see cref="OrderStatus.Pending"/>.
    /// </summary>
    /// <exception cref="EmptyOrderException">No lines were supplied.</exception>
    /// <exception cref="MixedCurrencyException">Lines disagree on currency.</exception>
    /// <exception cref="DuplicateOrderLineException">A product appears twice.</exception>
    public static Order Create(string userId, IEnumerable<OrderLineDraft> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var drafts = lines.ToList();
        if (drafts.Count == 0)
        {
            throw new EmptyOrderException();
        }

        var duplicate = drafts.GroupBy(draft => draft.ProductId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new DuplicateOrderLineException(duplicate.Key);
        }

        // The first line fixes the currency; any disagreement is a bug upstream, not a
        // conversion this service should silently perform.
        var currency = drafts[0].Currency.ToUpperInvariant();
        var order = new Order(userId, currency);

        foreach (var draft in drafts)
        {
            if (!string.Equals(draft.Currency, currency, StringComparison.OrdinalIgnoreCase))
            {
                throw new MixedCurrencyException(currency, draft.Currency.ToUpperInvariant());
            }

            var line = new OrderLine(draft.ProductId, draft.Sku, draft.ProductName,
                draft.UnitPrice, currency, draft.Quantity);
            order._lines.Add(line);
        }

        return order;
    }

    /// <summary>Stock has been reserved for every line; the order now awaits payment.</summary>
    public void MarkAwaitingPayment() => TransitionTo(OrderStatus.AwaitingPayment);

    /// <summary>Payment succeeded.</summary>
    public void MarkPaid() => TransitionTo(OrderStatus.Paid);

    /// <summary>Dispatched to the customer.</summary>
    public void MarkShipped() => TransitionTo(OrderStatus.Shipped);

    /// <summary>Cancelled by the customer or an operator.</summary>
    public void Cancel(string reason) => TransitionTo(OrderStatus.Cancelled, reason);

    /// <summary>Could not be fulfilled — no stock, or payment was declined.</summary>
    public void Fail(string reason) => TransitionTo(OrderStatus.Failed, reason);

    /// <summary>
    /// True once the order can no longer change state. Useful for callers deciding
    /// whether an incoming event is still relevant.
    /// </summary>
    public bool IsTerminal => OrderStateMachine.IsTerminal(Status);

    private void TransitionTo(OrderStatus target, string? reason = null)
    {
        if (!OrderStateMachine.CanTransition(Status, target))
        {
            throw new InvalidOrderTransitionException(Status, target);
        }

        Status = target;
        StatusReason = reason;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

/// <summary>
/// The data needed to add one line, gathered from the Product Service before the order
/// is created. A plain record so the domain does not depend on any transport type.
/// </summary>
public sealed record OrderLineDraft(
    string ProductId,
    string Sku,
    string ProductName,
    decimal UnitPrice,
    string Currency,
    int Quantity);
