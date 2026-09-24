using System.Globalization;
using System.Text;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderService.Application.Orders;

namespace OrderService.Infrastructure.Messaging;

/// <summary>
/// Everything that happens to one message from the <c>payments</c> topic, short of
/// committing its offset.
/// </summary>
/// <remarks>
/// <para>
/// Split out of <see cref="PaymentEventsConsumer"/> so it can be tested with a hand-built
/// <see cref="ConsumeResult{TKey,TValue}"/> and no broker. The consumer's only remaining
/// job is the poll-process-commit loop.
/// </para>
/// <para>
/// When <see cref="ProcessAsync"/> returns, the message is finished with — applied,
/// skipped as a duplicate, ignored, or dead-lettered — and its offset may be committed.
/// When it throws, it is not, and the offset must not move.
/// </para>
/// <para>
/// Each attempt runs in a fresh DI scope, and so with a fresh <c>DbContext</c>. That is
/// what makes the retry after a <see cref="DbUpdateConcurrencyException"/> correct: the
/// order is reloaded with the state that beat us, rather than retried against the stale
/// copy that lost.
/// </para>
/// </remarks>
public sealed class PaymentEventProcessor(
    IServiceScopeFactory scopes,
    IDeadLetterProducer deadLetters,
    ILogger<PaymentEventProcessor> logger,
    TimeSpan retryDelay)
{
    /// <summary>Attempts in total, including the first, before a message is dead-lettered.</summary>
    public const int MaxAttempts = 3;

    /// <summary>The <c>dlq-error</c> header is capped so one huge stack trace cannot bloat the topic.</summary>
    private const int MaxErrorLength = 500;

    public async Task ProcessAsync(ConsumeResult<string, string> record,
        CancellationToken cancellationToken)
    {
        PaymentOutcome? outcome;
        try
        {
            outcome = PaymentEventParser.Parse(record.Message.Value);
        }
        catch (PoisonMessageException exception)
        {
            // It will never parse, so retrying only delays the partition behind it.
            logger.LogError(exception, "Unreadable message at {Topic}/{Partition}@{Offset}",
                record.Topic, record.Partition.Value, record.Offset.Value);
            await DeadLetterAsync(record, exception, cancellationToken);
            return;
        }

        if (outcome is null)
        {
            // Another event type on the same topic; not ours to handle.
            logger.LogDebug("Ignoring event type {EventType} at {Topic}/{Partition}@{Offset}",
                Header(record, "eventType"), record.Topic, record.Partition.Value,
                record.Offset.Value);
            return;
        }

        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = outcome.CorrelationId,
            ["EventId"] = outcome.EventId
        });

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var handler = scope.ServiceProvider.GetRequiredService<HandlePaymentOutcomeHandler>();
                await handler.HandleAsync(outcome, cancellationToken);
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt >= MaxAttempts)
                {
                    logger.LogError(exception,
                        "Payment event {EventId} for order {OrderId} failed {Attempts} times; "
                        + "dead-lettering it", outcome.EventId, outcome.OrderId, attempt);
                    await DeadLetterAsync(record, exception, cancellationToken);
                    return;
                }

                if (exception is DbUpdateConcurrencyException)
                {
                    // Someone else changed the order first — typically a manual cancel. The
                    // next attempt reloads it and the state machine decides again.
                    logger.LogInformation(
                        "Order {OrderId} changed underneath payment event {EventId}; reloading "
                        + "(attempt {Attempt} of {MaxAttempts})",
                        outcome.OrderId, outcome.EventId, attempt, MaxAttempts);
                }
                else
                {
                    logger.LogWarning(exception,
                        "Payment event {EventId} for order {OrderId} failed (attempt {Attempt} "
                        + "of {MaxAttempts}); retrying",
                        outcome.EventId, outcome.OrderId, attempt, MaxAttempts);
                }

                await Task.Delay(retryDelay * attempt, cancellationToken);
            }
        }
    }

    private async Task DeadLetterAsync(ConsumeResult<string, string> record, Exception exception,
        CancellationToken cancellationToken)
    {
        var topic = $"{record.Topic}.dlq";
        var message = BuildDeadLetter(record, exception, DateTimeOffset.UtcNow);

        // If this throws the offset is not committed, so the message comes round again
        // rather than being lost.
        await deadLetters.ProduceAsync(topic, message, cancellationToken);

        logger.LogWarning("Dead-lettered {Topic}/{Partition}@{Offset} to {DeadLetterTopic}",
            record.Topic, record.Partition.Value, record.Offset.Value, topic);
    }

    /// <summary>
    /// The original key, value and headers, plus the <c>dlq-*</c> headers from the
    /// conventions in <c>docs/events/README.md</c>.
    /// </summary>
    internal static Message<string, string> BuildDeadLetter(ConsumeResult<string, string> record,
        Exception exception, DateTimeOffset failedAt)
    {
        var headers = new Headers();
        if (record.Message.Headers is { } original)
        {
            foreach (var header in original)
            {
                headers.Add(header.Key, header.GetValueBytes());
            }
        }

        var error = $"{exception.GetType().Name}: {exception.Message}";
        if (error.Length > MaxErrorLength)
        {
            error = error[..MaxErrorLength];
        }

        Add(headers, "dlq-original-topic", record.Topic);
        Add(headers, "dlq-original-partition",
            record.Partition.Value.ToString(CultureInfo.InvariantCulture));
        Add(headers, "dlq-original-offset", record.Offset.Value.ToString(CultureInfo.InvariantCulture));
        Add(headers, "dlq-consumer", HandlePaymentOutcomeHandler.ConsumerName);
        Add(headers, "dlq-error", error);
        Add(headers, "dlq-failed-at",
            failedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));

        return new Message<string, string>
        {
            Key = record.Message.Key,
            Value = record.Message.Value,
            Headers = headers
        };
    }

    private static void Add(Headers headers, string key, string value) =>
        headers.Add(key, Encoding.UTF8.GetBytes(value));

    private static string? Header(ConsumeResult<string, string> record, string key) =>
        record.Message.Headers is { } headers && headers.TryGetLastBytes(key, out var bytes)
            ? Encoding.UTF8.GetString(bytes)
            : null;
}
