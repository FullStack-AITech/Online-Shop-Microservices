# Notification Service API

**Base URL (local):** `http://localhost:8084`
**Stack:** Spring Boot 3 (Java 21) + PostgreSQL + Flyway + Kafka (Spring Kafka)
**Interactive docs:** `/swagger-ui.html` · `/v3/api-docs`

The Notification Service tells customers what happened to their orders and payments. It
reacts to events and has no write API. Every message it decides to send is recorded in
`notification_db` first, and only this service reads or writes that database.

## Conventions

- Resource routes are under `/api/v1`.
- Errors share one shape:
  `{"error": "<code>", "message": "...", "path": "...", "timestamp": "..."}`.
- **Nothing is really sent.** All delivery goes through a `NotificationSender` interface.
  The only implementation (`notification.sender: logging`, the default) writes one log line
  per message, with the channel, a masked recipient (`a***@example.com`) and the subject.
  It never logs the body.

## Health and metrics

| Method | Path                          | Purpose                          |
|--------|-------------------------------|----------------------------------|
| GET    | `/actuator/health/liveness`   | Liveness probe                   |
| GET    | `/actuator/health/readiness`  | Readiness probe (includes the DB)|
| GET    | `/actuator/prometheus`        | Metrics, used from week 10       |

Kafka is not part of readiness. The API still answers when the broker is down, and the
consumer catches up once it is back.

## Endpoints

The API is read-only on purpose. Notifications are created by events, and nothing yet
needs to create, change or delete one over HTTP. `POST`, `PATCH` and `DELETE` return `405`.

### `GET /api/v1/notifications?userId=...` — a user's history

Query: `userId` (required), `page` (default 0), `size` (1–100, default 20). Newest first.

```json
{
  "items": [
    {
      "id": "0b6c1d2e-...",
      "eventId": "7d1b8f5e-2c3a-4f6b-9a1e-0c5d4b3a2f10",
      "eventType": "OrderCreated",
      "type": "ORDER_CONFIRMATION",
      "channel": "EMAIL",
      "userId": "9a8b7c6d-...",
      "orderId": "3f2b1c9e-...",
      "recipient": "a***@b.com",
      "subject": "We have received your order 3f2b1c9e-...",
      "status": "SENT",
      "sendAttempts": 1,
      "lastError": null,
      "sentAt": "2026-09-24T10:00:01.120Z",
      "createdAt": "2026-09-24T10:00:01.100Z",
      "updatedAt": "2026-09-24T10:00:01.120Z"
    }
  ],
  "total": 1, "page": 0, "size": 20
}
```

| Status | Meaning                                              |
|--------|------------------------------------------------------|
| 200    | A page, possibly empty                               |
| 400    | `validation_failed` (no `userId`) or `invalid_request` (blank `userId`) |

The recipient is **masked** and the body left out, because the API has no authentication
yet and anyone who knows a user id can call it.

### `GET /api/v1/notifications/{id}`

`200` with one notification in the shape above, or `404 notification_not_found`.

## Events consumed

The consumer group is `notification-service`. It reads `orders` and `payments` through
one listener, with values as plain strings parsed by the service's own model of the
contract ([docs/events](../events/README.md)). It reads only the fields it uses.

| Event              | Topic      | Notification         | Recipient            |
|--------------------|------------|----------------------|----------------------|
| `OrderCreated` v1     | `orders`   | `ORDER_CONFIRMATION` | `payload.userEmail`  |
| `PaymentProcessed` v1 | `payments` | `PAYMENT_RECEIPT`    | `payload.userEmail`  |
| `PaymentFailed` v1    | `payments` | `PAYMENT_PROBLEM`, which quotes `reason` | `payload.userEmail`  |
| `OrderCancelled`, any other type | — | ignored and acknowledged (logged at debug) | — |

A known type with an unknown `eventVersion` is dead-lettered, not ignored: that is a
contract break. Unknown fields are ignored.

This service emits no events.

## How an event becomes a notification

1. **Record**, in one transaction: insert `(eventId, "notification-service")` into
   `processed_events` with `ON CONFLICT DO NOTHING`. If 0 rows are affected the event has
   been handled already, so skip it. Otherwise render the subject and body and insert a
   `PENDING` row into `notifications`.
2. **Send**, in a second transaction: call the sender, then mark the row `SENT`, or
   `FAILED` with the error.
3. The listener returns and the container commits the offset.

Insert-then-send is deliberate. The row always exists before any attempt, so the failure
it allows is "recorded but not sent", which the retry job repairs. Send-then-insert would
allow "sent but not recorded", which the customer sees as a second email when the event
is redelivered.

The unique constraint `(event_id, channel)` on `notifications` backs up `processed_events`.
Even if the dedup step were bypassed, an event could not produce two emails.

## Statuses and retry policy

```
PENDING ──send ok──► SENT
   │
   └──send fails──► FAILED ──retry ok──► SENT
                      │
                      └──5th failure──► PERMANENTLY_FAILED
```

There are two kinds of retry, and they solve different problems:

