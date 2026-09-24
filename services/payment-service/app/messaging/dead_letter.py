"""Retry a failing message a few times, then park it on ``<topic>.dlq``.

A message that always fails would otherwise block its partition forever: the offset can
only be committed past it. After the last attempt the record is copied to the dead letter
topic with its original key, value and headers plus the ``dlq-*`` headers from
docs/events/README.md, and the caller commits the offset and moves on.
"""

import asyncio
import logging
from collections.abc import Awaitable, Callable
from datetime import UTC, datetime
from typing import Any, Protocol

from app.messaging.envelope import to_utc_iso
from app.messaging.errors import NonRetryableError
from app.messaging.publisher import RecordProducer

logger = logging.getLogger(__name__)

MAX_ERROR_LENGTH = 500


class Record(Protocol):
    """The fields of an aiokafka ``ConsumerRecord`` this module reads."""

    topic: str
    partition: int
    offset: int
    key: bytes | None
    value: bytes | None
    headers: Any


async def process_with_retry(
    record: Record,
    handle: Callable[[Record], Awaitable[None]],
    producer: RecordProducer,
    *,
    consumer_name: str,
    max_attempts: int,
    backoff_seconds: float,
    sleep: Callable[[float], Awaitable[None]] = asyncio.sleep,
) -> None:
    """Handle ``record``; returns once its offset may be committed.

    Raises only if the dead letter publish itself fails. The offset is then left
    uncommitted and the record comes back after a restart, which is the safe way round.
    """
    attempt = 0
    while True:
        attempt += 1
        try:
            await handle(record)
            return
        except NonRetryableError as exc:
            error: Exception = exc
            logger.warning("%s at %s: not retryable: %s", record.topic, _where(record), exc)
            break
        except Exception as exc:  # noqa: BLE001 - anything else may be transient
            error = exc
            if attempt >= max_attempts:
                logger.exception("%s at %s failed %d times", record.topic, _where(record), attempt)
                break
            logger.warning(
                "%s at %s failed (attempt %d of %d): %s",
                record.topic,
                _where(record),
                attempt,
                max_attempts,
                exc,
            )
            # Linear backoff: short, because the partition is stuck while we wait.
            await sleep(backoff_seconds * attempt)

    await producer.send_and_wait(
        f"{record.topic}.dlq",
        value=record.value,
        key=record.key,
        headers=list(record.headers or ()) + dead_letter_headers(record, consumer_name, error),
    )
    logger.error("%s at %s dead-lettered to %s.dlq", record.topic, _where(record), record.topic)


def dead_letter_headers(
    record: Record, consumer_name: str, error: Exception, *, failed_at: datetime | None = None
) -> list[tuple[str, bytes]]:
    message = f"{type(error).__name__}: {error}"[:MAX_ERROR_LENGTH]
    values = {
        "dlq-original-topic": record.topic,
        "dlq-original-partition": str(record.partition),
        "dlq-original-offset": str(record.offset),
        # orders has two consumers, so one poison message can be dead-lettered twice.
        "dlq-consumer": consumer_name,
        "dlq-error": message,
        "dlq-failed-at": to_utc_iso(failed_at or datetime.now(UTC)),
    }
    return [(name, value.encode("utf-8")) for name, value in values.items()]


def _where(record: Record) -> str:
    return f"partition {record.partition} offset {record.offset}"
