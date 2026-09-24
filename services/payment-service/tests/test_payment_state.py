"""The payment state machine, checked as a full from x to matrix."""

from decimal import Decimal

import pytest

from app.exceptions import IllegalPaymentTransitionError, InvalidPaymentError
from app.models.payment import Payment
from app.models.payment_state import ALLOWED, PaymentStatus, can_transition, is_terminal

LEGAL = {
    (PaymentStatus.PENDING, PaymentStatus.AUTHORISED),
    (PaymentStatus.PENDING, PaymentStatus.FAILED),
    (PaymentStatus.AUTHORISED, PaymentStatus.CAPTURED),
    (PaymentStatus.AUTHORISED, PaymentStatus.FAILED),
    (PaymentStatus.CAPTURED, PaymentStatus.REFUNDED),
}

MATRIX = [(current, target) for current in PaymentStatus for target in PaymentStatus]


def _payment_in(status: PaymentStatus) -> Payment:
    payment = Payment.create(
        idempotency_key="k-1", order_id="o-1", user_id="u-1", amount=Decimal("20"), currency="GBP"
    )
    payment.status = status
    return payment


def _move(payment: Payment, target: PaymentStatus) -> None:
    # Drive the transition through the public method for that target, as callers do.
    {
        PaymentStatus.PENDING: lambda: payment._transition(PaymentStatus.PENDING),
        PaymentStatus.AUTHORISED: lambda: payment.authorise("ref-1"),
        PaymentStatus.CAPTURED: payment.capture,
        PaymentStatus.FAILED: lambda: payment.fail("card_declined"),
        PaymentStatus.REFUNDED: payment.refund,
    }[target]()


@pytest.mark.parametrize(("current", "target"), MATRIX, ids=lambda s: s.value)
def test_transition_matrix(current, target):
    payment = _payment_in(current)
    if (current, target) in LEGAL:
        _move(payment, target)
        assert payment.status is target
    else:
        with pytest.raises(IllegalPaymentTransitionError):
            _move(payment, target)
        assert payment.status is current


def test_the_table_is_exactly_the_legal_set():
    table = {(current, target) for current, targets in ALLOWED.items() for target in targets}
    assert table == LEGAL
    assert set(ALLOWED) == set(PaymentStatus)
    assert all(can_transition(c, t) == ((c, t) in LEGAL) for c, t in MATRIX)


@pytest.mark.parametrize("status", [PaymentStatus.FAILED, PaymentStatus.REFUNDED])
def test_terminal_states_have_no_exits(status):
    assert is_terminal(status)
    assert ALLOWED[status] == frozenset()


@pytest.mark.parametrize(
    "status", [PaymentStatus.PENDING, PaymentStatus.AUTHORISED, PaymentStatus.CAPTURED]
)
def test_non_terminal_states_have_an_exit(status):
    assert not is_terminal(status)


def test_authorise_records_the_provider_reference_and_fail_the_reason():
    payment = _payment_in(PaymentStatus.PENDING)
    payment.authorise("fake_abc")
    assert payment.provider_reference == "fake_abc"

    declined = _payment_in(PaymentStatus.PENDING)
    declined.fail("card_declined")
    assert declined.failure_reason == "card_declined"


def test_create_starts_pending_and_quantises_the_amount():
    payment = Payment.create(
        idempotency_key="k", order_id="o", user_id="u", amount=Decimal("20"), currency="GBP"
    )
    assert payment.status is PaymentStatus.PENDING
    assert payment.amount == Decimal("20.00")
    assert str(payment.amount) == "20.00"
    assert payment.id


@pytest.mark.parametrize("amount", [Decimal("0"), Decimal("-1.00")])
def test_create_rejects_a_non_positive_amount(amount):
    with pytest.raises(InvalidPaymentError):
        Payment.create(
            idempotency_key="k", order_id="o", user_id="u", amount=amount, currency="GBP"
        )


@pytest.mark.parametrize("currency", ["gbp", "GB", "GBPX", "12A"])
def test_create_rejects_a_bad_currency(currency):
    with pytest.raises(InvalidPaymentError):
        Payment.create(
            idempotency_key="k", order_id="o", user_id="u", amount=Decimal("1"), currency=currency
        )
