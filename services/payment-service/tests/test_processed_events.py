"""processed_events: the marker that makes redelivery harmless."""

import uuid
from datetime import UTC, datetime, timedelta

from sqlalchemy import func, select

from app.models.processed_event import ProcessedEvent
from app.repositories.processed_event import ProcessedEventRepository

CONSUMER = "payment-service.order-created"


def _count(session_factory) -> int:
    with session_factory() as session:
        return session.execute(select(func.count()).select_from(ProcessedEvent)).scalar_one()


def test_first_record_is_true_and_a_duplicate_is_false(session_factory):
    event_id = uuid.uuid4()
    with session_factory() as session:
        repo = ProcessedEventRepository(session)
        assert repo.try_record(event_id, CONSUMER) is True
        session.commit()
        assert repo.try_record(event_id, CONSUMER) is False
        # ON CONFLICT DO NOTHING leaves the transaction usable; no rollback needed.
        assert repo.exists(event_id, CONSUMER) is True
        session.commit()
    assert _count(session_factory) == 1


def test_the_same_event_may_be_recorded_once_per_consumer(session_factory):
    event_id = uuid.uuid4()
    with session_factory() as session:
        repo = ProcessedEventRepository(session)
        assert repo.try_record(event_id, CONSUMER) is True
        assert repo.try_record(event_id, "payment-service.another-handler") is True
        session.commit()
    assert _count(session_factory) == 2


def test_the_marker_lives_in_the_callers_transaction(session_factory):
    event_id = uuid.uuid4()
    with session_factory() as session:
        repo = ProcessedEventRepository(session)
        assert repo.try_record(event_id, CONSUMER) is True
        # try_record never commits: rolling back the business change takes the marker too.
        session.rollback()
        assert repo.exists(event_id, CONSUMER) is False
        assert repo.try_record(event_id, CONSUMER) is True
        session.commit()
    assert _count(session_factory) == 1


def test_prune_deletes_only_rows_older_than_the_retention(session_factory):
    now = datetime.now(UTC)
    with session_factory() as session:
        session.add_all(
            [
                ProcessedEvent(
                    event_id=uuid.uuid4(), consumer=CONSUMER, processed_at=now - timedelta(days=9)
                ),
                ProcessedEvent(
                    event_id=uuid.uuid4(), consumer=CONSUMER, processed_at=now - timedelta(days=7)
                ),
                ProcessedEvent(event_id=uuid.uuid4(), consumer=CONSUMER, processed_at=now),
            ]
        )
        session.commit()
        assert ProcessedEventRepository(session).prune(older_than_days=8, now=now) == 1
    assert _count(session_factory) == 2
