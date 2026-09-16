using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OrderService.Application.Abstractions;
using OrderService.Infrastructure.Catalog;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace OrderService.Infrastructure.Users;

/// <summary>
/// HTTP adapter for the User Service. Read-only, so every call is idempotent and retryable.
/// </summary>
public sealed class UserDirectoryClient(
    IHttpClientFactory httpClientFactory,
    ILogger<UserDirectoryClient> logger) : IUserDirectory
{
    public const string ClientName = "user-directory";

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public async Task<UserSummary?> GetUserAsync(string userId,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ClientName);

        try
        {
            using var response = await client.GetAsync($"/api/v1/users/{userId}",
                cancellationToken);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<UserResponse>(
                JsonOptions, cancellationToken);
            return payload is null
                ? null
                : new UserSummary(payload.Id, payload.Email, payload.FullName, payload.IsActive);
        }
        catch (Exception exception) when (exception is HttpRequestException
                                              or TimeoutRejectedException
                                              or BrokenCircuitException
                                              or TaskCanceledException)
        {
            logger.LogWarning(exception,
                "User Service unavailable while reading user {UserId}", userId);
            throw new DownstreamUnavailableException("User Service", exception);
        }
    }

    // The User Service serialises snake_case; map it explicitly rather than guessing.
    private sealed record UserResponse(
        string Id,
        string Email,
        [property: System.Text.Json.Serialization.JsonPropertyName("full_name")]
        string FullName,
        [property: System.Text.Json.Serialization.JsonPropertyName("is_active")]
        bool IsActive);
}
