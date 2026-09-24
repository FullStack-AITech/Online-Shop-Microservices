"""Request and response contracts for the Payments API.

These are deliberately separate from the ORM model: the wire contract is a public
interface other services depend on, while the table is a private implementation detail.
Money is a ``Decimal`` end to end, and serialises as a string such as ``"20.00"``.
"""

from datetime import UTC, datetime
from decimal import Decimal
from typing import Annotated

from pydantic import BaseModel, ConfigDict, Field, field_validator

from app.models.payment_state import PaymentStatus

Money = Annotated[Decimal, Field(max_digits=12, decimal_places=2, gt=0)]


class PaymentCreate(BaseModel):
    order_id: str = Field(min_length=1, max_length=36)
    user_id: str = Field(min_length=1, max_length=36)
    amount: Money
    currency: str = Field(pattern=r"^[A-Z]{3}$")


class PaymentRead(BaseModel):
    model_config = ConfigDict(from_attributes=True)

    id: str
    order_id: str
    user_id: str
    idempotency_key: str
    amount: Decimal
    currency: str
    status: PaymentStatus
    provider_reference: str | None
    failure_reason: str | None
    created_at: datetime
    updated_at: datetime

    @field_validator("created_at", "updated_at")
    @classmethod
    def _as_utc(cls, value: datetime) -> datetime:
        # Stored in UTC, but SQLite hands them back without an offset. Say so explicitly,
        # so a replayed response is identical to the first one on any database.
        return value if value.tzinfo is not None else value.replace(tzinfo=UTC)


class PaymentPage(BaseModel):
    """Offset-paginated list response."""

    items: list[PaymentRead]
    total: int
    limit: int
    offset: int
