package com.onlineshop.notification.service;

import com.onlineshop.notification.repository.ProcessedEventRepository;
import java.time.Duration;
import java.time.Instant;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.scheduling.annotation.Scheduled;
import org.springframework.stereotype.Component;
import org.springframework.transaction.annotation.Transactional;

/**
 * Keeps processed_events from growing forever.
 *
 * <p>A marker only matters while its event can still be redelivered, and nothing older than
 * the topic retention (7 days) can be. The default of 8 days leaves a day's margin.
 */
@Component
public class ProcessedEventPruner {

    private static final Logger log = LoggerFactory.getLogger(ProcessedEventPruner.class);

    private final ProcessedEventRepository processedEvents;
    private final Duration retention;

    public ProcessedEventPruner(ProcessedEventRepository processedEvents,
                                @Value("${notification.processed-events.retention:P8D}")
                                Duration retention) {
        this.processedEvents = processedEvents;
        this.retention = retention;
    }

    /** @return how many markers were deleted */
    @Scheduled(cron = "${notification.processed-events.prune-cron:0 0 * * * *}")
    @Transactional
    public int prune() {
        int deleted = processedEvents.deleteProcessedBefore(Instant.now().minus(retention));
        if (deleted > 0) {
            log.info("Pruned {} processed_events row(s) older than {}", deleted, retention);
        }
        return deleted;
    }
}
