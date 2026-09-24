using Microsoft.Extensions.Logging;
using OrderService.Application.Abstractions;
using OrderService.Application.Events;

namespace OrderService.Infrastructure.Messaging;

/// <summary>
/// Used when no broker is configured: writes the event to the log and nothing else.
/// </summary>
/// <remarks>
/// Lets the service run on a laptop without Kafka, while still showing exactly what would
/// have been published. Nothing downstream reacts, so orders stay <c>AwaitingPayment</c>.
/// </remarks>
public sealed class LoggingEventPublisher(ILogger<LoggingEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync(IntegrationEvent @event, string partitionKey,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "No broker configured; {EventType} {EventId} (key {Key}) not sent: {Event}",
            @event.EventType, @event.EventId, partitionKey, EventSerializer.Serialize(@event));
        return Task.CompletedTask;
    }
}
