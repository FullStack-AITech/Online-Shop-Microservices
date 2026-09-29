package com.onlineshop.notification;

import static org.assertj.core.api.Assertions.assertThat;
import static org.awaitility.Awaitility.await;

import com.onlineshop.notification.messaging.ShopEventListener;
import com.onlineshop.notification.repository.NotificationRepository;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.List;
import java.util.Map;
import java.util.UUID;
import org.apache.kafka.clients.consumer.Consumer;
import org.apache.kafka.clients.consumer.ConsumerConfig;
import org.apache.kafka.clients.consumer.ConsumerRecord;
import org.apache.kafka.clients.producer.ProducerRecord;
import org.apache.kafka.common.header.Header;
import org.apache.kafka.common.header.internals.RecordHeader;
import org.apache.kafka.common.serialization.StringDeserializer;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Tag;
import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.data.domain.Pageable;
import org.springframework.kafka.config.KafkaListenerEndpointRegistry;
import org.springframework.kafka.core.DefaultKafkaConsumerFactory;
import org.springframework.kafka.core.KafkaTemplate;
import org.springframework.kafka.test.EmbeddedKafkaBroker;
import org.springframework.kafka.test.context.EmbeddedKafka;
import org.springframework.kafka.test.utils.ContainerTestUtils;
import org.springframework.kafka.test.utils.KafkaTestUtils;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.test.context.TestPropertySource;

/**
 * Through a real (in-JVM) broker: the listener, the offset handling and the dead-letter
 * path, none of which a direct call to the listener exercises.
 *
 * <p>Tagged so it can be excluded ({@code -DexcludedGroups=embedded-kafka}) on a machine
 * where the JVM cannot open loopback sockets; it runs by default.
 */
@Tag("embedded-kafka")
@SpringBootTest
@ActiveProfiles("test")
@EmbeddedKafka(kraft = true, partitions = 1, topics = {"orders", "payments", "orders.dlq", "payments.dlq"})
@TestPropertySource(properties = {
        "spring.kafka.bootstrap-servers=${spring.embedded.kafka.brokers}",
        "spring.kafka.listener.auto-startup=true"
})
class NotificationKafkaIntegrationTest {

    @Autowired
    private KafkaTemplate<String, String> kafkaTemplate;

    @Autowired
    private NotificationRepository notifications;

    @Autowired
    private KafkaListenerEndpointRegistry registry;

    @Autowired
    private EmbeddedKafkaBroker broker;

    @BeforeEach
    void waitForTheListener() {
        // Publishing before the partitions are assigned is fine (auto-offset-reset: earliest),
        // but waiting makes a slow start fail here rather than as a mysterious timeout.
        ContainerTestUtils.waitForAssignment(
                registry.getListenerContainer(ShopEventListener.LISTENER_ID), 2);
    }

    private long countFor(String userId) {
        return notifications.findByUserIdOrderByCreatedAtDesc(userId, Pageable.unpaged())
                .getTotalElements();
    }

    @Test
    void publishingTheSameEventTwiceProducesExactlyOneNotification() throws Exception {
        String userId = "user-" + UUID.randomUUID();
        String orderId = UUID.randomUUID().toString();
        String event = TestEvents.orderCreated(UUID.randomUUID(), orderId, userId, "alice@example.com");

        kafkaTemplate.send("orders", orderId, event).get();
        kafkaTemplate.send("orders", orderId, event).get();

        await().atMost(Duration.ofSeconds(10)).until(() -> countFor(userId) == 1);
        // And it stays at one once the duplicate has been consumed too.
        await().during(Duration.ofSeconds(2)).atMost(Duration.ofSeconds(3))
                .until(() -> countFor(userId) == 1);
    }

    @Test
    void aPoisonMessageIsDeadLetteredAndTheNextMessageIsStillProcessed() throws Exception {
        try (Consumer<String, String> dlq = dlqConsumer()) {
            broker.consumeFromAnEmbeddedTopic(dlq, "orders.dlq");

            kafkaTemplate.send(new ProducerRecord<>("orders", null, "poison", "not-json",
                    List.of(new RecordHeader("correlationId",
                            "c-1".getBytes(StandardCharsets.UTF_8))))).get();
            String userId = "user-" + UUID.randomUUID();
            String orderId = UUID.randomUUID().toString();
            kafkaTemplate.send("orders", orderId,
                    TestEvents.orderCreated(UUID.randomUUID(), orderId, userId, "bob@example.com")).get();

            ConsumerRecord<String, String> dead =
                    KafkaTestUtils.getSingleRecord(dlq, "orders.dlq", Duration.ofSeconds(10));
            assertThat(dead.key()).isEqualTo("poison");
            assertThat(dead.value()).isEqualTo("not-json");
            assertThat(header(dead, "dlq-original-topic")).isEqualTo("orders");
            assertThat(header(dead, "dlq-original-partition")).isEqualTo("0");
            assertThat(header(dead, "dlq-original-offset")).matches("\\d+");
            assertThat(header(dead, "dlq-consumer")).isEqualTo("notification-service");
            assertThat(header(dead, "dlq-error")).contains("JsonParseException").hasSizeLessThanOrEqualTo(500);
            assertThat(header(dead, "dlq-failed-at")).endsWith("Z");
            // The original headers travel with it; Spring's own kafka_dlt-* set does not.
            assertThat(header(dead, "correlationId")).isEqualTo("c-1");
            assertThat(dead.headers().headers("kafka_dlt-exception-message")).isEmpty();

            // The partition was not blocked by the poison message.
            await().atMost(Duration.ofSeconds(10)).until(() -> countFor(userId) == 1);
        }
    }

    private Consumer<String, String> dlqConsumer() {
        Map<String, Object> props = KafkaTestUtils.consumerProps("dlq-reader-" + UUID.randomUUID(),
                "false", broker);
        props.put(ConsumerConfig.AUTO_OFFSET_RESET_CONFIG, "earliest");
        return new DefaultKafkaConsumerFactory<>(props, new StringDeserializer(), new StringDeserializer())
                .createConsumer();
    }

    private static String header(ConsumerRecord<?, ?> record, String name) {
        Header header = record.headers().lastHeader(name);
        assertThat(header).as("header " + name).isNotNull();
        return new String(header.value(), StandardCharsets.UTF_8);
    }
}
