# Payment Service API

**Base URL (local):** `http://localhost:8083`
**Stack:** FastAPI (Python) + PostgreSQL (`payment_db`, host port 5435) + Alembic + Kafka (aiokafka)
**Interactive docs:** `/docs` (Swagger UI) · `/openapi.json`

The Payment Service takes payment for orders. It is the only service that may read or
write `payment_db`. In the checkout it is driven by events: it consumes `OrderCreated`,
charges once per order, and publishes `PaymentProcessed` or `PaymentFailed`. The REST API
takes a payment directly and is how an operator inspects one.

## Conventions

- Resource routes are under `/api/v1`. JSON bodies are snake_case, like the User Service.
- Money is `NUMERIC(12, 2)` in the database and a decimal **string** on the wire
  (`"20.00"`), with an ISO-4217 `currency`. Never a float.
- Errors share one shape: `{"error": "<machine_code>", "message": "<human text>"}`.

## Health

| Method | Path            | Purpose                                              |
|--------|-----------------|------------------------------------------------------|
| GET    | `/health/live`  | Liveness. Does not touch the database.               |
| GET    | `/health/ready` | Readiness. Returns `503` when the database is down.  |

The consumer process serves no HTTP and has no health endpoint; see [Known gaps](#known-gaps).

## The payment lifecycle

```
   Pending ──► Authorised ──► Captured ──► Refunded
      │             │
      └─────────────┴──► Failed
```

| From         | May move to              |
|--------------|--------------------------|
| `Pending`    | `Authorised`, `Failed`   |
| `Authorised` | `Captured`, `Failed`     |
| `Captured`   | `Refunded`               |
| `Failed`     | — terminal               |
| `Refunded`   | — terminal               |

Anything else raises `illegal_payment_transition` (`409` if it ever reaches HTTP). The
table lives in `app/models/payment_state.py` and every transition goes through it. The
fake provider authorises and captures in one call, so a successful payment is `Captured`
by the time it is returned. Nothing refunds yet (week 7's saga does).

## Endpoints

### `POST /api/v1/payments` — take a payment

Headers: **`Idempotency-Key`** (required, 1–100 characters) — a client-chosen name for
this payment attempt.

```json
{ "order_id": "o-1", "user_id": "u-1", "amount": "20.00", "currency": "GBP" }
```

Validation: `order_id` and `user_id` 1–36 characters; `amount` > 0 with at most two
decimal places and twelve digits; `currency` three upper-case letters.

| Status | Meaning |
|--------|---------|
| 201    | Created. The provider was called; the body is the payment, `Captured` or `Failed` |
| 200    | A repeat of an earlier request with this key. The **same** payment, provider not called again |
| 409    | `idempotency_key_reused` — this key was already used with a different body |
| 422    | Missing `Idempotency-Key` header, or the body failed validation |

A declined payment is still a `201`: the request succeeded, the card did not. Read
`status` and `failure_reason` (`card_declined`).

Response (`201`):

```json
{
  "id": "0bcaa1f2-...",
  "order_id": "o-1",
  "user_id": "u-1",
  "idempotency_key": "smoke-1",
  "amount": "20.00",
  "currency": "GBP",
  "status": "Captured",
  "provider_reference": "fake_1f0c2b...",
  "failure_reason": null,
  "created_at": "2026-09-24T10:00:00Z",
  "updated_at": "2026-09-24T10:00:00Z"
}
```

#### Idempotency semantics

Taking a payment is not naturally idempotent: two identical POSTs are two charges. The
key makes a retry safe.

1. A row with this key already exists → return it (`200`). If the body differs, `409`.
   Amounts compare numerically, so `"20"` and `"20.00"` are the same request.
2. Otherwise insert the payment as `Pending` and **commit**, then call the provider, then
   record `Captured` or `Failed` and commit again. Committing `Pending` first means a
   crash mid-charge leaves evidence that a charge may be in flight, instead of nothing.
3. Two concurrent requests with one key: the unique constraint on `idempotency_key` lets
   exactly one insert win. The loser rolls back (in Postgres the failed insert has
   aborted its transaction) and returns the winner's row. The constraint is the real
   guard; the lookup in step 1 is only the fast path.

The provider is passed the idempotency key as its reference, the same way real providers
(Stripe, Adyen) deduplicate charges.

### `GET /api/v1/payments/{payment_id}`

`200` with the payment, or `404 payment_not_found`.

### `GET /api/v1/payments?order_id=...`

Query: `order_id` (optional filter), `limit` (1–100, default 20), `offset` (≥ 0,
default 0). Newest first.

```json
{ "items": [ /* payments */ ], "total": 1, "limit": 20, "offset": 0 }
```

## Events

Contract: [`docs/events/README.md`](../events/README.md). The consumer is a separate
process from the same image (`python -m app.consumer`), group `payment-service`.

### Consumed

| Topic    | Event            | Version | What happens |
|----------|------------------|---------|--------------|
| `orders` | `OrderCreated`   | 1       | One payment for the order, idempotency key `order:<orderId>`, amount `totalAmount` |
| `orders` | `OrderCancelled` | any     | Ignored and acknowledged (not ours to act on yet) |
| `orders` | `OrderCreated`   | ≠ 1     | Dead-lettered: a contract break, not noise |

Only `orderId`, `userId`, `userEmail`, `totalAmount` and `currency` are read; other fields
are ignored.

### Emitted

| Topic      | Event              | When | Payload |
|------------|--------------------|------|---------|
| `payments` | `PaymentProcessed` | the payment reached `Captured` | `paymentId`, `orderId`, `userId`, `userEmail`, `amount`, `currency`, `processedAt` |
| `payments` | `PaymentFailed`    | the payment reached `Failed`   | the same, with `reason` (e.g. `card_declined`) and `failedAt` instead of `processedAt` |

Key = `orderId`. Headers `eventType` and `correlationId`. `producer` is
`payment-service`; `correlationId` is copied from the `OrderCreated`, so one checkout can
be followed across services. `occurredAt` is when the payment reached its final status.
The producer uses `acks="all"` and `enable_idempotence=True`.

### Delivery, deduplication and the dead letter topic

Per message, in order:

1. Parse the envelope. Unparseable → dead letter at once.
2. If `processed_events` has `(eventId, "payment-service.order-created")`: skip it — unless
   the outcome was never published, in which case publish it again (see step 5).
3. Find or create the payment for `order:<orderId>` (committed `Pending`) and charge it if it
   is still `Pending`. A redelivery after a crash mid-charge therefore **resumes the same
   payment** rather than creating a second one.
4. In **one** transaction: the final status, the `processed_events` marker (inserted with
   `ON CONFLICT DO NOTHING`), and `outcome_event_id = uuid4()` on the payment.
5. Publish the outcome with `eventId = outcome_event_id`, then set `outcome_published_at`.
   Because the id is stored, a re-publish after a failed send or a crash carries the same
   `eventId`, and downstream deduplication still works.
6. Only then commit the Kafka offset.

A failure is retried up to **3 attempts** in total with a short linear backoff (0.5 s,
1 s). Unparseable and unknown-version messages are not retried. After the last attempt
the record is published to `orders.dlq` with its original key, value and headers plus
`dlq-original-topic`, `dlq-original-partition`, `dlq-original-offset`,
`dlq-consumer: payment-service.order-created`, `dlq-error` (≤ 500 characters) and
`dlq-failed-at`, and the offset is committed so the partition moves on. If the
dead-letter publish itself fails, the consumer exits without committing and the record is
redelivered after the restart.

The consumer deletes `processed_events` rows older than **8 days** (topic retention plus
a day) once at start-up and then hourly. `SIGTERM` stops it after the current message;
it leaves the consumer group cleanly.

## Data model

`payments`

| Column                 | Type          | Notes |
|------------------------|---------------|-------|
| `id`                   | varchar(36)   | UUID4, primary key |
| `order_id`             | varchar(36)   | Indexed |
| `user_id`              | varchar(36)   | |
| `idempotency_key`      | varchar(100)  | Unique (`uq_payments_idempotency_key`) — the double-charge guard |
| `amount`               | numeric(12,2) | |
| `currency`             | varchar(3)    | ISO-4217 |
| `status`               | varchar(20)   | `Pending`, `Authorised`, `Captured`, `Failed`, `Refunded` |
| `provider_reference`   | varchar(100)  | Set when authorised |
| `failure_reason`       | varchar(100)  | Set when failed, e.g. `card_declined` |
| `outcome_event_id`     | varchar(36)   | eventId of the outcome event; fixed once chosen |
| `outcome_published_at` | timestamptz   | Null until the broker acknowledged the outcome |
| `created_at`           | timestamptz   | |
| `updated_at`           | timestamptz   | |

`processed_events`

| Column         | Type        | Notes |
|----------------|-------------|-------|
| `event_id`     | uuid        | Primary key, with `consumer` |
| `consumer`     | varchar(64) | `payment-service.order-created` |
| `processed_at` | timestamptz | Default `now()`; pruned after 8 days |

The schema comes from the Alembic migrations in `services/payment-service/alembic/`,
applied by the container before uvicorn starts.

## Known gaps

These are deliberate, and scheduled for later weeks of the plan:

- **Fake provider only.** It declines anything above `PAYMENT_SERVICE_FAKE_DECLINE_ABOVE`
  (default `1000.00`) and approves everything else. No real card processor, no 3-D Secure.
- **No authentication on the endpoints.** Week 5–6 adds the API Gateway and JWT
  validation. Until then do not expose this service publicly.
- **Publish after commit, not an outbox.** If the consumer crashes after the database
  commit but before the publish, and the message is then never redelivered, the outcome
  is never sent. Redelivery covers the usual case (the offset is committed last); the
  transactional outbox (#34) closes it fully.
- **A `Pending` payment is not resumed over HTTP.** A repeat `POST` returns it as it is;
  only a redelivered `OrderCreated` resumes a charge that crashed midway.
- **The consumer has no health probe.** Its container's `HEALTHCHECK` is disabled; a
  stuck consumer shows up as consumer-group lag, not as an unhealthy container.
- **Unverified against real infrastructure.** Built without Docker: the tests run on
  SQLite with a fake provider, consumer and producer. The migrations have not yet been
  applied to Postgres on `localhost:5435`, and the consumer has not yet run against a real
  broker. The `ON CONFLICT DO NOTHING` path for Postgres is written but only exercised on
  SQLite.
- **Timestamps lack a UTC offset in the database when running on SQLite.** The API adds
  `Z`; the columns are declared `timestamptz` and Postgres stores the offset itself.
