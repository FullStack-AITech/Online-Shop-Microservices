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
| Order Service        | .NET (C#)           | ⬜ Week 3                          |
| Payment Service      | FastAPI (Python)    | ⬜ Week 4                          |
| Notification Service | Spring Boot (Java)  | ⬜ Week 4                          |
| Message Broker       | Kafka / RabbitMQ    | ⬜ Week 4                          |
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
| user_db          | `localhost:5432`                        |
| product_db       | `localhost:5433`                        |

To run a single service without Docker, see its own README:

- [services/user-service/](services/user-service/README.md) — runs on SQLite, no Postgres needed
- [services/product-service/](services/product-service/README.md) — needs JDK 21; use the
  bundled `./mvnw` so no Maven install is required

## API documentation

- [User Service API](docs/api/user-service.md)
- [Product Service API](docs/api/product-service.md)

Both services also serve live, generated documentation: Swagger UI at `/docs` (FastAPI) and
`/swagger-ui.html` (Spring Boot).

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

## Repository layout

```
services/          one directory per microservice
gateway/           API Gateway configuration (week 5)
infrastructure/    database init scripts, Kubernetes manifests, monitoring config
frontend/          Angular application
docs/api/          hand-written API reference
docker-compose.yml local development stack
```

## Learning plan

Built against a 90-day plan: weeks 1–4 foundations and the first services, weeks 5–8
production patterns (gateway, auth, Saga, Docker), weeks 9–12 Kubernetes, observability and
advanced architecture.
