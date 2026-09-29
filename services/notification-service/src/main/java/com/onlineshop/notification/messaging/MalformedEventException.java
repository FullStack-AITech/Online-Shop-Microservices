package com.onlineshop.notification.messaging;

/**
 * Valid JSON that is not a valid event: a missing envelope field, or a payload without the
 * fields the contract makes required. Like unparseable JSON, retrying cannot fix it.
 */
public class MalformedEventException extends RuntimeException {

    public MalformedEventException(String message) {
        super(message);
    }
}
