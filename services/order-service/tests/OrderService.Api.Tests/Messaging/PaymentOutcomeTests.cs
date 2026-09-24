using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderService.Api.Tests.Api;
using OrderService.Application.Orders;
using OrderService.Infrastructure.Messaging;
using OrderService.Infrastructure.Persistence;

namespace OrderService.Api.Tests.Messaging;

/// <summary>
/// Payment events driving real orders: the real processor, handler, aggregate and database
/// (SQLite), fed hand-built records instead of a broker.
/// </summary>
public class PaymentOutcomeTests : IClassFixture<OrderApiFactory>, IDisposable
{
    private readonly OrderApiFactory _factory;
    private readonly HttpClient _client;
    private readonly FakeDeadLetterProducer _deadLetters = new();
    private readonly PaymentEventProcessor _processor;

    public PaymentOutcomeTests(OrderApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _factory.Users.AddUser("user-1");
        _factory.Catalog.AddProduct("p-1", price: 10.00m, stock: 1000);

        _processor = new PaymentEventProcessor(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _deadLetters,
            NullLogger<PaymentEventProcessor>.Instance,
            retryDelay: TimeSpan.Zero);
    }

    public void Dispose() => _client.Dispose();

    private async Task<Guid> CreateOrderAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/orders", new
        {
            userId = "user-1",
            lines = new[] { new { productId = "p-1", quantity = 2 } }
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var order = await response.Content.ReadFromJsonAsync<JsonElement>();
        return order.GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> GetOrderAsync(Guid orderId) =>
        await _client.GetFromJsonAsync<JsonElement>($"/api/v1/orders/{orderId}");

    private async Task<PaymentOutcomeResult> HandleDirectlyAsync(PaymentOutcome outcome)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetRequiredService<HandlePaymentOutcomeHandler>();
        return await handler.HandleAsync(outcome, CancellationToken.None);
    }

    [Fact]
    public async Task PaymentProcessed_moves_the_order_to_Paid()
    {
        var orderId = await CreateOrderAsync();

        await _processor.ProcessAsync(PaymentEvents.Record(PaymentEvents.Processed(orderId)),
            CancellationToken.None);

        (await GetOrderAsync(orderId)).GetProperty("status").GetString().Should().Be("Paid");
        _deadLetters.Produced.Should().BeEmpty();
    }

    [Fact]
    public async Task PaymentFailed_moves_the_order_to_Failed_with_the_reason()
    {
        var orderId = await CreateOrderAsync();

        await _processor.ProcessAsync(
            PaymentEvents.Record(PaymentEvents.Failed(orderId), eventType: "PaymentFailed"),
            CancellationToken.None);

        var order = await GetOrderAsync(orderId);
        order.GetProperty("status").GetString().Should().Be("Failed");
        order.GetProperty("statusReason").GetString().Should().Be("payment_failed: card_declined");
    }

    [Fact]
    public async Task The_same_event_twice_changes_the_order_once()
    {
        var orderId = await CreateOrderAsync();
        var outcome = new PaymentOutcome(Guid.NewGuid(), orderId, true, null, "checkout-1");

        var first = await HandleDirectlyAsync(outcome);
        var updatedAt = (await GetOrderAsync(orderId)).GetProperty("updatedAt").GetString();
        var second = await HandleDirectlyAsync(outcome);

        first.Should().Be(PaymentOutcomeResult.Applied);
        second.Should().Be(PaymentOutcomeResult.Duplicate);
        (await GetOrderAsync(orderId)).GetProperty("updatedAt").GetString().Should().Be(updatedAt);

        await using var scope = _factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        (await context.ProcessedEvents.CountAsync(processed => processed.EventId == outcome.EventId))
            .Should().Be(1);
    }

    [Fact]
    public async Task A_redelivered_record_is_acknowledged_without_a_second_change()
    {
        var orderId = await CreateOrderAsync();
        var record = PaymentEvents.Record(PaymentEvents.Processed(orderId, Guid.NewGuid()));

        await _processor.ProcessAsync(record, CancellationToken.None);
        await _processor.ProcessAsync(record, CancellationToken.None);

        (await GetOrderAsync(orderId)).GetProperty("status").GetString().Should().Be("Paid");
        _deadLetters.Produced.Should().BeEmpty();
    }

    [Fact]
    public async Task A_payment_for_an_already_cancelled_order_is_ignored()
    {
        var orderId = await CreateOrderAsync();
        await _client.PostAsJsonAsync($"/api/v1/orders/{orderId}/cancel", new { reason = "too slow" });

        var result = await HandleDirectlyAsync(
            new PaymentOutcome(Guid.NewGuid(), orderId, true, null, "checkout-1"));

        result.Should().Be(PaymentOutcomeResult.Ignored);
        (await GetOrderAsync(orderId)).GetProperty("status").GetString().Should().Be("Cancelled");
    }

    [Fact]
    public async Task A_late_failure_for_a_paid_order_is_ignored()
    {
        var orderId = await CreateOrderAsync();
        await HandleDirectlyAsync(new PaymentOutcome(Guid.NewGuid(), orderId, true, null, "c"));

        var result = await HandleDirectlyAsync(
            new PaymentOutcome(Guid.NewGuid(), orderId, false, "card_declined", "c"));

        result.Should().Be(PaymentOutcomeResult.Ignored);
        (await GetOrderAsync(orderId)).GetProperty("status").GetString().Should().Be("Paid");
    }

    [Fact]
    public async Task A_payment_for_an_unknown_order_is_ignored_not_dead_lettered()
    {
        await _processor.ProcessAsync(PaymentEvents.Record(PaymentEvents.Processed(Guid.NewGuid())),
            CancellationToken.None);

        _deadLetters.Produced.Should().BeEmpty();
    }
}
