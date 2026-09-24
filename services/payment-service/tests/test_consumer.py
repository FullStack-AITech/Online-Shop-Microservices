"""The consumer's edge: retries, the dead letter topic, offset commits and pruning.

Fakes stand in for aiokafka's consumer and producer, so this runs without a broker.
"""

import asyncio
import json
import uuid
from dataclasses import dataclass, field
from datetime import UTC, datetime, timedelta

import pytest
from aiokafka import TopicPartition
from sqlalchemy import func, select

from app.consumer import consume, prune_periodically
from app.core.config import Settings
from app.messaging.dead_letter import MAX_ERROR_LENGTH, process_with_retry
from app.messaging.errors import MalformedEventError
from app.messaging.handlers import CONSUMER_NAME, OrderCreatedHandler, dispatch
from app.models.processed_event import ProcessedEvent
from tests.fakes import FakePublisher, order_created


@dataclass
class FakeRecord:
    value: bytes | None
    key: bytes | None = b"order-1"
    topic: str = "orders"
    partition: int = 2
    offset: int = 41
    headers: tuple = (("eventType", b"OrderCreated"), ("correlationId", b"c-1"))


@dataclass
class FakeProducer:
    sent: list[dict] = field(default_factory=list)

    async def send_and_wait(
        self, topic, value=None, key=None, partition=None, timestamp_ms=None, headers=None
    ):
        self.sent.append({"topic": topic, "value": value, "key": key, "headers": headers})


class FakeConsumer:
    """Hands out one batch, then nothing; records every commit."""

    def __init__(self, batch: dict, stop: asyncio.Event) -> None:
        self._batches = [batch]
        self._stop = stop
        self.commits: list[dict] = []

    async def getmany(self, *partitions, timeout_ms=0, max_records=None):
        if self._batches:
            return self._batches.pop(0)
        self._stop.set()
        return {}

    async def commit(self, offsets=None):
        self.commits.append(offsets)


async def _no_sleep(seconds: float) -> None:
    _no_sleep.calls.append(seconds)


_no_sleep.calls = []


@pytest.fixture(autouse=True)
def _reset_sleep():
    _no_sleep.calls = []


def _retry(record, handle, producer, **overrides):
    options = {
        "consumer_name": CONSUMER_NAME,
        "max_attempts": 3,
        "backoff_seconds": 0.5,
        "sleep": _no_sleep,
        **overrides,
    }
    return asyncio.run(process_with_retry(record, handle, producer, **options))


def test_a_garbage_record_is_dead_lettered_at_once_with_dlq_headers():
    producer = FakeProducer()
    calls = []

    async def handle(record):
        calls.append(record)
        raise MalformedEventError("Not a valid event envelope")

    record = FakeRecord(value=b"not-json")
    _retry(record, handle, producer)

    assert len(calls) == 1, "malformed messages are not retried"
    assert _no_sleep.calls == []
    [sent] = producer.sent
    assert sent["topic"] == "orders.dlq"
    assert (sent["key"], sent["value"]) == (b"order-1", b"not-json")
    headers = dict(sent["headers"])
    # The original headers are kept...
    assert headers["eventType"] == b"OrderCreated"
    assert headers["correlationId"] == b"c-1"
    # ...and the dlq-* ones added.
    assert headers["dlq-original-topic"] == b"orders"
    assert headers["dlq-original-partition"] == b"2"
    assert headers["dlq-original-offset"] == b"41"
    assert headers["dlq-consumer"] == b"payment-service.order-created"
    assert headers["dlq-error"].startswith(b"MalformedEventError: ")
    assert headers["dlq-failed-at"].endswith(b"Z")


def test_a_transient_failure_is_retried_and_then_succeeds():
    producer = FakeProducer()
    attempts = []

    async def handle(record):
        attempts.append(record)
        if len(attempts) < 3:
            raise ConnectionError("database restarting")

    _retry(FakeRecord(value=b"{}"), handle, producer)
    assert len(attempts) == 3
    assert _no_sleep.calls == [0.5, 1.0]
    assert producer.sent == []


