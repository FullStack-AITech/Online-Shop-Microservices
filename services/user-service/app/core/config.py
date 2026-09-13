"""Application settings, loaded from the environment.

Every service in this platform reads its configuration from environment variables so the
same image can run locally, in Docker Compose and in Kubernetes without code changes.
"""

from functools import lru_cache

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_prefix="USER_SERVICE_",
        env_file=".env",
        env_file_encoding="utf-8",
        extra="ignore",
    )

    service_name: str = "user-service"
    environment: str = "local"
    log_level: str = "INFO"

    # The User Service owns this database exclusively (database-per-service).
    database_url: str = "sqlite+pysqlite:///./user_service.db"

    # Convenient for local development; production environments should run migrations.
    create_tables_on_startup: bool = True

    # PBKDF2 work factor. Higher is slower and safer; tests override this to stay fast.
    password_hash_iterations: int = 600_000


@lru_cache
def get_settings() -> Settings:
    """Return the cached settings singleton."""
    return Settings()
