"""Test fixtures.

Each test gets a throwaway SQLite database and a fresh app instance, so tests are
isolated and need no Postgres or Kafka running.
"""

import os
from decimal import Decimal

import pytest

# Settings are cached and read at import time, so configure the environment first.
os.environ.setdefault("PAYMENT_SERVICE_DATABASE_URL", "sqlite+pysqlite:///:memory:")

from fastapi.testclient import TestClient  # noqa: E402
from sqlalchemy import create_engine  # noqa: E402
from sqlalchemy.orm import sessionmaker  # noqa: E402
from sqlalchemy.pool import StaticPool  # noqa: E402

from app import models  # noqa: F401,E402  (registers the tables on Base.metadata)
from app.api.deps import get_payment_provider  # noqa: E402
from app.db.base import Base  # noqa: E402
from app.db.session import get_session  # noqa: E402
from app.main import create_app  # noqa: E402
from app.providers.base import ProviderResult  # noqa: E402
from app.providers.fake import FakePaymentProvider  # noqa: E402

DECLINE_ABOVE = Decimal("1000.00")


class SpyProvider:
    """The real fake provider, plus a record of every charge it was asked for."""

    def __init__(self, decline_above: Decimal = DECLINE_ABOVE) -> None:
        self._inner = FakePaymentProvider(decline_above=decline_above)
        self.calls: list[dict] = []

    def charge(self, *, amount: Decimal, currency: str, reference: str) -> ProviderResult:
        self.calls.append({"amount": amount, "currency": currency, "reference": reference})
        return self._inner.charge(amount=amount, currency=currency, reference=reference)


@pytest.fixture
def session_factory():
    # StaticPool keeps the in-memory database alive across connections within one test.
    # create_all is fine here: test_migrations proves the migrations build the same schema.
    engine = create_engine(
        "sqlite+pysqlite:///:memory:",
        connect_args={"check_same_thread": False},
        poolclass=StaticPool,
    )
    Base.metadata.create_all(bind=engine)
    yield sessionmaker(bind=engine, autoflush=False, expire_on_commit=False)
    Base.metadata.drop_all(bind=engine)
    engine.dispose()


@pytest.fixture
def provider() -> SpyProvider:
    return SpyProvider()


@pytest.fixture
def client(session_factory, provider) -> TestClient:
    app = create_app()

    def override_get_session():
        session = session_factory()
        try:
            yield session
        finally:
            session.close()

    app.dependency_overrides[get_session] = override_get_session
    app.dependency_overrides[get_payment_provider] = lambda: provider
    with TestClient(app) as test_client:
        yield test_client
    app.dependency_overrides.clear()
