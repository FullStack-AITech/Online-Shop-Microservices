package com.onlineshop.notification.service;

import com.onlineshop.notification.domain.Notification;
import com.onlineshop.notification.domain.NotificationStatus;
import com.onlineshop.notification.repository.NotificationRepository;
import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.List;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.scheduling.annotation.Scheduled;
import org.springframework.stereotype.Component;

/**
 * Retries failed sends from their rows.
 *
 * <p>Provider failures are retried here, not by redelivering the Kafka message: the event
 * has already been recorded, so a redelivery would be skipped as a duplicate. Retrying the
 * row means no second row can ever appear, and the cap in
 * {@link Notification#markFailed(String, int)} means no row is retried forever.
 *
 * <p>It also picks up PENDING rows old enough to have been orphaned by a crash between
 * recording and sending, which is the one failure insert-then-send leaves.
 *
 * <p>With more than one replica two jobs can pick the same row; optimistic locking stops a
 * double write but not a double send. When the service scales out (#40) the select should
 * become {@code SELECT ... FOR UPDATE SKIP LOCKED}.
 */
@Component
public class NotificationRetryJob {

    private static final Logger log = LoggerFactory.getLogger(NotificationRetryJob.class);

    private final NotificationRepository notifications;
    private final NotificationService service;
    private final Duration stalePendingAfter;

    public NotificationRetryJob(NotificationRepository notifications, NotificationService service,
                                @Value("${notification.retry.stale-pending-after:PT5M}")
                                Duration stalePendingAfter) {
        this.notifications = notifications;
        this.service = service;
        this.stalePendingAfter = stalePendingAfter;
    }

    /** @return how many notifications were attempted */
    @Scheduled(fixedDelayString = "${notification.retry.interval-ms:60000}")
    public int retryFailed() {
        List<Notification> due = new ArrayList<>(notifications.findByStatus(NotificationStatus.FAILED));
        due.addAll(notifications.findByStatusAndCreatedAtBefore(
                NotificationStatus.PENDING, Instant.now().minus(stalePendingAfter)));
        int attempted = 0;
        for (Notification notification : due) {
            try {
                service.dispatch(notification.getId());
                attempted++;
            } catch (RuntimeException exception) {
                // One bad row (or a lost optimistic-lock race) must not stop the rest.
                log.warn("Retrying notification {} failed: {}", notification.getId(),
                        exception.getMessage());
            }
        }
        if (attempted > 0) {
            log.info("Retried {} notification(s)", attempted);
        }
        return attempted;
    }
}
