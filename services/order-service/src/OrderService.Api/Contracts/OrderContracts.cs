using System.ComponentModel.DataAnnotations;
using OrderService.Domain.Orders;

namespace OrderService.Api.Contracts;

/// <summary>
/// Wire contracts for the Orders API.
/// </summary>
/// <remarks>
/// Separate from the domain types on purpose: the JSON shape is a public contract other
/// services depend on, while the aggregate is free to change.
/// </remarks>
public sealed record CreateOrderLineRequest
{
    [Required]
    public string ProductId { get; init; } = string.Empty;

    [Range(1, 1000)]
    public int Quantity { get; init; }
}

public sealed record CreateOrderRequest
{
    [Required]
    public string UserId { get; init; } = string.Empty;

    [Required]
    [MinLength(1, ErrorMessage = "An order must contain at least one line")]
    public IReadOnlyList<CreateOrderLineRequest> Lines { get; init; } = [];
}

public sealed record CancelOrderRequest
{
    [MaxLength(500)]
    public string Reason { get; init; } = "Cancelled by request";
}

public sealed record OrderLineResponse(
    string ProductId, string Sku, string ProductName,
    decimal UnitPrice, string Currency, int Quantity, decimal LineTotal);

public sealed record OrderResponse(
    Guid Id,
    string UserId,
    string Status,
    string Currency,
    decimal TotalAmount,
    int TotalItems,
    string? StatusReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<OrderLineResponse> Lines)
{
    public static OrderResponse From(Order order) => new(
        order.Id,
        order.UserId,
        order.Status.ToString(),
        order.Currency,
        order.TotalAmount,
        order.TotalItems,
        order.StatusReason,
        order.CreatedAt,
        order.UpdatedAt,
        order.Lines.Select(line => new OrderLineResponse(
            line.ProductId, line.Sku, line.ProductName,
            line.UnitPrice, line.Currency, line.Quantity, line.LineTotal)).ToList());
}

public sealed record OrderPageResponse(
    IReadOnlyList<OrderResponse> Items, int Total, int Limit, int Offset);
