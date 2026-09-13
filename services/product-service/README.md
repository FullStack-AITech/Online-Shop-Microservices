# Product Service

Spring Boot 3 (Java 21) + PostgreSQL + Flyway. Owns the product catalogue and stock levels.

Full API reference: [`docs/api/product-service.md`](../../docs/api/product-service.md).

## Layout

```
src/main/java/com/onlineshop/product/
  ProductServiceApplication.java
  domain/       the Product aggregate and its invariants
  repository/   Spring Data JPA repository
  service/      business rules; no HTTP knowledge
  web/          REST controller and the global error handler
  dto/          request/response records (the public contract)
  exception/    domain exceptions
  config/       OpenAPI metadata
src/main/resources/
  application.yml
  db/migration/ Flyway migrations — versioned, never edited once applied
src/test/       unit tests for the aggregate, MockMvc tests for the API
```

Business rules that must always hold — you cannot reserve more stock than exists — live on
the `Product` entity itself, not in the service layer. That way they hold no matter which
code path reaches the object.

## Run it

With Docker Compose, from the repository root:

```bash
docker compose up --build product-service
```

Locally you need **JDK 21** and a Postgres on `localhost:5433`
(`docker compose up product-db` provides one). Maven itself is not required — the project
ships the Maven Wrapper, which fetches the pinned Maven version on first use:

```bash
./mvnw spring-boot:run     # Windows: .\mvnw.cmd spring-boot:run
```

Then open <http://localhost:8081/swagger-ui.html>.

## Tests

```bash
./mvnw test                # Windows: .\mvnw.cmd test
```

Tests run under the `test` profile against in-memory H2 with Flyway disabled and the schema
generated from the entities, so no Postgres is required. Current state: **21 tests passing**
(7 aggregate unit tests, 14 MockMvc API tests).

## Configuration

| Variable                              | Default                                          |
|---------------------------------------|--------------------------------------------------|
| `PRODUCT_SERVICE_DATASOURCE_URL`      | `jdbc:postgresql://localhost:5433/product_db`    |
| `PRODUCT_SERVICE_DATASOURCE_USERNAME` | `product_service`                                |
| `PRODUCT_SERVICE_DATASOURCE_PASSWORD` | `product_service`                                |
| `PRODUCT_SERVICE_PORT`                | `8081`                                           |
| `PRODUCT_SERVICE_LOG_LEVEL`           | `INFO`                                           |

Hibernate runs with `ddl-auto: validate`: Flyway owns the schema, and the application
refuses to start if the entities and the migrated tables disagree.
