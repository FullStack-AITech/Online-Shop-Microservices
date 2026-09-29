"""Data access for processed_events — the consumer's memory of what it has handled."""

from datetime import UTC, datetime, timedelta
from uuid import UUID

from sqlalchemy import delete, select
from sqlalchemy.dialects.postgresql import insert as pg_insert
from sqlalchemy.dialects.sqlite import insert as sqlite_insert
from sqlalchemy.orm import Session

from app.models.processed_event import ProcessedEvent


class ProcessedEventRepository:
    def __init__(self, session: Session) -> None:
        self._session = session

    def try_record(self, event_id: UUID, consumer: str) -> bool:
        """Record ``event_id`` in the caller's transaction. ``False`` means: already processed.

        Never commits: the marker is only worth anything if it commits or rolls back with
        the business change it guards. ON CONFLICT DO NOTHING rather than catching the
        IntegrityError, because in Postgres a failed INSERT aborts the whole transaction.
        """
        dialect = self._session.get_bind().dialect.name
        insert = pg_insert if dialect == "postgresql" else sqlite_insert
        stmt = (
            insert(ProcessedEvent)
            .values(event_id=event_id, consumer=consumer)
            .on_conflict_do_nothing(index_elements=["event_id", "consumer"])
        )
        return self._session.execute(stmt).rowcount == 1

    def exists(self, event_id: UUID, consumer: str) -> bool:
        """A cheap early exit only. ``try_record`` is the guard that closes the race."""
        stmt = select(ProcessedEvent.event_id).where(
            ProcessedEvent.event_id == event_id, ProcessedEvent.consumer == consumer
        )
        return self._session.execute(stmt).first() is not None

    def prune(self, *, older_than_days: int, now: datetime | None = None) -> int:
        """Delete markers older than the cut-off and commit. Returns how many went."""
        cutoff = (now or datetime.now(UTC)) - timedelta(days=older_than_days)
        result = self._session.execute(
            delete(ProcessedEvent).where(ProcessedEvent.processed_at < cutoff)
        )
        self._session.commit()
        return result.rowcount
