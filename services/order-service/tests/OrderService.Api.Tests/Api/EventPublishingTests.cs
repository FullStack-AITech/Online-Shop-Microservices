using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using OrderService.Application.Events;
using OrderService.Infrastructure.Messaging;

namespace OrderService.Api.Tests.Api;

/// <summary>
/// What the Order Service tells the rest of the platform, asserted through the fake
/// publisher. No broker is involved.
/// </summary>
/// <remarks>
/// Set <c>EVENT_CAPTURE_DIR</c> to have the wire-format tests write the serialised events
/// there, then check them against the contract with
/// <c>python scripts/validate-event-schemas.py &lt;file&gt;</c>.
/// </remarks>
public partial class EventPublishingTests : IClassFixture<OrderApiFactory>, IDisposable
{
    private readonly OrderApiFactory _factory;
    private readonly HttpClient _client;

    public EventPublishingTests(OrderApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        _factory.Users.AddUser("user-1");
        _factory.Catalog.AddProduct("p-1", price: 10.00m, stock: 1000);
        _factory.Catalog.AddProduct("p-2", price: 4.50m, stock: 1000);
    }

    public void Dispose() => _client.Dispose();

    private static object Body(params (string ProductId, int Quantity)[] lines) => new
    {
        userId = "user-1",
        lines = lines.Select(line => new { productId = line.ProductId, quantity = line.Quantity })
    };

    private async Task<string> CreateOrderAsync(HttpRequestMessage? request = null)
    {
        request ??= new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(Body(("p-1", 2), ("p-2", 4)))
        };

