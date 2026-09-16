# Order Service

.NET 8 (C#) + PostgreSQL + EF Core. Owns customer orders.

Full API reference: [`docs/api/order-service.md`](../../docs/api/order-service.md).

## Layout

```
src/
  OrderService.Domain/         the Order aggregate, its lines, and the state machine
  OrderService.Application/    use cases and the ports they depend on
  OrderService.Infrastructure/ EF Core, the HTTP clients, the resilience pipelines
  OrderService.Api/            endpoints, error mapping, health probes, composition root
tests/
  OrderService.Domain.Tests/   aggregate and state machine, no framework
  OrderService.Api.Tests/      resilience pipelines, plus the API end to end
```

Dependencies point inwards: `Api -> Infrastructure -> Application -> Domain`. The domain
references nothing, so the rules about what an order may do are free of EF Core, HTTP and
ASP.NET. That is what lets the same rules be driven by an event consumer in week 4 without
being rewritten.

The two interfaces the Application layer depends on — `IProductCatalog` and
`IUserDirectory` — are owned by this service, not shared libraries. Each describes only
what the Order Service needs. A narrow port is far easier to keep stable than a shared
model, and it means tests substitute a fake rather than a network.

## Run it

With Docker Compose, from the repository root:

```bash
docker compose up --build order-service
```

Locally you need **.NET 8 SDK** and the three dependencies. Start the database and the two
services it calls, then run:

```bash
docker compose up -d order-db user-service product-service
dotnet run --project src/OrderService.Api
```

Then open <http://localhost:8082/swagger>.

## Tests

```bash
dotnet test
```

Current state: **70 tests passing** — 36 domain, 34 API and resilience. No Postgres and no
network needed: the API tests run against in-memory SQLite with the downstream services
faked in process.

## Migrations

EF Core migrations are committed and applied on startup
(`ORDER_SERVICE_Database__MigrateOnStartup`, default `true`).

```bash
dotnet ef migrations add <Name> \
  --project src/OrderService.Infrastructure \
  --startup-project src/OrderService.Api \
  --output-dir Persistence/Migrations
```

## Configuration

Environment variables are prefixed `ORDER_SERVICE_`, with `__` as the section separator:
`ORDER_SERVICE_Downstream__ProductService__BaseUrl` sets
`Downstream:ProductService:BaseUrl`.

| Variable                                            | Default                    |
|-----------------------------------------------------|----------------------------|
| `ORDER_SERVICE_ConnectionStrings__OrderDatabase`     | localhost:5434/order_db    |
| `ORDER_SERVICE_Downstream__ProductService__BaseUrl`  | `http://localhost:8081`    |
| `ORDER_SERVICE_Downstream__UserService__BaseUrl`     | `http://localhost:8000`    |
| `ORDER_SERVICE_Database__MigrateOnStartup`           | `true`                     |
| `ORDER_SERVICE_Swagger__Enabled`                     | `true`                     |

Each downstream also accepts `AttemptTimeoutMs`, `TotalTimeoutMs`, `MaxRetries`,
`BaseRetryDelayMs` and the `CircuitBreaker*` settings — see the API reference for the
defaults and the reasoning behind them. Resilience settings that are hardcoded cannot be
adjusted when a dependency turns out to be slower in production than in testing.
