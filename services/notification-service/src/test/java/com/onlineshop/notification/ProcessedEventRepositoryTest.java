package com.onlineshop.notification;

import static org.assertj.core.api.Assertions.assertThat;

import com.onlineshop.notification.repository.ProcessedEventRepository;
import com.onlineshop.notification.service.ProcessedEventPruner;
import java.sql.Timestamp;
import java.time.Duration;
import java.time.Instant;
import java.util.UUID;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.transaction.support.TransactionTemplate;

/**
 * The deduplication marker, run as the real native statement against H2 in PostgreSQL mode,
 * which accepts {@code ON CONFLICT DO NOTHING}.
 */
@SpringBootTest
@ActiveProfiles("test")
class ProcessedEventRepositoryTest {

    private static final String CONSUMER = "notification-service";

    @Autowired
    private ProcessedEventRepository repository;

    @Autowired
    private ProcessedEventPruner pruner;

    @Autowired
    private TransactionTemplate transactions;

    @Autowired
    private JdbcTemplate jdbc;

    @BeforeEach
    void clear() {
        repository.deleteAll();
    }

    private int tryRecord(UUID eventId, String consumer) {
        return transactions.execute(status -> repository.tryRecord(eventId, consumer));
    }

    @Test
    void theFirstRecordWinsAndTheSecondIsADuplicate() {
        UUID eventId = UUID.randomUUID();
        assertThat(tryRecord(eventId, CONSUMER)).isEqualTo(1);
        assertThat(tryRecord(eventId, CONSUMER)).isZero();
        assertThat(repository.count()).isEqualTo(1);
    }

    @Test
    void anotherConsumerMayRecordTheSameEvent() {
        UUID eventId = UUID.randomUUID();
        assertThat(tryRecord(eventId, CONSUMER)).isEqualTo(1);
        assertThat(tryRecord(eventId, "notification-service.other")).isEqualTo(1);
    }

    @Test
    void theMarkerIsPartOfTheCallersTransaction() {
        UUID eventId = UUID.randomUUID();
        transactions.executeWithoutResult(status -> {
            repository.tryRecord(eventId, CONSUMER);
            status.setRollbackOnly();
        });
        // Rolled back with its transaction, so a redelivery is processed afresh.
        assertThat(repository.count()).isZero();
        assertThat(tryRecord(eventId, CONSUMER)).isEqualTo(1);
    }

    @Test
    void pruningDeletesOnlyMarkersOlderThanTheRetention() {
        UUID old = UUID.randomUUID();
        UUID recent = UUID.randomUUID();
        insert(old, Instant.now().minus(Duration.ofDays(9)));
        insert(recent, Instant.now().minus(Duration.ofDays(7)));

        assertThat(pruner.prune()).isEqualTo(1);
        assertThat(repository.findAll()).singleElement()
                .satisfies(marker -> assertThat(marker.getEventId()).isEqualTo(recent));
    }

    private void insert(UUID eventId, Instant processedAt) {
        jdbc.update("INSERT INTO processed_events (event_id, consumer, processed_at) VALUES (?, ?, ?)",
                eventId, CONSUMER, Timestamp.from(processedAt));
    }
}
