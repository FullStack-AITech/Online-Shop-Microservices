package com.onlineshop.notification;

import static org.hamcrest.Matchers.is;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.delete;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.post;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.jsonPath;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

import com.onlineshop.notification.domain.Notification;
import com.onlineshop.notification.domain.NotificationChannel;
import com.onlineshop.notification.domain.NotificationType;
import com.onlineshop.notification.repository.NotificationRepository;
import java.util.UUID;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.autoconfigure.web.servlet.AutoConfigureMockMvc;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.test.web.servlet.MockMvc;

/** Exercises the history API against an in-memory database. */
@SpringBootTest
@AutoConfigureMockMvc
@ActiveProfiles("test")
class NotificationApiIntegrationTest {

    @Autowired
    private MockMvc mockMvc;

    @Autowired
    private NotificationRepository repository;

    @BeforeEach
    void clear() {
        repository.deleteAll();
    }

    private Notification save(String userId, NotificationType type) {
        return repository.save(new Notification(UUID.randomUUID(), "OrderCreated", type,
                NotificationChannel.EMAIL, userId, "order-1", "alice@example.com",
                "Subject for " + type, "Body"));
    }

    @Test
    void listIsEmptyForAUserWithNoNotifications() throws Exception {
        mockMvc.perform(get("/api/v1/notifications").param("userId", "nobody"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.total", is(0)))
                .andExpect(jsonPath("$.items.length()", is(0)));
    }

    @Test
    void listReturnsOnlyThatUsersNotificationsNewestFirst() throws Exception {
        save("user-1", NotificationType.ORDER_CONFIRMATION);
        Thread.sleep(5); // distinct createdAt values, so the order is deterministic
        save("user-1", NotificationType.PAYMENT_RECEIPT);
        save("user-2", NotificationType.ORDER_CONFIRMATION);

        mockMvc.perform(get("/api/v1/notifications").param("userId", "user-1"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.total", is(2)))
                .andExpect(jsonPath("$.items[0].type", is("PAYMENT_RECEIPT")))
                .andExpect(jsonPath("$.items[1].type", is("ORDER_CONFIRMATION")))
                .andExpect(jsonPath("$.items[0].recipient", is("a***@example.com")))
                .andExpect(jsonPath("$.items[0].status", is("PENDING")));
    }

    @Test
    void pageSizeIsCappedAt100() throws Exception {
        mockMvc.perform(get("/api/v1/notifications").param("userId", "user-1").param("size", "500"))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.size", is(100)));
    }

    @Test
    void getByIdReturnsTheNotification() throws Exception {
        Notification saved = save("user-1", NotificationType.ORDER_CONFIRMATION);
        mockMvc.perform(get("/api/v1/notifications/{id}", saved.getId()))
                .andExpect(status().isOk())
                .andExpect(jsonPath("$.id", is(saved.getId())))
                .andExpect(jsonPath("$.channel", is("EMAIL")))
                .andExpect(jsonPath("$.body").doesNotExist());
    }

    @Test
    void getByUnknownIdReturns404() throws Exception {
        mockMvc.perform(get("/api/v1/notifications/{id}", "missing"))
                .andExpect(status().isNotFound())
                .andExpect(jsonPath("$.error", is("notification_not_found")));
    }

    @Test
    void listWithoutAUserIdIsRejected() throws Exception {
        mockMvc.perform(get("/api/v1/notifications"))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.error", is("validation_failed")));
    }

    @Test
    void listWithABlankUserIdIsRejected() throws Exception {
        mockMvc.perform(get("/api/v1/notifications").param("userId", " "))
                .andExpect(status().isBadRequest())
                .andExpect(jsonPath("$.error", is("invalid_request")));
    }

    @Test
    void theApiIsReadOnly() throws Exception {
        mockMvc.perform(post("/api/v1/notifications").contentType("application/json").content("{}"))
                .andExpect(status().isMethodNotAllowed());
        mockMvc.perform(delete("/api/v1/notifications/{id}", "any"))
                .andExpect(status().isMethodNotAllowed());
    }
}
