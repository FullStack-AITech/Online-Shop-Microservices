package com.onlineshop.notification.service;

import com.onlineshop.notification.domain.NotificationChannel;

/** What a {@link NotificationSender} delivers. Deliberately knows nothing about events. */
public record OutgoingMessage(
        NotificationChannel channel,
        String recipient,
        String subject,
        String body) {
}