def test_a_persistent_failure_is_dead_lettered_after_three_attempts():
    producer = FakeProducer()
    attempts = []

    async def handle(record):
        attempts.append(record)
        raise RuntimeError("x" * 2000)

    _retry(FakeRecord(value=b"{}"), handle, producer)
    assert len(attempts) == 3
    [sent] = producer.sent
    assert len(dict(sent["headers"])["dlq-error"]) == MAX_ERROR_LENGTH


def test_a_failing_dead_letter_publish_propagates_so_the_offset_is_not_committed():
    class BrokenProducer:
        async def send_and_wait(self, *args, **kwargs):
            raise ConnectionError("broker down")

    async def handle(record):
        raise MalformedEventError("bad")

    with pytest.raises(ConnectionError):
        _retry(FakeRecord(value=b"x"), handle, BrokenProducer())


def test_the_loop_commits_each_offset_after_processing_it():
    stop = asyncio.Event()
    partition = TopicPartition("orders", 0)
    records = [
        FakeRecord(value=b"a", partition=0, offset=5),
        FakeRecord(value=b"b", partition=0, offset=6),
    ]
    consumer = FakeConsumer({partition: records}, stop)
    order: list[str] = []

    async def process(record):
        # Nothing may be committed for a record until it has been processed.
        order.append(f"process {record.offset} after {len(consumer.commits)} commits")

    asyncio.run(consume(consumer, process, stop))
    assert order == ["process 5 after 0 commits", "process 6 after 1 commits"]
    assert consumer.commits == [{partition: 6}, {partition: 7}]


def test_garbage_on_the_topic_is_dead_lettered_committed_and_the_next_order_processed(
    session_factory, provider
):
    """The partition is not blocked: poison, then a valid order, both get past."""
    stop = asyncio.Event()
    partition = TopicPartition("orders", 0)
    poison = FakeRecord(value=b"not-json", partition=0, offset=0)
    valid = FakeRecord(value=json.dumps(order_created()).encode(), partition=0, offset=1)
    consumer = FakeConsumer({partition: [poison, valid]}, stop)
    producer = FakeProducer()
    publisher = FakePublisher()
    handler = OrderCreatedHandler(session_factory, provider, publisher, payments_topic="payments")

    async def process(record):
        await process_with_retry(
            record,
            lambda r: dispatch(r.value, handler),
            producer,
            consumer_name=CONSUMER_NAME,
            max_attempts=3,
            backoff_seconds=0,
            sleep=_no_sleep,
        )

    asyncio.run(consume(consumer, process, stop))

    assert [sent["topic"] for sent in producer.sent] == ["orders.dlq"]
    assert consumer.commits == [{partition: 1}, {partition: 2}]
    assert [event.event_type for _, _, event in publisher.published] == ["PaymentProcessed"]


def test_the_pruner_deletes_old_markers_and_stops_when_asked(session_factory):
    now = datetime.now(UTC)
    with session_factory() as session:
        session.add_all(
            [
                ProcessedEvent(
                    event_id=uuid.uuid4(),
                    consumer=CONSUMER_NAME,
                    processed_at=now - timedelta(days=30),
                ),
                ProcessedEvent(event_id=uuid.uuid4(), consumer=CONSUMER_NAME, processed_at=now),
            ]
        )
        session.commit()

    async def run():
        stop = asyncio.Event()
        settings = Settings(processed_events_prune_interval_seconds=3600)
        task = asyncio.create_task(prune_periodically(session_factory, settings, stop))
        # One pass happens straight away; then it waits out the hour, until stopped.
        for _ in range(100):
            await asyncio.sleep(0.01)
            with session_factory() as session:
                count = session.execute(
                    select(func.count()).select_from(ProcessedEvent)
                ).scalar_one()
            if count == 1:
                break
        stop.set()
        await asyncio.wait_for(task, timeout=1)
        return count

    assert asyncio.run(run()) == 1
