using System.Text.Json;
using OrderService.Application.Orders;

namespace OrderService.Infrastructure.Messaging;

/// <summary>
/// Reads <c>PaymentProcessed</c> and <c>PaymentFailed</c> v1 off the <c>payments</c> topic.
/// </summary>
/// <remarks>
/// <para>
/// This service's own model of someone else's event, holding only the fields it uses. There
/// is deliberately no shared contracts library: the Payment Service can add fields without
/// this service being rebuilt, and unknown fields are simply ignored.
/// </para>
/// <para>
/// Three outcomes, following <c>docs/events/README.md</c>: a <see cref="PaymentOutcome"/>;
/// <c>null</c> for an event type this service does not handle (acknowledge and move on);
/// or <see cref="PoisonMessageException"/> for anything that will never succeed however
/// often it is retried — bad JSON, a missing field, or a known type at an unknown version.
/// </para>
/// </remarks>
public static class PaymentEventParser
{
    public const string PaymentProcessed = "PaymentProcessed";
    public const string PaymentFailed = "PaymentFailed";
    public const int SupportedVersion = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static PaymentOutcome? Parse(string? value)
    {
        PaymentEventV1? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<PaymentEventV1>(value ?? string.Empty, Options);
        }
        catch (JsonException exception)
        {
            throw new PoisonMessageException($"Value is not a valid event: {exception.Message}",
                exception);
        }

        if (envelope is null || string.IsNullOrEmpty(envelope.EventType))
        {
            throw new PoisonMessageException("Value has no eventType");
        }

        if (envelope.EventType is not (PaymentProcessed or PaymentFailed))
        {
            return null;
        }

        if (envelope.EventVersion != SupportedVersion)
        {
            // A contract break, not noise: someone published a version nobody here reads.
            throw new PoisonMessageException(
                $"{envelope.EventType} version {envelope.EventVersion} is not supported "
                + $"(this consumer reads version {SupportedVersion})");
        }

        if (envelope.EventId == Guid.Empty || envelope.Payload is null
            || envelope.Payload.OrderId == Guid.Empty)
        {
            throw new PoisonMessageException(
                $"{envelope.EventType} is missing eventId or payload.orderId");
        }

        var succeeded = envelope.EventType == PaymentProcessed;
        if (!succeeded && string.IsNullOrWhiteSpace(envelope.Payload.Reason))
        {
            throw new PoisonMessageException("PaymentFailed is missing payload.reason");
        }

        return new PaymentOutcome(
            envelope.EventId,
            envelope.Payload.OrderId,
            succeeded,
            succeeded ? null : envelope.Payload.Reason,
            string.IsNullOrWhiteSpace(envelope.CorrelationId)
                ? envelope.EventId.ToString()
                : envelope.CorrelationId);
    }

    private sealed record PaymentEventV1(
        Guid EventId,
        string? EventType,
        int EventVersion,
        string? CorrelationId,
        PaymentPayloadV1? Payload);

    private sealed record PaymentPayloadV1(Guid OrderId, string? Reason);
}

/// <summary>
/// A message that can never be processed, however many times it is retried. It goes
/// straight to the dead letter topic.
/// </summary>
public sealed class PoisonMessageException(string message, Exception? inner = null)
    : Exception(message, inner);