        var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var order = await response.Content.ReadFromJsonAsync<JsonElement>();
        return order.GetProperty("id").GetString()!;
    }

    private static JsonElement Wire(IntegrationEvent @event) =>
        JsonDocument.Parse(EventSerializer.Serialize(@event)).RootElement;

    // ------------------------------------------------------------ OrderCreated

    [Fact]
    public async Task Placing_an_order_publishes_exactly_one_OrderCreated_keyed_by_order_id()
    {
        var orderId = await CreateOrderAsync();

        var published = _factory.Publisher.For(orderId);

        published.Should().ContainSingle();
        published[0].Event.EventType.Should().Be("OrderCreated");
        published[0].Event.EventVersion.Should().Be(1);
        published[0].Event.Producer.Should().Be("order-service");
    }

    [Fact]
    public async Task OrderCreated_carries_the_email_the_priced_lines_and_the_total()
    {
        var orderId = await CreateOrderAsync();

        var payload = Wire(_factory.Publisher.For(orderId).Single().Event).GetProperty("payload");

        payload.GetProperty("orderId").GetString().Should().Be(orderId);
        payload.GetProperty("userEmail").GetString().Should().Be("user-1@example.com");
        payload.GetProperty("currency").GetString().Should().Be("GBP");
        // Money is a two-place string, never a JSON number: 2 x 10.00 + 4 x 4.50.
        payload.GetProperty("totalAmount").GetString().Should().Be("38.00");
        payload.GetProperty("totalItems").GetInt32().Should().Be(6);

        var lines = payload.GetProperty("lines").EnumerateArray().ToList();
        lines.Should().HaveCount(2);
        var first = lines.Single(line => line.GetProperty("productId").GetString() == "p-1");
        first.GetProperty("sku").GetString().Should().Be("SKU-p-1");
        first.GetProperty("unitPrice").GetString().Should().Be("10.00");
        first.GetProperty("quantity").GetInt32().Should().Be(2);
        first.GetProperty("lineTotal").GetString().Should().Be("20.00");
    }

    [Fact]
    public async Task No_event_is_published_when_stock_cannot_be_reserved()
    {
        _factory.Catalog.AddProduct("p-none", stock: 0);
        var before = _factory.Publisher.Published.Count;

        var response = await _client.PostAsJsonAsync("/api/v1/orders", Body(("p-none", 1)));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.Publisher.Published.Should().HaveCount(before);
    }

    [Fact]
    public async Task A_broker_failure_still_returns_201_and_the_order_is_stored()
    {
        _factory.Catalog.AddProduct("p-unannounced", stock: 10);
        _factory.Publisher.Fail = true;
        try
        {
            var orderId = await CreateOrderAsync(new HttpRequestMessage(HttpMethod.Post,
                "/api/v1/orders") { Content = JsonContent.Create(Body(("p-unannounced", 1))) });

            // The known gap until the outbox (#34): stored, but nobody was told.
            var read = await _client.GetAsync($"/api/v1/orders/{orderId}");
            read.StatusCode.Should().Be(HttpStatusCode.OK);
            _factory.Publisher.For(orderId).Should().BeEmpty();
            _factory.Catalog.StockFor("p-unannounced").Should().Be(9,
                "the order was saved, so its stock must stay reserved");
        }
        finally
        {
            _factory.Publisher.Fail = false;
        }
    }

    [Fact]
    public async Task The_callers_correlation_id_flows_into_the_event()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(Body(("p-1", 1)))
        };
        request.Headers.Add("X-Correlation-Id", "checkout-1234");

        var orderId = await CreateOrderAsync(request);

        _factory.Publisher.For(orderId).Single().Event.CorrelationId.Should().Be("checkout-1234");
    }

    [Fact]
    public async Task Without_a_correlation_header_a_fresh_id_is_generated()
    {
        var orderId = await CreateOrderAsync();

        var correlationId = _factory.Publisher.For(orderId).Single().Event.CorrelationId;
        Guid.TryParse(correlationId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task OrderCreated_on_the_wire_matches_the_contract()
    {
        var orderId = await CreateOrderAsync();
        var @event = _factory.Publisher.For(orderId).Single().Event;

        var json = EventSerializer.Serialize(@event);
        var wire = JsonDocument.Parse(json).RootElement;

        // The envelope, camelCase, exactly as docs/events/README.md defines it.
        wire.GetProperty("eventId").GetString().Should().Be(@event.EventId.ToString());
        wire.GetProperty("eventType").GetString().Should().Be("OrderCreated");
        wire.GetProperty("eventVersion").GetInt32().Should().Be(1);
        wire.GetProperty("producer").GetString().Should().Be("order-service");
        wire.GetProperty("correlationId").GetString().Should().NotBeNullOrEmpty();
        UtcTimestamp().IsMatch(wire.GetProperty("occurredAt").GetString()!).Should().BeTrue();
        UtcTimestamp().IsMatch(wire.GetProperty("payload").GetProperty("createdAt").GetString()!)
            .Should().BeTrue();

        Capture("order-created.captured.json", json);
    }

    // ---------------------------------------------------------- OrderCancelled

    [Fact]
    public async Task Cancelling_an_order_publishes_OrderCancelled_with_the_previous_status()
    {
        var orderId = await CreateOrderAsync();

        var response = await _client.PostAsJsonAsync($"/api/v1/orders/{orderId}/cancel",
            new { reason = "changed my mind" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var cancelled = _factory.Publisher.For(orderId)
            .Single(published => published.Event.EventType == "OrderCancelled");
        var json = EventSerializer.Serialize(cancelled.Event);
        var payload = JsonDocument.Parse(json).RootElement.GetProperty("payload");

        payload.GetProperty("orderId").GetString().Should().Be(orderId);
        payload.GetProperty("reason").GetString().Should().Be("changed my mind");
        payload.GetProperty("previousStatus").GetString().Should().Be("AwaitingPayment");
        payload.GetProperty("lines").GetArrayLength().Should().Be(2);
        UtcTimestamp().IsMatch(payload.GetProperty("cancelledAt").GetString()!).Should().BeTrue();

        Capture("order-cancelled.captured.json", json);
    }

    [Fact]
    public async Task A_rejected_cancellation_publishes_nothing()
    {
        var orderId = await CreateOrderAsync();
        await _client.PostAsJsonAsync($"/api/v1/orders/{orderId}/cancel", new { reason = "first" });

        var again = await _client.PostAsJsonAsync($"/api/v1/orders/{orderId}/cancel",
            new { reason = "second" });

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.Publisher.For(orderId)
            .Count(published => published.Event.EventType == "OrderCancelled").Should().Be(1);
    }

    [Fact]
    public async Task A_broker_failure_does_not_fail_the_cancellation()
    {
        var orderId = await CreateOrderAsync();
        _factory.Publisher.Fail = true;
        try
        {
            var response = await _client.PostAsJsonAsync($"/api/v1/orders/{orderId}/cancel",
                new { reason = "broker down" });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("status").GetString().Should().Be("Cancelled");
        }
        finally
        {
            _factory.Publisher.Fail = false;
        }
    }

    private static void Capture(string fileName, string json)
    {
        var directory = Environment.GetEnvironmentVariable("EVENT_CAPTURE_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, fileName), json);
        }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")]
    private static partial Regex UtcTimestamp();
}
