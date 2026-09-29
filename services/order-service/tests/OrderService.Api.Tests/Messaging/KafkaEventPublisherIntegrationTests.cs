using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderService.Application.Abstractions;
using OrderService.Application.Events;
using OrderService.Domain.Orders;
using OrderService.Infrastructure.Messaging;

namespace OrderService.Api.Tests.Messaging;

/// <summary>
/// The real producer against a real broker. Skipped unless <c>KAFKA_BOOTSTRAP</c> is set:
/// <c>KAFKA_BOOTSTRAP=localhost:29092 dotnet test --filter FullyQualifiedName~KafkaEventPublisherIntegrationTests</c>
/// with the Compose stack up.
/// </summary>
/// <remarks>
/// Also proves the host listener: <c>localhost:29092</c> only works if the broker advertises
/// an address reachable from outside the Compose network.
/// </remarks>
public class KafkaEventPublisherIntegrationTests
{
    [SkippableFact]
    public async Task An_event_reaches_the_orders_topic_with_its_key_and_headers()
    {
        var bootstrap = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP");
        Skip.If(string.IsNullOrWhiteSpace(bootstrap), "KAFKA_BOOTSTRAP is not set");

        var options = new KafkaOptions { BootstrapServers = bootstrap!, OrdersTopic = "orders" };
        var order = Order.Create("user-it",
            [new OrderLineDraft("p-it", "SKU-IT", "Integration widget", 12.50m, "GBP", 2)]);
        order.MarkAwaitingPayment();
        var @event = OrderCreatedV1.From(order,
            new UserSummary("user-it", "it@example.com", "Integration Test", true), "it-correlation");

        using (var publisher = new KafkaEventPublisher(options, NullLogger<KafkaEventPublisher>.Instance))
        {
            await publisher.PublishAsync(@event, order.Id.ToString(), CancellationToken.None);
        }

        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = bootstrap,
            // A group of its own, so it neither disturbs nor is disturbed by real consumers.
            GroupId = $"order-service-it-{Guid.NewGuid()}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false
        }).Build();
        consumer.Subscribe("orders");

        ConsumeResult<string, string>? found = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (found is null && DateTime.UtcNow < deadline)
        {
            var record = consumer.Consume(TimeSpan.FromSeconds(1));
            if (record?.Message.Key == order.Id.ToString())
            {
                found = record;
            }
        }

        consumer.Close();

        found.Should().NotBeNull("the event should arrive within 30 seconds");
        Header(found!, "eventType").Should().Be("OrderCreated");
        Header(found!, "correlationId").Should().Be("it-correlation");
        JsonDocument.Parse(found!.Message.Value).RootElement.GetProperty("eventId").GetString()
            .Should().Be(@event.EventId.ToString());
    }

    private static string Header(ConsumeResult<string, string> record, string key) =>
        Encoding.UTF8.GetString(record.Message.Headers.GetLastBytes(key));
}
