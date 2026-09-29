"""The events this service emits match the published contract in docs/events/schemas.

Serialises one PaymentProcessed and one PaymentFailed exactly as the publisher sends them,
writes them to files and runs the repository's own validator over them.
"""

import asyncio
import json
import pathlib
import subprocess
import sys

import pytest

from app.messaging.handlers import OrderCreatedHandler, dispatch
from app.messaging.publisher import KafkaEventPublisher
from tests.fakes import order_created

REPO_ROOT = pathlib.Path(__file__).resolve().parents[3]
VALIDATOR = REPO_ROOT / "scripts" / "validate-event-schemas.py"

pytest.importorskip("jsonschema", reason="the validator needs jsonschema (requirements-dev)")
pytestmark = pytest.mark.skipif(not VALIDATOR.exists(), reason="validator script not found")


class RecordingProducer:
    def __init__(self) -> None:
        self.sent: list[dict] = []

    async def send_and_wait(
        self, topic, value=None, key=None, partition=None, timestamp_ms=None, headers=None
    ):
        self.sent.append({"topic": topic, "value": value, "key": key, "headers": headers})


def test_emitted_events_validate_against_their_schemas(session_factory, provider, tmp_path):
    producer = RecordingProducer()
    handler = OrderCreatedHandler(
        session_factory, provider, KafkaEventPublisher(producer), payments_topic="payments"
    )
    approved = order_created(total="99.98")
    declined = order_created(total="1199.98", orderId="6e5d4c3b-2a19-4f8e-9d7c-6b5a4f3e2d1c")
    for message in (approved, declined):
        asyncio.run(dispatch(json.dumps(message).encode(), handler))

    files = []
    for sent in producer.sent:
        event = json.loads(sent["value"])
        # Key = orderId; eventType and correlationId also travel as headers.
        assert sent["topic"] == "payments"
        assert sent["key"].decode() == event["payload"]["orderId"]
        assert dict(sent["headers"]) == {
            "eventType": event["eventType"].encode(),
            "correlationId": event["correlationId"].encode(),
        }
        # Not *.example.json, so the validator matches on eventType and eventVersion.
        path = tmp_path / f"{event['eventType']}.json"
        path.write_bytes(sent["value"])
        files.append(path)

    assert sorted(path.name for path in files) == ["PaymentFailed.json", "PaymentProcessed.json"]
    result = subprocess.run(
        [sys.executable, str(VALIDATOR), *map(str, files)],
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert "all valid" in result.stdout
