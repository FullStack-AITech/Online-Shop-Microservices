"""Business rules for payments.

Everything that is true of a payment regardless of *how* the request arrived lives here,
so the REST API and the OrderCreated consumer take money the same way: one payment per
idempotency key, recorded as Pending before the provider is called.
"""

from decimal import Decimal

from sqlalchemy.exc import IntegrityError

from app.exceptions import IdempotencyKeyReusedError, PaymentNotFoundError
from app.models.payment import Payment
from app.models.payment_state import PaymentStatus
from app.providers.base import PaymentProvider
from app.repositories.payment import PaymentRepository

MAX_PAGE_SIZE = 100
# Used when a provider declines without saying why; PaymentFailed's reason is required.
UNKNOWN_DECLINE = "declined"


class PaymentService:
    def __init__(self, repository: PaymentRepository, provider: PaymentProvider) -> None:
        self._repository = repository
        self._provider = provider

    def process(
        self,
        *,
        idempotency_key: str,
        order_id: str,
        user_id: str,
        amount: Decimal,
        currency: str,
    ) -> tuple[Payment, bool]:
        """Take a payment once per key. Returns the payment and whether it is new.

        A repeated key returns the existing payment untouched, whatever state it is in:
        the provider is only ever called by the request that created the row.
        """
        payment, created = self.start(
            idempotency_key=idempotency_key,
            order_id=order_id,
            user_id=user_id,
            amount=amount,
            currency=currency,
        )
        if created:
            self.settle(payment)
            self._repository.commit()
        return payment, created

    def start(
        self,
        *,
        idempotency_key: str,
        order_id: str,
        user_id: str,
        amount: Decimal,
        currency: str,
    ) -> tuple[Payment, bool]:
        """Find the payment for this key, or commit a new ``Pending`` one."""
        request = (order_id, user_id, amount, currency)
        existing = self._repository.get_by_idempotency_key(idempotency_key)
        if existing is not None:
            return _same_request(existing, idempotency_key, *request), False

        payment = Payment.create(
            idempotency_key=idempotency_key,
            order_id=order_id,
            user_id=user_id,
            amount=amount,
            currency=currency,
        )
        try:
            self._repository.add(payment)
            # Committed before the provider is called: if we crash mid-charge, the Pending
            # row is the evidence that a charge may be in flight.
            self._repository.commit()
        except IntegrityError:
            # A concurrent request with the same key won the unique constraint. In Postgres
            # the failed INSERT has aborted the transaction, so roll back before reading
            # the winner's row.
            self._repository.rollback()
            winner = self._repository.get_by_idempotency_key(idempotency_key)
            if winner is None:
                raise
            return _same_request(winner, idempotency_key, *request), False
        return payment, True

    def settle(self, payment: Payment) -> Payment:
        """Charge a ``Pending`` payment and apply the answer. Does not commit.

        Leaving the commit to the caller is what lets the event handler write the
        processed-events marker in the same transaction as the final status.
        """
        if payment.status is not PaymentStatus.PENDING:
            return payment
        result = self._provider.charge(
            amount=payment.amount, currency=payment.currency, reference=payment.idempotency_key
        )
        if result.approved:
            # The fake (and most card flows used here) authorise and capture in one call.
            payment.authorise(result.provider_reference)
            payment.capture()
        else:
            payment.fail(result.decline_code or UNKNOWN_DECLINE)
        return payment

    def get(self, payment_id: str) -> Payment:
        payment = self._repository.get(payment_id)
        if payment is None:
            raise PaymentNotFoundError(payment_id)
        return payment

    def list(self, *, order_id: str | None, limit: int, offset: int) -> tuple[list[Payment], int]:
        limit = min(max(limit, 1), MAX_PAGE_SIZE)
        offset = max(offset, 0)
        return (
            self._repository.list(order_id=order_id, limit=limit, offset=offset),
            self._repository.count(order_id=order_id),
        )


def _same_request(
    payment: Payment,
    idempotency_key: str,
    order_id: str,
    user_id: str,
    amount: Decimal,
    currency: str,
) -> Payment:
    """Reject a key reused for a different payment rather than silently returning the old one."""
    # Decimal equality is numeric, so "20" and "20.00" are the same amount.
    if (payment.order_id, payment.user_id, payment.amount, payment.currency) != (
        order_id,
        user_id,
        amount,
        currency,
    ):
        raise IdempotencyKeyReusedError(idempotency_key)
    return payment
