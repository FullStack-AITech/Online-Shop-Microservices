using FluentAssertions;
using OrderService.Domain.Exceptions;
using OrderService.Domain.Orders;

namespace OrderService.Domain.Tests;

/// <summary>
/// Exhaustive coverage of the transition table: every legal move is allowed, and every
/// other combination is rejected. Enumerating the illegal cases rather than listing a few
/// means a new status cannot be added without deciding what it may do.
/// </summary>
public class OrderStateMachineTests
{
    public static TheoryData<OrderStatus, OrderStatus> LegalTransitions() => new()
    {
        { OrderStatus.Pending, OrderStatus.AwaitingPayment },
        { OrderStatus.Pending, OrderStatus.Cancelled },
        { OrderStatus.Pending, OrderStatus.Failed },
        { OrderStatus.AwaitingPayment, OrderStatus.Paid },
        { OrderStatus.AwaitingPayment, OrderStatus.Cancelled },
        { OrderStatus.AwaitingPayment, OrderStatus.Failed },
        { OrderStatus.Paid, OrderStatus.Shipped },
        { OrderStatus.Paid, OrderStatus.Cancelled }
    };

    [Theory]
    [MemberData(nameof(LegalTransitions))]
    public void Legal_transitions_are_allowed(OrderStatus from, OrderStatus to) =>
        OrderStateMachine.CanTransition(from, to).Should().BeTrue();

    [Fact]
    public void Every_other_transition_is_rejected()
    {
        var legal = LegalTransitions()
            .Select(row => ((OrderStatus)row[0], (OrderStatus)row[1]))
            .ToHashSet();

        var all = Enum.GetValues<OrderStatus>();
        var illegal = from source in all
                      from target in all
                      where !legal.Contains((source, target))
                      select (source, target);

        foreach (var (source, target) in illegal)
        {
            OrderStateMachine.CanTransition(source, target).Should()
                .BeFalse($"{source} -> {target} is not in the transition table");
        }
    }

    [Theory]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Failed)]
    public void Terminal_states_have_no_successors(OrderStatus status)
    {
        OrderStateMachine.IsTerminal(status).Should().BeTrue();
        OrderStateMachine.NextStates(status).Should().BeEmpty();
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.AwaitingPayment)]
    [InlineData(OrderStatus.Paid)]
    public void Non_terminal_states_have_successors(OrderStatus status) =>
        OrderStateMachine.IsTerminal(status).Should().BeFalse();

    [Fact]
    public void A_state_can_never_transition_to_itself()
    {
        foreach (var status in Enum.GetValues<OrderStatus>())
        {
            OrderStateMachine.CanTransition(status, status).Should().BeFalse();
        }
    }
}
