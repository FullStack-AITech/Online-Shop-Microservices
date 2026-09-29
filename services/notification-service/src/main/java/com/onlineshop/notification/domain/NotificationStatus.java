package com.onlineshop.notification.domain;

/**
 * Where a notification is in its life.
 *
 * <pre>
 * PENDING --send ok--> SENT
 *    |
 *    +--send fails--> FAILED --retry ok--> SENT
 *                       |
 *                       +--cap reached--> PERMANENTLY_FAILED
 * </pre>
 */
public enum NotificationStatus {
    PENDING,
    SENT,
    FAILED,
    PERMANENTLY_FAILED;

    /** SENT and PERMANENTLY_FAILED are final: nothing sends them again. */
    public boolean isFinal() {
        return this == SENT || this == PERMANENTLY_FAILED;
    }
}
