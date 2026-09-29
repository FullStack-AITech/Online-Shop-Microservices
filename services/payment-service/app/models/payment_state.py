"""The single source of truth for which payment transitions are legal.

Kept apart from :class:`~app.models.payment.Payment` so the rules can be read, tested and
reasoned about as a table, like the Order Service's ``OrderStateMachine``. Anything not
listed here is illegal by default — the safe direction for a state machine to fail in.
"""

import enum


class PaymentStatus(str, enum.Enum):
    PENDING = "Pending"
    AUTHORISED = "Authorised"
    CAPTURED = "Captured"
    FAILED = "Failed"
    REFUNDED = "Refunded"


ALLOWED: dict[PaymentStatus, frozenset[PaymentStatus]] = {
    # The provider either approves the charge or declines it.
    PaymentStatus.PENDING: frozenset({PaymentStatus.AUTHORISED, PaymentStatus.FAILED}),
    # An authorisation can still fail to capture (expired, voided by the provider).
    PaymentStatus.AUTHORISED: frozenset({PaymentStatus.CAPTURED, PaymentStatus.FAILED}),
    # Money has moved; the only way back is a refund, which is a new movement of money.
    PaymentStatus.CAPTURED: frozenset({PaymentStatus.REFUNDED}),
    PaymentStatus.FAILED: frozenset(),
    PaymentStatus.REFUNDED: frozenset(),
}


def can_transition(current: PaymentStatus, target: PaymentStatus) -> bool:
    return target in ALLOWED.get(current, frozenset())


def is_terminal(status: PaymentStatus) -> bool:
    return not ALLOWED.get(status)
