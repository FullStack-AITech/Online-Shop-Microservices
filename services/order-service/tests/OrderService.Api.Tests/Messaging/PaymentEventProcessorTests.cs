using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderService.Api.Tests.Api;
using OrderService.Application.Abstractions;
using OrderService.Application.Orders;
using OrderService.Domain.Orders;
using OrderService.Infrastructure.Messaging;

namespace OrderService.Api.Tests.Messaging;

/// <summary>
/// The retry and dead-letter rules from <c>docs/events/README.md</c>, with the database and
/// the broker both faked so each failure can be staged exactly.
/// </summary>
public class PaymentEventProcessorTests
{
    private readonly ScriptedOrderStore _store = new();
    private readonly FakeDeadLetterProducer _deadLetters = new();
    private readonly PaymentEventProcessor _processor;

    public PaymentEventProcessorTests()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IOrderRepository>(_store)
            .AddSingleton<IProcessedEventStore>(_store)
            .AddScoped<HandlePaymentOutcomeHandler>()
            .BuildServiceProvider();

        _processor = new PaymentEventProcessor(
            services.GetRequiredService<IServiceScopeFactory>(),
            _deadLetters,
            NullLogger<PaymentEventProcessor>.Instance,
            retryDelay: TimeSpan.Zero);
    }

    [Fact]
    public async Task Unparseable_json_is_dead_lettered_at_once_with_its_key_value_and_headers()
    {
        var record = PaymentEvents.Record("not-json{", key: "poison", offset: 17, partition: 2);

        await _processor.ProcessAsync(record, CancellationToken.None);

        _store.Loads.Should().Be(0, "a message that cannot parse must never reach the handler");
        var (topic, message) = _deadLetters.Produced.Should().ContainSingle().Subject;
        topic.Should().Be("payments.dlq");
        message.Key.Should().Be("poison");
        message.Value.Should().Be("not-json{");
        PaymentEvents.Header(message, "eventType").Should().Be("PaymentProcessed");
        PaymentEvents.Header(message, "correlationId").Should().Be("checkout-1");
        PaymentEvents.Header(message, "dlq-original-topic").Should().Be("payments");
        PaymentEvents.Header(message, "dlq-original-partition").Should().Be("2");
        PaymentEvents.Header(message, "dlq-original-offset").Should().Be("17");
        PaymentEvents.Header(message, "dlq-consumer").Should().Be("order-service.payment-outcome");
        PaymentEvents.Header(message, "dlq-error").Should().StartWith("PoisonMessageException");
        DateTimeOffset.Parse(PaymentEvents.Header(message, "dlq-failed-at"))
            .Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task An_unknown_version_of_a_known_event_is_dead_lettered_without_retrying()
    {
        var record = PaymentEvents.Record(PaymentEvents.Processed(Guid.NewGuid(), version: 2));

        await _processor.ProcessAsync(record, CancellationToken.None);

        _store.Loads.Should().Be(0);
        _deadLetters.Produced.Should().ContainSingle();
        PaymentEvents.Header(_deadLetters.Produced[0].Message, "dlq-error")
            .Should().Contain("version 2");
    }

    [Fact]
    public async Task An_event_type_this_service_does_not_handle_is_skipped_quietly()
    {
        var record = PaymentEvents.Record(
            """{"eventId":"a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d","eventType":"PaymentRefunded","eventVersion":1,"payload":{}}""",
            eventType: "PaymentRefunded");

        await _processor.ProcessAsync(record, CancellationToken.None);

        _store.Loads.Should().Be(0);
        _deadLetters.Produced.Should().BeEmpty();
    }

    [Fact]
    public async Task A_message_that_keeps_failing_is_tried_three_times_then_dead_lettered()
    {
        var orderId = _store.AddOrder();
        _store.FailNextLoads(int.MaxValue, () => new TimeoutException(new string('x', 2_000)));

        await _processor.ProcessAsync(PaymentEvents.Record(PaymentEvents.Processed(orderId)),
            CancellationToken.None);

        _store.Loads.Should().Be(PaymentEventProcessor.MaxAttempts);
        var (_, message) = _deadLetters.Produced.Should().ContainSingle().Subject;
        var error = PaymentEvents.Header(message, "dlq-error");
        error.Should().StartWith("TimeoutException: xxx");
        error.Length.Should().Be(500);
    }

    [Fact]
    public async Task A_transient_failure_is_retried_and_then_succeeds()
    {
        var orderId = _store.AddOrder();
        _store.FailNextLoads(1, () => new TimeoutException("database blip"));

        await _processor.ProcessAsync(PaymentEvents.Record(PaymentEvents.Processed(orderId)),
            CancellationToken.None);

        _store.StatusOf(orderId).Should().Be(OrderStatus.Paid);
        _deadLetters.Produced.Should().BeEmpty();
    }

    [Fact]
    public async Task A_concurrency_conflict_reloads_the_order_and_retries()
    {
        // A manual cancel or a second instance saved the order between our read and our
        // write. The retry must reload it, not replay the stale copy.
        var orderId = _store.AddOrder();
        _store.FailNextSaves(1, () => new DbUpdateConcurrencyException("xmin changed"));

        await _processor.ProcessAsync(PaymentEvents.Record(PaymentEvents.Processed(orderId)),
            CancellationToken.None);

        _store.Loads.Should().Be(2);
        _store.StatusOf(orderId).Should().Be(OrderStatus.Paid);
        _deadLetters.Produced.Should().BeEmpty();
    }

    [Fact]
    public async Task When_the_dead_letter_topic_is_unreachable_the_message_is_not_acknowledged()
    {
        _deadLetters.Fail = true;

        var act = () => _processor.ProcessAsync(PaymentEvents.Record("garbage"),
            CancellationToken.None);

        // The consumer only commits when ProcessAsync returns, so throwing is what keeps
        // the message from being skipped.
        await act.Should().ThrowAsync<Confluent.Kafka.KafkaException>();
    }

    /// <summary>
    /// An order repository and processed-event store in one, so a marker is only "saved"
    /// when the order is — the same all-or-nothing unit of work as the real DbContext.
    /// Every load hands out a fresh copy, as a new DbContext would.
    /// </summary>
    private sealed class ScriptedOrderStore : IOrderRepository, IProcessedEventStore
    {
        private readonly Dictionary<Guid, OrderStatus> _saved = [];
        private readonly HashSet<Guid> _processed = [];
        private readonly List<Guid> _pendingMarkers = [];
        private readonly Dictionary<Guid, Guid> _realIds = [];
        private Order? _loaded;
        private int _failLoads;
        private int _failSaves;
        private Func<Exception> _loadFailure = () => new InvalidOperationException();
        private Func<Exception> _saveFailure = () => new InvalidOperationException();

        public int Loads { get; private set; }

        public Guid AddOrder()
        {
            var orderId = Guid.NewGuid();
            _saved[orderId] = OrderStatus.AwaitingPayment;
            return orderId;
        }

        public OrderStatus StatusOf(Guid orderId) => _saved[orderId];

        public void FailNextLoads(int count, Func<Exception> failure) =>
            (_failLoads, _loadFailure) = (count, failure);

        public void FailNextSaves(int count, Func<Exception> failure) =>
            (_failSaves, _saveFailure) = (count, failure);

        public Task<Order?> GetAsync(Guid orderId, CancellationToken cancellationToken)
        {
            Loads++;
            if (_failLoads-- > 0)
            {
                throw _loadFailure();
            }

            if (!_saved.TryGetValue(orderId, out var status))
            {
                return Task.FromResult<Order?>(null);
            }

            var order = Order.Create("user-1",
                [new OrderLineDraft("p-1", "SKU-p-1", "Product", 10.00m, "GBP", 1)]);
            if (status == OrderStatus.AwaitingPayment)
            {
                order.MarkAwaitingPayment();
            }

            _realIds[order.Id] = orderId;
            _loaded = order;
            return Task.FromResult<Order?>(order);
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            if (_failSaves-- > 0)
            {
                _pendingMarkers.Clear();
                throw _saveFailure();
            }

            if (_loaded is not null)
            {
                _saved[_realIds[_loaded.Id]] = _loaded.Status;
            }

            _processed.UnionWith(_pendingMarkers);
            _pendingMarkers.Clear();
            return Task.CompletedTask;
        }

        public Task<bool> HasProcessedAsync(Guid eventId, string consumer,
            CancellationToken cancellationToken) =>
            Task.FromResult(_processed.Contains(eventId));

        public void Record(Guid eventId, string consumer) => _pendingMarkers.Add(eventId);

        public bool IsDuplicateRecord(Exception exception) => false;

        public Task AddAsync(Order order, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<Order> Orders, int Total)> ListByUserAsync(string userId,
            int limit, int offset, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
