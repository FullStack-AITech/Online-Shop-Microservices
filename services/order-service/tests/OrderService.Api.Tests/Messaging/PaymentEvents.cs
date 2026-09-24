using System.Text;
using System.Text.Json;
using Confluent.Kafka;

namespace OrderService.Api.Tests.Messaging;

/// <summary>
/// Builds <c>payments</c> topic records as the Payment Service would send them, following
/// <c>docs/events/examples/payment-*.v1.example.json</c>.
/// </summary>
public static class PaymentEvents
{
    public static string Processed(Guid orderId, Guid? eventId = null, int version = 1) =>
        JsonSerializer.Serialize(new
        {
            eventId = eventId ?? Guid.NewGuid(),
            eventType = "PaymentProcessed",
            eventVersion = version,
            occurredAt = "2026-09-24T10:00:02Z",
            correlationId = "checkout-1",
            producer = "payment-service",
            payload = new
            {
                paymentId = Guid.NewGuid(),
                orderId,
                userId = "user-1",
                userEmail = "user-1@example.com",
                amount = "20.00",
                currency = "GBP",
                processedAt = "2026-09-24T10:00:02Z"
            }
        });

    public static string Failed(Guid orderId, string reason = "card_declined",
        Guid? eventId = null) =>
        JsonSerializer.Serialize(new
        {
            eventId = eventId ?? Guid.NewGuid(),
            eventType = "PaymentFailed",
            eventVersion = 1,
            occurredAt = "2026-09-24T10:00:02Z",
            correlationId = "checkout-1",
            producer = "payment-service",
            payload = new
            {
                paymentId = Guid.NewGuid(),
                orderId,
                userId = "user-1",
                userEmail = "user-1@example.com",
                amount = "1200.00",
                currency = "GBP",
                reason,
                failedAt = "2026-09-24T10:00:02Z"
            }
        });

    public static ConsumeResult<string, string> Record(string value, string key = "order-key",
        string eventType = "PaymentProcessed", long offset = 42, int partition = 1) => new()
    {
        Topic = "payments",
        Partition = new Partition(partition),
        Offset = new Offset(offset),
        Message = new Message<string, string>
        {
            Key = key,
            Value = value,
            Headers = new Headers
            {
                { "eventType", Encoding.UTF8.GetBytes(eventType) },
                { "correlationId", Encoding.UTF8.GetBytes("checkout-1") }
            }
        }
    };

    public static string Header(Message<string, string> message, string key) =>
        Encoding.UTF8.GetString(message.Headers.GetLastBytes(key));
}
