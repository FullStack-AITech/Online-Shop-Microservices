package com.onlineshop.notification.domain;

import java.io.Serializable;
import java.util.Objects;
import java.util.UUID;

/** Composite key of {@link ProcessedEvent}: one row per event per consumer. */
public class ProcessedEventId implements Serializable {

    private UUID eventId;
    private String consumer;

    protected ProcessedEventId() {
        // Required by JPA.
    }

    public ProcessedEventId(UUID eventId, String consumer) {
        this.eventId = eventId;
        this.consumer = consumer;
    }

    @Override
    public boolean equals(Object other) {
        if (this == other) {
            return true;
        }
        if (!(other instanceof ProcessedEventId that)) {
            return false;
        }
        return Objects.equals(eventId, that.eventId) && Objects.equals(consumer, that.consumer);
    }

    @Override
    public int hashCode() {
        return Objects.hash(eventId, consumer);
    }
}
