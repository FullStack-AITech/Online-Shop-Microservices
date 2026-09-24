"""Declarative base and shared column conventions."""

from datetime import UTC, datetime

from sqlalchemy import DateTime, MetaData, func
from sqlalchemy.orm import DeclarativeBase, Mapped, mapped_column


def utcnow() -> datetime:
    return datetime.now(UTC)


# Named constraints, so a migration can drop or alter one by name. SQLite in particular
# cannot drop an unnamed constraint, even in Alembic's batch mode.
NAMING_CONVENTION = {
    "ix": "ix_%(table_name)s_%(column_0_name)s",
    "uq": "uq_%(table_name)s_%(column_0_name)s",
    "ck": "ck_%(table_name)s_%(constraint_name)s",
    "fk": "fk_%(table_name)s_%(column_0_name)s_%(referred_table_name)s",
    "pk": "pk_%(table_name)s",
}


class Base(DeclarativeBase):
    """Base class for all ORM models in this service."""

    metadata = MetaData(naming_convention=NAMING_CONVENTION)


class TimestampMixin:
    """Adds audit timestamps that every table in this service carries."""

    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=utcnow, server_default=func.now()
    )
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        nullable=False,
        default=utcnow,
        onupdate=utcnow,
        server_default=func.now(),
    )
