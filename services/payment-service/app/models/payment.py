"""The Payment aggregate — one attempt to take money for one order."""

import re
import uuid
from datetime import datetime
from decimal import Decimal

from sqlalchemy import DateTime, Enum, Numeric, String
from sqlalchemy.orm import Mapped, mapped_column

from app.db.base import Base, TimestampMixin
from app.exceptions import IllegalPaymentTransitionError, InvalidPaymentError
from app.models.payment_state import PaymentStatus, can_transition

_CURRENCY = re.compile(r"^[A-Z]{3}$")
_CENT = Decimal("0.01")


def new_id() -> str:
    return str(uuid.uuid4())


class Payment(Base, TimestampMixin):
    __tablename__ = "payments"

    # UUIDs keep ids opaque and safe to publish in events consumed by other services.
    id: Mapped[str] = mapped_column(String(36), primary_key=True, default=new_id)
    order_id: Mapped[str] = mapped_column(String(36), nullable=False, index=True)
    user_id: Mapped[str] = mapped_column(String(36), nullable=False)
    # The unique index is what makes a retried request safe: a second insert with the same
    # key cannot succeed, however the two requests race.
    idempotency_key: Mapped[str] = mapped_column(String(100), nullable=False, unique=True)
    # NUMERIC(12, 2), like the Product and Order services. Never a float.
    amount: Mapped[Decimal] = mapped_column(Numeric(12, 2), nullable=False)
    currency: Mapped[str] = mapped_column(String(3), nullable=False)
    # Stored as text rather than a Postgres enum, so adding a state is a code change and
    # not an ALTER TYPE migration.
    status: Mapped[PaymentStatus] = mapped_column(
        Enum(
            PaymentStatus,
            native_enum=False,
            length=20,
            values_callable=lambda statuses: [status.value for status in statuses],
        ),
        nullable=False,
        default=PaymentStatus.PENDING,
    )
    provider_reference: Mapped[str | None] = mapped_column(String(100), nullable=True)
    failure_reason: Mapped[str | None] = mapped_column(String(100), nullable=True)

    # The eventId of the PaymentProcessed/PaymentFailed this payment announces. Chosen once,
    # in the transaction that sets the final status, so a re-publish after a crash or a
    # failed send carries the same id and downstream deduplication still works.
    outcome_event_id: Mapped[str | None] = mapped_column(String(36), nullable=True)
    # Set once the broker has acknowledged that event. Until then a redelivered OrderCreated
    # re-publishes it rather than being skipped as a duplicate.
    outcome_published_at: Mapped[datetime | None] = mapped_column(
        DateTime(timezone=True), nullable=True
    )

    @property
    def has_outcome(self) -> bool:
        """Captured or Failed: the two states a PaymentProcessed/PaymentFailed announces."""
        return self.status in (PaymentStatus.CAPTURED, PaymentStatus.FAILED)

    @classmethod
    def create(
        cls,
        *,
        idempotency_key: str,
        order_id: str,
        user_id: str,
        amount: Decimal,
        currency: str,
    ) -> "Payment":
        """Build a new ``Pending`` payment, enforcing the rules that make one valid."""
        if amount <= 0:
            raise InvalidPaymentError(f"Amount must be positive, got {amount}")
        if not _CURRENCY.match(currency):
            raise InvalidPaymentError(f"Currency must be an ISO-4217 code, got '{currency}'")
        return cls(
            id=new_id(),
            idempotency_key=idempotency_key,
            order_id=order_id,
            user_id=user_id,
            amount=amount.quantize(_CENT),
            currency=currency,
            status=PaymentStatus.PENDING,
        )

    def authorise(self, provider_reference: str | None) -> None:
        self._transition(PaymentStatus.AUTHORISED)
        self.provider_reference = provider_reference

    def capture(self) -> None:
        self._transition(PaymentStatus.CAPTURED)

    def fail(self, reason: str) -> None:
        self._transition(PaymentStatus.FAILED)
        self.failure_reason = reason

    def refund(self) -> None:
        self._transition(PaymentStatus.REFUNDED)

    def _transition(self, target: PaymentStatus) -> None:
        # Every state change funnels through here, so the table in payment_state is the
        # only place the rules live.
        if not can_transition(self.status, target):
            raise IllegalPaymentTransitionError(self.status.value, target.value)
        self.status = target

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        return f"<Payment id={self.id} order_id={self.order_id} status={self.status.value}>"
