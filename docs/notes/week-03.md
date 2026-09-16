# Week 3 — Order Service and resilience (Days 15–21)

Plan for the week: *build the Order Service; learn service communication, timeout, retry
and circuit breaker patterns.*

## What was built

- **Order Service** — .NET 8, EF Core, PostgreSQL. Order aggregate with an explicit state
  machine, create/read/list, and lifecycle endpoints. 70 tests, passing.
- **First cross-service calls** — Order → User (does this user exist?) and Order → Product
  (what does this cost, and can you reserve it?).
- **Resilience pipelines** — timeouts, retry with backoff and jitter, circuit breaker.
- **Docker Compose** — a third service and a third database.

## Concepts, and where they show up in the code

### There are no joins any more

This is the week the cost of database-per-service becomes real. An order line stores a
`product_id`, and there is no foreign key, because the products table is in another
database owned by another service. To price a line, the Order Service has to *ask*.

The consequence is visible in `Order.Create`: the price, SKU and name are **copied onto
the line**. Not referenced — copied. Two reasons, and only one of them is about joins:

1. There is nothing to join to.
2. Even if there were, a catalogue price change must never alter what a customer already
   agreed to pay.

The second reason is the one that matters. Copying here is not a workaround for the
missing join; it is what the domain actually requires.

### Which failures are safe to retry

The thing worth remembering from this week. The retry policy is not a property of the
*service* being called — it is a property of the *call*.

| Call | Idempotent | Retried |
|---|---|---|
| `GET /products/{id}` | yes | yes |
| `POST /products/{id}/stock/reserve` | **no** | **no** |

Both go to the Product Service. One retries, one must not.

When a reservation times out, the request may well have been processed — the response just
never arrived. Retrying reserves the stock a second time. So the reservation client gets a
pipeline with timeouts and a circuit breaker but **no retry at all**, and the order fails
instead. A failed order that a customer can retry is much cheaper than silently removing
stock twice.

`ResiliencePipelineTests` asserts the attempt count, because the number of requests that
actually reached the wire is the only observable difference between the two policies.

### Why a 404 must not be retried

`IsTransient` excludes every 4xx on purpose. A 404 or a 409 is a *correct answer from a
healthy service*. Retrying it wastes the caller's budget, and — worse — if 4xx counted
towards the circuit breaker, a burst of clients sending bad requests would trip the breaker
and take a perfectly healthy dependency offline for everyone.

Transient means *the same request might succeed if repeated*. A 404 never will.

### Pipeline order

Outermost to innermost: total timeout → retry → circuit breaker → attempt timeout.

The attempt timeout has to be **inside** the retry. Put it outside and a single slow call
consumes the entire budget, the outer timeout fires, and no retry is ever attempted — the
retry policy silently does nothing. The nesting is the behaviour.

### Jitter is not an optimisation

Exponential backoff spreads one client's retries out. Jitter spreads *all* clients' retries
out. Without it, everyone who failed at the same moment retries at the same moment, and the
dependency that was just coming back up is knocked straight over again. Backoff without
jitter converts one outage into a series of them.

### Compensation, and being honest about it

If the second of three reservations fails, the first has already been taken. The handler
releases it. That release can itself fail.

The code does not pretend otherwise: `CreateOrderHandler.ReleaseAsync` catches, logs at
error level, and says the units "will need reconciling". This is the interim position, not
the design. Week 7's Saga and outbox work is what makes it reliable. Writing it this way
keeps the gap visible instead of burying it in an optimistic `try`/`catch`.

### Business failure vs transport failure

`DownstreamUnavailableException` exists to keep these apart:

- "Not enough stock" → the Product Service answered → **409** → the customer should change
  their order.
- "The Product Service did not answer" → **503** → the customer should try again shortly.

Collapsing both into a 500 would tell the customer nothing and tell the on-call engineer
less.

### Rules belong on the aggregate

`Order.MarkShipped()` throws if the order is not `Paid`. Not the endpoint, not the handler —
the aggregate. In week 4 payment confirmation stops being an HTTP call and becomes a
`PaymentProcessed` event consumer. The transition rules will not change at all, because
nothing about them was ever tied to HTTP.

## Open items carried forward

1. **No authentication anywhere.** The list endpoint trusts `userId` from a query string.
   Do not expose this service publicly. Weeks 5–6.
2. **Compensation is best effort.** Week 7.
3. **No events published.** `OrderCreated` is week 4, and `pay` becomes a consumer then.
4. **Reservations are immediate decrements, not time-limited holds.** Week 7.
5. **Tests run on SQLite.** It needed a `DateTimeOffset` value converter that production
   does not use, and it cannot catch a Postgres-specific bug. Testcontainers once Docker is
   available.
6. **Docker Compose still unverified** — Docker is not installed on this machine.

## Questions worth being able to answer in an interview

*"Your call to reserve stock timed out. Do you retry?"* No. The request may have been
applied; you cannot tell from a timeout. Retrying a non-idempotent operation after an
ambiguous failure is how stock, charges and emails get duplicated. Fail, and make the
whole operation retryable instead — or make the operation idempotent with a key, which is
what week 4 does for event consumers.

*"Why is a 404 not a transient failure?"* Because transient means the same request might
succeed if repeated, and a 404 never will. Retrying it burns the caller's timeout budget,
and counting it towards a circuit breaker lets bad clients take down a healthy service.
