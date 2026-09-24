package com.onlineshop.notification.messaging;

/**
 * A known event type arrived with a version this service cannot read. That is a contract
 * break, not noise, so it is dead-lettered without retrying: it will never succeed.
 */
public class UnsupportedEventVersionException extends RuntimeException {

    public UnsupportedEventVersionException(String eventType, Integer version) {
        super(eventType + " version " + version + " is not supported");
    }
}
