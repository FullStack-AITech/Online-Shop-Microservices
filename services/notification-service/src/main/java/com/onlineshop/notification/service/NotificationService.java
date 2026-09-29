package com.onlineshop.notification.service;

import com.onlineshop.notification.domain.Notification;
import com.onlineshop.notification.domain.NotificationChannel;
import com.onlineshop.notification.exception.NotificationNotFoundException;
import com.onlineshop.notification.exception.NotificationSendException;
import com.onlineshop.notification.repository.NotificationRepository;
import com.onlineshop.notification.repository.ProcessedEventRepository;
import java.time.Instant;
import java.util.Optional;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.data.domain.Page;
import org.springframework.data.domain.Pageable;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;
import org.springframework.util.StringUtils;

/**
 * Notification business rules. Knows nothing about HTTP or Kafka.
 *
 * <p>Recording and sending are two separate transactions, called one after the other from
 * outside this bean (a self-call would bypass the transactional proxy). The row commits
 * first, so a send can never happen without a record of it: insert-then-send. The failure
 * mode that leaves is a row that was never sent, which the retry job finds. The opposite
 * order fails with an email the customer received and no row, and a second email when the
 * event is redelivered.
 */
@Service
@Transactional(readOnly = true)
public class NotificationService {

    /** The processed_events consumer name. Shared across services: docs/events/README.md. */
    public static final String CONSUMER = "notification-service";

    private static final Logger log = LoggerFactory.getLogger(NotificationService.class);

    private final NotificationRepository notifications;
    private final ProcessedEventRepository processedEvents;
    private final NotificationTemplates templates;
    private final NotificationSender sender;
    private final int maxAttempts;

    public NotificationService(NotificationRepository notifications,
                               ProcessedEventRepository processedEvents,
                               NotificationTemplates templates,
                               NotificationSender sender,
                               @Value("${notification.retry.max-attempts:5}") int maxAttempts) {
        this.notifications = notifications;
        this.processedEvents = processedEvents;
        this.templates = templates;
        this.sender = sender;
        this.maxAttempts = maxAttempts;
    }

    /**
     * Records a PENDING notification for an event, once.
     *
     * <p>The processed_events marker and the notification row commit together or not at
     * all, so a redelivered event is recognised and skipped, and a crash mid-way leaves
     * nothing behind to confuse the redelivery.
     *
     * @return the new notification's id, or empty when this event was already handled
     */
    @Transactional
    public Optional<String> record(NewNotification request) {
        if (processedEvents.tryRecord(request.eventId(), CONSUMER) == 0) {
            log.info("Event {} ({}) already handled; skipping", request.eventId(),
                    request.eventType());
            return Optional.empty();
        }
        NotificationTemplates.Content content = templates.render(request);
        Notification notification = new Notification(
                request.eventId(),
                request.eventType(),
                request.type(),
                NotificationChannel.EMAIL,
                request.userId(),
                request.orderId(),
                request.recipient(),
                content.subject(),
                content.body());
        notifications.save(notification);
        log.info("Recorded {} notification {} for event {}", request.type(),
                notification.getId(), request.eventId());
        return Optional.of(notification.getId());
    }

    /**
     * Attempts delivery of one notification and writes the outcome back to its row.
     *
     * <p>A row that is already final is left alone, so the listener and the retry job can
     * both call this safely. Any exception from the sender counts as a failed attempt: a
     * failure that escaped here would roll the outcome back and leave the row unmarked.
     */
    @Transactional
    public void dispatch(String notificationId) {
        Notification notification = notifications.findById(notificationId)
                .orElseThrow(() -> new NotificationNotFoundException(notificationId));
        if (notification.getStatus().isFinal()) {
            return;
        }
        OutgoingMessage message = new OutgoingMessage(
                notification.getChannel(),
                notification.getRecipient(),
                notification.getSubject(),
                notification.getBody());
        try {
            sender.send(message);
            notification.markSent(Instant.now());
        } catch (NotificationSendException | RuntimeException exception) {
            notification.markFailed(describe(exception), maxAttempts);
            log.warn("Sending notification {} to {} failed (attempt {} of {}, now {}): {}",
                    notification.getId(), Recipients.mask(notification.getRecipient()),
                    notification.getSendAttempts(), maxAttempts, notification.getStatus(),
                    exception.getMessage());
        }
        notifications.save(notification);
    }

    public Notification getById(String id) {
        return notifications.findById(id).orElseThrow(() -> new NotificationNotFoundException(id));
    }

    /** A user's notifications, newest first. */
    public Page<Notification> listForUser(String userId, Pageable pageable) {
        if (!StringUtils.hasText(userId)) {
            throw new IllegalArgumentException("userId must not be blank");
        }
        return notifications.findByUserIdOrderByCreatedAtDesc(userId.trim(), pageable);
    }

    private static String describe(Exception exception) {
        String message = exception.getMessage();
        return exception.getClass().getSimpleName() + (message == null ? "" : ": " + message);
    }
}
