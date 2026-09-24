"""Publishing events to Kafka.

Handlers depend on the ``EventPublisher`` protocol, not on aiokafka, so they can be unit
tested with a fake that just records what it was given.
"""

from typing import Protocol

from aiokafka import AIOKafkaProducer

from app.core.config import Settings
from app.messaging.envelope import EventEnvelope


class EventPublisher(Protocol):
    async def publish(self, topic: str, key: str, envelope: EventEnvelope) -> None: ...


class RecordProducer(Protocol):
    """The slice of ``AIOKafkaProducer`` this service uses; a fake only needs this."""

    async def send_and_wait(
        self,
        topic: str,
        value: bytes | None = None,
        key: bytes | None = None,
        partition: int | None = None,
        timestamp_ms: int | None = None,
        headers: list[tuple[str, bytes]] | None = None,
    ) -> object: ...


class KafkaEventPublisher:
    def __init__(self, producer: RecordProducer) -> None:
        self._producer = producer

    async def publish(self, topic: str, key: str, envelope: EventEnvelope) -> None:
        # The key is the order id, so every event about one order lands on one partition
        # and stays in order. The headers let tooling and the DLQ route without parsing.
        await self._producer.send_and_wait(
            topic,
            value=envelope.to_bytes(),
            key=key.encode("utf-8"),
            headers=[
                ("eventType", envelope.event_type.encode("utf-8")),
                ("correlationId", envelope.correlation_id.encode("utf-8")),
            ],
        )


def create_producer(settings: Settings) -> AIOKafkaProducer:
    # acks="all" waits for every in-sync replica; idempotence stops the producer's own
    # retries from writing a message twice. Neither covers a crash before the send — the
    # stored outcome_event_id and redelivery do.
    return AIOKafkaProducer(
        bootstrap_servers=settings.kafka_bootstrap_servers,
        client_id=settings.service_name,
        acks="all",
        enable_idempotence=True,
    )
