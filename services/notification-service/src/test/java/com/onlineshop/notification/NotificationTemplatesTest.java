package com.onlineshop.notification;

import static org.assertj.core.api.Assertions.assertThat;

import com.onlineshop.notification.domain.NotificationType;
import com.onlineshop.notification.service.NewNotification;
import com.onlineshop.notification.service.NotificationTemplates;
import com.onlineshop.notification.service.Recipients;
import java.math.BigDecimal;
import java.util.UUID;
import org.junit.jupiter.api.Test;

/** Wording and masking. No Spring context needed. */
class NotificationTemplatesTest {

    private final NotificationTemplates templates = new NotificationTemplates();

    private NewNotification request(NotificationType type, String reason) {
        return new NewNotification(UUID.randomUUID(), "Event", type, "user-1", "order-1",
                "alice@example.com", new BigDecimal("99.98"), "GBP", reason);
    }

    @Test
    void everyTypeHasASubjectAndBodyNamingTheOrder() {
        for (NotificationType type : NotificationType.values()) {
            NotificationTemplates.Content content = templates.render(request(type, "card_declined"));
            assertThat(content.subject()).contains("order-1").hasSizeLessThanOrEqualTo(200);
            assertThat(content.body()).contains("order-1").contains("GBP 99.98");
        }
    }

    @Test
    void aPaymentProblemExplainsTheReason() {
        NotificationTemplates.Content content =
                templates.render(request(NotificationType.PAYMENT_PROBLEM, "card_declined"));
        assertThat(content.body()).contains("card declined");
    }

    @Test
    void recipientsAreMaskedToTheFirstLetterAndDomain() {
        assertThat(Recipients.mask("alice@example.com")).isEqualTo("a***@example.com");
        assertThat(Recipients.mask("not-an-address")).isEqualTo("***");
        assertThat(Recipients.mask(null)).isEqualTo("***");
    }
}
