package com.onlineshop.notification;

import com.onlineshop.notification.exception.NotificationSendException;
import com.onlineshop.notification.service.NotificationSender;
import com.onlineshop.notification.service.OutgoingMessage;
import java.util.List;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.concurrent.atomic.AtomicInteger;

/**
 * A sender that fails the next N calls and then succeeds, recording what it delivered.
 * Stands in for a flaky provider.
 */
public class FailingNotificationSender implements NotificationSender {

    private final AtomicInteger failuresLeft = new AtomicInteger();
    private final AtomicInteger calls = new AtomicInteger();
    private final List<OutgoingMessage> delivered = new CopyOnWriteArrayList<>();

    /** The next {@code times} sends throw; later ones succeed. */
    public void failNext(int times) {
        failuresLeft.set(times);
    }

    public void reset() {
        failuresLeft.set(0);
        calls.set(0);
        delivered.clear();
    }

    @Override
    public void send(OutgoingMessage message) throws NotificationSendException {
        calls.incrementAndGet();
        if (failuresLeft.getAndUpdate(left -> Math.max(left - 1, 0)) > 0) {
            throw new NotificationSendException("provider unavailable");
        }
        delivered.add(message);
    }

    public int calls() {
        return calls.get();
    }

    public List<OutgoingMessage> delivered() {
        return delivered;
    }
}
