# Runbook: dead letter queue has messages

**Alert:** DLQ depth > 0 (#44). **Severity:** page — something was dropped from the normal
flow, and until someone acts, an order, a payment or a notification is stuck.

The rules that put a message here are in
[docs/events/README.md](../events/README.md#dead-letter-topics): a message went to
`<topic>.dlq` after 3 failed attempts, or immediately if it could not be parsed or had an
`eventVersion` the consumer does not know.

## 1. See what is there

Kafka UI → **Topics** → `orders.dlq` / `payments.dlq` → **Messages**, or:

```bash
docker compose exec kafka kafka-get-offsets --bootstrap-server localhost:9092 --topic orders.dlq
docker compose exec kafka kafka-console-consumer --bootstrap-server localhost:9092 \
  --topic orders.dlq --from-beginning --timeout-ms 5000 \
  --property print.key=true --property print.headers=true
```

For each message, read the headers:

| Header | Tells you |
|---|---|
| `dlq-consumer` | **Which** consumer gave up. `orders` has two consumers, so one bad message can appear twice |
| `dlq-error` | Why |
| `dlq-original-topic` / `-partition` / `-offset` | Where it came from |
| `eventType`, `correlationId` | What it was, and how to find the rest of the request in the logs |

## 2. Decide

| Cause | Action |
|---|---|
| **A bug in the consumer**, now fixed and deployed | Replay the message onto its original topic (step 3) |
| **A dependency was down** for longer than the retries (database, provider) | Once it is back, replay |
| **Unknown `eventVersion`** | A producer shipped a breaking change before its consumer. Deploy the consumer that understands it, then replay |
| **Genuinely bad data** (unparseable, impossible values) | Do not replay. Record the incident, fix the producer, and correct the business state by hand if needed (e.g. fail the order) |

Replaying is safe: every consumer deduplicates on `eventId`, so a message that was in fact
processed before it failed has no second effect. That property is what makes this runbook
short.

## 3. Replay

Replay only messages for the consumer that failed. Messages keep their original key, so they land on the
same partition as before and per-order ordering is preserved.

```bash
# Dump the DLQ to a file, one key<TAB>value per line.
docker compose exec -T kafka kafka-console-consumer --bootstrap-server localhost:9092 \
  --topic orders.dlq --from-beginning --timeout-ms 5000 \
  --property print.key=true --property key.separator=$'\t' > dlq.tsv

# Review dlq.tsv. Delete lines that must not be replayed. Then republish:
docker compose exec -T kafka kafka-console-producer --bootstrap-server localhost:9092 \
  --topic orders --property parse.key=true --property key.separator=$'\t' < dlq.tsv
```

The console producer drops the original headers. The events still process correctly,
because consumers read everything from the value, but Kafka UI will not show the
`eventType` header on the replayed copies.

Replaying onto `orders` also re-delivers to the consumer that did **not** fail. It will
deduplicate and skip. That is expected.

## 4. Close it out

1. Confirm the affected orders reached the right state (`GET /api/v1/orders/{id}`).
2. The DLQ is not drained by consuming it. Its retention is 30 days, so the depth alert
   compares against the offset recorded at the last incident, not against zero.
   Record the current end offset in the incident log.
3. Write down what happened, what the fix was, and whether a test would have caught it.
