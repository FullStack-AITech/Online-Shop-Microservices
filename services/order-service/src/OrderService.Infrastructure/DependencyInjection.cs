using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OrderService.Application.Abstractions;
using OrderService.Application.Orders;
using OrderService.Infrastructure.Catalog;
using OrderService.Infrastructure.Persistence;
using OrderService.Infrastructure.Persistence.Repositories;
using OrderService.Infrastructure.Resilience;
using OrderService.Infrastructure.Users;

namespace OrderService.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOrderServiceInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<OrderDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString("OrderDatabase")
                              ?? throw new InvalidOperationException(
                                  "Connection string 'OrderDatabase' is not configured")));

        services.AddScoped<IOrderRepository, OrderRepository>();

        services.AddScoped<CreateOrderHandler>();
        services.AddScoped<GetOrderHandler>();
        services.AddScoped<ListOrdersHandler>();
        services.AddScoped<UpdateOrderStatusHandler>();

        AddDownstreamClients(services, configuration);

        return services;
    }

    private static void AddDownstreamClients(IServiceCollection services,
        IConfiguration configuration)
    {
        var productOptions = Bind(configuration, "Downstream:ProductService");
        var userOptions = Bind(configuration, "Downstream:UserService");

        services.AddSingleton(productOptions);

        // Reads: retried, because asking twice costs nothing.
        services.AddHttpClient(ProductCatalogClient.IdempotentClient, client =>
            {
                client.BaseAddress = new Uri(productOptions.BaseUrl);
                // The pipeline owns timeouts; leaving this at its 100s default would let a
                // hung socket outlive every budget configured below.
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .AddIdempotentPipeline(productOptions);

        // Writes: not retried. Reserving stock twice is not the same as reserving once.
        services.AddHttpClient(ProductCatalogClient.NonIdempotentClient, client =>
            {
                client.BaseAddress = new Uri(productOptions.BaseUrl);
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .AddNonIdempotentPipeline(productOptions);

        services.AddHttpClient(UserDirectoryClient.ClientName, client =>
            {
                client.BaseAddress = new Uri(userOptions.BaseUrl);
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .AddIdempotentPipeline(userOptions);

        services.AddScoped<IProductCatalog, ProductCatalogClient>();
        services.AddScoped<IUserDirectory, UserDirectoryClient>();
    }

    private static DownstreamOptions Bind(IConfiguration configuration, string section)
    {
        var options = new DownstreamOptions();
        configuration.GetSection(section).Bind(options);

        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new InvalidOperationException($"Configuration section '{section}:BaseUrl' is required");
        }

        return options;
    }
}
