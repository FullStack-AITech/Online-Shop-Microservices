# Online Shop Microservices

A production-style microservices platform built as the practical half of a 90-day learning
plan. Each service is deliberately written in a different stack, so the architectural
patterns have to stand on their own rather than lean on one framework's conventions.

## Architecture

```
                 ┌─────────────┐
   Users ──────► │  Angular    │
                 └──────┬──────┘
                        │ HTTPS
                 ┌──────▼──────┐      ┌──────────────┐
                 │ API Gateway │◄────►│ Auth (.NET)  │
                 └──────┬──────┘      └──────────────┘
        ┌───────────────┼───────────────┬──────────────┐
        ▼               ▼               ▼              ▼
  ┌──────────┐   ┌────────────┐   ┌──────────┐  ┌──────────────┐
  │   User   │   │  Product   │   │  Order   │  │   Payment    │
  │ FastAPI  │   │Spring Boot │   │  .NET    │  │   FastAPI    │
  └────┬─────┘   └─────┬──────┘   └────┬─────┘  └──────┬───────┘
       │               │                │               │
   user_db        product_db        order_db      payment_db
                                         │
                                   ┌─────▼─────┐
                                   │   Kafka   │──► Notification (Spring Boot)
                                   └───────────┘
```

Every service owns its own database. No service reads another's tables — that is the rule
the whole design rests on, because a shared database turns independent services back into
a distributed monolith.

## Status

| Component            | Stack               | State                              |
|----------------------|---------------------|------------------------------------|
| User Service         | FastAPI (Python)    | ✅ Built, 24 tests passing          |
| Product Service      | Spring Boot (Java)  | ✅ Built, 21 tests passing          |
| Order Service        | .NET (C#)           | ✅ Built, 102 tests passing (+1 needs a live broker) |
| Payment Service      | FastAPI (Python)    | ✅ Built, 90 tests passing          |
| Notification Service | Spring Boot (Java)  | ✅ Built, 48 tests (2 need Linux/CI, see its README) |
| Message Broker       | Kafka (KRaft)       | ✅ In Compose — [ADR 0001](docs/decisions/0001-message-broker.md) |
| API Gateway          | NGINX / Kong        | ⬜ Week 5                          |
| Auth Service         | .NET Identity       | ⬜ Week 6                          |
| Observability        | Prometheus, Grafana, Jaeger | ⬜ Week 10                 |
| Kubernetes           | —                   | ⬜ Week 9                          |

## Quick start

```bash
docker compose up --build
```

| Service          | URL                                     |
|------------------|-----------------------------------------|
| User Service     | http://localhost:8000/docs              |
| Product Service  | http://localhost:8081/swagger-ui.html   |
| Order Service    | http://localhost:8082/swagger           |
| Payment Service  | http://localhost:8083/docs              |
| Notification Service | http://localhost:8084/swagger-ui.html |
| Kafka UI         | http://localhost:8090                   |
| Kafka (host)     | `localhost:29092`                       |
| user_db          | `localhost:5432`                        |
| product_db       | `localhost:5433`                        |
| order_db         | `localhost:5434`                        |
| payment_db       | `localhost:5435`                        |
| notification_db  | `localhost:5436`                        |

Then prove the whole flow — order → payment → notification, plus the decline path:

```bash
bash scripts/smoke-test.sh
bash scripts/redelivery-drill.sh   # replays every topic; nothing may change
```

To run a single service without Docker, see its own README:

- [services/user-service/](services/user-service/README.md) — runs on SQLite, no Postgres needed
- [services/product-service/](services/product-service/README.md) — needs JDK 21; use the
  bundled `./mvnw` so no Maven install is required
- [services/order-service/](services/order-service/README.md) — needs the .NET 8 SDK
- [services/payment-service/](services/payment-service/README.md) — runs on SQLite; the
  event consumer is a separate process, `python -m app.consumer`
- [services/notification-service/](services/notification-service/README.md) — needs JDK 21

## API documentation

- [User Service API](docs/api/user-service.md)
- [Product Service API](docs/api/product-service.md)
- [Order Service API](docs/api/order-service.md)
- [Payment Service API](docs/api/payment-service.md)
- [Notification Service API](docs/api/notification-service.md)
- [Events: envelope, schemas and consumer rules](docs/events/README.md)
- [Decision records](docs/decisions/) · [Runbooks](docs/runbooks/)

Each service also serves live, generated documentation: `/docs` (FastAPI),
`/swagger-ui.html` (Spring Boot) and `/swagger` (.NET).

## Conventions shared by every service

These are decided once here so the services stay consistent as more are added.

- **Versioned paths.** All resource routes live under `/api/v1`.
- **One error shape.** `{"error": "<machine_code>", "message": "<human text>"}`, so the
  gateway and the frontend can handle failures uniformly.
- **Status codes carry meaning.** `400`/`422` invalid request, `404` unknown resource,
  `409` state conflict (duplicate key, insufficient stock).
- **Separate liveness and readiness probes.** Liveness never depends on a downstream
  dependency; a failing liveness check restarts the pod, which cannot fix a database outage.
- **Config from the environment.** The same image runs locally, in Compose and in
  Kubernetes with no code changes.
- **Layered inside each service.** Transport → business rules → data access. The business
  layer never imports HTTP types, so it stays reusable from an event consumer.
- **UUID identifiers.** Opaque, non-enumerable, and safe to publish in events.
- **Every outbound call has a timeout, and only idempotent calls are retried.** Retrying a
  non-idempotent operation after an ambiguous failure is how stock, charges and emails get
  duplicated. See [the Order Service reference](docs/api/order-service.md#resilience).
- **Events are contracts.** One envelope, money as decimal strings, keyed by order id, and
  every consumer idempotent on `eventId` — delivery is at least once. See
  [docs/events](docs/events/README.md).

## Repository layout

```
services/          one directory per microservice
gateway/           API Gateway configuration (week 5)
infrastructure/    Kafka topic script; later Kubernetes manifests, monitoring config
scripts/           smoke test, redelivery drill, event schema validator
frontend/          Angular application
docs/api/          hand-written API reference
docs/events/       event envelope, JSON Schemas and examples
docs/decisions/    architecture decision records
docs/runbooks/     what to do when an alert fires
docker-compose.yml local development stack
```

## Learning plan

Built against a 90-day plan: weeks 1–4 foundations and the first services, weeks 5–8
production patterns (gateway, auth, Saga, Docker), weeks 9–12 Kubernetes, observability and
advanced architecture.
