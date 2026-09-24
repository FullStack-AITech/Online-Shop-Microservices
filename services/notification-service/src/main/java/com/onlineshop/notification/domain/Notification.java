package com.onlineshop.notification.domain;

import jakarta.persistence.Column;
import jakarta.persistence.Entity;
import jakarta.persistence.EnumType;
import jakarta.persistence.Enumerated;
import jakarta.persistence.Id;
import jakarta.persistence.Table;
import jakarta.persistence.UniqueConstraint;
import jakarta.persistence.Version;
import java.time.Instant;
import java.util.UUID;

/**
 * One message the service decided to send, and what became of it.
 *
 * <p>The row is written <em>before</em> the send is attempted. A crash between the two
 * leaves a PENDING row that the retry job picks up, whereas sending first and then
 * crashing would leave a customer email with no record of it, or a duplicate on
 * redelivery.
 *
 * <p>Subject and body are rendered once, when the row is created, so a retry an hour later
 * sends exactly the words the first attempt would have.
 */
@Entity
@Table(name = "notifications", uniqueConstraints = @UniqueConstraint(
        name = "uq_notifications_event_channel", columnNames = {"event_id", "channel"}))
public class Notification {

    public static final int MAX_ERROR_LENGTH = 500;

    @Id
    @Column(length = 36, nullable = false, updatable = false)
    private String id;

    @Column(name = "event_id", nullable = false, updatable = false)
    private UUID eventId;

    @Column(name = "event_type", nullable = false, updatable = false, length = 64)
    private String eventType;

    @Enumerated(EnumType.STRING)
    @Column(name = "notification_type", nullable = false, updatable = false, length = 40)
    private NotificationType notificationType;

    @Enumerated(EnumType.STRING)
    @Column(nullable = false, updatable = false, length = 20)
    private NotificationChannel channel;

    @Column(name = "user_id", nullable = false, updatable = false, length = 36)
    private String userId;

    @Column(name = "order_id", updatable = false, length = 36)
    private String orderId;

    /** 320 characters: the longest address RFC 5321 allows (64 local + @ + 255 domain). */
    @Column(nullable = false, updatable = false, length = 320)
    private String recipient;

    @Column(nullable = false, updatable = false, length = 200)
    private String subject;

    @Column(nullable = false, updatable = false, length = 2000)
    private String body;

    @Enumerated(EnumType.STRING)
    @Column(nullable = false, length = 30)
    private NotificationStatus status;

    @Column(name = "send_attempts", nullable = false)
    private int sendAttempts;

    @Column(name = "last_error", length = MAX_ERROR_LENGTH)
    private String lastError;

    @Column(name = "sent_at")
    private Instant sentAt;

    @Column(name = "created_at", nullable = false, updatable = false)
    private Instant createdAt;

    @Column(name = "updated_at", nullable = false)
    private Instant updatedAt;

    /**
     * Optimistic locking: the listener and the retry job can both reach a row, and the
     * loser of that race must not overwrite the winner's outcome.
     *
     * <p>Boxed for the same reason as in the Product Service: the id is assigned in the
     * constructor, so a null version is what tells Spring Data the entity is new.
     */
    @Version
    @Column(nullable = false)
    private Long version;

    protected Notification() {
        // Required by JPA.
    }

    public Notification(UUID eventId, String eventType, NotificationType notificationType,
                        NotificationChannel channel, String userId, String orderId,
                        String recipient, String subject, String body) {
        this.id = UUID.randomUUID().toString();
        this.eventId = eventId;
        this.eventType = eventType;
        this.notificationType = notificationType;
        this.channel = channel;
        this.userId = userId;
        this.orderId = orderId;
        this.recipient = recipient;
        this.subject = subject;
        this.body = body;
        this.status = NotificationStatus.PENDING;
        this.sendAttempts = 0;
        Instant now = Instant.now();
        this.createdAt = now;
        this.updatedAt = now;
    }

    /** Records a successful send. A successful attempt still counts as an attempt. */
    public void markSent(Instant sentAt) {
        requireNotFinal();
        this.sendAttempts++;
        this.status = NotificationStatus.SENT;
        this.sentAt = sentAt;
        touch();
    }

    /**
     * Records a failed send. At {@code maxAttempts} the notification is given up on rather
     * than retried forever: a provider that has rejected an address five times will not
     * accept it on the sixth.
     */
    public void markFailed(String error, int maxAttempts) {
        if (maxAttempts < 1) {
            throw new IllegalArgumentException("maxAttempts must be at least 1");
        }
        requireNotFinal();
        this.sendAttempts++;
        this.lastError = truncate(error);
        this.status = sendAttempts >= maxAttempts
                ? NotificationStatus.PERMANENTLY_FAILED
                : NotificationStatus.FAILED;
        touch();
    }

    private void requireNotFinal() {
        if (status.isFinal()) {
            throw new IllegalStateException(
                    "Notification '" + id + "' is already " + status + " and cannot change");
        }
    }

    private static String truncate(String error) {
        if (error == null) {
            return null;
        }
        return error.length() <= MAX_ERROR_LENGTH ? error : error.substring(0, MAX_ERROR_LENGTH);
    }

    private void touch() {
        this.updatedAt = Instant.now();
    }

    public String getId() {
        return id;
    }

    public UUID getEventId() {
        return eventId;
    }

    public String getEventType() {
        return eventType;
    }

    public NotificationType getNotificationType() {
        return notificationType;
    }

    public NotificationChannel getChannel() {
        return channel;
    }

    public String getUserId() {
        return userId;
    }

    public String getOrderId() {
        return orderId;
    }

    public String getRecipient() {
        return recipient;
    }

    public String getSubject() {
        return subject;
    }

    public String getBody() {
        return body;
    }

    public NotificationStatus getStatus() {
        return status;
    }

    public int getSendAttempts() {
        return sendAttempts;
    }

    public String getLastError() {
        return lastError;
    }

    public Instant getSentAt() {
        return sentAt;
    }

    public Instant getCreatedAt() {
        return createdAt;
    }

    public Instant getUpdatedAt() {
        return updatedAt;
    }

    public Long getVersion() {
        return version;
    }
}
