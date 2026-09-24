package com.onlineshop.notification.domain;

import jakarta.persistence.Column;
import jakarta.persistence.Entity;
import jakarta.persistence.Id;
import jakarta.persistence.IdClass;
import jakarta.persistence.Table;
import java.time.Instant;
import java.util.UUID;

/**
 * Marker that an event has been handled by a consumer, written in the same transaction as
 * the notification it produced.
 *
 * <p>Rows are only ever inserted through
 * {@link com.onlineshop.notification.repository.ProcessedEventRepository#tryRecord}. The
 * entity exists so Hibernate validates the table, and so the H2 test schema includes it.
 */
@Entity
@Table(name = "processed_events")
@IdClass(ProcessedEventId.class)
public class ProcessedEvent {

    @Id
    @Column(name = "event_id", nullable = false, updatable = false)
    private UUID eventId;

    @Id
    @Column(nullable = false, updatable = false, length = 64)
    private String consumer;

    @Column(name = "processed_at", nullable = false, updatable = false)
    private Instant processedAt;

    protected ProcessedEvent() {
        // Required by JPA.
    }

    public UUID getEventId() {
        return eventId;
    }

    public String getConsumer() {
        return consumer;
    }

    public Instant getProcessedAt() {
        return processedAt;
    }
}
