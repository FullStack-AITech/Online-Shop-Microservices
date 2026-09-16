using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace OrderService.Api.Tests.Api;

/// <summary>End-to-end tests for the Orders API, exercised over HTTP.</summary>
public class OrderApiTests : IClassFixture<OrderApiFactory>, IDisposable
{
    private readonly OrderApiFactory _factory;
    private readonly HttpClient _client;

    public OrderApiTests(OrderApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();

        _factory.Users.AddUser("user-1");
        _factory.Catalog.AddProduct("p-1", price: 10.00m, stock: 100);
        _factory.Catalog.AddProduct("p-2", price: 4.50m, stock: 100);
    }

    public void Dispose() => _client.Dispose();

    private static object Body(string userId = "user-1", params (string ProductId, int Quantity)[] lines)
    {
        var effective = lines.Length == 0 ? [("p-1", 2)] : lines;
        return new
        {
            userId,
            lines = effective.Select(line => new { productId = line.ProductId, quantity = line.Quantity })
        };
    }

    private async Task<JsonElement> CreateOrderAsync(object body)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/orders", body);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ------------------------------------------------------------------ create

    [Fact]
    public async Task Placing_an_order_returns_201_with_the_priced_order()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/orders",
            Body(lines: [("p-1", 2), ("p-2", 4)]));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();

        var order = await response.Content.ReadFromJsonAsync<JsonElement>();
        order.GetProperty("status").GetString().Should().Be("AwaitingPayment");
        order.GetProperty("currency").GetString().Should().Be("GBP");
        // Priced from the catalogue, not from the request: 2 x 10.00 + 4 x 4.50.
        order.GetProperty("totalAmount").GetDecimal().Should().Be(38.00m);
        order.GetProperty("totalItems").GetInt32().Should().Be(6);
        order.GetProperty("lines").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Placing_an_order_reserves_stock_in_the_catalogue()
    {
        _factory.Catalog.AddProduct("p-stock", stock: 10);

        await CreateOrderAsync(Body(lines: [("p-stock", 3)]));

        _factory.Catalog.StockFor("p-stock").Should().Be(7);
    }

    [Fact]
    public async Task An_unknown_user_is_rejected_with_404()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/orders", Body(userId: "ghost"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("error").GetString().Should().Be("user_not_found");
    }

    [Fact]
    public async Task An_inactive_user_cannot_place_an_order()
    {
        _factory.Users.AddUser("user-inactive", isActive: false);

        var response = await _client.PostAsJsonAsync("/api/v1/orders",
            Body(userId: "user-inactive"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("error").GetString().Should().Be("user_inactive");
    }

    [Fact]
    public async Task An_unknown_product_is_rejected_with_404()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/orders",
            Body(lines: [("no-such-product", 1)]));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_inactive_product_cannot_be_ordered()
    {
        _factory.Catalog.AddProduct("p-retired", active: false);

        var response = await _client.PostAsJsonAsync("/api/v1/orders",
            Body(lines: [("p-retired", 1)]));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("error").GetString().Should().Be("product_inactive");
    }

    [Fact]
    public async Task Insufficient_stock_returns_409()
    {
        _factory.Catalog.AddProduct("p-scarce", stock: 2);

        var response = await _client.PostAsJsonAsync("/api/v1/orders",
            Body(lines: [("p-scarce", 5)]));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("error").GetString().Should().Be("insufficient_stock");
    }

    [Fact]
    public async Task A_failed_reservation_releases_the_stock_already_reserved()
    {
        // The compensation that stops a partially-reserved order from leaking stock.
        _factory.Catalog.AddProduct("p-ok", stock: 50);
        _factory.Catalog.AddProduct("p-short", stock: 1);

        var response = await _client.PostAsJsonAsync("/api/v1/orders",
            Body(lines: [("p-ok", 5), ("p-short", 10)]));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        _factory.Catalog.StockFor("p-ok").Should().Be(50,
            "the units reserved for the first line must be given back");
        _factory.Catalog.Releases.Should().Contain(("p-ok", 5));
    }

    [Fact]
    public async Task An_empty_order_is_rejected()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/orders",
            new { userId = "user-1", lines = Array.Empty<object>() });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_same_product_twice_is_rejected()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/orders",
            Body(lines: [("p-1", 1), ("p-1", 2)]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("error").GetString().Should().Be("duplicate_order_line");
    }

    [Fact]
    public async Task Lines_in_different_currencies_are_rejected()
    {
        _factory.Catalog.AddProduct("p-gbp", currency: "GBP");
        _factory.Catalog.AddProduct("p-usd", currency: "USD");

        var response = await _client.PostAsJsonAsync("/api/v1/orders",
            Body(lines: [("p-gbp", 1), ("p-usd", 1)]));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("error").GetString().Should().Be("mixed_currency");
    }

    // ------------------------------------------------------------ dependencies

    [Fact]
    public async Task A_downstream_outage_returns_503_not_500()
    {
        _factory.Users.SimulateUnavailable = true;
        try
        {
            var response = await _client.PostAsJsonAsync("/api/v1/orders", Body());

            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            error.GetProperty("error").GetString().Should().Be("dependency_unavailable");
        }
        finally
        {
            _factory.Users.SimulateUnavailable = false;
        }
    }

    // -------------------------------------------------------------- read paths

    [Fact]
    public async Task An_order_can_be_read_back_by_id()
    {
        var created = await CreateOrderAsync(Body());
        var id = created.GetProperty("id").GetString();

        var response = await _client.GetAsync($"/api/v1/orders/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var order = await response.Content.ReadFromJsonAsync<JsonElement>();
        order.GetProperty("id").GetString().Should().Be(id);
    }

    [Fact]
    public async Task An_unknown_order_returns_404()
    {
        var response = await _client.GetAsync($"/api/v1/orders/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("error").GetString().Should().Be("order_not_found");
    }

    [Fact]
    public async Task Orders_are_listed_per_user_and_paginated()
    {
        _factory.Users.AddUser("user-paging");
        for (var i = 0; i < 3; i++)
        {
            await CreateOrderAsync(Body(userId: "user-paging"));
        }

        var response = await _client.GetAsync("/api/v1/orders?userId=user-paging&limit=2&offset=0");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        page.GetProperty("total").GetInt32().Should().Be(3);
        page.GetProperty("items").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task One_user_does_not_see_another_users_orders()
    {
        _factory.Users.AddUser("user-a");
        _factory.Users.AddUser("user-b");
        await CreateOrderAsync(Body(userId: "user-a"));

        var response = await _client.GetAsync("/api/v1/orders?userId=user-b");
        var page = await response.Content.ReadFromJsonAsync<JsonElement>();

        page.GetProperty("total").GetInt32().Should().Be(0);
    }

    // --------------------------------------------------------------- lifecycle

    [Fact]
    public async Task An_order_can_be_paid_then_shipped()
    {
        var created = await CreateOrderAsync(Body());
        var id = created.GetProperty("id").GetString();

        var paid = await _client.PostAsync($"/api/v1/orders/{id}/pay", null);
        paid.StatusCode.Should().Be(HttpStatusCode.OK);
        (await paid.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString().Should().Be("Paid");

        var shipped = await _client.PostAsync($"/api/v1/orders/{id}/ship", null);
        shipped.StatusCode.Should().Be(HttpStatusCode.OK);
        (await shipped.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString().Should().Be("Shipped");
    }

    [Fact]
    public async Task An_illegal_transition_returns_409()
    {
        var created = await CreateOrderAsync(Body());
        var id = created.GetProperty("id").GetString();

        // Shipping before payment is not a legal move.
        var response = await _client.PostAsync($"/api/v1/orders/{id}/ship", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("error").GetString().Should().Be("invalid_order_transition");
    }

    [Fact]
    public async Task An_order_can_be_cancelled_with_a_reason()
    {
        var created = await CreateOrderAsync(Body());
        var id = created.GetProperty("id").GetString();

        var response = await _client.PostAsJsonAsync($"/api/v1/orders/{id}/cancel",
            new { reason = "changed my mind" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var order = await response.Content.ReadFromJsonAsync<JsonElement>();
        order.GetProperty("status").GetString().Should().Be("Cancelled");
        order.GetProperty("statusReason").GetString().Should().Be("changed my mind");
    }

    [Fact]
    public async Task A_shipped_order_cannot_be_cancelled()
    {
        var created = await CreateOrderAsync(Body());
        var id = created.GetProperty("id").GetString();
        await _client.PostAsync($"/api/v1/orders/{id}/pay", null);
        await _client.PostAsync($"/api/v1/orders/{id}/ship", null);

        var response = await _client.PostAsJsonAsync($"/api/v1/orders/{id}/cancel",
            new { reason = "too late" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ------------------------------------------------------------------ probes

    [Fact]
    public async Task The_liveness_probe_reports_ok()
    {
        var response = await _client.GetAsync("/health/live");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task The_readiness_probe_checks_the_database()
    {
        var response = await _client.GetAsync("/health/ready");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("checks").GetProperty("order-database").GetString()
            .Should().Be("healthy");
    }
}
