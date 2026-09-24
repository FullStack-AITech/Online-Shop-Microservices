package com.onlineshop.notification.messaging;

import com.fasterxml.jackson.annotation.JsonIgnoreProperties;
import java.math.BigDecimal;

/** The fields of {@code PaymentProcessed} v1 this service uses. */
@JsonIgnoreProperties(ignoreUnknown = true)
public record PaymentProcessedV1(
        String paymentId,
        String orderId,
        String userId,
        String userEmail,
        BigDecimal amount,
        String currency) {
}
