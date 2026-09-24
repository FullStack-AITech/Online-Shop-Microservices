package com.onlineshop.notification.service;

/** Helpers for showing an address without disclosing it. */
public final class Recipients {

    private Recipients() {
    }

    /**
     * {@code alice@example.com} becomes {@code a***@example.com}: enough to tell two
     * customers apart in a log or a support screen, not enough to harvest the address.
     */
    public static String mask(String recipient) {
        if (recipient == null || recipient.isBlank()) {
            return "***";
        }
        int at = recipient.indexOf('@');
        if (at <= 0) {
            return "***";
        }
        return recipient.charAt(0) + "***" + recipient.substring(at);
    }
}
