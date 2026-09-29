package com.onlineshop.notification.messaging;

import com.fasterxml.jackson.annotation.JsonIgnoreProperties;
import java.math.BigDecimal;

/**
 * The fields of {@code OrderCreated} v1 this service uses. The lines are left out on
 * purpose: the confirmation quotes the total, and a field not read is a field whose change
 * cannot break us.
 */
@JsonIgnoreProperties(ignoreUnknown = true)
public record OrderCreatedV1(
        String orderId,
        String userId,
        String userEmail,
        String currency,
        BigDecimal totalAmount,
        Integer totalItems) {
}
