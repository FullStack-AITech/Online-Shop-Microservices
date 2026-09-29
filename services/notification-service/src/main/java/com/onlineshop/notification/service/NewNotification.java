package com.onlineshop.notification.service;

import com.onlineshop.notification.domain.NotificationType;
import java.math.BigDecimal;
import java.util.UUID;

/**
 * What the event layer asks the service to record. Carries plain values rather than event
 * classes, so the business layer does not depend on the wire format.
 *
 * @param amount   the money the message mentions; may be null
 * @param reason   machine code for a failure, e.g. {@code card_declined}; may be null
 */
public record NewNotification(
        UUID eventId,
        String eventType,
        NotificationType type,
        String userId,
        String orderId,
        String recipient,
        BigDecimal amount,
        String currency,
        String reason) {
}
