package com.onlineshop.notification;

import static org.assertj.core.api.Assertions.assertThat;

import com.onlineshop.notification.domain.Notification;
import com.onlineshop.notification.domain.NotificationChannel;
import com.onlineshop.notification.domain.NotificationStatus;
import com.onlineshop.notification.domain.NotificationType;
import com.onlineshop.notification.messaging.ShopEventListener;
import com.onlineshop.notification.repository.NotificationRepository;
import com.onlineshop.notification.repository.ProcessedEventRepository;
import com.onlineshop.notification.service.NotificationRetryJob;
import java.sql.Timestamp;
import java.time.Duration;
import java.time.Instant;
import java.util.List;
import java.util.UUID;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.test.context.TestConfiguration;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Primary;
import org.springframework.jdbc.core.JdbcTemplate;
import org.springframework.test.context.ActiveProfiles;

/**
 * Send failures and the row-driven retry, with a provider that fails on demand. The retry
 * job is called directly; its timer is off under the test profile.
 */
@SpringBootTest(properties = "notification.retry.max-attempts=5")
@ActiveProfiles("test")
class NotificationRetryTest {

    @TestConfiguration
    static class FlakyProvider {

        @Bean
        @Primary
        FailingNotificationSender failingNotificationSender() {
            return new FailingNotificationSender();
        }
    }

    @Autowired
    private FailingNotificationSender sender;

    @Autowired
    private ShopEventListener listener;

    @Autowired
    private NotificationRetryJob retryJob;

    @Autowired
    private NotificationRepository notifications;

    @Autowired
    private ProcessedEventRepository processedEvents;

    @Autowired
    private JdbcTemplate jdbc;

    @BeforeEach
    void clear() {
        notifications.deleteAll();
        processedEvents.deleteAll();
        sender.reset();
    }

    private Notification only() {
        List<Notification> all = notifications.findAll();
        assertThat(all).hasSize(1);
        return all.get(0);
    }

    @Test
    void aFailedSendLeavesAFailedRowBehind() throws Exception {
        sender.failNext(1);
        listener.onEvent(TestEvents.orderCreated(UUID.randomUUID(), "user-1"));

        Notification notification = only();
        assertThat(notification.getStatus()).isEqualTo(NotificationStatus.FAILED);
        assertThat(notification.getSendAttempts()).isEqualTo(1);
        assertThat(notification.getLastError()).contains("provider unavailable");
    }

    @Test
    void aProviderThatRecoversOnTheSecondAttemptSendsOnce() throws Exception {
        sender.failNext(1);
        listener.onEvent(TestEvents.orderCreated(UUID.randomUUID(), "user-1"));

        assertThat(retryJob.retryFailed()).isEqualTo(1);

        Notification notification = only();
        assertThat(notification.getStatus()).isEqualTo(NotificationStatus.SENT);
        assertThat(notification.getSendAttempts()).isEqualTo(2);
        assertThat(sender.delivered()).hasSize(1);
        // Sent now, so the next run has nothing to do.
        assertThat(retryJob.retryFailed()).isZero();
    }

    @Test
    void retriesStopAtTheCap() throws Exception {
        sender.failNext(100);
        listener.onEvent(TestEvents.orderCreated(UUID.randomUUID(), "user-1"));
        for (int run = 0; run < 4; run++) {
            retryJob.retryFailed();
        }

        Notification notification = only();
        assertThat(notification.getStatus()).isEqualTo(NotificationStatus.PERMANENTLY_FAILED);
        assertThat(notification.getSendAttempts()).isEqualTo(5);

        // No longer picked up.
        assertThat(retryJob.retryFailed()).isZero();
        assertThat(sender.calls()).isEqualTo(5);
        assertThat(only().getSendAttempts()).isEqualTo(5);
    }

    @Test
    void aRedeliveredEventDoesNotResetOrDuplicateAFailedRow() throws Exception {
        sender.failNext(1);
        String event = TestEvents.orderCreated(UUID.randomUUID(), "user-1");
        listener.onEvent(event);
        listener.onEvent(event);

        Notification notification = only();
        assertThat(notification.getStatus()).isEqualTo(NotificationStatus.FAILED);
        assertThat(sender.calls()).isEqualTo(1);
    }

    @Test
    void aPendingRowOrphanedByACrashIsSentByTheJob() {
        // As if the service died between committing the row and attempting the send.
        Notification orphan = notifications.save(new Notification(UUID.randomUUID(), "OrderCreated",
                NotificationType.ORDER_CONFIRMATION, NotificationChannel.EMAIL, "user-1",
                "order-1", "alice@example.com", "Subject", "Body"));
        Notification fresh = notifications.save(new Notification(UUID.randomUUID(), "OrderCreated",
                NotificationType.ORDER_CONFIRMATION, NotificationChannel.EMAIL, "user-1",
                "order-2", "alice@example.com", "Subject", "Body"));
        jdbc.update("UPDATE notifications SET created_at = ? WHERE id = ?",
                Timestamp.from(Instant.now().minus(Duration.ofHours(1))), orphan.getId());

        assertThat(retryJob.retryFailed()).isEqualTo(1);

        assertThat(notifications.findById(orphan.getId())).get()
                .extracting(Notification::getStatus).isEqualTo(NotificationStatus.SENT);
        // A fresh PENDING row may still be mid-send in the listener; it is left alone.
        assertThat(notifications.findById(fresh.getId())).get()
                .extracting(Notification::getStatus).isEqualTo(NotificationStatus.PENDING);
    }
}
