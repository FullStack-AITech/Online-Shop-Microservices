"""Markers for events this service has already acted on.

Kafka delivers at least once, so every consumer has to recognise a redelivery. A row here
is written in the same transaction as the business change it guards: both commit or
neither does. See "Consuming events" in docs/events/README.md.
"""

import uuid
from datetime import datetime

from sqlalchemy import DateTime, String, Uuid, func
from sqlalchemy.orm import Mapped, mapped_column

from app.db.base import Base, utcnow


class ProcessedEvent(Base):
    __tablename__ = "processed_events"

    # (event_id, consumer) rather than event_id alone: two handlers in one service may each
    # need to process the same event once.
    event_id: Mapped[uuid.UUID] = mapped_column(Uuid, primary_key=True)
    consumer: Mapped[str] = mapped_column(String(64), primary_key=True)
    processed_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=utcnow, server_default=func.now()
    )
