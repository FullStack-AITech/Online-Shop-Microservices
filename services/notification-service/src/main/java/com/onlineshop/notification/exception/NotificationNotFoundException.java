package com.onlineshop.notification.exception;

/** Raised when a notification id does not resolve to a row. */
public class NotificationNotFoundException extends RuntimeException {

    public NotificationNotFoundException(String notificationId) {
        super("Notification '" + notificationId + "' was not found");
    }
}
