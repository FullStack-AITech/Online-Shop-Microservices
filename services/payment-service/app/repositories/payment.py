"""Data access for the Payment aggregate.

The repository is the only layer that talks SQL. Unlike the User Service's, it does not
commit on every write: the payment flow needs to put a status change and a
processed-events marker into one transaction, so the caller decides when to commit.
"""

from sqlalchemy import func, select
from sqlalchemy.orm import Session

from app.models.payment import Payment


class PaymentRepository:
    def __init__(self, session: Session) -> None:
        self._session = session

    def add(self, payment: Payment) -> None:
        self._session.add(payment)
        # Flush now so a duplicate idempotency key fails here, where the caller expects it.
        self._session.flush()

    def get(self, payment_id: str) -> Payment | None:
        return self._session.get(Payment, payment_id)

    def get_by_idempotency_key(self, idempotency_key: str) -> Payment | None:
        stmt = select(Payment).where(Payment.idempotency_key == idempotency_key)
        return self._session.execute(stmt).scalar_one_or_none()

    def list(self, *, order_id: str | None, limit: int, offset: int) -> list[Payment]:
        stmt = select(Payment).order_by(Payment.created_at.desc()).limit(limit).offset(offset)
        if order_id is not None:
            stmt = stmt.where(Payment.order_id == order_id)
        return list(self._session.execute(stmt).scalars())

    def count(self, *, order_id: str | None) -> int:
        stmt = select(func.count()).select_from(Payment)
        if order_id is not None:
            stmt = stmt.where(Payment.order_id == order_id)
        return int(self._session.execute(stmt).scalar_one())

    def commit(self) -> None:
        self._session.commit()

    def rollback(self) -> None:
        self._session.rollback()
