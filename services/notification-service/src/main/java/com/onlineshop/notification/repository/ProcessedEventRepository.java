package com.onlineshop.notification.repository;

import com.onlineshop.notification.domain.ProcessedEvent;
import com.onlineshop.notification.domain.ProcessedEventId;
import java.time.Instant;
import java.util.UUID;
import org.springframework.data.jpa.repository.JpaRepository;
import org.springframework.data.jpa.repository.Modifying;
import org.springframework.data.jpa.repository.Query;
import org.springframework.data.repository.query.Param;
import org.springframework.stereotype.Repository;

@Repository
public interface ProcessedEventRepository extends JpaRepository<ProcessedEvent, ProcessedEventId> {

    /**
     * Records that {@code consumer} has handled {@code eventId}, inside the caller's
     * transaction.
     *
     * <p>Returns 1 for a first sighting and 0 for a duplicate. The conflict clause is the
     * guard: a "have I seen it?" select followed by an insert lets two deliveries both see
     * nothing, and catching a key violation instead is not an option on Postgres, where a
     * failed statement aborts the whole transaction. H2's PostgreSQL mode accepts the same
     * syntax, so the tests run the real statement.
     */
    @Modifying
    @Query(value = "INSERT INTO processed_events (event_id, consumer, processed_at) "
            + "VALUES (:eventId, :consumer, CURRENT_TIMESTAMP) ON CONFLICT DO NOTHING",
            nativeQuery = true)
    int tryRecord(@Param("eventId") UUID eventId, @Param("consumer") String consumer);

    @Modifying
    @Query("DELETE FROM ProcessedEvent p WHERE p.processedAt < :cutoff")
    int deleteProcessedBefore(@Param("cutoff") Instant cutoff);
}
