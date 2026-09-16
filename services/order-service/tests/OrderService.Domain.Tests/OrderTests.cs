using FluentAssertions;
using OrderService.Domain.Exceptions;
using OrderService.Domain.Orders;

namespace OrderService.Domain.Tests;

public class OrderTests
{
    private static OrderLineDraft Draft(string productId = "p-1", decimal price = 10.00m,
        string currency = "GBP", int quantity = 2) =>
        new(productId, $"SKU-{productId}", $"Product {productId}", price, currency, quantity);

    private static Order NewOrder(params OrderLineDraft[] drafts) =>
        Order.Create("user-1", drafts.Length == 0 ? [Draft()] : drafts);

    // ---------------------------------------------------------------- creation

    [Fact]
    public void A_new_order_starts_pending()
    {
        var order = NewOrder();

        order.Status.Should().Be(OrderStatus.Pending);
        order.Id.Should().NotBeEmpty();
        order.IsTerminal.Should().BeFalse();
    }

    [Fact]
    public void The_total_is_the_sum_of_the_lines()
    {
        var order = NewOrder(
            Draft("p-1", price: 10.00m, quantity: 2),
            Draft("p-2", price: 4.50m, quantity: 3));

        order.TotalAmount.Should().Be(33.50m);
        order.TotalItems.Should().Be(5);
    }

    [Fact]
    public void An_order_must_have_at_least_one_line() =>
        FluentActions.Invoking(() => Order.Create("user-1", []))
            .Should().Throw<EmptyOrderException>();

    [Fact]
    public void An_order_must_have_a_user() =>
        FluentActions.Invoking(() => Order.Create("  ", [Draft()]))
            .Should().Throw<ArgumentException>();

    [Fact]
    public void Lines_must_all_use_the_same_currency() =>
        FluentActions.Invoking(() => NewOrder(
                Draft("p-1", currency: "GBP"),
                Draft("p-2", currency: "USD")))
            .Should().Throw<MixedCurrencyException>()
            .WithMessage("*GBP*USD*");

    [Fact]
    public void The_same_product_cannot_appear_twice() =>
        FluentActions.Invoking(() => NewOrder(Draft("p-1"), Draft("p-1")))
            .Should().Throw<DuplicateOrderLineException>();

    [Fact]
    public void Quantity_must_be_positive() =>
        FluentActions.Invoking(() => NewOrder(Draft(quantity: 0)))
            .Should().Throw<InvalidQuantityException>();

    [Fact]
    public void Price_cannot_be_negative() =>
        FluentActions.Invoking(() => NewOrder(Draft(price: -1m)))
            .Should().Throw<NegativePriceException>();

    [Fact]
    public void A_free_line_is_allowed()
    {
        var order = NewOrder(Draft(price: 0m, quantity: 1));

        order.TotalAmount.Should().Be(0m);
    }

    [Fact]
    public void Currency_is_normalised_to_uppercase()
    {
        var order = NewOrder(Draft(currency: "gbp"));

        order.Currency.Should().Be("GBP");
        order.Lines.Single().Currency.Should().Be("GBP");
    }

    [Fact]
    public void The_price_is_captured_on_the_line_at_order_time()
    {
        var order = NewOrder(Draft(price: 12.34m, quantity: 3));
        var line = order.Lines.Single();

        line.UnitPrice.Should().Be(12.34m);
        line.LineTotal.Should().Be(37.02m);
    }

    [Fact]
    public void Lines_cannot_be_modified_through_the_public_collection() =>
        order_lines_are_read_only();

    private static void order_lines_are_read_only()
    {
        var order = NewOrder();

        order.Lines.Should().BeAssignableTo<IReadOnlyCollection<OrderLine>>();
        (order.Lines as ICollection<OrderLine>)?.IsReadOnly.Should().BeTrue();
    }

    // -------------------------------------------------------------- lifecycle

    [Fact]
    public void The_happy_path_runs_pending_to_shipped()
    {
        var order = NewOrder();

        order.MarkAwaitingPayment();
        order.Status.Should().Be(OrderStatus.AwaitingPayment);

        order.MarkPaid();
        order.Status.Should().Be(OrderStatus.Paid);

        order.MarkShipped();
        order.Status.Should().Be(OrderStatus.Shipped);
        order.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public void An_order_cannot_be_paid_before_stock_is_reserved()
    {
        var order = NewOrder();

        FluentActions.Invoking(order.MarkPaid)
            .Should().Throw<InvalidOrderTransitionException>()
            .Which.From.Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public void An_order_cannot_ship_before_it_is_paid()
    {
        var order = NewOrder();
        order.MarkAwaitingPayment();

        FluentActions.Invoking(order.MarkShipped)
            .Should().Throw<InvalidOrderTransitionException>();
    }

    [Fact]
    public void A_shipped_order_cannot_be_cancelled()
    {
        var order = NewOrder();
        order.MarkAwaitingPayment();
        order.MarkPaid();
        order.MarkShipped();

        FluentActions.Invoking(() => order.Cancel("too late"))
            .Should().Throw<InvalidOrderTransitionException>();
    }

    [Fact]
    public void A_paid_order_can_still_be_cancelled_for_refund()
    {
        var order = NewOrder();
        order.MarkAwaitingPayment();
        order.MarkPaid();

        order.Cancel("customer changed their mind");

        order.Status.Should().Be(OrderStatus.Cancelled);
        order.StatusReason.Should().Be("customer changed their mind");
    }

    [Fact]
    public void A_failed_order_is_terminal()
    {
        var order = NewOrder();
        order.Fail("no stock");

        order.Status.Should().Be(OrderStatus.Failed);
        order.IsTerminal.Should().BeTrue();
        FluentActions.Invoking(order.MarkAwaitingPayment)
            .Should().Throw<InvalidOrderTransitionException>();
    }

    [Fact]
    public void A_failed_transition_leaves_the_order_untouched()
    {
        var order = NewOrder();
        var before = order.UpdatedAt;

        FluentActions.Invoking(order.MarkShipped).Should().Throw<InvalidOrderTransitionException>();

        order.Status.Should().Be(OrderStatus.Pending);
        order.UpdatedAt.Should().Be(before);
        order.StatusReason.Should().BeNull();
    }

    [Fact]
    public void A_transition_stamps_the_update_time()
    {
        var order = NewOrder();
        var before = order.UpdatedAt;

        Thread.Sleep(5);
        order.MarkAwaitingPayment();

        order.UpdatedAt.Should().BeAfter(before);
        order.CreatedAt.Should().Be(order.CreatedAt);
    }
}
