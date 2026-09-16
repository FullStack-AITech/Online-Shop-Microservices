namespace OrderService.Application.Common;

/// <summary>
/// Failures that are expected outcomes of a use case rather than exceptions.
/// </summary>
/// <remarks>
/// Returned rather than thrown: "the user does not exist" is a normal answer to a request,
/// and modelling it as a value keeps the happy path free of try/catch.
/// </remarks>
public sealed record OrderError(string Code, string Message)
{
    public static OrderError UserNotFound(string userId) =>
        new("user_not_found", $"User '{userId}' was not found");

    public static OrderError UserInactive(string userId) =>
        new("user_inactive", $"User '{userId}' is not active and cannot place orders");

    public static OrderError ProductNotFound(string productId) =>
        new("product_not_found", $"Product '{productId}' was not found");

    public static OrderError ProductInactive(string productId) =>
        new("product_inactive", $"Product '{productId}' is no longer available");

    public static OrderError InsufficientStock(string productId, string reason) =>
        new("insufficient_stock", $"Stock could not be reserved for product '{productId}': {reason}");

    public static OrderError OrderNotFound(Guid orderId) =>
        new("order_not_found", $"Order '{orderId}' was not found");

    public static OrderError DependencyUnavailable(string dependency) =>
        new("dependency_unavailable", $"The {dependency} is not responding; please retry shortly");
}
