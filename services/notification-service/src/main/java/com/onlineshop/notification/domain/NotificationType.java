package com.onlineshop.notification.domain;

/** Why the customer is being told something. One per event the service reacts to. */
public enum NotificationType {
    ORDER_CONFIRMATION,
    PAYMENT_RECEIPT,
    PAYMENT_PROBLEM
}
