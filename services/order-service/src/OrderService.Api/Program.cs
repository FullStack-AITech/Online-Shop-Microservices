using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OrderService.Api.Endpoints;
using OrderService.Api.Infrastructure;
using OrderService.Infrastructure;
using OrderService.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

// Configuration comes from the environment, so one image runs locally, under Compose and
// on Kubernetes unchanged. Double underscore maps onto the config section separator:
// ORDER_SERVICE_Downstream__ProductService__BaseUrl -> Downstream:ProductService:BaseUrl
builder.Configuration.AddEnvironmentVariables("ORDER_SERVICE_");

builder.Services.AddOrderServiceInfrastructure(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options => options.SwaggerDoc("v1", new()
{
    Title = "Order Service",
    Version = "0.1.0",
    Description = "Owns customer orders for the Online Shop platform. Holds user and "
                  + "product ids but never reads those services' databases."
}));

builder.Services.AddHealthChecks()
    // Readiness fails when the database is unreachable, so traffic is routed away.
    .AddDbContextCheck<OrderDbContext>("order-database", tags: ["ready"]);

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Configuration.GetValue("Swagger:Enabled", true))
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", "Order Service"));
}

// Liveness deliberately checks nothing external: a failing liveness probe restarts the
// pod, and restarting a healthy pod because Postgres is down makes the outage worse.
app.MapHealthChecks("/health/live", new()
{
    Predicate = _ => false,
    ResponseWriter = HealthResponse.WriteAsync
});

app.MapHealthChecks("/health/ready", new()
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = HealthResponse.WriteAsync
});

app.MapGet("/", () => Results.Ok(new
{
    service = "order-service",
    docs = "/swagger",
    api = "/api/v1/orders"
})).ExcludeFromDescription();

app.MapOrderEndpoints();

await MigrateDatabaseAsync(app);

app.Run();
return;

static async Task MigrateDatabaseAsync(WebApplication app)
{
    if (!app.Configuration.GetValue("Database:MigrateOnStartup", true))
    {
        return;
    }

    using var scope = app.Services.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("Applying database migrations");
    await context.Database.MigrateAsync();
    logger.LogInformation("Database is up to date");
}

/// <summary>Writes the same health payload shape as the other services in the platform.</summary>
internal static class HealthResponse
{
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new
        {
            status = report.Status == HealthStatus.Healthy ? "ok" : "unavailable",
            service = "order-service",
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => entry.Value.Status.ToString().ToLowerInvariant())
        });
    }
}

/// <summary>Exposed so the integration tests can use <c>WebApplicationFactory</c>.</summary>
public partial class Program;
