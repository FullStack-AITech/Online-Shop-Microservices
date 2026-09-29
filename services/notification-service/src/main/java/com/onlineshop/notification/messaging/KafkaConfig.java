package com.onlineshop.notification.messaging;

import com.fasterxml.jackson.core.JsonProcessingException;
import com.onlineshop.notification.service.NotificationService;
import java.nio.charset.StandardCharsets;
import java.time.Instant;
import org.apache.kafka.clients.consumer.ConsumerRecord;
import org.apache.kafka.common.TopicPartition;
import org.apache.kafka.common.header.Headers;
import org.apache.kafka.common.header.internals.RecordHeaders;
import org.springframework.beans.factory.annotation.Value;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.kafka.core.KafkaOperations;
import org.springframework.kafka.listener.DeadLetterPublishingRecoverer;
import org.springframework.kafka.listener.DeadLetterPublishingRecoverer.HeaderNames.HeadersToAdd;
import org.springframework.kafka.listener.DefaultErrorHandler;
import org.springframework.kafka.listener.ListenerExecutionFailedException;
import org.springframework.util.backoff.FixedBackOff;

/**
 * What happens when the listener throws.
 *
 * <p>The rules are the platform's (docs/events/README.md, "Dead letter topics"): up to
 * three attempts in total with a short backoff, then the message goes to
 * {@code <topic>.dlq} and the offset moves on, so one poison message cannot block its
 * partition. Messages that can never succeed (unparseable, malformed, unknown version) skip
 * the retries.
 *
 * <p>Boot wires a {@code CommonErrorHandler} bean into its listener container factory, so
 * declaring the bean is enough.
 */
@Configuration
public class KafkaConfig {

    static final int MAX_ERROR_HEADER_LENGTH = 500;

    @Bean
    public DefaultErrorHandler kafkaErrorHandler(
            KafkaOperations<?, ?> kafkaTemplate,
            @Value("${notification.kafka.retry-backoff-ms:1000}") long backoffMs) {
        // Every DLQ topic has one partition (infrastructure/kafka/create-topics.sh).
        DeadLetterPublishingRecoverer recoverer = new DeadLetterPublishingRecoverer(kafkaTemplate,
                (record, exception) -> new TopicPartition(record.topic() + ".dlq", 0));
        // The dlq-* headers are the platform's contract, read the same way whichever stack
        // dead-lettered the message. Spring's own kafka_dlt-* set (which includes a full stack
        // trace) is left out so the three services' DLQ entries look alike.
        recoverer.excludeHeader(HeadersToAdd.values());
        recoverer.setHeadersFunction(KafkaConfig::deadLetterHeaders);

        // FixedBackOff counts retries, not attempts: 1 attempt + 2 retries = 3 in total.
        DefaultErrorHandler handler = new DefaultErrorHandler(recoverer, new FixedBackOff(backoffMs, 2));
        handler.addNotRetryableExceptions(
                JsonProcessingException.class,
                MalformedEventException.class,
                UnsupportedEventVersionException.class);
        return handler;
    }

    static Headers deadLetterHeaders(ConsumerRecord<?, ?> record, Exception exception) {
        Throwable cause = exception instanceof ListenerExecutionFailedException && exception.getCause() != null
                ? exception.getCause()
                : exception;
        String error = cause.getClass().getName() + ": " + cause.getMessage();
        if (error.length() > MAX_ERROR_HEADER_LENGTH) {
            error = error.substring(0, MAX_ERROR_HEADER_LENGTH);
        }
        Headers headers = new RecordHeaders();
        headers.add("dlq-original-topic", bytes(record.topic()));
        headers.add("dlq-original-partition", bytes(String.valueOf(record.partition())));
        headers.add("dlq-original-offset", bytes(String.valueOf(record.offset())));
        headers.add("dlq-consumer", bytes(NotificationService.CONSUMER));
        headers.add("dlq-error", bytes(error));
        headers.add("dlq-failed-at", bytes(Instant.now().toString()));
        return headers;
    }

    private static byte[] bytes(String value) {
        return value.getBytes(StandardCharsets.UTF_8);
    }
}
