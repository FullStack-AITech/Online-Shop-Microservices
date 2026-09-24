namespace OrderService.Application.Abstractions;

/// <summary>
/// Remembers which incoming events this service has already acted on.
/// </summary>
/// <remarks>
/// Kafka delivers at least once, so every consumer sees duplicates sooner or later. The
/// marker is written in the <b>same</b> unit of work as the business change — the one
/// <see cref="IOrderRepository.SaveChangesAsync"/> commits both or neither — so "applied"
/// and "recorded as applied" cannot drift apart.
/// </remarks>
public interface IProcessedEventStore
{
    Task<bool> HasProcessedAsync(Guid eventId, string consumer, CancellationToken cancellationToken);

    /// <summary>Adds the marker to the unit of work. Nothing is written until it is saved.</summary>
    void Record(Guid eventId, string consumer);

    /// <summary>
    /// True when a failed save was the marker's primary key rejecting a second copy — that
    /// is, another delivery of the same event won the race.
    /// </summary>
    /// <remarks>
    /// The check in <see cref="HasProcessedAsync"/> is only an optimisation; the key is the
    /// real guard. This lets a handler recognise that case without knowing the database.
    /// </remarks>
    bool IsDuplicateRecord(Exception exception);
}
