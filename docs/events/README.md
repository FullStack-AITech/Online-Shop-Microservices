# Events

Events are a public contract between services, and a harder one than a REST endpoint: a
producer cannot see who consumes them, and old events stay in the log for the topic's
retention. This page is the contract. Every producer and consumer follows it.

- [Envelope](#envelope)
- [Serialisation](#serialisation)
- [Topics and keys](#topics-and-keys)
- [Event catalogue](#event-catalogue)
- [Compatibility](#compatibility)
- [Consuming events](#consuming-events)
- [Ownership](#ownership)

## Envelope

Every event has the same outer shape, so any consumer can route, deduplicate and trace it
without understanding the payload.

```json
{
  "eventId": "7d1b8f5e-2c3a-4f6b-9a1e-0c5d4b3a2f10",
  "eventType": "OrderCreated",
  "eventVersion": 1,
  "occurredAt": "2026-09-24T10:00:00Z",
  "correlationId": "5b0f6f5c-9a8e-4d3c-b2a1-f0e9d8c7b6a5",
  "producer": "order-service",
  "payload": { }
}
```

| Field | Type | Rule |
|---|---|---|
| `eventId` | UUID | Generated **once**, when the event is created. A retried publish of the same event reuses it — a fresh id on retry defeats every consumer's deduplication |
| `eventType` | string, PascalCase | Past tense: something that has happened |
| `eventVersion` | integer ≥ 1 | The payload schema version. See [Compatibility](#compatibility) |
| `occurredAt` | ISO-8601 UTC, `Z` suffix | When the fact became true, not when it was published |
| `correlationId` | string | Taken from the incoming `X-Correlation-Id` header, else a new UUID. A consumer copies it onto every event it emits in reaction, so one checkout can be followed across every service (#41) |
| `producer` | string | The service name, e.g. `order-service` |
| `payload` | object | The event-specific body |

## Serialisation

- UTF-8 JSON, **camelCase** property names.
- **Money is a decimal string with two decimal places** (`"38.00"`) plus an ISO-4217
  `currency`. Never a JSON number: Python's `json` parses numbers as `float`, and
  `0.1 + 0.2` is not a price. `Decimal`, `BigDecimal` and `decimal` all parse strings
  losslessly.
- Timestamps are ISO-8601 in UTC.

## Topics and keys

| Topic | Events | Key |
|---|---|---|
| `orders` | `OrderCreated`, `OrderCancelled` | `orderId` |
| `payments` | `PaymentProcessed`, `PaymentFailed` | `orderId` |
| `orders.dlq`, `payments.dlq` | Poison messages from the topic above | original key |

The message key is **always the order id**. Kafka orders messages only within a
partition, and what must stay ordered is the sequence of events about one order.

`eventType` and `correlationId` are also sent as Kafka **headers**, so tooling and the
dead-letter queue can read them without parsing the value.

Topic partitions and retention: [ADR 0001](../decisions/0001-message-broker.md).

## Event catalogue

| Event | Version | Topic | Producer | Consumers | Schema | Example |
|---|---|---|---|---|---|---|
| `OrderCreated` | 1 | `orders` | order-service | payment-service, notification-service | [schema](schemas/order-created.v1.schema.json) | [example](examples/order-created.v1.example.json) |
| `OrderCancelled` | 1 | `orders` | order-service | — (week 7 saga) | [schema](schemas/order-cancelled.v1.schema.json) | [example](examples/order-cancelled.v1.example.json) |
| `PaymentProcessed` | 1 | `payments` | payment-service | order-service, notification-service | [schema](schemas/payment-processed.v1.schema.json) | [example](examples/payment-processed.v1.example.json) |
| `PaymentFailed` | 1 | `payments` | payment-service | order-service, notification-service | [schema](schemas/payment-failed.v1.schema.json) | [example](examples/payment-failed.v1.example.json) |

All schemas are JSON Schema draft 2020-12 and build on the
[envelope schema](schemas/envelope.v1.schema.json). Check the examples with:

```bash
python scripts/validate-event-schemas.py            # every example
python scripts/validate-event-schemas.py some.json  # a captured message
```

### Events carry everything the consumer needs

`OrderCreated` carries the user's email, the priced lines and the total — not just an order
id. A consumer that has to call the producer back to read the order has reintroduced the
coupling the broker was supposed to remove, and fails differently when the producer is
down.

`OrderCancelled` carries no email: the order does not store one, and the email is only
known at creation time. A consumer that needs it keeps it from `OrderCreated`.

### Personal data

Only `userEmail` travels, and it persists in the log for the topic retention (7 days).
Nothing else personal goes into an event.

## Compatibility

- **Safe:** adding an optional payload field. Consumers ignore fields they do not know,
  and the schemas allow extra payload properties so validators agree.
- **Breaking:** removing or renaming a field, or changing its type or its meaning.

To ship a breaking change, publish `eventVersion: N+1` **alongside** version N, move the
consumers across, and retire version N only when no consumer reads it. There is no flag
day. Contract tests (#49) are what enforce this rule rather than trusting it.

## Consuming events

The rules every consumer follows, in every stack.

### Delivery is at least once

Consumers disable auto-commit and **commit the offset only after the database transaction
has committed**. A crash between the two redelivers the message. That is the intended
failure mode: a duplicate can be detected, a lost message cannot.

Exactly-once delivery is not the goal, because across a broker and a database it does
not exist. At-least-once delivery plus idempotent consumers is the real answer.

### Deduplicate on `eventId`, in the business transaction

Each consuming service owns a `processed_events` table in its **own** database:

```sql
CREATE TABLE processed_events (
    event_id     UUID        NOT NULL,
    consumer     VARCHAR(64) NOT NULL,
    processed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (event_id, consumer)
);
```

- The marker row is written **in the same local transaction as the business change**.
  Either both commit or neither does. A separate "have I seen this?" check followed by a
  separate write is the race this closes.
- Key on `eventId` from the envelope, never on a hash of the payload — two genuinely
  different events can have identical payloads.
- The key is `(event_id, consumer)`, so two handlers in one service can each process the
  same event once.
- Where the database allows it, insert with `ON CONFLICT DO NOTHING` and treat 0 rows
  affected as "duplicate: skip and acknowledge". In Postgres a failed insert aborts the
  whole transaction, so catching the error is not an option.

| Service | Consumer name |
|---|---|
| payment-service | `payment-service.order-created` |
| order-service | `order-service.payment-outcome` |
| notification-service | `notification-service` |

### Unknown things

- Unknown **fields**: ignore.
- Unknown **`eventType`**: ignore and acknowledge (log at debug). A topic carries several
  types and a consumer handles the ones it cares about.
- Known type, unknown **`eventVersion`**: dead-letter it. That is a contract break, not
  noise.
- Unparseable JSON: dead-letter it.

### Dead letter topics

A message that always fails blocks its partition forever. So:

1. Retry a failing message up to **3 attempts** in total, with a short backoff.
   Unparseable and unknown-version messages are not retried — they will never succeed.
2. Then publish it to `<topic>.dlq` with its original key, value and headers, plus:

   | Header | Value |
   |---|---|
   | `dlq-original-topic` | e.g. `orders` |
   | `dlq-original-partition` | partition number |
   | `dlq-original-offset` | offset |
   | `dlq-consumer` | the consumer name — `orders` has two consumers, so one poison message can produce two DLQ entries |
   | `dlq-error` | exception type and message, truncated to 500 characters |
   | `dlq-failed-at` | ISO-8601 UTC |

3. Commit the original offset and move on.

**A DLQ nobody looks at is a data loss bug with extra steps.** Draining it is in the
[dead letter queue runbook](../runbooks/dead-letter-queue.md), and its depth is alerted on
(#44).

### Pruning

`processed_events` grows forever otherwise. Each service deletes rows older than the
topic retention plus a margin — **8 days** — because beyond retention a message cannot be
redelivered anyway.

## Ownership

The **producer owns the schema** and publishes it here. Each consumer writes its **own**
model of the events it consumes, containing only the fields it uses. There is deliberately
no shared contracts library: that would couple every service's release to every other's —
a distributed monolith with extra steps.
