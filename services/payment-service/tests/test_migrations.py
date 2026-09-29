"""The Alembic migrations, run against a throwaway SQLite file.

The URL is set on the Alembic config explicitly, which alembic/env.py prefers over the app
settings, so these tests can never touch a database configured in .env or the shell.
"""

import pathlib

import pytest
from alembic import command
from alembic.autogenerate import compare_metadata
from alembic.config import Config
from alembic.runtime.migration import MigrationContext
from sqlalchemy import create_engine, inspect

from app.db.base import Base

SERVICE_ROOT = pathlib.Path(__file__).resolve().parents[1]


@pytest.fixture
def database_url(tmp_path) -> str:
    return f"sqlite+pysqlite:///{(tmp_path / 'migrations.db').as_posix()}"


@pytest.fixture
def alembic_config(database_url) -> Config:
    config = Config(str(SERVICE_ROOT / "alembic.ini"))
    config.set_main_option("script_location", str(SERVICE_ROOT / "alembic"))
    config.set_main_option("sqlalchemy.url", database_url)
    # Leave pytest's logging alone; fileConfig would otherwise replace its handlers.
    config.attributes["configure_logger"] = False
    return config


def _tables(database_url: str) -> set[str]:
    engine = create_engine(database_url)
    try:
        return set(inspect(engine).get_table_names())
    finally:
        engine.dispose()


def _columns(database_url: str, table: str) -> set[str]:
    engine = create_engine(database_url)
    try:
        return {column["name"] for column in inspect(engine).get_columns(table)}
    finally:
        engine.dispose()


def test_migrations_match_the_models(alembic_config, database_url):
    """Drift test: the migrated schema is exactly what the ORM models describe."""
    assert "migrations.db" in database_url  # the guard: never a configured dev database
    command.upgrade(alembic_config, "head")

    engine = create_engine(database_url)
    try:
        with engine.connect() as connection:
            context = MigrationContext.configure(
                connection, opts={"compare_type": True, "render_as_batch": True}
            )
            diff = compare_metadata(context, Base.metadata)
    finally:
        engine.dispose()
    assert diff == [], (
        f"models and migrations disagree; run alembic revision --autogenerate: {diff}"
    )


def test_upgrade_downgrade_upgrade_is_clean(alembic_config, database_url):
    command.upgrade(alembic_config, "head")
    assert {"payments", "processed_events", "alembic_version"} <= _tables(database_url)

    command.downgrade(alembic_config, "base")
    assert _tables(database_url) == {"alembic_version"}

    command.upgrade(alembic_config, "head")
    assert {"payments", "processed_events"} <= _tables(database_url)


def test_each_revision_downgrades_one_step(alembic_config, database_url):
    command.upgrade(alembic_config, "head")
    command.downgrade(alembic_config, "-1")
    assert "outcome_event_id" not in _columns(database_url, "payments")
    assert "processed_events" in _tables(database_url)
    command.upgrade(alembic_config, "head")
