# Order Service

.NET 8 (C#) + PostgreSQL + EF Core + Kafka (Confluent.Kafka). Owns customer orders.

Publishes `OrderCreated` and `OrderCancelled` to `orders`; consumes `PaymentProcessed` and
`PaymentFailed` from `payments` to move orders to `Paid` or `Failed`.

Full API reference: [`docs/api/order-service.md`](../../docs/api/order-service.md).

## Layout

```
src/
  OrderService.Domain/         the Order aggregate, its lines, and the state machine
  OrderService.Application/    use cases, the ports they depend on, and the event payloads
    Events/                    the envelope plus OrderCreated / OrderCancelled v1
  OrderService.Infrastructure/ EF Core, the HTTP clients, the resilience pipelines, Kafka
    Messaging/                 publisher, payments consumer, dead-lettering, pruner
  OrderService.Api/            endpoints, error mapping, health probes, composition root
tests/
  OrderService.Domain.Tests/   aggregate and state machine, no framework
  OrderService.Api.Tests/      resilience pipelines, the API end to end, and messaging
    Messaging/                 payment events, retries and dead letters, processed_events
```

Dependencies point inwards: `Api -> Infrastructure -> Application -> Domain`. The domain
references nothing, so the rules about what an order may do are free of EF Core, HTTP and
ASP.NET. That is what lets the same rules be driven by an HTTP call or a payment event
without being rewritten.

The interfaces the Application layer depends on — `IProductCatalog`, `IUserDirectory`,
`IEventPublisher` and `IProcessedEventStore` — are owned by this service, not shared
libraries. Each describes only
what the Order Service needs. A narrow port is far easier to keep stable than a shared
model, and it means tests substitute a fake rather than a network.

## Run it

With Docker Compose, from the repository root:

```bash
docker compose up --build order-service
```

Locally you need **.NET 8 SDK** and the dependencies. Start the database, the two
services it calls and Kafka, then run:

```bash
docker compose up -d order-db user-service product-service kafka kafka-init
dotnet run --project src/OrderService.Api
```

Without Kafka, set `ORDER_SERVICE_Kafka__BootstrapServers=` (empty): events are then only
logged, no consumer runs, and orders stay `AwaitingPayment` unless paid by hand with
`POST /api/v1/orders/{id}/pay`.

Then open <http://localhost:8082/swagger>.

## Tests

```bash
dotnet test
```

Current state: **102 tests passing, 1 skipped** — 36 domain, 66 API, resilience and
messaging. No Postgres, no broker and no network needed: the API tests run against
in-memory SQLite, the downstream services and the event publisher are faked in process,
and the payment consumer's per-message logic (`PaymentEventProcessor`) is fed hand-built
records. The background consumer and pruner are switched off (`Kafka:ConsumersEnabled=false`).

The skipped test is the real-broker check. With the Compose stack up:

```bash
KAFKA_BOOTSTRAP=localhost:29092 dotnet test --filter FullyQualifiedName~KafkaEventPublisherIntegrationTests
```

To check the events this service emits against the contract in `docs/events/schemas`:

```bash
EVENT_CAPTURE_DIR=/tmp/events dotnet test --filter FullyQualifiedName~EventPublishingTests
python ../../scripts/validate-event-schemas.py /tmp/events/*.captured.json
```

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
| `ORDER_SERVICE_Kafka__BootstrapServers`              | `localhost:29092`; empty = no broker, events only logged |
| `ORDER_SERVICE_Kafka__OrdersTopic`                   | `orders`                   |
| `ORDER_SERVICE_Kafka__PaymentsTopic`                 | `payments`                 |
| `ORDER_SERVICE_Kafka__DeliveryTimeoutMs`             | `5000` — how long a publish waits for the broker |
| `ORDER_SERVICE_Kafka__ConsumersEnabled`              | `true` — the payments consumer and the processed_events pruner |

Each downstream also accepts `AttemptTimeoutMs`, `TotalTimeoutMs`, `MaxRetries`,
`BaseRetryDelayMs` and the `CircuitBreaker*` settings — see the API reference for the
defaults and the reasoning behind them. Resilience settings that are hardcoded cannot be
adjusted when a dependency turns out to be slower in production than in testing.
