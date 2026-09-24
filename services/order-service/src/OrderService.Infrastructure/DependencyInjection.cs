using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderService.Application.Abstractions;
using OrderService.Application.Orders;
using OrderService.Infrastructure.Catalog;
using OrderService.Infrastructure.Messaging;
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
        services.AddScoped<ProcessedEventStore>();
        services.AddScoped<IProcessedEventStore>(provider =>
            provider.GetRequiredService<ProcessedEventStore>());

        services.AddScoped<CreateOrderHandler>();
        services.AddScoped<GetOrderHandler>();
        services.AddScoped<ListOrdersHandler>();
        services.AddScoped<UpdateOrderStatusHandler>();
        services.AddScoped<HandlePaymentOutcomeHandler>();

        AddDownstreamClients(services, configuration);
        AddMessaging(services, configuration);

        return services;
    }

    /// <summary>
    /// Kafka when a broker is configured, a logging stand-in when it is not.
    /// </summary>
    /// <remarks>
    /// The background workers are opt-out through <c>Kafka:ConsumersEnabled</c>, which the
    /// API tests set to false: they drive the handlers directly and must not try to reach a
    /// broker or race the test for the database.
    /// </remarks>
    private static void AddMessaging(IServiceCollection services, IConfiguration configuration)
    {
        var options = new KafkaOptions();
        configuration.GetSection(KafkaOptions.SectionName).Bind(options);
        services.AddSingleton(options);

        if (options.IsConfigured)
        {
            services.AddSingleton<IEventPublisher, KafkaEventPublisher>();
        }
        else
        {
            services.AddSingleton<IEventPublisher, LoggingEventPublisher>();
        }

        if (!options.ConsumersEnabled)
        {
            return;
        }

        services.AddHostedService<ProcessedEventsPruner>();

        if (!options.IsConfigured)
        {
            // Nothing to consume from. Orders simply stay AwaitingPayment, and the manual
            // POST /pay endpoint still works.
            return;
        }

        services.AddSingleton<IDeadLetterProducer, KafkaDeadLetterProducer>();
        services.AddSingleton(provider => new PaymentEventProcessor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IDeadLetterProducer>(),
            provider.GetRequiredService<ILogger<PaymentEventProcessor>>(),
            retryDelay: TimeSpan.FromMilliseconds(500)));
        services.AddHostedService<PaymentEventsConsumer>();
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
