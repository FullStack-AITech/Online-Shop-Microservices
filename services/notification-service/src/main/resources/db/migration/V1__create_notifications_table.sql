-- Every notification the service decided to send, and what became of it. Migrations are
-- versioned and never edited once applied.
CREATE TABLE notifications (
    id                VARCHAR(36)   NOT NULL,
    event_id          UUID          NOT NULL,
    event_type        VARCHAR(64)   NOT NULL,
    notification_type VARCHAR(40)   NOT NULL,  -- ORDER_CONFIRMATION, PAYMENT_RECEIPT, PAYMENT_PROBLEM
    channel           VARCHAR(20)   NOT NULL,  -- EMAIL
    user_id           VARCHAR(36)   NOT NULL,
    order_id          VARCHAR(36),
    recipient         VARCHAR(320)  NOT NULL,
    -- Rendered once at creation, so a retry sends exactly what the first attempt would have.
    subject           VARCHAR(200)  NOT NULL,
    body              VARCHAR(2000) NOT NULL,
    status            VARCHAR(30)   NOT NULL,  -- PENDING, SENT, FAILED, PERMANENTLY_FAILED
    send_attempts     INTEGER       NOT NULL DEFAULT 0,
    last_error        VARCHAR(500),
    sent_at           TIMESTAMPTZ,
    created_at        TIMESTAMPTZ   NOT NULL,
    updated_at        TIMESTAMPTZ   NOT NULL,
    version           BIGINT        NOT NULL DEFAULT 0,
    CONSTRAINT pk_notifications PRIMARY KEY (id),
    -- A second guard behind processed_events: one message per event per channel, whatever
    -- the code does.
    CONSTRAINT uq_notifications_event_channel UNIQUE (event_id, channel)
);

-- The history API lists a user's notifications newest first.
CREATE INDEX idx_notifications_user_created ON notifications (user_id, created_at DESC);

-- The retry job scans by status every minute.
CREATE INDEX idx_notifications_status ON notifications (status);
