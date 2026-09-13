# User Service API

**Base URL (local):** `http://localhost:8000`
**Stack:** FastAPI (Python) + PostgreSQL
**Interactive docs:** `/docs` (Swagger UI) · `/openapi.json`

The User Service owns user accounts and profiles. It is the only service that may read or
write the `user_db` database. Other services reference users by `id` and obtain user data
through this API.

## Conventions

- All resource routes are under `/api/v1`. The version is in the path so the contract can
  change without breaking existing consumers.
- Emails are stored folded to lowercase and compared case-insensitively.
- The password hash is never returned by any endpoint.
- Errors share one shape: `{"error": "<machine_code>", "message": "<human text>"}`.

## Health

| Method | Path            | Purpose                                              |
|--------|-----------------|------------------------------------------------------|
| GET    | `/health/live`  | Liveness. Does not touch the database.               |
| GET    | `/health/ready` | Readiness. Returns `503` when the database is down.  |

Liveness must not depend on the database: a failing liveness probe restarts the pod, which
would not fix a database outage. Readiness must, so traffic is routed away instead.

## Endpoints

### `POST /api/v1/users` — register a user

Request:

```json
{
  "email": "ada@example.com",
  "full_name": "Ada Lovelace",
  "password": "s3cret-pass"
}
```

Validation: valid email; `full_name` 1–200 characters; `password` 8–128 characters.

| Status | Meaning                                       |
|--------|-----------------------------------------------|
| 201    | Created; body is the user                     |
| 409    | `email_already_registered`                    |
| 422    | Request body failed validation                |

Response (`201`):

```json
{
  "id": "1f1c1e2a-...",
  "email": "ada@example.com",
  "full_name": "Ada Lovelace",
  "is_active": true,
  "created_at": "2026-09-13T10:00:00Z",
  "updated_at": "2026-09-13T10:00:00Z"
}
```

### `GET /api/v1/users` — list users

Query: `limit` (1–100, default 20), `offset` (≥ 0, default 0).

```json
{ "items": [ /* users */ ], "total": 42, "limit": 20, "offset": 0 }
```

A `limit` above 100 is rejected with `422` rather than silently clamped, so a caller that
expects a larger page learns about it instead of paging through wrong results.

### `GET /api/v1/users/{user_id}`

`200` with the user, or `404` `user_not_found`.

### `PATCH /api/v1/users/{user_id}` — partial update

Only supplied fields change. `email` is deliberately not updatable: it is the login
identity, and changing it needs a verification flow that belongs with the Auth Service.

```json
{ "full_name": "Ada King", "is_active": false }
```

`200` with the updated user, or `404`.

### `DELETE /api/v1/users/{user_id}`

`204` on success, `404` if unknown. This is a hard delete; once the Order Service exists,
this should become a deactivation so historic orders keep a valid user reference.

### `POST /api/v1/users/credentials:verify` — internal

Used by the Auth Service to validate a login. Not exposed through the public gateway.

```json
{ "email": "ada@example.com", "password": "s3cret-pass" }
```

Always `200`:

```json
{ "valid": true, "user": { "id": "...", "email": "..." } }
```

An unknown email and a wrong password return the identical `{"valid": false, "user": null}`
so the endpoint cannot be used to enumerate registered accounts. Deactivated users always
fail verification.

## Data model

| Column          | Type         | Notes                                  |
|-----------------|--------------|----------------------------------------|
| `id`            | varchar(36)  | UUID4, primary key                     |
| `email`         | varchar(320) | Unique, indexed, lowercase             |
| `full_name`     | varchar(200) |                                        |
| `password_hash` | varchar(255) | `pbkdf2_sha256$iterations$salt$digest` |
| `is_active`     | boolean      | Inactive users cannot authenticate     |
| `created_at`    | timestamptz  |                                        |
| `updated_at`    | timestamptz  |                                        |

## Known gaps

These are deliberate, and scheduled for later weeks of the plan:

- **No authentication on the endpoints.** Week 5–6 adds the API Gateway and JWT validation.
- **`create_all` instead of migrations.** Tables are created on startup; a real deployment
  needs Alembic. The Product Service already uses Flyway and is the model to follow.
- **PBKDF2 rather than Argon2id.** Chosen to keep the image free of native build
  dependencies. The stored hash records its algorithm, so users can be migrated on login.
- **Timestamps lack a UTC offset when running on SQLite.** SQLite has no timezone-aware
  type, so local runs return `2026-09-13T10:22:24.354202` where Postgres returns
  `...+00:00`. The columns are declared `timestamptz`; only the dev fallback differs.
