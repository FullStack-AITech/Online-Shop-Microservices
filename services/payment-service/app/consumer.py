"""The OrderCreated consumer, run as its own process: ``python -m app.consumer``.

Same image as the API, separate container, so a stuck broker never takes the API down
and the two scale independently. This module is the edge: it is the only place that
builds aiokafka clients. Everything it calls takes plain objects, so the handler, retry
and dead letter logic are unit tested without a broker.

Delivery is at least once. Auto-commit is off and an offset is committed only after the
handler's database transaction has committed (and its event has been published); a crash
in between redelivers the message, and the handler is built to take that.
"""

import asyncio
import logging
import signal
from collections.abc import Awaitable, Callable
from functools import partial
from typing import Any, Protocol

from aiokafka import AIOKafkaConsumer, TopicPartition
from aiokafka.errors import KafkaConnectionError
from sqlalchemy.orm import Session

from app.core.config import Settings, get_settings
from app.core.logging import configure_logging
from app.db.session import SessionLocal
from app.messaging.dead_letter import Record, process_with_retry
from app.messaging.handlers import CONSUMER_NAME, OrderCreatedHandler, dispatch
from app.messaging.publisher import KafkaEventPublisher, create_producer
from app.providers import build_provider
from app.repositories.processed_event import ProcessedEventRepository

logger = logging.getLogger("app.consumer")

POLL_TIMEOUT_MS = 1000


class RecordConsumer(Protocol):
    """The slice of ``AIOKafkaConsumer`` the loop uses."""

    async def getmany(
        self, *partitions: TopicPartition, timeout_ms: int = 0, max_records: int | None = None
    ) -> dict[TopicPartition, list[Any]]: ...

    async def commit(self, offsets: dict[TopicPartition, int] | None = None) -> None: ...


async def consume(
    consumer: RecordConsumer,
    process: Callable[[Record], Awaitable[None]],
    stop: asyncio.Event,
) -> None:
    """Process records one at a time, committing each offset only after it was handled."""
    while not stop.is_set():
        # getmany with a timeout rather than ``async for``, so a SIGTERM is noticed within
        # a second even when the topic is idle.
        batches = await consumer.getmany(timeout_ms=POLL_TIMEOUT_MS)
        for partition, records in batches.items():
            for record in records:
                await process(record)
                # The committed offset is the *next* one to read.
                await consumer.commit({partition: record.offset + 1})
                if stop.is_set():
                    # Anything fetched but not handled is simply read again next time.
                    return


async def prune_periodically(
    session_factory: Callable[[], Session], settings: Settings, stop: asyncio.Event
) -> None:
    """Delete old processed_events rows now, then every interval, until stopped."""
    while not stop.is_set():
        try:
            deleted = await asyncio.to_thread(
                _prune, session_factory, settings.processed_events_retention_days
            )
            logger.info(
                "pruned %d processed_events older than %d days",
                deleted,
                settings.processed_events_retention_days,
            )
        except Exception:  # noqa: BLE001 - a failed prune must not stop the consumer
            logger.exception("pruning processed_events failed; will retry next interval")
        try:
            await asyncio.wait_for(
                stop.wait(), timeout=settings.processed_events_prune_interval_seconds
            )
        except TimeoutError:
            pass


def _prune(session_factory: Callable[[], Session], older_than_days: int) -> int:
    with session_factory() as session:
        return ProcessedEventRepository(session).prune(older_than_days=older_than_days)


def _install_signal_handlers(stop: asyncio.Event) -> None:
    loop = asyncio.get_running_loop()
    for sig in (signal.SIGTERM, signal.SIGINT):
        try:
            loop.add_signal_handler(sig, stop.set)
        except NotImplementedError:
            # Windows' event loop has no add_signal_handler; fall back to the plain hook.
            signal.signal(sig, lambda *_: loop.call_soon_threadsafe(stop.set))


async def run(settings: Settings) -> None:
    stop = asyncio.Event()
    _install_signal_handlers(stop)

    consumer = AIOKafkaConsumer(
        settings.kafka_orders_topic,
        bootstrap_servers=settings.kafka_bootstrap_servers,
        group_id=settings.kafka_group_id,
        client_id=settings.service_name,
        enable_auto_commit=False,
        auto_offset_reset="earliest",
    )
    producer = create_producer(settings)
    handler = OrderCreatedHandler(
        SessionLocal,
        build_provider(settings),
        KafkaEventPublisher(producer),
        payments_topic=settings.kafka_payments_topic,
    )
    process = partial(
        process_with_retry,
        handle=lambda record: dispatch(record.value, handler),
        producer=producer,
        consumer_name=CONSUMER_NAME,
        max_attempts=settings.kafka_max_attempts,
        backoff_seconds=settings.kafka_retry_backoff_seconds,
    )

    pruner: asyncio.Task[None] | None = None
    try:
        await producer.start()
        await consumer.start()
        pruner = asyncio.create_task(prune_periodically(SessionLocal, settings, stop))
        logger.info(
            "consuming %s as group %s from %s",
            settings.kafka_orders_topic,
            settings.kafka_group_id,
            settings.kafka_bootstrap_servers,
        )
        await consume(consumer, process, stop)
    finally:
        stop.set()
        if pruner is not None:
            await pruner
        # Leaves the group cleanly, so the partitions move to another instance now rather
        # than after the session timeout. Both are safe to stop even if start() failed.
        await consumer.stop()
        await producer.stop()
    logger.info("consumer stopped")


def main() -> None:
    configure_logging()
    try:
        asyncio.run(run(get_settings()))
    except KafkaConnectionError as exc:
        # Exit non-zero so the container's restart policy tries again once Kafka is up.
        logger.error("cannot reach Kafka: %s", exc)
        raise SystemExit(1) from exc


if __name__ == "__main__":
    main()
