package com.onlineshop.notification;

import static org.assertj.core.api.Assertions.assertThat;

import com.onlineshop.notification.service.LoggingNotificationSender;
import com.onlineshop.notification.service.NotificationSender;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.test.context.ActiveProfiles;

@SpringBootTest
@ActiveProfiles("test")
class NotificationServiceApplicationTests {

    @Autowired
    private NotificationSender sender;

    @Test
    void contextLoads() {
        // Starting at all proves the wiring; no broker is needed with the listener stopped.
    }

    @Test
    void theLoggingSenderIsTheDefault() {
        assertThat(sender).isInstanceOf(LoggingNotificationSender.class);
    }
}
