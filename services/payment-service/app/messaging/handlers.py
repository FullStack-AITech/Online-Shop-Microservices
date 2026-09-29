"""Turning an OrderCreated into exactly one charge and one outcome event.

Kafka delivers at least once, so this handler assumes every message may arrive twice —
or arrive again after we crashed halfway through it. Three things make that harmless:

- the idempotency key ``order:<orderId>`` means a redelivery resumes the same payment
  instead of creating a second one;
- the ``processed_events`` marker is written in the transaction that records the final
  status, so "charged" and "handled" can never disagree;
- the outcome's ``eventId`` is stored on the payment, so if the publish has to be repeated
  downstream consumers still see one event.

The database work is synchronous SQLAlchemy, so it runs in a worker thread. Blocking the
event loop would stop aiokafka's heartbeats and trigger a rebalance.
"""

import asyncio
import logging
import uuid
from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime

from pydantic import ValidationError
from sqlalchemy import update
from sqlalchemy.orm import Session

from app.messaging.envelope import EventEnvelope, parse_envelope
from app.messaging.errors import MalformedEventError, UnsupportedEventVersionError
from app.messaging.events import (
    ORDER_CREATED,
    PAYMENT_FAILED,
    PAYMENT_PROCESSED,
    OrderCreatedV1,
    PaymentFailedV1,
    PaymentProcessedV1,
)
from app.messaging.publisher import EventPublisher
from app.models.payment import Payment
from app.models.payment_state import PaymentStatus
from app.providers.base import PaymentProvider
from app.repositories.payment import PaymentRepository
from app.repositories.processed_event import ProcessedEventRepository
from app.services.payment import PaymentService

logger = logging.getLogger(__name__)

CONSUMER_NAME = "payment-service.order-created"
PRODUCER_NAME = "payment-service"
SUPPORTED_ORDER_CREATED_VERSION = 1


def idempotency_key_for(order_id: str) -> str:
    return f"order:{order_id}"


@dataclass(frozen=True)
class _Outcome:
    payment_id: str
    key: str
    envelope: EventEnvelope


