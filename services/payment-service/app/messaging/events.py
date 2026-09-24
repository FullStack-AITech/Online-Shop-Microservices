"""Payloads this service consumes and produces.

``OrderCreatedV1`` holds only the fields this service uses; the Order Service may add
more without telling us, and extra fields are ignored. The outcome events are built to
docs/events/schemas/payment-{processed,failed}.v1.schema.json.
"""

from datetime import datetime
from decimal import Decimal

from pydantic import Field, field_serializer

from app.messaging.envelope import CamelModel, to_utc_iso

ORDER_CREATED = "OrderCreated"
PAYMENT_PROCESSED = "PaymentProcessed"
PAYMENT_FAILED = "PaymentFailed"


class OrderCreatedV1(CamelModel):
    order_id: str = Field(min_length=1, max_length=36)
    user_id: str = Field(min_length=1, max_length=36)
    user_email: str
    # Arrives as a decimal string ("99.98"); Decimal parses it losslessly.
    total_amount: Decimal
    currency: str


class _PaymentOutcome(CamelModel):
    payment_id: str
    order_id: str
    user_id: str
    user_email: str
    amount: Decimal
    currency: str

    @field_serializer("amount")
    def _money(self, value: Decimal) -> str:
        # Money travels as a string with exactly two places, never a JSON number.
        return f"{value:.2f}"


class PaymentProcessedV1(_PaymentOutcome):
    processed_at: datetime

    @field_serializer("processed_at")
    def _utc(self, value: datetime) -> str:
        return to_utc_iso(value)


class PaymentFailedV1(_PaymentOutcome):
    reason: str
    failed_at: datetime

    @field_serializer("failed_at")
    def _utc(self, value: datetime) -> str:
        return to_utc_iso(value)
