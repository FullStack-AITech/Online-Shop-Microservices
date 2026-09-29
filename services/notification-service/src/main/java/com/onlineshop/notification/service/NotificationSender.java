package com.onlineshop.notification.service;

import com.onlineshop.notification.exception.NotificationSendException;

/**
 * The only way a message leaves the service.
 *
 * <p>Everything goes through this interface so the implementation is chosen by
 * configuration. The default one only logs, which means no environment can email a real
 * customer by accident; a real provider is a new implementation, not a code change here.
 */
public interface NotificationSender {

    void send(OutgoingMessage message) throws NotificationSendException;
}
