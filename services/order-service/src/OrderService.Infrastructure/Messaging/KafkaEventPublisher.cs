using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using OrderService.Application.Abstractions;
using OrderService.Application.Events;

namespace OrderService.Infrastructure.Messaging;

/// <summary>
/// Publishes events to the <c>orders</c> topic.
/// </summary>
/// <remarks>
/// <para>
/// A singleton with one producer for the life of the process. The producer is thread-safe
/// and expensive to create — it holds connections and a background thread — so one per
/// request would be both slow and wasteful.
/// </para>
/// <para>
/// <c>ProduceAsync</c> completes only once the broker has acknowledged the message (or
/// <see cref="KafkaOptions.DeliveryTimeoutMs"/> has passed), so a failure here is a real
/// failure and the caller hears about it.
/// </para>
/// </remarks>
public sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaEventPublisher> _logger;
    private readonly IProducer<string, string> _producer;

    public KafkaEventPublisher(KafkaOptions options, ILogger<KafkaEventPublisher> logger)
    {
        _options = options;
        _logger = logger;
        _producer = new ProducerBuilder<string, string>(options.ToProducerConfig("order-service"))
            .SetErrorHandler((_, error) =>
                logger.LogWarning("Kafka producer error: {Reason}", error.Reason))
            .Build();
    }

    public async Task PublishAsync(IntegrationEvent @event, string partitionKey,
        CancellationToken cancellationToken)
    {
        var message = new Message<string, string>
        {
            Key = partitionKey,
            Value = EventSerializer.Serialize(@event),
            // Duplicated from the value so tooling and the dead letter queue can route and
            // trace a message without parsing it.
            Headers = new Headers
            {
                { "eventType", Encoding.UTF8.GetBytes(@event.EventType) },
                { "correlationId", Encoding.UTF8.GetBytes(@event.CorrelationId) }
            }
        };

        var result = await _producer.ProduceAsync(_options.OrdersTopic, message, cancellationToken);

        _logger.LogInformation(
            "Published {EventType} {EventId} for key {Key} to {Topic} partition {Partition} offset {Offset}",
            @event.EventType, @event.EventId, partitionKey, result.Topic,
            result.Partition.Value, result.Offset.Value);
    }

    public void Dispose()
    {
        // Give anything still in flight a moment to reach the broker on shutdown.
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
