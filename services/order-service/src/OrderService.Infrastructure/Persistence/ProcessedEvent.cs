namespace OrderService.Infrastructure.Persistence;

/// <summary>
/// One row of <c>processed_events</c>: this consumer has acted on this event.
/// </summary>
/// <remarks>
/// A persistence detail rather than a domain concept, so it lives here and not in the
/// domain. The key is <c>(EventId, Consumer)</c> so two handlers in this service could each
/// process the same event once.
/// </remarks>
public sealed class ProcessedEvent
{
    private ProcessedEvent()
    {
        // Required by EF Core.
        Consumer = string.Empty;
    }

    public ProcessedEvent(Guid eventId, string consumer, DateTimeOffset processedAt)
    {
        EventId = eventId;
        Consumer = consumer;
        ProcessedAt = processedAt;
    }

    public Guid EventId { get; private set; }

    public string Consumer { get; private set; }

    public DateTimeOffset ProcessedAt { get; private set; }
}
