# Week 4 — Event-driven architecture (Days 22–28)

Plan for the week: *add a message broker; publish OrderCreated; build the Payment and
Notification services as consumers.*

## What was built

- **Kafka** in Compose, KRaft mode, one broker. Topics come from a script in source control,
  not from auto-creation. Kafka UI on 8090. [ADR 0001](../decisions/0001-message-broker.md)
  records why Kafka and what that cost.
- **The event contract** — [docs/events](../events/README.md): one envelope, four versioned
  JSON Schemas, examples, a validator script, and written rules for consumers.
- **Order Service** publishes `OrderCreated` and `OrderCancelled` and consumes payment
  outcomes, which now drive `AwaitingPayment → Paid | Failed`. 102 tests.
- **Payment Service** — FastAPI, Postgres, Alembic from the first commit. An idempotent
  payments API, a fake provider that declines deterministically, and a separate consumer
  process. 90 tests.
- **Notification Service** — Spring Boot, Postgres, Flyway. Records every notification,
  sends through an interface whose only implementation logs, retries failed sends from the
  row. 48 tests.
- **Idempotent consumers and dead letter topics** in all three stacks, plus a
  [runbook](../runbooks/dead-letter-queue.md), a [smoke test](../../scripts/smoke-test.sh)
  for a full checkout and a [redelivery drill](../../scripts/redelivery-drill.sh).

## Concepts, and where they show up in the code

### An event is a contract, and a harder one than an endpoint

A REST endpoint has callers you can see in the logs. A topic has consumers you may not
know about, and every event it ever carried stays readable for the retention period. So the
contract was written before the first producer: the envelope, the schemas, and the rule for
what counts as a breaking change (removing, renaming or changing the type or meaning of a
field). Breaking changes ship as a new `eventVersion` alongside the old one — never as a
flag day.

Two details that look small and are not:

- **Money is a string.** `"99.98"`, not `99.98`. Python's `json` parses a number as a
  `float`, and the Payment Service is Python.
