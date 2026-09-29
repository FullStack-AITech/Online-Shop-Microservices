using Confluent.Kafka;
using OrderService.Application.Abstractions;
using OrderService.Application.Events;
using OrderService.Infrastructure.Catalog;
using OrderService.Infrastructure.Messaging;

namespace OrderService.Api.Tests.Api;

/// <summary>
/// An in-memory Product Service that tracks stock, so a test can assert that reserved
/// units were actually given back after a failure.
/// </summary>
public sealed class FakeProductCatalog : IProductCatalog
{
    private readonly Dictionary<string, CatalogProduct> _products = [];
    private readonly Dictionary<string, int> _stock = [];

    /// <summary>Set to make every call behave as though the service is unreachable.</summary>
    public bool SimulateUnavailable { get; set; }

    public List<(string ProductId, int Quantity)> Reservations { get; } = [];

    public List<(string ProductId, int Quantity)> Releases { get; } = [];

    public FakeProductCatalog AddProduct(string id, decimal price = 10.00m,
        string currency = "GBP", int stock = 100, bool active = true)
    {
        _products[id] = new CatalogProduct(id, $"SKU-{id}", $"Product {id}", price, currency,
            stock, active);
        _stock[id] = stock;
        return this;
    }

    public int StockFor(string productId) => _stock.GetValueOrDefault(productId);

    public Task<CatalogProduct?> GetProductAsync(string productId,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        var product = _products.GetValueOrDefault(productId);
        return Task.FromResult(product is null
            ? null
            : product with { StockQuantity = _stock[productId] });
    }

    public Task<StockReservationResult> ReserveStockAsync(string productId, int quantity,
        CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();

        if (!_stock.TryGetValue(productId, out var available))
        {
            return Task.FromResult(StockReservationResult.Failure("product not found"));
        }

        if (available < quantity)
        {
            return Task.FromResult(
                StockReservationResult.Failure($"only {available} units available"));
        }

        _stock[productId] = available - quantity;
        Reservations.Add((productId, quantity));
        return Task.FromResult(StockReservationResult.Success());
    }

    public Task<bool> ReleaseStockAsync(string productId, int quantity,
        CancellationToken cancellationToken)
    {
        _stock[productId] = _stock.GetValueOrDefault(productId) + quantity;
        Releases.Add((productId, quantity));
        return Task.FromResult(true);
    }

    private void ThrowIfUnavailable()
    {
        if (SimulateUnavailable)
        {
            throw new DownstreamUnavailableException("Product Service",
                new HttpRequestException("simulated outage"));
        }
    }
}

public sealed class FakeUserDirectory : IUserDirectory
{
    private readonly Dictionary<string, UserSummary> _users = [];

    public bool SimulateUnavailable { get; set; }

    public FakeUserDirectory AddUser(string id, bool isActive = true)
    {
        _users[id] = new UserSummary(id, $"{id}@example.com", $"User {id}", isActive);
        return this;
    }

    public Task<UserSummary?> GetUserAsync(string userId, CancellationToken cancellationToken)
    {
        if (SimulateUnavailable)
        {
            throw new DownstreamUnavailableException("User Service",
                new HttpRequestException("simulated outage"));
        }

        return Task.FromResult(_users.GetValueOrDefault(userId));
    }
}

/// <summary>
/// Records every event instead of sending it, so a test can assert on exactly what would
/// have gone to the broker.
/// </summary>
/// <remarks>
/// The factory is shared by every test in a class, so tests look for the event about
/// <b>their</b> order rather than counting the whole list.
/// </remarks>
public sealed class FakeEventPublisher : IEventPublisher
{
    private readonly List<(IntegrationEvent Event, string Key)> _published = [];

    /// <summary>Set to make every publish throw, as an unreachable broker would.</summary>
    public bool Fail { get; set; }

    public IReadOnlyList<(IntegrationEvent Event, string Key)> Published
    {
        get
        {
            lock (_published)
            {
                return _published.ToList();
            }
        }
    }

    public IReadOnlyList<(IntegrationEvent Event, string Key)> For(string orderId) =>
        Published.Where(published => published.Key == orderId).ToList();

    public Task PublishAsync(IntegrationEvent @event, string partitionKey,
        CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new KafkaException(ErrorCode.Local_MsgTimedOut);
        }

        lock (_published)
        {
            _published.Add((@event, partitionKey));
        }

        return Task.CompletedTask;
    }
}

/// <summary>Captures dead letters rather than producing them.</summary>
public sealed class FakeDeadLetterProducer : IDeadLetterProducer
{
    public List<(string Topic, Message<string, string> Message)> Produced { get; } = [];

    /// <summary>Set to make the dead letter topic unreachable too.</summary>
    public bool Fail { get; set; }

    public Task ProduceAsync(string topic, Message<string, string> message,
        CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new KafkaException(ErrorCode.Local_MsgTimedOut);
        }

        Produced.Add((topic, message));
        return Task.CompletedTask;
    }
}
