"""OrderCreated -> one charge and one PaymentProcessed/PaymentFailed, however often it arrives.

The handler is driven with a fake provider and a fake publisher: no broker involved.
"""

import asyncio
import json
from decimal import Decimal

import pytest
from sqlalchemy import func, select

from app.messaging.errors import MalformedEventError, UnsupportedEventVersionError
from app.messaging.handlers import CONSUMER_NAME, OrderCreatedHandler, dispatch
from app.models.payment import Payment
from app.models.payment_state import PaymentStatus
from app.models.processed_event import ProcessedEvent
from app.repositories.payment import PaymentRepository
from tests.fakes import CORRELATION_ID, ORDER_ID, FakePublisher, order_created


@pytest.fixture
def publisher() -> FakePublisher:
    return FakePublisher()


@pytest.fixture
def handler(session_factory, provider, publisher) -> OrderCreatedHandler:
    return OrderCreatedHandler(session_factory, provider, publisher, payments_topic="payments")


def deliver(handler: OrderCreatedHandler, message: dict) -> None:
    asyncio.run(dispatch(json.dumps(message).encode("utf-8"), handler))


def payments(session_factory) -> list[Payment]:
    with session_factory() as session:
        return list(session.execute(select(Payment)).scalars())


def test_an_approved_charge_captures_and_emits_payment_processed(
    handler, publisher, provider, session_factory
):
    deliver(handler, order_created())

    [payment] = payments(session_factory)
    assert payment.status is PaymentStatus.CAPTURED
    assert payment.idempotency_key == f"order:{ORDER_ID}"
    assert provider.calls[0]["reference"] == f"order:{ORDER_ID}"

    [(topic, key, event)] = publisher.published
    assert (topic, key) == ("payments", ORDER_ID)
    assert event.event_type == "PaymentProcessed"
    assert event.producer == "payment-service"
    assert str(event.event_id) == payment.outcome_event_id
    body = json.loads(event.to_bytes())
    assert body["payload"]["amount"] == "99.98"
    assert body["payload"]["paymentId"] == payment.id
    assert body["payload"]["userEmail"] == "a@b.com"
    assert body["occurredAt"].endswith("Z")
    assert payment.outcome_published_at is not None


def test_an_amount_over_the_threshold_fails_and_emits_payment_failed(
    handler, publisher, session_factory
):
    deliver(handler, order_created(total="1199.98"))

    [payment] = payments(session_factory)
    assert payment.status is PaymentStatus.FAILED
    assert payment.failure_reason == "card_declined"
    [(_, _, event)] = publisher.published
    assert event.event_type == "PaymentFailed"
    assert event.payload["reason"] == "card_declined"
    assert event.payload["amount"] == "1199.98"


def test_the_same_envelope_twice_charges_once_and_emits_once(
    handler, publisher, provider, session_factory
):
    message = order_created()
    deliver(handler, message)
    deliver(handler, message)

    assert len(provider.calls) == 1
    assert len(publisher.published) == 1
    assert len(payments(session_factory)) == 1


def test_a_second_event_for_the_same_order_does_not_charge_again(
    handler, publisher, provider, session_factory
):
    # A new eventId for the same order (e.g. a republished-with-new-id bug upstream):
    # the idempotency key still holds it to one payment and one outcome.
    deliver(handler, order_created())
    deliver(handler, order_created())

    assert len(provider.calls) == 1
    assert len(publisher.published) == 1
    assert len(payments(session_factory)) == 1


def test_the_correlation_id_is_copied_onto_the_outcome(handler, publisher):
    deliver(handler, order_created())
    [(_, _, event)] = publisher.published
    assert event.correlation_id == CORRELATION_ID


def test_the_marker_is_committed_with_the_final_status(handler, session_factory):
    message = order_created()
    deliver(handler, message)
    with session_factory() as session:
        marker = session.execute(select(ProcessedEvent)).scalar_one()
        assert str(marker.event_id) == message["eventId"]
        assert marker.consumer == CONSUMER_NAME


def test_a_failed_publish_is_retried_with_the_same_event_id(session_factory, provider):
    publisher = FakePublisher(failures=1)
    handler = OrderCreatedHandler(session_factory, provider, publisher, payments_topic="payments")
    message = order_created()

    with pytest.raises(ConnectionError):
        deliver(handler, message)
    # The payment and its marker committed before the send failed...
    [payment] = payments(session_factory)
    assert payment.status is PaymentStatus.CAPTURED
    assert payment.outcome_published_at is None

    # ...so the retry does not charge again, and re-publishes the stored eventId.
    deliver(handler, message)
    assert len(provider.calls) == 1
    [(_, _, event)] = publisher.published
    assert str(event.event_id) == payment.outcome_event_id
    [payment] = payments(session_factory)
    assert payment.outcome_published_at is not None


def test_a_payment_left_pending_by_a_crash_is_resumed(
    handler, publisher, provider, session_factory
):
    # A crash after the Pending commit but before the charge result was saved.
    with session_factory() as session:
        repo = PaymentRepository(session)
        pending = Payment.create(
            idempotency_key=f"order:{ORDER_ID}",
            order_id=ORDER_ID,
            user_id="9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d",
            amount=Decimal("99.98"),
            currency="GBP",
        )
        repo.add(pending)
        repo.commit()

    deliver(handler, order_created())
    [payment] = payments(session_factory)
    assert payment.id == pending.id
    assert payment.status is PaymentStatus.CAPTURED
    assert len(publisher.published) == 1


def test_an_unknown_version_is_rejected(handler, provider):
    with pytest.raises(UnsupportedEventVersionError):
        deliver(handler, order_created(version=2))
    assert provider.calls == []


@pytest.mark.parametrize(
    "value", [b"not-json", b"{}", b'{"eventType": "OrderCreated"}', None], ids=repr
)
def test_garbage_is_malformed(handler, value):
    with pytest.raises(MalformedEventError):
        asyncio.run(dispatch(value, handler))


def test_a_payload_missing_fields_is_malformed(handler):
    message = order_created()
    del message["payload"]["totalAmount"]
    with pytest.raises(MalformedEventError):
        deliver(handler, message)


def test_other_event_types_on_the_topic_are_ignored(handler, publisher, session_factory):
    message = order_created()
    message["eventType"] = "OrderCancelled"
    message["eventVersion"] = 7  # an unknown type is ignored whatever its version
    deliver(handler, message)
    assert publisher.published == []
    with session_factory() as session:
        assert session.execute(select(func.count()).select_from(Payment)).scalar_one() == 0
