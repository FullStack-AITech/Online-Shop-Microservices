"""Application settings, loaded from the environment.

Every service in this platform reads its configuration from environment variables so the
same image can run locally, in Docker Compose and in Kubernetes without code changes. The
API and the event consumer are the same image, so they share this one settings class.
"""

from decimal import Decimal
from functools import lru_cache
from typing import Literal

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_prefix="PAYMENT_SERVICE_",
        env_file=".env",
        env_file_encoding="utf-8",
        extra="ignore",
    )

    service_name: str = "payment-service"
    environment: str = "local"
    log_level: str = "INFO"

    # The Payment Service owns this database exclusively (database-per-service). The
    # schema comes from Alembic, never from create_all.
    database_url: str = "sqlite+pysqlite:///./payment_service.db"

    # Which PaymentProvider to charge through. Only the fake exists until a real provider
    # is integrated; a Literal makes a typo fail at startup instead of at the first charge.
    payment_provider: Literal["fake"] = "fake"
    # The fake declines anything above this, so tests and the smoke test can force a
    # failure deterministically.
    fake_decline_above: Decimal = Decimal("1000.00")

    kafka_bootstrap_servers: str = "localhost:29092"
    kafka_orders_topic: str = "orders"
    kafka_payments_topic: str = "payments"
    kafka_group_id: str = "payment-service"

    # Attempts in total before a message is dead-lettered, and the base of the linear
    # backoff between them. Kept short: the partition is blocked while we wait.
    kafka_max_attempts: int = 3
    kafka_retry_backoff_seconds: float = 0.5

    # processed_events only has to outlive the topic retention (7 days); anything older
    # can never be redelivered. One day of margin.
    processed_events_retention_days: int = 8
    processed_events_prune_interval_seconds: int = 3600


@lru_cache
def get_settings() -> Settings:
    """Return the cached settings singleton."""
    return Settings()
