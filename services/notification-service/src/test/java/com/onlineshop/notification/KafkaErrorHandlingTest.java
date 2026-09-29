package com.onlineshop.notification;

import static org.assertj.core.api.Assertions.assertThat;
import static org.assertj.core.api.Assertions.assertThatThrownBy;
import static org.mockito.Mockito.mock;

import com.fasterxml.jackson.core.JsonParseException;
import com.onlineshop.notification.messaging.KafkaConfig;
import com.onlineshop.notification.messaging.MalformedEventException;
import com.onlineshop.notification.messaging.UnsupportedEventVersionException;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.List;
import org.apache.kafka.clients.consumer.ConsumerRecord;
import org.apache.kafka.clients.consumer.MockConsumer;
import org.apache.kafka.clients.consumer.OffsetResetStrategy;
import org.apache.kafka.clients.producer.MockProducer;
import org.apache.kafka.clients.producer.ProducerRecord;
import org.apache.kafka.common.PartitionInfo;
import org.apache.kafka.common.TopicPartition;
import org.apache.kafka.common.header.Header;
import org.apache.kafka.common.serialization.StringSerializer;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.springframework.kafka.KafkaException;
import org.springframework.kafka.core.KafkaTemplate;
import org.springframework.kafka.listener.DefaultErrorHandler;
import org.springframework.kafka.listener.ListenerExecutionFailedException;
import org.springframework.kafka.listener.MessageListenerContainer;
import org.springframework.kafka.mock.MockProducerFactory;

/**
 * The retry and dead-letter policy, driven through the real error handler and recoverer
 * with Kafka's mock producer and consumer. Needs no broker, so it runs everywhere; the
 * embedded-broker test covers the same path end to end.
 */
class KafkaErrorHandlingTest {

    private MockProducer<String, String> producer;
    private MockConsumer<String, String> consumer;
    private DefaultErrorHandler handler;
    private final MessageListenerContainer container = mock(MessageListenerContainer.class);
    private final TopicPartition orders = new TopicPartition("orders", 2);

    @BeforeEach
    void setUp() {
        // KafkaTemplate closes a factory's producer after each send; keep this one open so
        // its history spans the whole test.
        producer = new MockProducer<>(true, new StringSerializer(), new StringSerializer()) {
            @Override
            public void close(Duration timeout) {
            }
        };
        KafkaTemplate<String, String> template =
                new KafkaTemplate<>(new MockProducerFactory<>(() -> producer));
        handler = new KafkaConfig().kafkaErrorHandler(template, 0L);

        consumer = new MockConsumer<>(OffsetResetStrategy.EARLIEST);
        consumer.assign(List.of(orders));
        consumer.updatePartitions("orders.dlq",
                List.of(new PartitionInfo("orders.dlq", 0, null, null, null)));
    }

    private ConsumerRecord<String, String> record() {
        return record(41L);
    }

    private ConsumerRecord<String, String> record(long offset) {
        ConsumerRecord<String, String> record =
                new ConsumerRecord<>("orders", 2, offset, "order-key", "not-json");
        record.headers().add("correlationId", "c-1".getBytes(StandardCharsets.UTF_8));
        return record;
    }

    /** What the container does with an exception thrown by the listener. */
    private void fail(ConsumerRecord<String, String> record, Exception cause) {
        handler.handleRemaining(new ListenerExecutionFailedException("listener failed", cause),
                List.of(record), consumer, container);
    }

    private static String header(ProducerRecord<?, ?> record, String name) {
        Header header = record.headers().lastHeader(name);
        assertThat(header).as("header " + name).isNotNull();
        return new String(header.value(), StandardCharsets.UTF_8);
    }

    @Test
    void unparseableMessagesAreDeadLetteredAtOnceWithTheContractHeaders() {
        fail(record(), new JsonParseException(null, "Unrecognized token 'not'"));

        assertThat(producer.history()).singleElement().satisfies(dead -> {
            assertThat(dead.topic()).isEqualTo("orders.dlq");
            assertThat(dead.partition()).isZero();
            assertThat(dead.key()).isEqualTo("order-key");
            assertThat(dead.value()).isEqualTo("not-json");
            assertThat(header(dead, "dlq-original-topic")).isEqualTo("orders");
            assertThat(header(dead, "dlq-original-partition")).isEqualTo("2");
            assertThat(header(dead, "dlq-original-offset")).isEqualTo("41");
            assertThat(header(dead, "dlq-consumer")).isEqualTo("notification-service");
            assertThat(header(dead, "dlq-error"))
                    .startsWith("com.fasterxml.jackson.core.JsonParseException: Unrecognized token");
            assertThat(header(dead, "dlq-failed-at")).endsWith("Z");
            assertThat(header(dead, "correlationId")).isEqualTo("c-1");
            assertThat(dead.headers().headers("kafka_dlt-exception-stacktrace")).isEmpty();
        });
    }

    @Test
    void unknownVersionsAndMalformedEventsAreNotRetried() {
        fail(record(41L), new UnsupportedEventVersionException("OrderCreated", 2));
        fail(record(42L), new MalformedEventException("no userEmail"));
        assertThat(producer.history()).hasSize(2);
    }

    @Test
    void otherFailuresGetThreeAttemptsInTotalBeforeTheDeadLetterTopic() {
        ConsumerRecord<String, String> record = record();
        IllegalStateException databaseDown = new IllegalStateException("database unavailable");

        // Attempts 1 and 2: the handler seeks back so the record is redelivered.
        assertThatThrownBy(() -> fail(record, databaseDown)).isInstanceOf(KafkaException.class);
        assertThatThrownBy(() -> fail(record, databaseDown)).isInstanceOf(KafkaException.class);
        assertThat(producer.history()).isEmpty();

        // Attempt 3 gives up and dead-letters it.
        fail(record, databaseDown);
        assertThat(producer.history()).singleElement()
                .satisfies(dead -> assertThat(header(dead, "dlq-error"))
                        .isEqualTo("java.lang.IllegalStateException: database unavailable"));
    }

    @Test
    void theErrorHeaderIsCappedAt500Characters() {
        fail(record(), new MalformedEventException("x".repeat(2000)));
        assertThat(header(producer.history().get(0), "dlq-error")).hasSize(500);
    }
}
