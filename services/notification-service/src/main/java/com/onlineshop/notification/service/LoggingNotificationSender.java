package com.onlineshop.notification.service;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.boot.autoconfigure.condition.ConditionalOnProperty;
import org.springframework.stereotype.Component;

/**
 * The default sender: writes a line to the log and delivers nothing.
 *
 * <p>It logs the channel, a masked recipient and the subject, and never the body. Logs are
 * shipped, retained and read far more widely than a mailbox, so they are the wrong place
 * for a customer's address or the contents of their order.
 */
@Component
@ConditionalOnProperty(name = "notification.sender", havingValue = "logging", matchIfMissing = true)
public class LoggingNotificationSender implements NotificationSender {

    private static final Logger log = LoggerFactory.getLogger(LoggingNotificationSender.class);

    @Override
    public void send(OutgoingMessage message) {
        log.info("Would send {} to {}: \"{}\"",
                message.channel(), Recipients.mask(message.recipient()), message.subject());
    }
}
