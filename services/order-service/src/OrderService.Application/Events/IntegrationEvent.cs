namespace OrderService.Application.Events;

/// <summary>
/// The envelope every event shares: see <c>docs/events/README.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EventId"/> is fixed when the event is created, not when it is sent. A retried
/// publish must reuse it, or every consumer's deduplication is defeated.
/// </para>
/// <para>
/// <see cref="Payload"/> is typed <c>object</c> so the serialiser writes whatever concrete
/// payload record it holds. The shape of each payload is the contract, and it lives in the
/// payload records alongside this one.
/// </para>
/// </remarks>
public sealed record IntegrationEvent(
    Guid EventId,
    string EventType,
    int EventVersion,
    DateTimeOffset OccurredAt,
    string CorrelationId,
    object Payload)
{
    public const string ProducerName = "order-service";

    public string Producer { get; init; } = ProducerName;

    public static IntegrationEvent Create(string eventType, int eventVersion,
        DateTimeOffset occurredAt, string correlationId, object payload) =>
        new(Guid.NewGuid(), eventType, eventVersion, occurredAt, correlationId, payload);
}
