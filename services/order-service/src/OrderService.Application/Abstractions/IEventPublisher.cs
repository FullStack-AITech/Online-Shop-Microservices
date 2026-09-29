using OrderService.Application.Events;

namespace OrderService.Application.Abstractions;

/// <summary>
/// Announces what happened to an order to whoever is listening.
/// </summary>
/// <remarks>
/// Owned by this service, like the other ports: the handlers know that an event goes out,
/// not that Kafka carries it. Tests substitute an in-memory fake, and local runs without a
/// broker use a publisher that only logs.
/// </remarks>
public interface IEventPublisher
{
    /// <summary>Publishes one event. Throws if the broker did not acknowledge it.</summary>
    /// <param name="partitionKey">
    /// Always the order id, so every event about one order lands on the same partition and
    /// stays in order.
    /// </param>
    Task PublishAsync(IntegrationEvent @event, string partitionKey, CancellationToken cancellationToken);
}
