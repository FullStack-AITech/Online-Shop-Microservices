# User Service

FastAPI + PostgreSQL. Owns user accounts and profiles.

Full API reference: [`docs/api/user-service.md`](../../docs/api/user-service.md).

## Layout

```
app/
  main.py          FastAPI app, domain-error -> HTTP mapping
  core/            settings, logging, password hashing
  db/              engine, session, declarative base
  models/          SQLAlchemy ORM models (private to this service)
  schemas/         Pydantic request/response contracts (the public API)
  repositories/    the only layer that writes SQL
  services/        business rules; knows nothing about HTTP
  api/v1/          routes
tests/             pytest suite, runs against in-memory SQLite
```

The layering exists so each concern has one home: swapping Postgres for something else
touches `repositories/`, changing the wire format touches `schemas/`, and a business rule
such as "inactive users cannot log in" lives in `services/` where a future gRPC or event
handler can reuse it.

## Run it

With Docker Compose, from the repository root:

```bash
docker compose up --build user-service
```

Without Docker (SQLite, no Postgres needed):

```bash
python -m venv .venv
.venv/Scripts/python.exe -m pip install -r requirements-dev.txt   # Windows
# source .venv/bin/activate && pip install -r requirements-dev.txt  # macOS/Linux

cp .env.example .env
.venv/Scripts/python.exe -m uvicorn app.main:app --reload --port 8000
```

Then open <http://localhost:8000/docs>.

## Tests and linting

```bash
.venv/Scripts/python.exe -m pytest -q
.venv/Scripts/python.exe -m ruff check app tests
.venv/Scripts/python.exe -m ruff format app tests
```

Tests use an in-memory SQLite database via a dependency override, so they need no running
Postgres and each test starts from an empty schema.

## Configuration

All settings are environment variables prefixed `USER_SERVICE_`; see
[`.env.example`](.env.example).

| Variable                            | Default                            | Purpose                        |
|-------------------------------------|------------------------------------|--------------------------------|
| `USER_SERVICE_DATABASE_URL`         | `sqlite+pysqlite:///./user_service.db` | SQLAlchemy URL             |
| `USER_SERVICE_CREATE_TABLES_ON_STARTUP` | `true`                         | Dev convenience; use migrations in production |
| `USER_SERVICE_LOG_LEVEL`            | `INFO`                             |                                |
| `USER_SERVICE_PASSWORD_HASH_ITERATIONS` | `600000`                       | PBKDF2 work factor             |
