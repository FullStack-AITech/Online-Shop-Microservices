package com.onlineshop.notification.service;

import java.math.BigDecimal;
import java.math.RoundingMode;
import org.springframework.stereotype.Component;

/**
 * Turns a notification into the words the customer reads.
 *
 * <p>Plain text in code for now. Templates in files, localisation and HTML email are for
 * when a real provider exists; until then this keeps the wording reviewable in one place.
 */
@Component
public class NotificationTemplates {

    public record Content(String subject, String body) {
    }

    public Content render(NewNotification notification) {
        String order = notification.orderId();
        return switch (notification.type()) {
            case ORDER_CONFIRMATION -> new Content(
                    "We have received your order " + order,
                    "Thank you for your order " + order + ". The total is "
                            + money(notification) + ". We will let you know as soon as "
                            + "payment has been taken.");
            case PAYMENT_RECEIPT -> new Content(
                    "Payment received for order " + order,
                    "We have received your payment of " + money(notification)
                            + " for order " + order + ". Thank you for shopping with us.");
            case PAYMENT_PROBLEM -> new Content(
                    "There was a problem with the payment for order " + order,
                    "We could not take the payment of " + money(notification)
                            + " for order " + order + " (" + readable(notification.reason())
                            + "). The order has not been completed and you have not been "
                            + "charged.");
        };
    }

    private static String money(NewNotification notification) {
        BigDecimal amount = notification.amount();
        if (amount == null) {
            return "the amount shown in your account";
        }
        String formatted = amount.setScale(2, RoundingMode.HALF_UP).toPlainString();
        return notification.currency() == null ? formatted : notification.currency() + " " + formatted;
    }

    /** {@code card_declined} reads as "card declined". */
    private static String readable(String reason) {
        return reason == null || reason.isBlank() ? "no reason given" : reason.replace('_', ' ');
    }
}
