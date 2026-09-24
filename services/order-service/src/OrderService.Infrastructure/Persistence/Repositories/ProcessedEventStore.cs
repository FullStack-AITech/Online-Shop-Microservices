using Microsoft.EntityFrameworkCore;
using Npgsql;
using OrderService.Application.Abstractions;

namespace OrderService.Infrastructure.Persistence.Repositories;

/// <summary>
/// <c>processed_events</c> through the same <see cref="OrderDbContext"/> as the orders, so
/// a marker and the change it guards are saved by one <c>SaveChangesAsync</c>.
/// </summary>
/// <remarks>
/// EF Core has no <c>INSERT ... ON CONFLICT DO NOTHING</c>, so this checks first and lets
/// the primary key catch the race. Checking first is safe here only because one partition
/// is read by one consumer in the group at a time; the key is still the real guard.
/// </remarks>
public sealed class ProcessedEventStore(OrderDbContext context) : IProcessedEventStore
{
    /// <summary>Postgres <c>unique_violation</c>.</summary>
    private const string UniqueViolation = "23505";

    public async Task<bool> HasProcessedAsync(Guid eventId, string consumer,
        CancellationToken cancellationToken) =>
        await context.ProcessedEvents.AnyAsync(
            processed => processed.EventId == eventId && processed.Consumer == consumer,
            cancellationToken);

    public void Record(Guid eventId, string consumer) =>
        context.ProcessedEvents.Add(new ProcessedEvent(eventId, consumer, DateTimeOffset.UtcNow));

    public bool IsDuplicateRecord(Exception exception) =>
        exception is DbUpdateException
        {
            InnerException: PostgresException { SqlState: UniqueViolation } postgres
        }
        && postgres.TableName == "processed_events";

    /// <summary>Deletes markers older than <paramref name="cutoff"/>. Returns how many.</summary>
    public async Task<int> PruneAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        await context.ProcessedEvents
            .Where(processed => processed.ProcessedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
}
