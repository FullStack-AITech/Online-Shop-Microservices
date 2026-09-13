"""Test fixtures.

Each test gets a throwaway SQLite database and a fresh app instance, so tests are
isolated and need no Postgres running.
"""

import os

import pytest

# Settings are cached and read at import time, so configure the environment first.
os.environ.setdefault("USER_SERVICE_DATABASE_URL", "sqlite+pysqlite:///:memory:")
os.environ.setdefault("USER_SERVICE_CREATE_TABLES_ON_STARTUP", "false")
os.environ.setdefault("USER_SERVICE_PASSWORD_HASH_ITERATIONS", "1000")

from fastapi.testclient import TestClient  # noqa: E402
from sqlalchemy import create_engine  # noqa: E402
from sqlalchemy.orm import sessionmaker  # noqa: E402
from sqlalchemy.pool import StaticPool  # noqa: E402

from app.db.base import Base  # noqa: E402
from app.db.session import get_session  # noqa: E402
from app.main import create_app  # noqa: E402
from app.models import User  # noqa: F401,E402  (registers the table on Base.metadata)


@pytest.fixture
def session_factory():
    # StaticPool keeps the in-memory database alive across connections within one test.
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
def client(session_factory) -> TestClient:
    app = create_app()

    def override_get_session():
        session = session_factory()
        try:
            yield session
        finally:
            session.close()

    app.dependency_overrides[get_session] = override_get_session
    with TestClient(app) as test_client:
        yield test_client
    app.dependency_overrides.clear()


@pytest.fixture
def registered_user(client: TestClient) -> dict:
    response = client.post(
        "/api/v1/users",
        json={"email": "ada@example.com", "full_name": "Ada Lovelace", "password": "s3cret-pass"},
    )
    assert response.status_code == 201
    return response.json()
