using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OrderService.Infrastructure.Persistence;
using OrderService.Infrastructure.Persistence.Repositories;

namespace OrderService.Api.Tests.Messaging;

/// <summary>
/// <c>processed_events</c> against a real (SQLite) database: the key does the
/// deduplicating, and the pruner only removes what is old enough.
/// </summary>
public sealed class ProcessedEventStoreTests : IDisposable
{
    private const string Consumer = "order-service.payment-outcome";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public ProcessedEventStoreTests()
    {
        _connection.Open();
        using var context = NewContext();
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private OrderDbContext NewContext() =>
        new(new DbContextOptionsBuilder<OrderDbContext>().UseSqlite(_connection).Options);

    [Fact]
    public async Task A_recorded_event_is_reported_as_processed_once_saved()
    {
        var eventId = Guid.NewGuid();
        await using (var context = NewContext())
        {
            var store = new ProcessedEventStore(context);
            store.Record(eventId, Consumer);

            (await store.HasProcessedAsync(eventId, Consumer, CancellationToken.None))
                .Should().BeFalse("Record only adds to the unit of work");

            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            var store = new ProcessedEventStore(context);
            (await store.HasProcessedAsync(eventId, Consumer, CancellationToken.None)).Should().BeTrue();
            (await store.HasProcessedAsync(eventId, "another-consumer", CancellationToken.None))
                .Should().BeFalse("the key is (event_id, consumer)");
        }
    }

    [Fact]
    public async Task Recording_the_same_event_twice_violates_the_key()
    {
        var eventId = Guid.NewGuid();
        await using (var context = NewContext())
        {
            new ProcessedEventStore(context).Record(eventId, Consumer);
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            new ProcessedEventStore(context).Record(eventId, Consumer);

            var act = () => context.SaveChangesAsync();

            await act.Should().ThrowAsync<DbUpdateException>();
        }
    }

    [Fact]
    public async Task The_same_event_may_be_recorded_by_two_consumers()
    {
        var eventId = Guid.NewGuid();
        await using var context = NewContext();
        var store = new ProcessedEventStore(context);
        store.Record(eventId, Consumer);
        store.Record(eventId, "another-consumer");

        var act = () => context.SaveChangesAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Pruning_removes_only_rows_older_than_the_cutoff()
    {
        var old = Guid.NewGuid();
        var recent = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var context = NewContext())
        {
            context.ProcessedEvents.Add(new ProcessedEvent(old, Consumer, now.AddDays(-9)));
            context.ProcessedEvents.Add(new ProcessedEvent(recent, Consumer, now.AddDays(-7)));
            await context.SaveChangesAsync();
        }

        await using (var context = NewContext())
        {
            var store = new ProcessedEventStore(context);

            var deleted = await store.PruneAsync(now.AddDays(-8), CancellationToken.None);

            deleted.Should().Be(1);
            (await store.HasProcessedAsync(old, Consumer, CancellationToken.None)).Should().BeFalse();
            (await store.HasProcessedAsync(recent, Consumer, CancellationToken.None)).Should().BeTrue();
        }
    }
}
