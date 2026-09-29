package com.onlineshop.notification.web;

import com.onlineshop.notification.dto.NotificationResponse;
import com.onlineshop.notification.dto.PageResponse;
import com.onlineshop.notification.service.NotificationService;
import io.swagger.v3.oas.annotations.Operation;
import io.swagger.v3.oas.annotations.tags.Tag;
import org.springframework.data.domain.PageRequest;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.PathVariable;
import org.springframework.web.bind.annotation.RequestMapping;
import org.springframework.web.bind.annotation.RequestParam;
import org.springframework.web.bind.annotation.RestController;

/**
 * Notification history REST API (v1). Read-only on purpose: notifications are created by
 * events, and there is no caller yet that needs to create, change or delete one.
 */
@RestController
@RequestMapping("/api/v1/notifications")
@Tag(name = "notifications", description = "History of the messages sent to customers")
public class NotificationController {

    private static final int MAX_PAGE_SIZE = 100;

    private final NotificationService service;

    public NotificationController(NotificationService service) {
        this.service = service;
    }

    @GetMapping
    @Operation(summary = "List a user's notifications, newest first")
    public PageResponse<NotificationResponse> list(
            @RequestParam String userId,
            @RequestParam(defaultValue = "0") int page,
            @RequestParam(defaultValue = "20") int size) {
        // Unsorted here: the repository query already orders by createdAt descending.
        PageRequest pageRequest = PageRequest.of(
                Math.max(page, 0), Math.min(Math.max(size, 1), MAX_PAGE_SIZE));
        return PageResponse.of(service.listForUser(userId, pageRequest), NotificationResponse::from);
    }

    @GetMapping("/{id}")
    @Operation(summary = "Get a notification by id")
    public NotificationResponse getById(@PathVariable String id) {
        return NotificationResponse.from(service.getById(id));
    }
}
