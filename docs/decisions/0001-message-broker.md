# 0001 — Message broker: Kafka

- **Status:** Accepted
- **Date:** 2026-09-24

## Context

Week 4 introduces asynchronous communication. The Order Service must announce
`OrderCreated`, the Payment Service must react and announce the outcome, and the
Notification Service must react to both. The later weeks of the plan want more from the
broker than delivery:

- **Replay** — event sourcing for orders (#46) and rebuilding a CQRS projection (#45)
  both need to read history again, not just the next message.
- **Consumer lag as a metric** — the most useful single number in an event-driven system
  (#42), and the basis of the lag alert (#44).
- **New consumers reading from the start** — the Notification Service was added after the
  Order Service started publishing; with a retained log it can catch up.

## Options considered

| | Kafka | RabbitMQ |
|---|---|---|
| Model | Partitioned, retained log. Consumers track their own offset | Queues. A message is gone once acknowledged |
| Replay | Reset an offset | Not possible once consumed |
| Ordering | Per partition, by key | Per queue, lost with competing consumers |
| Routing | Topic + key only | Exchanges, bindings, headers — very flexible |
| Per-message TTL, priority, delayed retry | No | Yes |
| Operational weight | Heavier (KRaft removes ZooKeeper) | Lighter |

## Decision

**Kafka**, in KRaft mode (no ZooKeeper), one broker locally.

## Consequences

- **What we gave up.** RabbitMQ is the better tool for work queues and rich routing:
  per-message TTL, priorities and delayed retries would all have to be built by hand on
  Kafka. Retries in this project are therefore in-process with a cap, followed by a dead
  letter topic (#25), rather than a broker feature.
- **Ordering is per partition only.** Every event is keyed by **order id**, so all events
  about one order land on one partition in order. Nothing is ordered across orders, and
  nothing needs to be.
- **Replication factor 1 is local only.** Production needs at least three brokers,
  `replication.factor=3` and `min.insync.replicas=2`, or one broker failure loses
  acknowledged writes. This must not silently become the deployed setting (#39).
- **At-least-once delivery.** Consumers commit offsets after processing, so a crash
  redelivers. Every consumer must be idempotent on `eventId` (#25).
- **Topics are created by a script, not on demand.** Auto-creation is off; partition count
  and retention live in `infrastructure/kafka/create-topics.sh`.

### Topics

| Topic | Partitions | Retention | Events |
|---|---|---|---|
| `orders` | 3 | 7 days | `OrderCreated`, `OrderCancelled` |
| `payments` | 3 | 7 days | `PaymentProcessed`, `PaymentFailed` |
| `orders.dlq`, `payments.dlq` | 1 | 30 days | Poison messages, read by humans (#25) |

Partitions can be added later but never removed, and adding them remaps keys — which would
break per-order ordering for orders in flight. Three is enough for three consumer
instances per group.