- **There is no shared contracts library.** Each consumer has its own model of the events it
  reads, with only the fields it uses (`OrderCreatedV1` exists three times, in three
  languages, all different). A shared library would couple every service's release to every
  other's. The schemas and the validator are what keep the copies honest; contract tests
  (#49) will do it automatically.

### Events carry state, not just an id

`OrderCreated` carries the email, the priced lines and the total. If it carried only an order
id, the Payment and Notification services would both call the Order Service back to read the
order — which puts the synchronous coupling straight back, and fails in a new way when the
Order Service is down while its events are not.

### Key by order id

Kafka orders messages within a partition and nowhere else. The thing that must stay in
order is the story of one order: `OrderCreated` before `PaymentProcessed`. So every event on
both topics is keyed by `orderId`. Keying by user would put a user's unrelated orders in
sequence behind each other and buy nothing.

### The dual write, made visible rather than solved

`CreateOrderHandler` commits the order, *then* publishes. A crash or broker outage in
between leaves an order in `AwaitingPayment` that nobody heard about. Publishing first is not
better: a failed commit would announce an order that does not exist. No ordering of two
writes to two systems is safe.

The handler therefore logs `OrderCreated for order … was NOT published` at Critical, still
returns 201 — the order really was placed — and points at #34. The transactional outbox is
the fix, in week 7. This is the same approach as the week 3 compensation code: say where the
gap is.

### At least once, and why exactly-once is the wrong goal

Every consumer turns auto-commit off and commits the offset **after** its database
transaction commits. A crash in between redelivers the message. That is deliberate: a
duplicate can be detected, a lost message cannot.

Exactly-once delivery across a broker and a database does not exist. The answer is
at-least-once delivery plus consumers that are idempotent:

- Each consuming service has a `processed_events` table, primary key `(event_id, consumer)`.
- The marker row is written **in the same transaction as the business change**. A separate
  "have I seen this?" query followed by a write is exactly the race it is meant to close.
- Where the database allows it, `INSERT … ON CONFLICT DO NOTHING` and check the row count.
  In Postgres a failed insert aborts the whole transaction, so catching the error is not an
  option.

`scripts/redelivery-drill.sh` proves it by replaying every topic from the beginning into
every consumer and asserting nothing changed — no second payment, no second notification,
no new event on `payments`.

### Money needs two keys, not one

The Payment Service uses idempotency twice, at two different layers:

- **`Idempotency-Key` on the API.** The same key returns the same payment (`201`, then `200`
  with the identical body). The same key with a different body is a `409`. The uniqueness is
  a database constraint, not a check in code.
- **`order:<orderId>` from the consumer.** A redelivered `OrderCreated` resumes the same
  payment instead of creating a second one — including a payment that crashed half way,
  because `Pending` is committed *before* the provider is called.

This is week 3's lesson from the other side. Week 3: do not retry a call that is not
idempotent. Week 4: make the call idempotent, and then the caller can retry.

### Re-publishing must reuse the event id

When the Payment Service re-publishes an outcome (the first publish failed, or a duplicate
arrived before the outcome was acknowledged), it uses the `outcome_event_id` stored on the
payment, not a new UUID. A fresh id would make the duplicate look like a new event to every
downstream consumer, and they would all act on it again.

### Insert, then send

The Notification Service records the notification row and commits, *then* sends. If it
crashes after sending but before recording, the retry sends a second email. If it crashes
after recording but before sending, the row is there as `PENDING`, and the retry job sends
it. The first failure is visible to the customer and the second is not, so the order is
insert-then-send.

`record()` and `dispatch()` are separate `@Transactional` methods called from the listener.
If one called the other inside the same bean, Spring's proxy would be bypassed and there
would be no separate transaction.

Failed sends are retried **from the row** by a scheduled job, not by redelivering the Kafka
message. The message has been handled; the email is what failed. Attempts are capped at
five, then `PERMANENTLY_FAILED` — a bad address never becomes a good one.

### A poison message must not block a partition

A message that always fails would stop its partition forever, because the offset cannot
move past it. After three attempts it goes to `<topic>.dlq` with its original key, value and
headers plus `dlq-*` headers saying who failed and why, and the offset is committed.
Unparseable messages and unknown versions go there at once — retrying them cannot help.

`orders` has two consumers, so one bad message produces two DLQ entries. `dlq-consumer`
tells them apart.

### Decoupling you can see

Stop `payment-consumer` and place an order: it is accepted and sits in `AwaitingPayment`.
Start the consumer again and the order becomes `Paid`. The Order Service never knew the
Payment Service was down. That is the thing the broker buys, and it is also why the frontend
(#48) has to show a pending state — payment is genuinely not ready when the order is placed.

## Open items carried forward

1. **Nothing has run against a real broker or Postgres.** Docker is still not installed
   (#8). Every consumer and producer is unit-tested against fakes, and captured events
   validate against the schemas, but the Compose stack, `smoke-test.sh` and
   `redelivery-drill.sh` have never been run.
2. **Two Notification Service tests have never run.** The `@EmbeddedKafka` tests crash the
   JVM on this machine: a system-wide Winsock provider (`ASProxy64.dll`) breaks Java NIO
   sockets. They need Linux or CI (#50).
3. **The dual write** in both producers. Order and Payment publish after committing. Outbox,
   #34.
4. **A failed payment leaves stock reserved.** The order becomes `Failed`, the reservation
   is not released. Compensation, #33.
5. **The fake provider is the only provider**, and the logging sender is the only sender.
   Deliberately.
6. **Still no authentication.** The payment and notification APIs are as open as the rest.
   Weeks 5–6.
7. **Kafka has replication factor 1.** Local only; see the ADR.

## Questions worth being able to answer in an interview

*"Why commit the offset after processing and not before?"* Before means a crash loses the
message: it is acknowledged but never handled. After means a crash redelivers it. You can
detect a duplicate, with an id and a unique key; you cannot detect a loss. So you commit
after, and make the consumer idempotent.

*"The consumer charged the card and crashed before recording the event. What happens?"*
The message is redelivered. The payment was keyed `order:<orderId>`, so the service finds
the existing payment instead of creating a new one. If it was still `Pending`, the provider
is called again **with the same reference**, and a real provider treats that reference as its
own idempotency key, so the customer is charged once. That makes two keys in the chain:
ours stops a second payment row, the provider's stops a second charge. Without them, the
customer is charged twice.

*"Why not have the Payment Service call the Order Service to read the order?"* Because then
the Payment Service is only as available as the Order Service, and the broker has bought
nothing. The event carries what the consumer needs.
