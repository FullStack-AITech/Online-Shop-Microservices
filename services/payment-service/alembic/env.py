"""Alembic environment for the Payment Service.

The database URL comes from the app settings (``PAYMENT_SERVICE_DATABASE_URL``) so the
migrations and the service can never disagree about which database they mean. A caller
that sets ``sqlalchemy.url`` on the config explicitly — the migration tests do, with a
throwaway SQLite file — wins, so a test run can never touch a configured dev database.
"""

from logging.config import fileConfig

from alembic import context
from sqlalchemy import engine_from_config, pool

from app import models  # noqa: F401  (registers every table on Base.metadata)
from app.core.config import get_settings
from app.db.base import Base

config = context.config

if config.config_file_name is not None and config.attributes.get("configure_logger", True):
    fileConfig(config.config_file_name, disable_existing_loggers=False)

if not config.get_main_option("sqlalchemy.url"):
    config.set_main_option("sqlalchemy.url", get_settings().database_url)

target_metadata = Base.metadata

# render_as_batch: SQLite cannot ALTER most things in place, so Alembic rebuilds the table
# instead. It is a no-op on Postgres, and keeps one set of migrations working on both.
# compare_type: without it autogenerate misses a changed column type, e.g. a longer String.
_OPTIONS = {"target_metadata": target_metadata, "render_as_batch": True, "compare_type": True}


def run_migrations_offline() -> None:
    """Emit SQL to stdout instead of running it (``alembic upgrade head --sql``)."""
    context.configure(
        url=config.get_main_option("sqlalchemy.url"),
        literal_binds=True,
        dialect_opts={"paramstyle": "named"},
        **_OPTIONS,
    )
    with context.begin_transaction():
        context.run_migrations()


def run_migrations_online() -> None:
    connectable = engine_from_config(
        config.get_section(config.config_ini_section, {}),
        prefix="sqlalchemy.",
        poolclass=pool.NullPool,
    )
    with connectable.connect() as connection:
        context.configure(connection=connection, **_OPTIONS)
        with context.begin_transaction():
            context.run_migrations()


if context.is_offline_mode():
    run_migrations_offline()
else:
    run_migrations_online()
