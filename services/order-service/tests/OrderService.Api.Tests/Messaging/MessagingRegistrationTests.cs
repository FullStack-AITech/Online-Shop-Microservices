using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrderService.Api.Tests.Api;
using OrderService.Application.Abstractions;
using OrderService.Infrastructure;
using OrderService.Infrastructure.Messaging;

namespace OrderService.Api.Tests.Messaging;

/// <summary>Which publisher and which background workers the composition root picks.</summary>
public class MessagingRegistrationTests
{
    private static IServiceCollection Register(string bootstrapServers, bool consumersEnabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:OrderDatabase"] = "Host=unused",
                ["Downstream:ProductService:BaseUrl"] = "http://product.test",
                ["Downstream:UserService:BaseUrl"] = "http://user.test",
                ["Kafka:BootstrapServers"] = bootstrapServers,
                ["Kafka:ConsumersEnabled"] = consumersEnabled.ToString()
            })
            .Build();

        return new ServiceCollection().AddOrderServiceInfrastructure(configuration);
    }

    private static Type? PublisherType(IServiceCollection services) =>
        services.Single(descriptor => descriptor.ServiceType == typeof(IEventPublisher))
            .ImplementationType;

    private static IEnumerable<Type?> HostedServices(IServiceCollection services) =>
        services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType);

    [Fact]
    public void Without_a_broker_events_are_only_logged_and_nothing_consumes()
    {
        var services = Register(bootstrapServers: "", consumersEnabled: true);

        PublisherType(services).Should().Be(typeof(LoggingEventPublisher));
        HostedServices(services).Should().NotContain(typeof(PaymentEventsConsumer));
        HostedServices(services).Should().Contain(typeof(ProcessedEventsPruner));
    }

    [Fact]
    public void With_a_broker_events_go_to_Kafka_and_the_consumer_runs()
    {
        var services = Register(bootstrapServers: "kafka:9092", consumersEnabled: true);

        PublisherType(services).Should().Be(typeof(KafkaEventPublisher));
        HostedServices(services).Should()
            .Contain([typeof(PaymentEventsConsumer), typeof(ProcessedEventsPruner)]);
    }

    [Fact]
    public void Disabling_consumers_leaves_publishing_alone()
    {
        var services = Register(bootstrapServers: "kafka:9092", consumersEnabled: false);

        PublisherType(services).Should().Be(typeof(KafkaEventPublisher));
        HostedServices(services).Should()
            .NotContain([typeof(PaymentEventsConsumer), typeof(ProcessedEventsPruner)]);
    }

    [Fact]
    public void The_test_host_runs_no_background_workers()
    {
        using var factory = new OrderApiFactory();

        var hosted = factory.Services.GetServices<IHostedService>().Select(service => service.GetType());

        hosted.Should().NotContain([typeof(PaymentEventsConsumer), typeof(ProcessedEventsPruner)]);
    }
}
