"""PaymentService rules that HTTP tests cannot reach, such as losing an insert race."""

from decimal import Decimal

import pytest

from app.exceptions import IdempotencyKeyReusedError
from app.models.payment import Payment
from app.models.payment_state import PaymentStatus
from app.repositories.payment import PaymentRepository
from app.services.payment import PaymentService

REQUEST = {"order_id": "o-1", "user_id": "u-1", "amount": Decimal("20.00"), "currency": "GBP"}


class _RacingRepository(PaymentRepository):
    """Misses the key on the first look, as if the other request had not committed yet,
    and lets a second session commit the winning row just before our insert."""

    def __init__(self, session, other_session) -> None:
        super().__init__(session)
        self._other = other_session
        self._looked = False

    def get_by_idempotency_key(self, idempotency_key):
        if not self._looked:
            self._looked = True
            winner = Payment.create(idempotency_key=idempotency_key, **REQUEST)
            winner.status = PaymentStatus.CAPTURED
            self._other.add(winner)
            self._other.commit()
            return None
        return super().get_by_idempotency_key(idempotency_key)


def test_losing_the_insert_race_returns_the_winner_without_charging(session_factory, provider):
    with session_factory() as session, session_factory() as other:
        service = PaymentService(_RacingRepository(session, other), provider)
        payment, created = service.process(idempotency_key="k-1", **REQUEST)

    assert created is False
    assert payment.status is PaymentStatus.CAPTURED
    assert provider.calls == []
    with session_factory() as session:
        assert PaymentRepository(session).count(order_id="o-1") == 1


def test_losing_the_race_to_a_different_request_is_still_a_conflict(session_factory, provider):
    with session_factory() as session, session_factory() as other:
        service = PaymentService(_RacingRepository(session, other), provider)
        with pytest.raises(IdempotencyKeyReusedError):
            service.process(idempotency_key="k-1", **{**REQUEST, "amount": Decimal("99.00")})


def test_pending_is_committed_before_the_provider_is_called(session_factory):
    seen: list[str] = []

    class _PeekingProvider:
        def charge(self, *, amount, currency, reference):
            # A separate session only sees committed rows.
            with session_factory() as peek:
                seen.append(PaymentRepository(peek).get_by_idempotency_key(reference).status)
            from app.providers.base import ProviderResult

            return ProviderResult(approved=True, provider_reference="ref")

    with session_factory() as session:
        PaymentService(PaymentRepository(session), _PeekingProvider()).process(
            idempotency_key="k-1", **REQUEST
        )
    assert seen == [PaymentStatus.PENDING]


def test_a_repeated_key_returns_a_pending_payment_untouched(session_factory, provider):
    with session_factory() as session:
        service = PaymentService(PaymentRepository(session), provider)
        service.start(idempotency_key="k-1", **REQUEST)
        payment, created = service.process(idempotency_key="k-1", **REQUEST)
    assert created is False
    assert payment.status is PaymentStatus.PENDING
    assert provider.calls == []