class OrderCreatedHandler:
    def __init__(
        self,
        session_factory: Callable[[], Session],
        provider: PaymentProvider,
        publisher: EventPublisher,
        *,
        payments_topic: str,
    ) -> None:
        self._session_factory = session_factory
        self._provider = provider
        self._publisher = publisher
        self._payments_topic = payments_topic

    async def handle(self, envelope: EventEnvelope) -> None:
        if envelope.event_version != SUPPORTED_ORDER_CREATED_VERSION:
            # A known type in an unknown shape is a contract break, not noise.
            raise UnsupportedEventVersionError(envelope.event_type, envelope.event_version)
        try:
            order = OrderCreatedV1.model_validate(envelope.payload)
        except ValidationError as exc:
            raise MalformedEventError(f"OrderCreated payload is invalid: {exc}") from exc

        logger.info(
            "OrderCreated received: order_id=%s correlation_id=%s event_id=%s",
            order.order_id,
            envelope.correlation_id,
            envelope.event_id,
        )
        outcome = await asyncio.to_thread(self._take_payment, envelope, order)
        if outcome is None:
            return
        # After the DB commit: the payment and its marker are durable before anyone hears
        # about them. A crash between the two is covered by the next redelivery, which
        # finds outcome_published_at unset and publishes the same event again.
        await self._publisher.publish(self._payments_topic, outcome.key, outcome.envelope)
        await asyncio.to_thread(self._mark_published, outcome.payment_id)
        logger.info(
            "%s published: order_id=%s payment_id=%s event_id=%s",
            outcome.envelope.event_type,
            order.order_id,
            outcome.payment_id,
            outcome.envelope.event_id,
        )

    def _take_payment(self, envelope: EventEnvelope, order: OrderCreatedV1) -> _Outcome | None:
        with self._session_factory() as session:
            payments = PaymentRepository(session)
            processed = ProcessedEventRepository(session)
            service = PaymentService(payments, self._provider)
            key = idempotency_key_for(order.order_id)

            if processed.exists(envelope.event_id, CONSUMER_NAME):
                payment = payments.get_by_idempotency_key(key)
                if payment is None or not _awaiting_publish(payment):
                    logger.info("duplicate OrderCreated %s skipped", envelope.event_id)
                    return None
                # Handled, but the outcome never reached the broker (a failed send, or a
                # crash after commit). Publish it again under the same eventId.
                logger.info("re-publishing the outcome for order %s", order.order_id)
                return _outcome(payment, envelope, order)

            payment, _ = service.start(
                idempotency_key=key,
                order_id=order.order_id,
                user_id=order.user_id,
                amount=order.total_amount,
                currency=order.currency,
            )
            # A no-op unless the payment is still Pending — which is also how a crash
            # mid-charge resumes: the provider deduplicates on the same reference.
            service.settle(payment)

            if not processed.try_record(envelope.event_id, CONSUMER_NAME):
                # Another instance recorded it between our check and now (a consumer that
                # lost its partition in a rebalance but had not noticed yet).
                payments.rollback()
                logger.info("OrderCreated %s was handled concurrently; skipped", envelope.event_id)
                return None
            if payment.has_outcome and payment.outcome_event_id is None:
                payment.outcome_event_id = str(uuid.uuid4())
            # One commit: final status, outcome event id and the processed marker together.
            payments.commit()

            if not _awaiting_publish(payment):
                return None
            return _outcome(payment, envelope, order)

    def _mark_published(self, payment_id: str) -> None:
        with self._session_factory() as session:
            session.execute(
                update(Payment)
                .where(Payment.id == payment_id, Payment.outcome_published_at.is_(None))
                # Keep updated_at: it is the outcome's occurredAt, and a re-publish must
                # carry the same timestamp as the first.
                .values(outcome_published_at=datetime.now(UTC), updated_at=Payment.updated_at)
            )
            session.commit()


async def dispatch(value: bytes | None, order_created: OrderCreatedHandler) -> None:
    """Route one record from the ``orders`` topic to its handler."""
    envelope = parse_envelope(value)
    if envelope.event_type != ORDER_CREATED:
        # OrderCancelled shares the topic; it is not ours to act on (yet).
        logger.debug("ignoring %s %s", envelope.event_type, envelope.event_id)
        return
    await order_created.handle(envelope)


def _awaiting_publish(payment: Payment) -> bool:
    return (
        payment.has_outcome
        and payment.outcome_event_id is not None
        and payment.outcome_published_at is None
    )


def _outcome(payment: Payment, incoming: EventEnvelope, order: OrderCreatedV1) -> _Outcome:
    # updated_at is when the payment reached its final status: the fact's occurredAt.
    settled_at = payment.updated_at
    common = {
        "payment_id": payment.id,
        "order_id": payment.order_id,
        "user_id": payment.user_id,
        "user_email": order.user_email,
        "amount": payment.amount,
        "currency": payment.currency,
    }
    if payment.status is PaymentStatus.CAPTURED:
        event_type = PAYMENT_PROCESSED
        payload = PaymentProcessedV1(**common, processed_at=settled_at)
    else:
        event_type = PAYMENT_FAILED
        payload = PaymentFailedV1(
            **common, reason=payment.failure_reason or "declined", failed_at=settled_at
        )
    envelope = EventEnvelope(
        event_id=uuid.UUID(payment.outcome_event_id),
        event_type=event_type,
        event_version=1,
        occurred_at=settled_at,
        # Copied from the OrderCreated, so one checkout can be followed across services.
        correlation_id=incoming.correlation_id,
        producer=PRODUCER_NAME,
        payload=payload.model_dump(mode="json", by_alias=True),
    )
    return _Outcome(payment_id=payment.id, key=payment.order_id, envelope=envelope)
