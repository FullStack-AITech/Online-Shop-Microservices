namespace OrderService.Application.Abstractions;

/// <summary>
/// The Order Service's view of the User Service.
/// </summary>
/// <remarks>
/// Only asks the one question this service needs answered: does this user exist and may
/// they place an order? Deliberately not a full user client — a narrow port is far easier
/// to keep stable than a shared model.
/// </remarks>
public interface IUserDirectory
{
    Task<UserSummary?> GetUserAsync(string userId, CancellationToken cancellationToken);
}

public sealed record UserSummary(string Id, string Email, string FullName, bool IsActive);
