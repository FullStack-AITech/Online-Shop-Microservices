package com.onlineshop.notification;

import org.springframework.boot.SpringApplication;
import org.springframework.boot.autoconfigure.SpringBootApplication;

/**
 * Entry point for the Notification Service.
 *
 * <p>This service reacts to order and payment events and tells the customer what happened.
 * It owns its own database of every notification it attempted, and no other service reads
 * or writes that database.
 */
@SpringBootApplication
public class NotificationServiceApplication {

    public static void main(String[] args) {
        SpringApplication.run(NotificationServiceApplication.class, args);
    }
}
