"""The event envelope every message on every topic shares (docs/events/README.md).

This is the service's own model of it, not a shared library: the contract is the
published JSON schema, and each service reads it with its own code.
"""

from datetime import UTC, datetime
from typing import Any
from uuid import UUID

from pydantic import BaseModel, ConfigDict, ValidationError, field_serializer
from pydantic.alias_generators import to_camel

from app.messaging.errors import MalformedEventError


class CamelModel(BaseModel):
    """camelCase on the wire, snake_case in Python; unknown fields are ignored."""

    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True, extra="ignore")


class EventEnvelope(CamelModel):
    event_id: UUID
    event_type: str
    event_version: int
    occurred_at: datetime
    correlation_id: str
    producer: str
    payload: dict[str, Any]

    @field_serializer("occurred_at")
    def _utc(self, value: datetime) -> str:
        return to_utc_iso(value)

    def to_bytes(self) -> bytes:
        return self.model_dump_json(by_alias=True).encode("utf-8")


def parse_envelope(value: bytes | None) -> EventEnvelope:
    """Parse a Kafka record value, or raise :class:`MalformedEventError`."""
    if value is None:
        raise MalformedEventError("Record has no value")
    try:
        return EventEnvelope.model_validate_json(value)
    except ValidationError as exc:
        raise MalformedEventError(f"Not a valid event envelope: {exc}") from exc


def to_utc_iso(value: datetime) -> str:
    """ISO-8601 in UTC with a ``Z`` suffix, as the contract requires.

    SQLite hands timestamps back without an offset; they were written in UTC, so a naive
    value is read as UTC rather than as local time.
    """
    if value.tzinfo is None:
        value = value.replace(tzinfo=UTC)
    return value.astimezone(UTC).isoformat().replace("+00:00", "Z")
