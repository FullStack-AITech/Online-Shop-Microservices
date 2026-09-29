using Confluent.Kafka;
using Microsoft.Extensions.Logging;

namespace OrderService.Infrastructure.Messaging;

/// <summary>
/// Sends one message to a dead letter topic and waits for the broker to accept it.
/// </summary>
/// <remarks>
/// The narrowest seam over <see cref="IProducer{TKey,TValue}"/> that the consumer needs, so
/// the retry-then-dead-letter logic can be tested without a broker.
/// </remarks>
public interface IDeadLetterProducer
{
    Task ProduceAsync(string topic, Message<string, string> message,
        CancellationToken cancellationToken);
}

/// <summary>The real dead letter producer. Only created when a consumer runs.</summary>
public sealed class KafkaDeadLetterProducer : IDeadLetterProducer, IDisposable
{
    private readonly IProducer<string, string> _producer;

    public KafkaDeadLetterProducer(KafkaOptions options, ILogger<KafkaDeadLetterProducer> logger)
    {
        _producer = new ProducerBuilder<string, string>(
                options.ToProducerConfig("order-service-dlq"))
            .SetErrorHandler((_, error) =>
                logger.LogWarning("Kafka dead letter producer error: {Reason}", error.Reason))
            .Build();
    }

    public async Task ProduceAsync(string topic, Message<string, string> message,
        CancellationToken cancellationToken) =>
        await _producer.ProduceAsync(topic, message, cancellationToken);

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
