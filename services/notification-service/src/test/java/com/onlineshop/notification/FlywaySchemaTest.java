package com.onlineshop.notification;

import static org.assertj.core.api.Assertions.assertThat;

import java.util.UUID;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.test.context.ActiveProfiles;

/**
 * The real migrations, with Hibernate validating the entities against them, as production
 * starts. Every other test builds its schema from the entities and would miss a mapping
 * mismatch.
 *
 * <p>H2 in PostgreSQL mode is not Postgres: this catches a missing column or a wrong type
 * family, not every Postgres-specific difference. The Compose start-up is the full check.
 */
@SpringBootTest(properties = {
        // H2 has no TIMESTAMPTZ; a domain gives it Postgres's alias for the same type.
        "spring.datasource.url=jdbc:h2:mem:notification_flyway;MODE=PostgreSQL;DB_CLOSE_DELAY=-1;"
                + "INIT=CREATE DOMAIN IF NOT EXISTS TIMESTAMPTZ AS TIMESTAMP WITH TIME ZONE",
        "spring.flyway.enabled=true",
        "spring.jpa.hibernate.ddl-auto=validate"
})
@ActiveProfiles("test")
class FlywaySchemaTest {

    @Autowired
    private JdbcTemplate jdbc;

    @Test
    void migrationsApplyAndTheEntitiesValidateAgainstThem() {
        Integer applied = jdbc.queryForObject(
                "SELECT COUNT(*) FROM \"flyway_schema_history\" WHERE \"success\" = TRUE AND \"version\" IS NOT NULL", Integer.class);
        assertThat(applied).isEqualTo(2);
    }

    @Test
    void processedAtDefaultsToNowWhenOmitted() {
        jdbc.update("INSERT INTO processed_events (event_id, consumer) VALUES (?, ?)",
                UUID.randomUUID(), "notification-service");
        assertThat(jdbc.queryForObject(
                "SELECT COUNT(*) FROM processed_events WHERE processed_at IS NOT NULL", Integer.class))
                .isEqualTo(1);
    }
}
