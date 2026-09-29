using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderService.Infrastructure.Persistence.Repositories;

namespace OrderService.Infrastructure.Messaging;

/// <summary>
/// Deletes <c>processed_events</c> rows once they can no longer matter.
/// </summary>
/// <remarks>
/// A marker only has to outlive the chance of a redelivery, and nothing older than the
/// topic's retention (7 days, ADR 0001) can be redelivered. Eight days leaves a margin.
/// Without this the table grows by one row per payment, forever.
/// </remarks>
public sealed class ProcessedEventsPruner(
    IServiceScopeFactory scopes,
    ILogger<ProcessedEventsPruner> logger) : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(8);

    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            // Once at start-up, then hourly: a service restarted more often than hourly
            // would otherwise never prune at all.
            do
            {
                await PruneOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task PruneOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ProcessedEventStore>();
            var deleted = await store.PruneAsync(DateTimeOffset.UtcNow - Retention, stoppingToken);
            if (deleted > 0)
            {
                logger.LogInformation("Pruned {Count} processed_events rows older than {Days} days",
                    deleted, Retention.TotalDays);
            }
        }
        catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
        {
            // A missed prune costs nothing but disk; try again next hour rather than stop.
            logger.LogError(exception, "Pruning processed_events failed");
        }
    }
}
