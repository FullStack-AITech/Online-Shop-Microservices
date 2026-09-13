# Product Service API

**Base URL (local):** `http://localhost:8081`
**Stack:** Spring Boot 3 (Java 21) + PostgreSQL + Flyway
**Interactive docs:** `/swagger-ui.html` · `/v3/api-docs`

The Product Service owns the catalogue and its stock levels. It is the only service that
may read or write `product_db`.

## Conventions

- Resource routes are under `/api/v1`.
- SKUs are normalised to uppercase and are unique; they are the catalogue's natural key.
- Prices are `NUMERIC(12,2)` with an explicit ISO-4217 currency. Never floating point.
- Errors share one shape:
  `{"error": "<code>", "message": "...", "path": "...", "timestamp": "..."}`.

## Health and metrics

| Method | Path                          | Purpose                          |
|--------|-------------------------------|----------------------------------|
| GET    | `/actuator/health/liveness`   | Liveness probe                   |
| GET    | `/actuator/health/readiness`  | Readiness probe (includes the DB)|
| GET    | `/actuator/prometheus`        | Metrics, used from week 10       |

## Endpoints

### `POST /api/v1/products` — create a product

```json
{
  "sku": "KB-001",
  "name": "Mechanical Keyboard",
  "description": "Tactile switches",
  "price": 49.99,
  "currency": "GBP",
  "stockQuantity": 10
}
```

| Status | Meaning                                         |
|--------|-------------------------------------------------|
| 201    | Created; `Location` header points at the product|
| 400    | `validation_failed`                             |
| 409    | `sku_already_exists`                            |

### `GET /api/v1/products` — list or search

Query: `search` (optional free text over name and SKU), `page` (default 0),
`size` (1–100, default 20). Only **active** products are listed.

```json
{ "items": [ /* products */ ], "total": 42, "page": 0, "size": 20 }
```

The envelope is defined by this service rather than returned as Spring's `Page`, whose
JSON shape is a framework detail that has changed between versions.

### `GET /api/v1/products/{id}`

`200`, or `404` `product_not_found`. Returns inactive products too, so the Order Service
can still resolve a product referenced by an old order.

### `GET /api/v1/products/sku/{sku}`

Lookup by SKU, case-insensitive. Same status codes as above.

### `PATCH /api/v1/products/{id}` — partial update

`name`, `description`, `price`, `active`. Null fields are left untouched. `sku` and
`currency` are immutable: both are referenced by orders already placed.

### `DELETE /api/v1/products/{id}` — deactivate

`204`. This is a **soft delete** — the row is marked inactive and disappears from listings
but remains resolvable by id. Orders and invoices reference products long after they leave
the catalogue, so the row must survive.

### `POST /api/v1/products/{id}/stock/reserve`

```json
{ "quantity": 2 }
```

| Status | Meaning                                                |
|--------|--------------------------------------------------------|
| 200    | Stock reduced; body is the updated product             |
| 409    | `insufficient_stock` — well-formed request, state conflict |
| 404    | `product_not_found`                                    |

### `POST /api/v1/products/{id}/stock/release`

The compensating action: returns units to the catalogue when an order is cancelled or a
payment fails. This pair is the basis of the Saga compensation added in week 7.

## Data model

| Column           | Type           | Notes                                  |
|------------------|----------------|----------------------------------------|
| `id`             | varchar(36)    | UUID4, primary key                     |
| `sku`            | varchar(64)    | Unique, uppercase                      |
| `name`           | varchar(200)   | Indexed on `LOWER(name)` for search    |
| `description`    | varchar(2000)  |                                        |
| `price`          | numeric(12,2)  | Check constraint `>= 0`                |
| `currency`       | char(3)        | ISO-4217                               |
| `stock_quantity` | integer        | Check constraint `>= 0`                |
| `active`         | boolean        | Soft-delete flag, indexed              |
| `created_at`     | timestamptz    |                                        |
| `updated_at`     | timestamptz    |                                        |
| `version`        | bigint         | Optimistic lock                        |

The `version` column matters: the Order Service will reserve stock concurrently, and
without optimistic locking two simultaneous reservations can both read the same stock
level and each write back a value that ignores the other.

## Known gaps

- **No authentication.** Writes are unprotected until the gateway lands in weeks 5–6.
- **Reservations are immediate decrements**, not time-limited holds. A real checkout needs
  a reservation that expires; that arrives with the Saga work in week 7.
- **Search is a `LIKE` query.** Fine at this scale, wrong at catalogue scale — a search
  index is the eventual answer.
