package com.onlineshop.notification;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import com.onlineshop.notification.domain.Notification;
import com.onlineshop.notification.domain.NotificationChannel;
import com.onlineshop.notification.domain.NotificationStatus;
import com.onlineshop.notification.domain.NotificationType;
import java.time.Instant;
import java.util.UUID;
import org.junit.jupiter.api.Test;

/** Unit tests for the notification's status transitions. No Spring context needed. */
class NotificationTest {

    private Notification notification() {
        return new Notification(UUID.randomUUID(), "OrderCreated", NotificationType.ORDER_CONFIRMATION,
                NotificationChannel.EMAIL, "user-1", "order-1", "alice@example.com",
                "Subject", "Body");
    }

    @Test
    void newNotificationsArePendingWithNoAttempts() {
        Notification notification = notification();
        assertThat(notification.getStatus()).isEqualTo(NotificationStatus.PENDING);
        assertThat(notification.getSendAttempts()).isZero();
        assertThat(notification.getSentAt()).isNull();
    }

    @Test
    void markSentRecordsTheTimeAndCountsTheAttempt() {
        Notification notification = notification();
        Instant sentAt = Instant.parse("2026-09-24T10:00:00Z");
        notification.markSent(sentAt);
        assertThat(notification.getStatus()).isEqualTo(NotificationStatus.SENT);
        assertThat(notification.getSentAt()).isEqualTo(sentAt);
        assertThat(notification.getSendAttempts()).isEqualTo(1);
    }

    @Test
    void markFailedBelowTheCapLeavesItRetryable() {
        Notification notification = notification();
        notification.markFailed("timeout", 3);
        assertThat(notification.getStatus()).isEqualTo(NotificationStatus.FAILED);
        assertThat(notification.getSendAttempts()).isEqualTo(1);
        assertThat(notification.getLastError()).isEqualTo("timeout");
    }

    @Test
    void reachingTheCapIsPermanent() {
        Notification notification = notification();
        notification.markFailed("timeout", 3);
        notification.markFailed("timeout", 3);
        notification.markFailed("timeout", 3);
        assertThat(notification.getStatus()).isEqualTo(NotificationStatus.PERMANENTLY_FAILED);
        assertThat(notification.getSendAttempts()).isEqualTo(3);
    }

    @Test
    void aFailedNotificationCanStillBeSent() {
        Notification notification = notification();
        notification.markFailed("timeout", 3);
        notification.markSent(Instant.now());
        assertThat(notification.getStatus()).isEqualTo(NotificationStatus.SENT);
        assertThat(notification.getSendAttempts()).isEqualTo(2);
    }

    @Test
    void finalStatusesCannotChange() {
        Notification sent = notification();
        sent.markSent(Instant.now());
        assertThatThrownBy(() -> sent.markFailed("late", 3)).isInstanceOf(IllegalStateException.class);
        assertThatThrownBy(() -> sent.markSent(Instant.now())).isInstanceOf(IllegalStateException.class);

        Notification abandoned = notification();
        abandoned.markFailed("bounced", 1);
        assertThat(abandoned.getStatus()).isEqualTo(NotificationStatus.PERMANENTLY_FAILED);
        assertThatThrownBy(() -> abandoned.markSent(Instant.now()))
                .isInstanceOf(IllegalStateException.class);
    }

    @Test
    void longErrorsAreTruncatedToTheColumnWidth() {
        Notification notification = notification();
        notification.markFailed("x".repeat(2000), 5);
        assertThat(notification.getLastError()).hasSize(Notification.MAX_ERROR_LENGTH);
    }

    @Test
    void aCapBelowOneIsRejected() {
        assertThatThrownBy(() -> notification().markFailed("x", 0))
                .isInstanceOf(IllegalArgumentException.class);
    }
}
