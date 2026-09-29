"""Fakes and sample messages shared by the messaging tests."""

import uuid

from app.messaging.envelope import EventEnvelope

ORDER_ID = "3f2b1c9e-8d7a-4e6f-b5a4-1c2d3e4f5a6b"
CORRELATION_ID = "5b0f6f5c-9a8e-4d3c-b2a1-f0e9d8c7b6a5"


class FakePublisher:
    def __init__(self, failures: int = 0) -> None:
        self.published: list[tuple[str, str, EventEnvelope]] = []
        self._failures = failures

    async def publish(self, topic: str, key: str, envelope: EventEnvelope) -> None:
        if self._failures:
            self._failures -= 1
            raise ConnectionError("broker unavailable")
        self.published.append((topic, key, envelope))


def order_created(
    *, total: str = "99.98", event_id: str | None = None, version: int = 1, **payload
) -> dict:
    return {
        "eventId": event_id or str(uuid.uuid4()),
        "eventType": "OrderCreated",
        "eventVersion": version,
        "occurredAt": "2026-09-24T10:00:00Z",
        "correlationId": CORRELATION_ID,
        "producer": "order-service",
        "payload": {
            "orderId": ORDER_ID,
            "userId": "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d",
            "userEmail": "a@b.com",
            "currency": "GBP",
            "totalAmount": total,
            "totalItems": 2,
            "lines": [],
            "createdAt": "2026-09-24T10:00:00Z",
            "somethingNew": "consumers ignore fields they do not know",
            **payload,
        },
    }
