using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using OrderService.Application.Abstractions;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace OrderService.Infrastructure.Catalog;

/// <summary>
/// HTTP adapter for the Product Service.
/// </summary>
/// <remarks>
/// Two named clients are injected, not one. Reads go through a pipeline that retries;
/// stock reservation goes through one that does not, because reserving twice is not the
/// same as reserving once. Which pipeline a call uses is a property of the call, not of
/// the service being called.
/// </remarks>
public sealed class ProductCatalogClient(
    IHttpClientFactory httpClientFactory,
    ILogger<ProductCatalogClient> logger) : IProductCatalog
{
    public const string IdempotentClient = "product-catalog-read";
    public const string NonIdempotentClient = "product-catalog-write";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<CatalogProduct?> GetProductAsync(string productId,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(IdempotentClient);

        try
        {
            using var response = await client.GetAsync($"/api/v1/products/{productId}",
                cancellationToken);

            // A missing product is a valid answer, not a failure.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();

            var payload = await response.Content.ReadFromJsonAsync<ProductResponse>(
                JsonOptions, cancellationToken);
            return payload?.ToCatalogProduct();
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            logger.LogWarning(exception,
                "Product Service unavailable while reading product {ProductId}", productId);
            throw new DownstreamUnavailableException("Product Service", exception);
        }
    }

    public async Task<StockReservationResult> ReserveStockAsync(string productId, int quantity,
        CancellationToken cancellationToken)
    {
        // Note the client: no retry. See ResiliencePipelines.AddNonIdempotentPipeline.
        var client = httpClientFactory.CreateClient(NonIdempotentClient);

        try
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/products/{productId}/stock/reserve",
                new StockAdjustmentRequest(quantity), JsonOptions, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return StockReservationResult.Success();
            }

            // 409 means the Product Service considered the request and refused it: there
            // is not enough stock. That is a business answer, not a transport failure.
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                return StockReservationResult.Failure(error);
            }

            response.EnsureSuccessStatusCode();
            return StockReservationResult.Failure("unexpected response");
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            // Critical: after a timeout we do not know whether the reservation was applied.
            // Failing the order is the safe answer; silently retrying is not.
            logger.LogError(exception,
                "Stock reservation for product {ProductId} failed in an unknown state; "
                + "{Quantity} units may or may not be reserved", productId, quantity);
            throw new DownstreamUnavailableException("Product Service", exception);
        }
    }

    public async Task<bool> ReleaseStockAsync(string productId, int quantity,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(NonIdempotentClient);

        try
        {
            using var response = await client.PostAsJsonAsync(
                $"/api/v1/products/{productId}/stock/release",
                new StockAdjustmentRequest(quantity), JsonOptions, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (IsDependencyFailure(exception))
        {
            // Compensation is best effort here; the caller logs and carries on.
            logger.LogError(exception,
                "Failed to release {Quantity} units of product {ProductId}", quantity, productId);
            return false;
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(
                JsonOptions, cancellationToken);
            return error?.Message ?? error?.Error ?? response.StatusCode.ToString();
        }
        catch (Exception)
        {
            return response.StatusCode.ToString();
        }
    }

    private static bool IsDependencyFailure(Exception exception) =>
        exception is HttpRequestException or TimeoutRejectedException
            or BrokenCircuitException or TaskCanceledException;

    private sealed record ProductResponse(
        string Id, string Sku, string Name, decimal Price, string Currency,
        int StockQuantity, bool Active)
    {
        public CatalogProduct ToCatalogProduct() =>
            new(Id, Sku, Name, Price, Currency, StockQuantity, Active);
    }

    private sealed record StockAdjustmentRequest(int Quantity);

    private sealed record ErrorResponse(string? Error, string? Message);
}

/// <summary>
/// Raised when a downstream service could not be reached at all — timed out, refused the
/// connection, or its circuit is open.
/// </summary>
/// <remarks>
/// Distinct from a business failure on purpose. "No stock" is a 409 the customer should
/// see; "the catalogue is down" is a 503 they should retry.
/// </remarks>
public sealed class DownstreamUnavailableException(string dependency, Exception inner)
    : Exception($"{dependency} is unavailable", inner)
{
    public string Dependency { get; } = dependency;
}
