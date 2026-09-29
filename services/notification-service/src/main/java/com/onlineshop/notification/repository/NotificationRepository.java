package com.onlineshop.notification.repository;

import com.onlineshop.notification.domain.Notification;
import com.onlineshop.notification.domain.NotificationStatus;
import java.time.Instant;
import java.util.List;
import org.springframework.data.domain.Page;
import org.springframework.data.domain.Pageable;
import org.springframework.data.jpa.repository.JpaRepository;
import org.springframework.stereotype.Repository;

@Repository
public interface NotificationRepository extends JpaRepository<Notification, String> {

    Page<Notification> findByUserIdOrderByCreatedAtDesc(String userId, Pageable pageable);

    List<Notification> findByStatus(NotificationStatus status);

    /** PENDING rows older than the cut-off were orphaned by a crash between insert and send. */
    List<Notification> findByStatusAndCreatedAtBefore(NotificationStatus status, Instant cutoff);
}
