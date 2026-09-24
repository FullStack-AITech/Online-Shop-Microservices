using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderService.Infrastructure.Messaging;

/// <summary>
/// Reads the <c>payments</c> topic and moves orders to Paid or Failed.
/// </summary>
/// <remarks>
/// <para>
/// Auto-commit is off. An offset is committed only after <see cref="PaymentEventProcessor"/>
/// has finished with the message — which for a real change means after the database
/// transaction has committed. A crash in between redelivers the message, and the
/// <c>processed_events</c> marker makes the second delivery harmless. That is
/// at-least-once delivery with an idempotent consumer: a duplicate can be detected, a lost
/// message cannot.
/// </para>
/// <para>
/// <c>Consume</c> blocks its thread, so the loop runs on its own task. Run inline, the
/// first poll would stop <c>ExecuteAsync</c> ever returning, and the host would never get
/// as far as starting the HTTP server.
/// </para>
/// </remarks>
public sealed class PaymentEventsConsumer(
    KafkaOptions options,
    PaymentEventProcessor processor,
    ILogger<PaymentEventsConsumer> logger) : BackgroundService
{
    /// <summary>Pause before re-reading a message that could not be finished with.</summary>
    private static readonly TimeSpan RedeliveryPause = TimeSpan.FromSeconds(5);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Run(() => ConsumeLoopAsync(stoppingToken), stoppingToken);

    private async Task ConsumeLoopAsync(CancellationToken stoppingToken)
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = KafkaOptions.ConsumerGroup,
            ClientId = "order-service",
            EnableAutoCommit = false,
            // A new group starts from the beginning of retention rather than silently
            // skipping every payment that arrived before it first joined.
            AutoOffsetReset = AutoOffsetReset.Earliest
        };

        using var consumer = new ConsumerBuilder<string, string>(config)
            .SetErrorHandler((_, error) =>
                logger.LogWarning("Kafka consumer error: {Reason}", error.Reason))
            .Build();

        consumer.Subscribe(options.PaymentsTopic);
        logger.LogInformation("Consuming {Topic} as group {Group}", options.PaymentsTopic,
            KafkaOptions.ConsumerGroup);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ConsumeResult<string, string>? record;
                try
                {
                    record = consumer.Consume(stoppingToken);
                }
                catch (ConsumeException exception)
                {
                    logger.LogError(exception, "Failed to read from {Topic}", options.PaymentsTopic);
                    continue;
                }

                if (record?.Message is null)
                {
                    continue;
                }

                try
                {
                    await processor.ProcessAsync(record, stoppingToken);
                    consumer.Commit(record);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // Shutting down mid-message: leave the offset where it is, so the next
                    // instance to own the partition picks it up.
                    break;
                }
                catch (Exception exception)
                {
                    // Not even the dead letter topic would take it — the broker is probably
                    // down. Committing a later offset would skip this one, so rewind to it
                    // and try again after a pause.
                    logger.LogError(exception,
                        "Could not finish {Topic}/{Partition}@{Offset}; it will be redelivered",
                        record.Topic, record.Partition.Value, record.Offset.Value);
                    consumer.Seek(record.TopicPartitionOffset);
                    await Task.Delay(RedeliveryPause, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        finally
        {
            // Leaves the group cleanly, so its partitions are reassigned at once rather than
            // after the session times out.
            consumer.Close();
        }
    }
}
