using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using OrderService.Api.Contracts;
using OrderService.Application.Common;
using OrderService.Application.Orders;
using OrderService.Domain.Orders;

namespace OrderService.Api.Endpoints;

/// <summary>
/// Orders REST API (v1).
/// </summary>
/// <remarks>
/// Resource-oriented: nouns in paths, verbs as HTTP methods, status codes carrying the
/// outcome. The version sits in the path so the contract can evolve without breaking the
/// services that depend on it.
/// </remarks>
public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/api/v1/orders").WithTags("orders");

        orders.MapPost("/", CreateAsync)
            .WithSummary("Place an order")
            .Produces<OrderResponse>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status503ServiceUnavailable);

        orders.MapGet("/{orderId:guid}", GetAsync)
            .WithSummary("Get an order by id")
            .Produces<OrderResponse>()
            .Produces(StatusCodes.Status404NotFound);

        orders.MapGet("/", ListAsync)
            .WithSummary("List a user's orders, newest first")
            .Produces<OrderPageResponse>();

        orders.MapPost("/{orderId:guid}/pay", PayAsync)
            .WithSummary("Mark an order paid")
            .WithDescription("Driven by an explicit call for now; week 4 replaces this with "
                             + "a PaymentProcessed event consumer.")
            .Produces<OrderResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        orders.MapPost("/{orderId:guid}/ship", ShipAsync)
            .WithSummary("Mark an order shipped")
            .Produces<OrderResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        orders.MapPost("/{orderId:guid}/cancel", CancelAsync)
            .WithSummary("Cancel an order")
            .Produces<OrderResponse>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateOrderRequest request,
        CreateOrderHandler handler,
        CancellationToken cancellationToken)
    {
        var command = new CreateOrderCommand(
            request.UserId,
            request.Lines.Select(line => new CreateOrderLine(line.ProductId, line.Quantity))
                .ToList());

        var result = await handler.HandleAsync(command, cancellationToken);
        if (!result.IsSuccess)
        {
            return Problem(result.Error!);
        }

        var order = result.Value!;
        return Results.Created($"/api/v1/orders/{order.Id}", OrderResponse.From(order));
    }

    private static async Task<IResult> GetAsync(Guid orderId, GetOrderHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(orderId, cancellationToken);
        return result.IsSuccess
            ? Results.Ok(OrderResponse.From(result.Value!))
            : Problem(result.Error!);
    }

    private static async Task<Ok<OrderPageResponse>> ListAsync(
        [FromQuery] string userId,
        ListOrdersHandler handler,
        CancellationToken cancellationToken,
        [FromQuery] int limit = 20,
        [FromQuery] int offset = 0)
    {
        var page = await handler.HandleAsync(userId, limit, offset, cancellationToken);
        return TypedResults.Ok(new OrderPageResponse(
            page.Orders.Select(OrderResponse.From).ToList(),
            page.Total, page.Limit, page.Offset));
    }

    private static Task<IResult> PayAsync(Guid orderId, UpdateOrderStatusHandler handler,
        CancellationToken cancellationToken) =>
        MutateAsync(() => handler.PayAsync(orderId, cancellationToken));

    private static Task<IResult> ShipAsync(Guid orderId, UpdateOrderStatusHandler handler,
        CancellationToken cancellationToken) =>
        MutateAsync(() => handler.ShipAsync(orderId, cancellationToken));

    private static Task<IResult> CancelAsync(Guid orderId,
        [FromBody] CancelOrderRequest? request,
        UpdateOrderStatusHandler handler,
        CancellationToken cancellationToken) =>
        MutateAsync(() => handler.CancelAsync(orderId,
            request?.Reason ?? "Cancelled by request", cancellationToken));

    private static async Task<IResult> MutateAsync(Func<Task<Result<Order>>> operation)
    {
        var result = await operation();
        return result.IsSuccess
            ? Results.Ok(OrderResponse.From(result.Value!))
            : Problem(result.Error!);
    }

    /// <summary>
    /// Maps an application error onto a status code.
    /// </summary>
    /// <remarks>
    /// 409 for insufficient stock: the request was well formed and understood, but the
    /// state of the catalogue conflicts with it. 400 would blame the caller for something
    /// they could not have known.
    /// </remarks>
    private static IResult Problem(OrderError error)
    {
        var status = error.Code switch
        {
            "user_not_found" or "product_not_found" or "order_not_found"
                => StatusCodes.Status404NotFound,
            "insufficient_stock" or "user_inactive" or "product_inactive"
                => StatusCodes.Status409Conflict,
            "dependency_unavailable"
                => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status400BadRequest
        };

        return Results.Json(new
        {
            error = error.Code,
            message = error.Message,
            timestamp = DateTimeOffset.UtcNow
        }, statusCode: status);
    }
}
