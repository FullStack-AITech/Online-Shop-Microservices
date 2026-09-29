using Confluent.Kafka;

namespace OrderService.Infrastructure.Messaging;

/// <summary>
/// The <c>Kafka</c> configuration section.
/// </summary>
/// <remarks>
/// An empty <see cref="BootstrapServers"/> means "no broker": events are only logged and no
/// consumer runs. That keeps <c>dotnet test</c> and a laptop without Docker working.
/// </remarks>
public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    /// <summary>The consumer group every Order Service instance joins.</summary>
    public const string ConsumerGroup = "order-service";

    public string BootstrapServers { get; set; } = string.Empty;

    public string OrdersTopic { get; set; } = "orders";

    public string PaymentsTopic { get; set; } = "payments";

    /// <summary>
    /// How long a publish may wait for the broker's acknowledgement. librdkafka's default
    /// is five minutes, which would hold an HTTP request that long while the broker is down.
    /// </summary>
    public int DeliveryTimeoutMs { get; set; } = 5_000;

    /// <summary>Runs the payment-events consumer and the processed_events pruner. Tests turn it off.</summary>
    public bool ConsumersEnabled { get; set; } = true;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BootstrapServers);

    /// <summary>The one producer configuration this service uses, for events and dead letters alike.</summary>
    public ProducerConfig ToProducerConfig(string clientId) => new()
    {
        BootstrapServers = BootstrapServers,
        ClientId = clientId,
        // Wait for every in-sync replica, and let the broker drop the duplicates that a
        // producer retry would otherwise write.
        Acks = Acks.All,
        EnableIdempotence = true,
        MessageTimeoutMs = DeliveryTimeoutMs
    };
}
