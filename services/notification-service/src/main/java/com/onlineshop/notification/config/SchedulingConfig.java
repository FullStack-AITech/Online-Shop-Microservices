package com.onlineshop.notification.config;

import org.springframework.boot.autoconfigure.condition.ConditionalOnProperty;
import org.springframework.context.annotation.Configuration;
import org.springframework.scheduling.annotation.EnableScheduling;

/**
 * Turns on the background jobs (send retries, processed_events pruning).
 *
 * <p>Behind one switch rather than on the application class so the test profile can turn
 * the timers off while keeping the job beans: tests call the job methods directly, which
 * is deterministic where waiting for a timer is not.
 */
@Configuration
@EnableScheduling
@ConditionalOnProperty(name = "notification.scheduling.enabled", havingValue = "true",
        matchIfMissing = true)
public class SchedulingConfig {
}
