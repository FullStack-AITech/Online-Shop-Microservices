using System.Text.Json;
using OrderService.Domain.Exceptions;
using OrderService.Infrastructure.Catalog;

namespace OrderService.Api.Infrastructure;

/// <summary>
/// The one place in this service that turns an exception into an HTTP status code.
/// </summary>
/// <remarks>
/// Keeps status codes out of the domain and the handlers, and guarantees every error
/// leaves the service in the shape the gateway and the frontend expect.
/// </remarks>
public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (InvalidOrderTransitionException exception)
        {
            // The request is well formed; the order's current state forbids it.
            await WriteAsync(context, StatusCodes.Status409Conflict,
                exception.Code, exception.Message);
        }
        catch (DomainException exception)
        {
            await WriteAsync(context, StatusCodes.Status400BadRequest,
                exception.Code, exception.Message);
        }
        catch (DownstreamUnavailableException exception)
        {
            // A dependency is down, not the caller's fault. 503 tells them to retry.
            logger.LogError(exception, "Downstream dependency {Dependency} unavailable",
                exception.Dependency);
            await WriteAsync(context, StatusCodes.Status503ServiceUnavailable,
                "dependency_unavailable",
                $"{exception.Dependency} is not responding; please retry shortly");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled exception processing {Method} {Path}",
                context.Request.Method, context.Request.Path);
            await WriteAsync(context, StatusCodes.Status500InternalServerError,
                "internal_error", "An unexpected error occurred");
        }
    }

    private static async Task WriteAsync(HttpContext context, int statusCode,
        string code, string message)
    {
        if (context.Response.HasStarted)
        {
            // Too late to change the response; the exception is already logged.
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        var payload = new
        {
            error = code,
            message,
            path = context.Request.Path.Value,
            timestamp = DateTimeOffset.UtcNow
        };

        await context.Response.WriteAsync(JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}
