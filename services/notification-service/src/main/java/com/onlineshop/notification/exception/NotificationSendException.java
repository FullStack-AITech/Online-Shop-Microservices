package com.onlineshop.notification.exception;

/**
 * A delivery attempt failed. Checked on purpose: every caller of a sender must decide what a
 * failure means for the row, rather than letting it escape by accident.
 */
public class NotificationSendException extends Exception {

    public NotificationSendException(String message) {
        super(message);
    }

    public NotificationSendException(String message, Throwable cause) {
        super(message, cause);
    }
}
