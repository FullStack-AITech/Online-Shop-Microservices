# Order Service API

**Base URL (local):** `http://localhost:8082`
**Stack:** .NET 8 (C#) + PostgreSQL + EF Core migrations + Kafka (Confluent.Kafka)
**Interactive docs:** `/swagger`

The Order Service owns customer orders. It is the only service that may read or write
`order_db`. It holds a `userId` and a `productId` per line, but never reads the User or
Product databases — everything it needs is copied onto the order when it is placed.

## Conventions

- Resource routes are under `/api/v1`.
- Errors share one shape:
  `{"error": "<code>", "message": "...", "path": "...", "timestamp": "..."}`.
- Money is `numeric(12,2)` with an explicit ISO-4217 currency, matching the Product Service.
- All lines on an order must share one currency; this service never converts between them.

## Health

| Method | Path            | Purpose                                             |
|--------|-----------------|-----------------------------------------------------|
| GET    | `/health/live`  | Liveness. Checks nothing external.                  |
| GET    | `/health/ready` | Readiness. Returns `503` when the database is down. |

## The order lifecycle

```
                 ┌──────────────────────────────► Cancelled
                 │                          ▲         ▲
   Pending ──────┼──► AwaitingPayment ──────┼──► Paid ─┴──► Shipped
      │          │           │              │
      └──────────┴───────────┴──────────────┴──► Failed
```

| From              | May move to                          |
|-------------------|--------------------------------------|
| `Pending`         | `AwaitingPayment`, `Cancelled`, `Failed` |
| `AwaitingPayment` | `Paid`, `Cancelled`, `Failed`        |
| `Paid`            | `Shipped`, `Cancelled`               |
| `Shipped`         | — terminal                           |
| `Cancelled`       | — terminal                           |
| `Failed`          | — terminal                           |

Anything not in that table is rejected with `409 invalid_order_transition`. `Paid` can still
move to `Cancelled` because that is the refund path — which is why cancelling a paid order
needs a compensating action, not just a status change.

## Endpoints

### `POST /api/v1/orders` — place an order

```json
{
  "userId": "1f1c1e2a-...",
  "lines": [
    { "productId": "a3b1...", "quantity": 2 },
    { "productId": "c7d2...", "quantity": 1 }
  ]
}
```

Note what the request does **not** contain: no prices. The client cannot set what it pays.
Prices are read from the Product Service and copied onto the order.

| Status | Meaning                                                         |
|--------|-----------------------------------------------------------------|
| 201    | Created; `Location` header points at the order                  |
| 400    | Empty order, duplicate product, mixed currency, bad quantity    |
| 404    | `user_not_found` or `product_not_found`                         |
| 409    | `insufficient_stock`, `user_inactive`, `product_inactive`       |
| 503    | `dependency_unavailable` — the User or Product Service is down  |

The order is returned in `AwaitingPayment`: stock is already reserved by the time the
response is sent. An `OrderCreated` event is published once it is stored (see
[Events published](#events-published)); the Payment Service reacts to it and the order
moves on to `Paid` or `Failed` asynchronously.

Send an `X-Correlation-Id` header to have it copied onto the event and everything that
follows from it; without one the service generates a UUID.

### `GET /api/v1/orders/{orderId}`

`200` with the order, or `404 order_not_found`.

### `GET /api/v1/orders?userId=...`

Query: `userId` (required), `limit` (1–100, default 20), `offset` (≥ 0, default 0).
Newest first.

```json
{ "items": [ /* orders */ ], "total": 12, "limit": 20, "offset": 0 }
```

> There is no authentication yet, so this endpoint currently trusts the `userId` in the
> query string. Weeks 5–6 move that to a verified JWT claim; until then **do not expose
> this service publicly**.

### `POST /api/v1/orders/{orderId}/pay` · `/ship` · `/cancel`

Advance the order. `cancel` accepts an optional `{"reason": "..."}` and an optional
`X-Correlation-Id` header, and publishes `OrderCancelled`.

`200` with the updated order, `404` if unknown, `409` if the transition is illegal.

Payment normally arrives as an event (see [Events consumed](#events-consumed)). `pay` is
kept **for manual recovery only** — an operator fixing an order whose payment event was
dead-lettered, say — and should not be routed through the gateway. The transition rules
are the same either way, because they live on the aggregate rather than in the endpoint or
the consumer.

## What happens when you place an order

1. `GET /api/v1/users/{userId}` on the User Service — exists and active?
2. `GET /api/v1/products/{id}` on the Product Service for each line — exists, active, and
   at what price?
3. Build the order in memory. Domain rules run here, so a bad request fails before any
   stock is touched.
4. `POST /api/v1/products/{id}/stock/reserve` for each line.
5. If any reservation fails, release the ones already taken and return `409`.
6. Persist the order as `AwaitingPayment`.
7. Publish `OrderCreated` to `orders`. A failure here is logged at Critical
   (`OrderCreated for order ... was NOT published`) and the request still returns `201` —
   see [Known gaps](#known-gaps).

Step 5 is a compensating action, and it is **best effort**. The release call can itself
fail, which would leave stock reserved against an order that does not exist. The service
logs that loudly rather than hiding it. Making it reliable is the Saga and outbox work in
week 7 — this is the honest interim position, not the finished design.

## Events published

Topic `orders`, **key = order id**, headers `eventType` and `correlationId`. The envelope,
serialisation rules and schemas are the contract in [`docs/events`](../events/README.md).

| Event | Version | When | Schema |
|---|---|---|---|
| `OrderCreated` | 1 | An order is stored as `AwaitingPayment` | [order-created.v1](../events/schemas/order-created.v1.schema.json) |
| `OrderCancelled` | 1 | `POST /cancel` succeeds | [order-cancelled.v1](../events/schemas/order-cancelled.v1.schema.json) |

Keyed by order id rather than user id because what must stay in order is the sequence of
events about **one order** — created before cancelled — and Kafka only orders within a
partition. Keying by user would also do that, but would pile a busy customer's traffic onto
one partition for no benefit.

Publishing happens **after** the database commit, with `acks=all`, idempotence on, and a
5 s delivery timeout (`ORDER_SERVICE_Kafka__DeliveryTimeoutMs`) so a broker outage cannot
hold an HTTP request for librdkafka's default five minutes. With
`ORDER_SERVICE_Kafka__BootstrapServers` empty, events are only written to the log.

## Events consumed

Topic `payments`, consumer group `order-service`, from the Payment Service.

| Event | Effect |
|---|---|
| `PaymentProcessed` v1 | `AwaitingPayment` → `Paid` |
| `PaymentFailed` v1 | `AwaitingPayment` → `Failed`, `statusReason` = `payment_failed: <reason>` (e.g. `payment_failed: card_declined`) |

- **At least once.** Auto-commit is off; the offset is committed only after the order change
  has been saved.
- **Idempotent.** The order change and a `processed_events` row
  (`consumer = order-service.payment-outcome`) are written by one `SaveChanges`, so a
  redelivered event finds the row and changes nothing.
- **Late events are ignored, not failed.** A payment for an order that was already
  cancelled, or a failure for one already paid, is logged and acknowledged — the state
  machine decides, and retrying cannot make an illegal move legal. A failed order keeps its
  reserved stock until #33.
- **Concurrency.** A save that loses an optimistic-concurrency race (`xmin`, e.g. against a
  manual `cancel`) is retried in a fresh scope, which reloads the order.
- **Dead letters.** A handler failure is tried 3 times in total, then the message goes to
  `payments.dlq` with its original key, value and headers plus `dlq-original-topic`,
  `dlq-original-partition`, `dlq-original-offset`, `dlq-consumer`, `dlq-error` and
  `dlq-failed-at`, and the offset moves on. Unparseable JSON and a known event at an
  unknown `eventVersion` go there at once. Other event types are ignored. If even the DLQ
  publish fails, the offset is not committed and the message is re-read after 5 s.
- **Pruning.** `processed_events` rows older than 8 days (topic retention plus a margin) are
  deleted hourly.

## Resilience

Every outbound call runs through a pipeline, outermost first:

1. **Total timeout** — caps the whole operation, retries included.
2. **Retry** — exponential backoff *with jitter*.
3. **Circuit breaker** — stops calling a dependency that is clearly down.
4. **Attempt timeout** — caps each individual try.

The attempt timeout sits *inside* the retry deliberately. Reversed, one slow call would
consume the whole budget and no retry would ever happen.

### Retries apply to idempotent calls only

| Call                            | Idempotent | Retried |
|---------------------------------|------------|---------|
| `GET /users/{id}`               | yes        | **yes** |
| `GET /products/{id}`            | yes        | **yes** |
| `POST /products/{id}/stock/reserve` | **no** | **no**  |
| `POST /products/{id}/stock/release` | **no** | **no**  |

This is the single most important decision in the client. Reserving stock twice reserves
twice. When a reservation times out we genuinely do not know whether it was applied, so
repeating it risks removing units that were already taken. The service fails the order and
lets the caller retry the whole operation instead.

### What counts as a transient failure

5xx, 408 and 429 responses, plus connection failures and timeouts. **4xx responses do not.**
A 404 or a 409 is a correct, considered answer from a healthy service — retrying it wastes
time, and letting it trip a circuit breaker would take a working dependency offline because
callers were sending bad requests.

### Defaults

| Setting                 | Default | Why                                          |
|-------------------------|---------|----------------------------------------------|
| Attempt timeout         | 2000 ms | A call with no timeout can hang forever      |
| Total timeout           | 8000 ms | A retrying call must not outlive its caller  |
| Max retries             | 2       | Three attempts total                         |
| Base retry delay        | 200 ms  | Doubles each retry, with jitter              |
| Breaker failure ratio   | 0.5     | Over a 30 s window                           |
| Breaker min throughput  | 8       | Below this the ratio is not meaningful       |
| Breaker break duration  | 15 s    | Then one trial call is allowed through       |

All tunable per environment via `ORDER_SERVICE_Downstream__*`.

## Data model

**orders**

| Column          | Type          | Notes                              |
|-----------------|---------------|------------------------------------|
| `id`            | uuid          | Primary key                        |
| `user_id`       | varchar(36)   | Indexed; no foreign key — different database |
| `status`        | integer       | Enum ordinal, not a string         |
| `currency`      | varchar(3)    | ISO-4217                           |
| `status_reason` | varchar(500)  | Why it was cancelled or failed     |
| `created_at`    | timestamptz   |                                    |
| `updated_at`    | timestamptz   |                                    |
| `xmin`          | xid           | Postgres system column, used as the concurrency token |

**order_lines**

| Column         | Type          | Notes                               |
|----------------|---------------|-------------------------------------|
| `id`           | uuid          | Primary key                         |
| `order_id`     | uuid          | FK to orders, cascade delete        |
| `product_id`   | varchar(36)   | No foreign key — different database |
| `sku`          | varchar(64)   | Copied at order time                |
| `product_name` | varchar(200)  | Copied at order time                |
| `unit_price`   | numeric(12,2) | Copied at order time                |
| `currency`     | varchar(3)    |                                     |
| `quantity`     | integer       |                                     |

**processed_events** — the shared deduplication table from
[`docs/events`](../events/README.md#consuming-events)

| Column         | Type          | Notes                               |
|----------------|---------------|-------------------------------------|
| `event_id`     | uuid          | Primary key, with `consumer`        |
| `consumer`     | varchar(64)   | `order-service.payment-outcome`     |
| `processed_at` | timestamptz   | Default `now()`; pruned after 8 days |

`status` is stored as an integer rather than a string so that renaming an enum member in
C# cannot silently orphan existing rows.

There are no foreign keys to users or products, and there cannot be — they live in other
databases. That is the cost of database-per-service, paid deliberately.

## Known gaps

- **No authentication.** `userId` comes from the request body and query string and is
  trusted. Weeks 5–6.
- **Reservation compensation is best effort.** See above; week 7.
- **Publishing is not transactional.** A crash or broker outage after commit loses the
  event (log line `was NOT published`), and the order sits in `AwaitingPayment` with nobody
  told. Reordering the two writes cannot fix a dual write. Fixed by the outbox, #34.
- **`ship` is a manual endpoint**, and `pay` remains as a manual recovery path.
- **A failed payment does not release stock.** #33.
- **Not yet run against a real broker or Postgres.** The Kafka publisher, the payments
  consumer loop, the migration and the Postgres duplicate-key detection (`23505`) are
  written for the real thing but have only been built, not exercised: the test suite
  fakes the broker and uses SQLite. The skippable `KafkaEventPublisherIntegrationTests`
  and the smoke test are what prove them once Docker is available.
- **Tests run on SQLite, not Postgres.** Close enough for the query shapes used here, but
  it needed a value converter for `DateTimeOffset` ordering, and it would not catch a
  Postgres-specific problem. Testcontainers is the real answer once Docker is available.
