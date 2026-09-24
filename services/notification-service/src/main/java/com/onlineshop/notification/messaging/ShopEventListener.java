package com.onlineshop.notification.messaging;

import com.fasterxml.jackson.core.JsonProcessingException;
import com.fasterxml.jackson.databind.DeserializationFeature;
import com.fasterxml.jackson.databind.ObjectMapper;
import com.fasterxml.jackson.databind.json.JsonMapper;
import com.fasterxml.jackson.datatype.jsr310.JavaTimeModule;
import com.onlineshop.notification.domain.NotificationType;
import com.onlineshop.notification.service.NewNotification;
import com.onlineshop.notification.service.NotificationService;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.kafka.annotation.KafkaListener;
import org.springframework.stereotype.Component;
import org.springframework.util.StringUtils;

/**
 * Receives events from the orders and payments topics and turns the ones this service
 * cares about into notifications.
 *
 * <p>Values arrive as plain strings and are parsed here with this class's own
 * {@link ObjectMapper}. Spring's {@code JsonDeserializer} with type headers would tie this
 * service to the producer's class names; parsing ourselves keeps the model ours.
 *
 * <p>Failures are thrown, not swallowed: the container's error handler (see
 * {@link KafkaConfig}) retries or dead-letters them, and the offset is only committed once
 * this method returns.
 */
@Component
public class ShopEventListener {

    /** The listener container id, so tests can wait for its partition assignment. */
    public static final String LISTENER_ID = "shop-events";

    private static final Logger log = LoggerFactory.getLogger(ShopEventListener.class);

    private final ObjectMapper mapper = JsonMapper.builder()
            .addModule(new JavaTimeModule())
            // Unknown fields are the safe kind of contract change; ignore them.
            .disable(DeserializationFeature.FAIL_ON_UNKNOWN_PROPERTIES)
            .build();

    private final NotificationService service;

    public ShopEventListener(NotificationService service) {
        this.service = service;
    }

    // idIsGroup = false: the id names the container; the group comes from configuration.
    @KafkaListener(id = LISTENER_ID, idIsGroup = false,
            topics = {"${notification.topics.orders}", "${notification.topics.payments}"})
    public void onEvent(String value) throws JsonProcessingException {
        EventEnvelope envelope = parse(value);
        NewNotification request = switch (envelope.eventType()) {
            case "OrderCreated" -> fromOrderCreated(envelope);
            case "PaymentProcessed" -> fromPaymentProcessed(envelope);
            case "PaymentFailed" -> fromPaymentFailed(envelope);
            default -> null;
        };
        if (request == null) {
            // A topic carries several types; OrderCancelled, for one, needs no message.
            log.debug("Ignoring {} event {}", envelope.eventType(), envelope.eventId());
            return;
        }
        // Two calls on another bean, so the row commits before the send is attempted.
        service.record(request).ifPresent(service::dispatch);
    }

    private EventEnvelope parse(String value) throws JsonProcessingException {
        if (value == null) {
            throw new MalformedEventException("Message has no value");
        }
        EventEnvelope envelope = mapper.readValue(value, EventEnvelope.class);
        if (envelope == null || envelope.eventId() == null
                || !StringUtils.hasText(envelope.eventType())
                || envelope.eventVersion() == null || envelope.payload() == null
                || !envelope.payload().isObject()) {
            throw new MalformedEventException(
                    "Message is not an event envelope: eventId, eventType, eventVersion and "
                            + "an object payload are required");
        }
        return envelope;
    }

    private NewNotification fromOrderCreated(EventEnvelope envelope) throws JsonProcessingException {
        requireVersion(envelope, 1);
        OrderCreatedV1 order = mapper.treeToValue(envelope.payload(), OrderCreatedV1.class);
        requirePayload(envelope, order.orderId(), order.userId(), order.userEmail());
        return new NewNotification(envelope.eventId(), envelope.eventType(),
                NotificationType.ORDER_CONFIRMATION, order.userId(), order.orderId(),
                order.userEmail(), order.totalAmount(), order.currency(), null);
    }

    private NewNotification fromPaymentProcessed(EventEnvelope envelope)
            throws JsonProcessingException {
        requireVersion(envelope, 1);
        PaymentProcessedV1 payment = mapper.treeToValue(envelope.payload(), PaymentProcessedV1.class);
        requirePayload(envelope, payment.orderId(), payment.userId(), payment.userEmail());
        return new NewNotification(envelope.eventId(), envelope.eventType(),
                NotificationType.PAYMENT_RECEIPT, payment.userId(), payment.orderId(),
                payment.userEmail(), payment.amount(), payment.currency(), null);
    }

    private NewNotification fromPaymentFailed(EventEnvelope envelope) throws JsonProcessingException {
        requireVersion(envelope, 1);
        PaymentFailedV1 payment = mapper.treeToValue(envelope.payload(), PaymentFailedV1.class);
        requirePayload(envelope, payment.orderId(), payment.userId(), payment.userEmail());
        return new NewNotification(envelope.eventId(), envelope.eventType(),
                NotificationType.PAYMENT_PROBLEM, payment.userId(), payment.orderId(),
                payment.userEmail(), payment.amount(), payment.currency(), payment.reason());
    }

    private static void requireVersion(EventEnvelope envelope, int supported) {
        if (envelope.eventVersion() != supported) {
            throw new UnsupportedEventVersionException(envelope.eventType(), envelope.eventVersion());
        }
    }

    /** orderId, userId and userEmail: without them there is no one to tell, or nothing to say. */
    private static void requirePayload(EventEnvelope envelope, String orderId, String userId,
                                       String userEmail) {
        if (!StringUtils.hasText(orderId) || !StringUtils.hasText(userId)
                || !StringUtils.hasText(userEmail)) {
            throw new MalformedEventException(envelope.eventType() + " " + envelope.eventId()
                    + " lacks orderId, userId or userEmail");
        }
    }
}