| Failure | Who retries | Policy |
|---|---|---|
| The **listener** throws (database down, bad JSON, unknown version) | Spring Kafka's `DefaultErrorHandler` | 3 attempts in total, 1 s apart, then the message goes to `<topic>.dlq`. Unparseable, malformed and unknown-version messages skip the retries |
| The **sender** fails (provider down, address rejected) | `NotificationRetryJob`, every 60 s, driven by the row | Up to `NOTIFICATION_SERVICE_MAX_SEND_ATTEMPTS` (5) attempts in total, then `PERMANENTLY_FAILED`, never picked up again |

A send failure is not retried through Kafka. The event is already recorded, so a
redelivery would be skipped as a duplicate. Retrying the row means no second row can
appear. The job also sends `PENDING` rows older than 5 minutes, which only exist if the
service died between recording and sending.

### Dead letter topics

A message that exhausts its attempts goes to `orders.dlq` or `payments.dlq` (partition 0)
with its original key, value and headers, plus:

| Header | Value |
|---|---|
| `dlq-original-topic` / `-partition` / `-offset` | where it came from |
| `dlq-consumer` | `notification-service` |
| `dlq-error` | exception class and message, at most 500 characters |
| `dlq-failed-at` | ISO-8601 UTC |

Spring's own `kafka_dlt-*` headers are suppressed so all three services' DLQ entries look
the same. The offset is then committed and the partition moves on. `orders` also has the
Payment Service as a consumer, so one poison message there produces two DLQ entries, which
`dlq-consumer` tells apart. Replaying a DLQ message onto its topic is safe because
consumers deduplicate on `eventId`.

### Pruning

Every hour (`0 0 * * * *`), `processed_events` rows older than 8 days are deleted
(`NOTIFICATION_SERVICE_PROCESSED_EVENTS_RETENTION`, default `P8D`). That is the topic
retention of 7 days plus a margin. Beyond it an event cannot be redelivered anyway.
Notification rows are kept: they are the audit trail.

## Data model

`notifications`

| Column              | Type          | Notes                                            |
|---------------------|---------------|--------------------------------------------------|
| `id`                | varchar(36)   | UUID4, primary key                               |
| `event_id`          | uuid          | The envelope's `eventId`; unique with `channel`  |
| `event_type`        | varchar(64)   | e.g. `OrderCreated`                              |
| `notification_type` | varchar(40)   | `ORDER_CONFIRMATION`, `PAYMENT_RECEIPT`, `PAYMENT_PROBLEM` |
| `channel`           | varchar(20)   | `EMAIL`                                          |
| `user_id`           | varchar(36)   | Indexed with `created_at DESC` for the history API |
| `order_id`          | varchar(36)   |                                                  |
| `recipient`         | varchar(320)  | The longest address RFC 5321 allows              |
| `subject`           | varchar(200)  | Rendered once, when the row is created           |
| `body`              | varchar(2000) | Likewise, so a retry sends the same words        |
| `status`            | varchar(30)   | `PENDING`, `SENT`, `FAILED`, `PERMANENTLY_FAILED`; indexed |
| `send_attempts`     | integer       | Successful attempts count too                    |
| `last_error`        | varchar(500)  | The most recent failure, kept after a later success |
| `sent_at`           | timestamptz   |                                                  |
| `created_at`        | timestamptz   |                                                  |
| `updated_at`        | timestamptz   |                                                  |
| `version`           | bigint        | Optimistic lock: the listener and the retry job can race for a row |

`processed_events`: `event_id uuid`, `consumer varchar(64)`, `processed_at timestamptz
DEFAULT NOW()`, primary key `(event_id, consumer)`. This is the shared shape from
[docs/events](../events/README.md#consuming-events).

## Known gaps

- **Logging sender only.** No email is delivered. A real provider is a new
  `NotificationSender`, selected with `NOTIFICATION_SERVICE_SENDER`.
- **No authentication.** Anyone who knows a user id can read that user's history, which is
  why recipients are masked. This comes with the gateway in weeks 5–6.
- **Unverified against real infrastructure.** This was built without Docker. H2 (PostgreSQL
  mode) with Flyway and `ddl-auto: validate` is tested, but Flyway on a real Postgres, the
  Compose container and the smoke-test rows have not been run. The `@EmbeddedKafka` tests
  (duplicate publish, poison message to `orders.dlq`) are written but could not run on the
  development machine, because a third-party Winsock provider there crashes Java NIO. The
  error-handler and DLQ logic is also tested without a broker, using Kafka's mock producer
  and consumer.
- **A send is not atomic with its outcome.** If the database commit fails after the provider
  accepted a message, the row stays `FAILED` or `PENDING` and the message is sent again.
  Only an idempotency key at the provider fixes that.
- **One replica only.** Two retry jobs could pick the same row. Optimistic locking prevents
  a double write but not a double send. Scaling out (#40) needs `SELECT ... FOR UPDATE SKIP
  LOCKED`.
- **Listener retries are short.** A database outage longer than about 3 s dead-letters
  valid messages. They can be replayed safely from the DLQ (runbook), but a longer backoff
  or pausing the container on database errors would be kinder.
- **English plain-text templates in code.** Nothing is localised and there is no HTML.
