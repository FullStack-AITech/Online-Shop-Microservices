# Week 2 — Building the first two services (Days 8–14)

Plan for the week: *build User Service and Product Service with databases; then review,
clean the code, and document the APIs.*

## What was built

- **User Service** — FastAPI, SQLAlchemy 2.0, PostgreSQL. Registration, listing with
  pagination, retrieval, partial update, deletion, and an internal credential-verification
  endpoint for the future Auth Service. 24 tests, passing.
- **Product Service** — Spring Boot 3, JPA, Flyway, PostgreSQL. Catalogue CRUD, search,
  soft delete, and stock reserve/release. 21 tests, passing (7 aggregate unit tests,
  14 MockMvc API tests).
- **Docker Compose** — both services, each with its own Postgres instance.

## Concepts, and where they show up in the code

### Database per service

Two separate Postgres containers, not two schemas in one. The moment two services share
tables, a migration in one can break the other at runtime and the services can no longer be
deployed independently — which is the whole point of splitting them up.

The consequence to get used to: **there are no joins across services.** The Order Service
will hold a `user_id` and a `product_id` and will have to call out, or cache, or listen for
events. That is a real cost, paid deliberately.

### DTOs are not entities

Both services keep the wire contract (`app/schemas/`, `dto/`) separate from the persistence
model (`app/models/`, `domain/`). It looks like duplication on day one. It stops being
duplication the first time a column is renamed and forty consumers must not notice, or a
field like `password_hash` must never leave the process.

### Where business rules live

`Product.reserveStock()` is a method on the entity, not a block inside the service class.
A rule that must *always* hold belongs on the object it constrains, so it holds no matter
which code path gets there. The service layer coordinates; the entity protects itself.

The equivalent in the User Service is `UserService.verify_credentials()`, which collapses
"unknown email" and "wrong password" into one result. That is a business rule about
account enumeration, so it lives in the domain layer, not in the route handler.

### Status codes as contract

- `409` for both `email_already_registered` and `insufficient_stock`. Both requests are
  perfectly well formed — what fails is the *state* of the system. `400` would be wrong.
- `422`/`400` for validation failures, which are the client's fault to fix.
- `204` on delete, with no body.

### Optimistic locking

`Product` carries a `@Version` column. Without it, two concurrent reservations both read
`stock = 10`, both write `8`, and four units vanish. With it, the second write fails and
can be retried. This becomes essential in week 7 when the Saga pattern arrives and stock
reservations start getting compensated.

### Liveness vs readiness

Deliberately different endpoints. Liveness must not check the database: a failing liveness
probe restarts the pod, and restarting a healthy pod because Postgres is down makes an
outage worse. Readiness *does* check it, so the load balancer stops sending traffic.

### Migrations

The Product Service uses Flyway with `ddl-auto: validate` — the schema is versioned in
`db/migration/`, and the app refuses to start if the entities disagree with the tables.
The User Service still calls `create_all()` on startup, which is fine for local work and
wrong for production. Alembic is the fix; it is on the list.

## Open items carried forward

1. **Alembic migrations for the User Service** — the one real inconsistency between the two.
2. **No authentication anywhere yet** — every endpoint is open. Weeks 5–6.
3. **`DELETE /users/{id}` is a hard delete** while the Product Service soft-deletes. Once
   orders reference users, the User Service needs to deactivate instead.
4. **Stock reservation is an immediate decrement**, not a time-limited hold. Week 7.
5. **Docker Compose has not been run** — Docker is not installed on this machine, so both
   services have been verified natively but the composed stack has not been exercised.

## Question worth being able to answer in an interview

*"Why is the Order Service allowed to know a `product_id` but not to read the products
table?"* Because the id is part of the Product Service's published contract, and the table
is not. The contract can be kept stable on purpose; the table needs to be free to change.
