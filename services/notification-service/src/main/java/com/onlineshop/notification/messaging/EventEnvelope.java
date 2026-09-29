package com.onlineshop.notification.messaging;

import com.fasterxml.jackson.annotation.JsonIgnoreProperties;
import com.fasterxml.jackson.databind.JsonNode;
import java.time.Instant;
import java.util.UUID;

/**
 * The outer shape every event shares (docs/events/README.md, "Envelope").
 *
 * <p>This is the service's own model of the contract, not a shared library class: the
 * producer owns the schema, each consumer owns its reading of it. The payload stays a tree
 * until {@link #eventType} says which record to read it into.
 */
@JsonIgnoreProperties(ignoreUnknown = true)
public record EventEnvelope(
        UUID eventId,
        String eventType,
        Integer eventVersion,
        Instant occurredAt,
        String correlationId,
        String producer,
        JsonNode payload) {
}
