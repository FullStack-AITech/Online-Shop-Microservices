package com.onlineshop.notification;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;

import com.fasterxml.jackson.core.JsonProcessingException;
import com.onlineshop.notification.domain.Notification;
import com.onlineshop.notification.domain.NotificationStatus;
import com.onlineshop.notification.domain.NotificationType;
import com.onlineshop.notification.messaging.MalformedEventException;
import com.onlineshop.notification.messaging.ShopEventListener;
import com.onlineshop.notification.messaging.UnsupportedEventVersionException;
import com.onlineshop.notification.repository.NotificationRepository;
import com.onlineshop.notification.repository.ProcessedEventRepository;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.List;
import java.util.UUID;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.test.context.ActiveProfiles;

/**
 * Feeds event JSON straight to the listener, with the default logging sender. No broker:
 * the container is not started under the test profile.
 */
@SpringBootTest
@ActiveProfiles("test")
class ShopEventListenerTest {

    private static final Path EXAMPLES = Path.of("../../docs/events/examples");

    @Autowired
    private ShopEventListener listener;

    @Autowired
    private NotificationRepository notifications;

    @Autowired
    private ProcessedEventRepository processedEvents;

    @BeforeEach
    void clear() {
        notifications.deleteAll();
        processedEvents.deleteAll();
    }

    private Notification only() {
        List<Notification> all = notifications.findAll();
        assertThat(all).hasSize(1);
        return all.get(0);
    }

    @Test
    void orderCreatedBecomesASentOrderConfirmation() throws Exception {
        String orderId = UUID.randomUUID().toString();
        listener.onEvent(TestEvents.orderCreated(UUID.randomUUID(), orderId, "user-1", "alice@example.com"));

        Notification notification = only();
        assertThat(notification.getNotificationType()).isEqualTo(NotificationType.ORDER_CONFIRMATION);
        assertThat(notification.getRecipient()).isEqualTo("alice@example.com");
        assertThat(notification.getUserId()).isEqualTo("user-1");
        assertThat(notification.getOrderId()).isEqualTo(orderId);
        assertThat(notification.getBody()).contains("GBP 99.98");
        assertThat(notification.getStatus()).isEqualTo(NotificationStatus.SENT);
        assertThat(notification.getSendAttempts()).isEqualTo(1);
        assertThat(notification.getSentAt()).isNotNull();
    }

    @Test
    void paymentProcessedBecomesAPaymentReceipt() throws Exception {
        listener.onEvent(TestEvents.paymentProcessed(UUID.randomUUID(), UUID.randomUUID().toString(),
                "user-1", "bob@example.com"));

        Notification notification = only();
        assertThat(notification.getNotificationType()).isEqualTo(NotificationType.PAYMENT_RECEIPT);
        assertThat(notification.getRecipient()).isEqualTo("bob@example.com");
        assertThat(notification.getStatus()).isEqualTo(NotificationStatus.SENT);
    }

    @Test
    void paymentFailedBecomesAPaymentProblemWithTheReason() throws Exception {
        listener.onEvent(TestEvents.paymentFailed(UUID.randomUUID(), UUID.randomUUID().toString(),
                "user-1", "carol@example.com"));

        Notification notification = only();
        assertThat(notification.getNotificationType()).isEqualTo(NotificationType.PAYMENT_PROBLEM);
        assertThat(notification.getRecipient()).isEqualTo("carol@example.com");
        assertThat(notification.getBody()).contains("card declined").contains("GBP 1199.98");
        assertThat(notification.getStatus()).isEqualTo(NotificationStatus.SENT);
    }

    @Test
    void theSameEventTwiceProducesOneNotification() throws Exception {
        String event = TestEvents.orderCreated(UUID.randomUUID(), "user-1");
        listener.onEvent(event);
        listener.onEvent(event);

        assertThat(only().getSendAttempts()).isEqualTo(1);
        assertThat(processedEvents.count()).isEqualTo(1);
    }

    @Test
    void theContractExamplesAreAllAccepted() throws Exception {
        // The producers' own examples from docs/events: if the contract moves, this notices.
        for (String name : List.of("order-created", "payment-processed", "payment-failed",
                "order-cancelled")) {
            listener.onEvent(Files.readString(EXAMPLES.resolve(name + ".v1.example.json")));
        }
        assertThat(notifications.findAll())
                .extracting(Notification::getNotificationType)
                .containsExactlyInAnyOrder(NotificationType.ORDER_CONFIRMATION,
                        NotificationType.PAYMENT_RECEIPT, NotificationType.PAYMENT_PROBLEM);
    }

    @Test
    void unknownEventTypesAreIgnored() throws Exception {
        String cancelled = TestEvents.orderCreated(UUID.randomUUID(), "user-1")
                .replace("\"OrderCreated\"", "\"OrderCancelled\"");
        listener.onEvent(cancelled);
        listener.onEvent(cancelled.replace("\"OrderCancelled\"", "\"SomethingNew\""));

        assertThat(notifications.count()).isZero();
        assertThat(processedEvents.count()).isZero();
    }

    @Test
    void unknownFieldsAreIgnored() throws Exception {
        listener.onEvent(TestEvents.orderCreated(UUID.randomUUID(), "user-1")
                .replace("\"producer\"", "\"newEnvelopeField\": true, \"producer\"")
                .replace("\"currency\"", "\"giftWrap\": {\"colour\": \"red\"}, \"currency\""));
        assertThat(only().getStatus()).isEqualTo(NotificationStatus.SENT);
    }

    @Test
    void anUnknownVersionOfAKnownTypeIsRejected() {
        String v2 = TestEvents.orderCreated(UUID.randomUUID(), "user-1")
                .replace("\"eventVersion\": 1", "\"eventVersion\": 2");
        assertThatThrownBy(() -> listener.onEvent(v2))
                .isInstanceOf(UnsupportedEventVersionException.class);
        assertThat(notifications.count()).isZero();
    }

    @Test
    void unparseableJsonIsRejected() {
        assertThatThrownBy(() -> listener.onEvent("not-json"))
                .isInstanceOf(JsonProcessingException.class);
        assertThatThrownBy(() -> listener.onEvent("[]"))
                .isInstanceOf(JsonProcessingException.class);
    }

    @Test
    void anEventWithoutARecipientIsRejected() {
        String noEmail = TestEvents.orderCreated(UUID.randomUUID(), "user-1")
                .replace("\"userEmail\": \"alice@example.com\",", "");
        assertThatThrownBy(() -> listener.onEvent(noEmail))
                .isInstanceOf(MalformedEventException.class);
        assertThatThrownBy(() -> listener.onEvent("{}"))
                .isInstanceOf(MalformedEventException.class);
        assertThat(notifications.count()).isZero();
    }
}
