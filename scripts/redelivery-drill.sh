#!/usr/bin/env bash
# Redelivery drill (#25): replay every topic from the beginning into every consumer and
# prove nothing changes.
#
#   docker compose up -d --build && bash scripts/smoke-test.sh && bash scripts/redelivery-drill.sh
#
# Kafka delivers at least once, so every consumer will eventually see a message twice.
# This forces the worst case — every message, again, at once — and asserts:
#   - no second payment row, no second notification row, no order changed status;
#   - no new event was published onto `payments` (a replayed OrderCreated must not
#     produce a second PaymentProcessed).
#
# Offsets can only be reset on an empty group, so each consumer is stopped first.
set -euo pipefail

DEADLINE_SECONDS=${DEADLINE_SECONDS:-60}
dc() { docker compose "$@"; }
fail() { echo "FAIL: $*" >&2; exit 1; }
sql() { # container user db query
  dc exec -T "$1" psql -U "$2" -d "$3" -tAc "$4" | tr -d '[:space:]'
}

snapshot() {
  echo "payments=$(sql payment-db payment_service payment_db 'SELECT count(*) FROM payments')"
  echo "notifications=$(sql notification-db notification_service notification_db 'SELECT count(*) FROM notifications')"
  echo "orders=$(sql order-db order_service order_db \
    "SELECT string_agg(status || ':' || n, ',' ORDER BY status) FROM (SELECT status, count(*) n FROM orders GROUP BY status) s")"
  echo "payments_end_offset=$(dc exec -T kafka kafka-get-offsets --bootstrap-server localhost:9092 --topic payments \
    | awk -F: '{ sum += $3 } END { print sum }')"
}

# group container topics...
replay() {
  local group=$1 container=$2; shift 2
  local topic_args=()
  for topic in "$@"; do topic_args+=(--topic "$topic"); done

  echo "--> replaying ${*} into $group"
  dc stop "$container" > /dev/null
  dc exec -T kafka kafka-consumer-groups --bootstrap-server localhost:9092 \
    --group "$group" --reset-offsets --to-earliest "${topic_args[@]}" --execute > /dev/null
  dc start "$container" > /dev/null

  local deadline=$((SECONDS + DEADLINE_SECONDS)) lag
  while (( SECONDS < deadline )); do
    lag=$(dc exec -T kafka kafka-consumer-groups --bootstrap-server localhost:9092 --describe --group "$group" 2>/dev/null \
      | awk 'NR > 1 && $6 ~ /^[0-9]+$/ { sum += $6; rows++ } END { print (rows ? sum : "unknown") }')
    [[ $lag == 0 ]] && { echo "    $group caught up"; return 0; }
    sleep 1
  done
  fail "$group did not catch up within ${DEADLINE_SECONDS}s (lag $lag)"
}

before=$(snapshot)
echo "before:"; echo "$before" | sed 's/^/    /'

replay payment-service      payment-consumer     orders
replay order-service        order-service        payments
replay notification-service notification-service orders payments

after=$(snapshot)
echo "after:"; echo "$after" | sed 's/^/    /'

[[ $before == "$after" ]] || fail "replaying the topics changed state — a consumer is not idempotent"
echo "PASS: replaying every topic from the start had no second effect"
