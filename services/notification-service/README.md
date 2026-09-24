# Notification Service

Spring Boot 3 (Java 21) + PostgreSQL + Flyway + Spring Kafka. Turns order and payment events
into customer notifications and keeps a record of every one.

Full API reference: [`docs/api/notification-service.md`](../../docs/api/notification-service.md).

## Layout

```
src/main/java/com/onlineshop/notification/
  NotificationServiceApplication.java
  domain/       Notification (status transitions, attempt cap) and ProcessedEvent
  repository/   Spring Data JPA repositories, including the ON CONFLICT dedup insert
  service/      record-then-send, the sender interface and its logging implementation,
                templates, the retry job and the processed_events pruner; no HTTP or Kafka
  messaging/    the Kafka listener, this service's own event records, retry and DLQ config
  web/          read-only REST controller and the global error handler
  dto/          response records (the public contract)
  exception/    domain exceptions
  config/       OpenAPI metadata, the scheduling switch
src/main/resources/
  application.yml
  db/migration/ Flyway migrations: versioned, never edited once applied
src/test/       unit tests, MockMvc tests, listener and retry tests, an embedded-broker test
```

Every event is handled in two transactions: **record** (dedup marker + `PENDING` row), then
**send** (call the sender, write the outcome). The row therefore exists before any send.
The listener calls them one after the other from outside the service bean, because a
self-call would bypass the transactional proxy.

## Run it

With Docker Compose, from the repository root:

```bash
docker compose up --build notification-service
```

Locally you need **JDK 21**, a Postgres on `localhost:5436`
(`docker compose up notification-db`) and Kafka on `localhost:29092`
(`docker compose up kafka kafka-init`). Maven is not required, because the project ships the
Maven Wrapper:

```bash
./mvnw spring-boot:run     # Windows: .\mvnw.cmd spring-boot:run
```

Then open <http://localhost:8084/swagger-ui.html>. Without a broker the service still
starts and serves the API; the consumer keeps retrying the connection in the background.

## Tests

```bash
./mvnw verify              # Windows: .\mvnw.cmd verify
```

Tests run under the `test` profile against in-memory H2 (PostgreSQL mode), so no Postgres
is needed. The Kafka listener does not start in that profile, and the retry and prune
timers are off: tests call the listener, the retry job and the pruner directly.
`FlywaySchemaTest` runs the real migrations on H2 with `ddl-auto: validate`.
`NotificationKafkaIntegrationTest` starts an in-JVM broker (`@EmbeddedKafka`, KRaft).

Current state: **48 tests** (8 aggregate, 3 template and masking, 4 dedup repository,
2 Flyway schema, 2 context, 8 MockMvc API, 10 listener, 5 retry, 4 broker-less error
handler and DLQ, 2 embedded broker).

On a machine where the JVM cannot open loopback sockets, the two embedded-broker tests can
be skipped with `./mvnw verify -DexcludedGroups=embedded-kafka` (46 tests). This happens,
for example, when a third-party Winsock LSP crashes Java NIO. They run by default and must
pass in CI.

## Configuration

| Variable                                        | Default                                          |
|-------------------------------------------------|--------------------------------------------------|
| `NOTIFICATION_SERVICE_DATASOURCE_URL`           | `jdbc:postgresql://localhost:5436/notification_db` |
| `NOTIFICATION_SERVICE_DATASOURCE_USERNAME`      | `notification_service`                           |
| `NOTIFICATION_SERVICE_DATASOURCE_PASSWORD`      | `notification_service`                           |
| `NOTIFICATION_SERVICE_PORT`                     | `8084`                                           |
| `NOTIFICATION_SERVICE_LOG_LEVEL`                | `INFO`                                           |
| `NOTIFICATION_SERVICE_KAFKA_BOOTSTRAP_SERVERS`  | `localhost:29092`                                |
| `NOTIFICATION_SERVICE_KAFKA_ORDERS_TOPIC`       | `orders`                                         |
| `NOTIFICATION_SERVICE_KAFKA_PAYMENTS_TOPIC`     | `payments`                                       |
| `NOTIFICATION_SERVICE_KAFKA_RETRY_BACKOFF_MS`   | `1000` (pause between the 3 listener attempts)   |
| `NOTIFICATION_SERVICE_SENDER`                   | `logging` (the only sender)                      |
| `NOTIFICATION_SERVICE_MAX_SEND_ATTEMPTS`        | `5`                                              |
| `NOTIFICATION_SERVICE_RETRY_INTERVAL_MS`        | `60000`                                          |
| `NOTIFICATION_SERVICE_STALE_PENDING_AFTER`      | `PT5M`                                           |
| `NOTIFICATION_SERVICE_PROCESSED_EVENTS_RETENTION` | `P8D`                                          |

Hibernate runs with `ddl-auto: validate`: Flyway owns the schema, and the application
refuses to start if the entities and the migrated tables disagree.
