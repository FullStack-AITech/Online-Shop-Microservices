using Microsoft.Extensions.Logging;
using OrderService.Application.Abstractions;
using OrderService.Domain.Orders;

namespace OrderService.Application.Orders;

/// <summary>
/// The result of a <c>PaymentProcessed</c> or <c>PaymentFailed</c> event, as this service
/// understands it. Only the fields it acts on.
/// </summary>
/// <param name="FailureReason">The payment's reason, e.g. <c>card_declined</c>. Null on success.</param>
public sealed record PaymentOutcome(
    Guid EventId,
    Guid OrderId,
    bool Succeeded,
    string? FailureReason,
    string CorrelationId);

public enum PaymentOutcomeResult
{
    /// <summary>The order moved to Paid or Failed.</summary>
    Applied,

    /// <summary>This event was already processed; nothing changed.</summary>
    Duplicate,

    /// <summary>The order is unknown, or already past the point this event could move it.</summary>
    Ignored
}

/// <summary>
/// Moves an order to <c>Paid</c> or <c>Failed</c> when the Payment Service reports back.
/// </summary>
/// <remarks>
/// <para>
/// Delivery is at least once, so this must be safe to run twice for the same event. The
/// order change and the <c>processed_events</c> marker go out in <b>one</b>
/// <c>SaveChangesAsync</c>: either both commit or neither does, and a redelivery finds the
/// marker and stops.
/// </para>
/// <para>
/// A late or out-of-date event — a payment for an order the customer already cancelled —
/// is not an error. The aggregate's transition table decides, the event is logged and
/// acknowledged, and nothing is retried: retrying cannot make an illegal move legal.
/// </para>
/// </remarks>
public sealed class HandlePaymentOutcomeHandler(
    IOrderRepository repository,
    IProcessedEventStore processedEvents,
    ILogger<HandlePaymentOutcomeHandler> logger)
{
    /// <summary>This handler's name in <c>processed_events</c>.</summary>
    public const string ConsumerName = "order-service.payment-outcome";

    public async Task<PaymentOutcomeResult> HandleAsync(PaymentOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (await processedEvents.HasProcessedAsync(outcome.EventId, ConsumerName,
                cancellationToken))
        {
            logger.LogInformation("Event {EventId} for order {OrderId} already processed; skipping",
                outcome.EventId, outcome.OrderId);
            return PaymentOutcomeResult.Duplicate;
        }

        var order = await repository.GetAsync(outcome.OrderId, cancellationToken);
        if (order is null)
        {
            // Retrying will not make it appear: orders are committed before OrderCreated is
            // published, so a payment for an unknown order is not a race.
            logger.LogWarning("Payment event {EventId} refers to unknown order {OrderId}; ignoring",
                outcome.EventId, outcome.OrderId);
            return PaymentOutcomeResult.Ignored;
        }

        var target = outcome.Succeeded ? OrderStatus.Paid : OrderStatus.Failed;
        if (order.Status == target || !OrderStateMachine.CanTransition(order.Status, target))
        {
            logger.LogWarning(
                "Payment event {EventId} would move order {OrderId} from {Status} to {Target}; "
                + "the order has moved on, so the event is ignored",
                outcome.EventId, order.Id, order.Status, target);
            return PaymentOutcomeResult.Ignored;
        }

        if (outcome.Succeeded)
        {
            order.MarkPaid();
        }
        else
        {
            order.Fail($"payment_failed: {outcome.FailureReason ?? "unknown"}");
        }

        processedEvents.Record(outcome.EventId, ConsumerName);

        try
        {
            await repository.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (processedEvents.IsDuplicateRecord(exception))
        {
            // Another delivery of the same event committed between our check and our save.
            // The key rejected the second marker and rolled the whole change back with it.
            logger.LogInformation("Event {EventId} for order {OrderId} was processed concurrently",
                outcome.EventId, outcome.OrderId);
            return PaymentOutcomeResult.Duplicate;
        }

        logger.LogInformation("Order {OrderId} is now {Status} after payment event {EventId}",
            order.Id, order.Status, outcome.EventId);
        return PaymentOutcomeResult.Applied;
    }
}
