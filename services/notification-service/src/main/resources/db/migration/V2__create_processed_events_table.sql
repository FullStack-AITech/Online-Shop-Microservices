-- Deduplication markers, one per event per consumer, written in the same transaction as the
-- notification. The shape is shared by every consuming service: see docs/events/README.md,
-- "Consuming events".
CREATE TABLE processed_events (
    event_id     UUID        NOT NULL,
    consumer     VARCHAR(64) NOT NULL,
    processed_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT pk_processed_events PRIMARY KEY (event_id, consumer)
);

-- The hourly prune deletes by age.
CREATE INDEX idx_processed_events_processed_at ON processed_events (processed_at);
