package com.onlineshop.notification.dto;

import com.onlineshop.notification.domain.Notification;
import com.onlineshop.notification.domain.NotificationChannel;
import com.onlineshop.notification.domain.NotificationStatus;
import com.onlineshop.notification.domain.NotificationType;
import com.onlineshop.notification.service.Recipients;
import java.time.Instant;
import java.util.UUID;

/**
 * Public view of a notification. Keeps the wire contract decoupled from the JPA entity.
 *
 * <p>The recipient is masked and the body left out: the API has no authentication until
 * the gateway lands, and anyone who can guess a user id can call it.
 */
public record NotificationResponse(
        String id,
        UUID eventId,
        String eventType,
        NotificationType type,
        NotificationChannel channel,
        String userId,
        String orderId,
        String recipient,
        String subject,
        NotificationStatus status,
        int sendAttempts,
        String lastError,
        Instant sentAt,
        Instant createdAt,
        Instant updatedAt) {

    public static NotificationResponse from(Notification notification) {
        return new NotificationResponse(
                notification.getId(),
                notification.getEventId(),
                notification.getEventType(),
                notification.getNotificationType(),
                notification.getChannel(),
                notification.getUserId(),
                notification.getOrderId(),
                Recipients.mask(notification.getRecipient()),
                notification.getSubject(),
                notification.getStatus(),
                notification.getSendAttempts(),
                notification.getLastError(),
                notification.getSentAt(),
                notification.getCreatedAt(),
                notification.getUpdatedAt());
    }
}
