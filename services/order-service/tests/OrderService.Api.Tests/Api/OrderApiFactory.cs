using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OrderService.Application.Abstractions;
using OrderService.Infrastructure.Persistence;

namespace OrderService.Api.Tests.Api;

/// <summary>
/// Hosts the real application with two things swapped out: the database becomes in-memory
/// SQLite, and the two downstream services become in-memory fakes.
/// </summary>
/// <remarks>
/// Everything else — routing, model binding, the exception middleware, the handlers, the
/// aggregate — is the production code path. These tests would catch a broken status-code
/// mapping or a bad JSON contract, which a handler unit test would not.
/// </remarks>
public sealed class OrderApiFactory : WebApplicationFactory<Program>
{
    private SqliteConnection? _connection;

    public FakeProductCatalog Catalog { get; } = new();

    public FakeUserDirectory Users { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:OrderDatabase", "Data Source=:memory:");
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Swagger:Enabled", "false");
        builder.UseSetting("Downstream:ProductService:BaseUrl", "http://product.test");
        builder.UseSetting("Downstream:UserService:BaseUrl", "http://user.test");

        builder.ConfigureServices(services =>
        {
            // The connection must stay open: an in-memory SQLite database exists only for
            // as long as a connection to it does.
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();

            services.RemoveAll<DbContextOptions<OrderDbContext>>();
            services.RemoveAll<OrderDbContext>();
            services.AddDbContext<OrderDbContext>(options => options.UseSqlite(_connection));

            services.RemoveAll<IProductCatalog>();
            services.RemoveAll<IUserDirectory>();
            services.AddSingleton<IProductCatalog>(Catalog);
            services.AddSingleton<IUserDirectory>(Users);

            using var scope = services.BuildServiceProvider().CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
            context.Database.EnsureCreated();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection?.Dispose();
        }
    }
}
