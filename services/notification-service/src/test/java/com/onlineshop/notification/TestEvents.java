package com.onlineshop.notification;

import java.util.UUID;

/** Event JSON in the shape docs/events/schemas defines, with the ids a test cares about. */
final class TestEvents {

    private TestEvents() {
    }

    static String orderCreated(UUID eventId, String orderId, String userId, String email) {
        return """
                {
                  "eventId": "%s",
                  "eventType": "OrderCreated",
                  "eventVersion": 1,
                  "occurredAt": "2026-09-24T10:00:00Z",
                  "correlationId": "5b0f6f5c-9a8e-4d3c-b2a1-f0e9d8c7b6a5",
                  "producer": "order-service",
                  "payload": {
                    "orderId": "%s",
                    "userId": "%s",
                    "userEmail": "%s",
                    "currency": "GBP",
                    "totalAmount": "99.98",
                    "totalItems": 2,
                    "lines": [
                      {
                        "productId": "6e5d4c3b-2a19-4f8e-9d7c-6b5a4f3e2d1c",
                        "sku": "KB-1",
                        "productName": "Keyboard",
                        "unitPrice": "49.99",
                        "quantity": 2,
                        "lineTotal": "99.98"
                      }
                    ],
                    "createdAt": "2026-09-24T10:00:00Z"
                  }
                }
                """.formatted(eventId, orderId, userId, email);
    }

    static String orderCreated(UUID eventId, String userId) {
        return orderCreated(eventId, UUID.randomUUID().toString(), userId, "alice@example.com");
    }

    static String paymentProcessed(UUID eventId, String orderId, String userId, String email) {
        return """
                {
                  "eventId": "%s",
                  "eventType": "PaymentProcessed",
                  "eventVersion": 1,
                  "occurredAt": "2026-09-24T10:00:02Z",
                  "correlationId": "5b0f6f5c-9a8e-4d3c-b2a1-f0e9d8c7b6a5",
                  "producer": "payment-service",
                  "payload": {
                    "paymentId": "c1d2e3f4-a5b6-4c7d-8e9f-0a1b2c3d4e5f",
                    "orderId": "%s",
                    "userId": "%s",
                    "userEmail": "%s",
                    "amount": "99.98",
                    "currency": "GBP",
                    "processedAt": "2026-09-24T10:00:02Z"
                  }
                }
                """.formatted(eventId, orderId, userId, email);
    }

    static String paymentFailed(UUID eventId, String orderId, String userId, String email) {
        return """
                {
                  "eventId": "%s",
                  "eventType": "PaymentFailed",
                  "eventVersion": 1,
                  "occurredAt": "2026-09-24T10:00:02Z",
                  "correlationId": "5b0f6f5c-9a8e-4d3c-b2a1-f0e9d8c7b6a5",
                  "producer": "payment-service",
                  "payload": {
                    "paymentId": "c1d2e3f4-a5b6-4c7d-8e9f-0a1b2c3d4e5f",
                    "orderId": "%s",
                    "userId": "%s",
                    "userEmail": "%s",
                    "amount": "1199.98",
                    "currency": "GBP",
                    "reason": "card_declined",
                    "failedAt": "2026-09-24T10:00:02Z"
                  }
                }
                """.formatted(eventId, orderId, userId, email);
    }
}
