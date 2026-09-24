#!/usr/bin/env bash
# End-to-end smoke test for the Compose stack: one full checkout through every service.
#
#   docker compose up -d --build && bash scripts/smoke-test.sh
#
# Happy path:  register a user, create a product, place an order, and watch it become
#              Paid — which only happens if OrderCreated reached the Payment Service and
#              PaymentProcessed came back. Then check the stock went down and the
#              customer was notified.
# Decline path: an order over the fake provider's limit must end Failed (card_declined).
#
# Asynchronous results are polled with a deadline, never asserted after a fixed sleep:
# a cold JVM or CLR makes any fixed sleep either flaky or slow.
#
# Exits non-zero on the first failure, so CI (#50) can run it.
set -euo pipefail

USER_URL=${USER_URL:-http://localhost:8000}
PRODUCT_URL=${PRODUCT_URL:-http://localhost:8081}
ORDER_URL=${ORDER_URL:-http://localhost:8082}
PAYMENT_URL=${PAYMENT_URL:-http://localhost:8083}
NOTIFICATION_URL=${NOTIFICATION_URL:-http://localhost:8084}
DEADLINE_SECONDS=${DEADLINE_SECONDS:-30}
PYTHON=${PYTHON:-$(command -v python3 || command -v python)}

RUN_ID=$(date +%s)-$RANDOM
CORRELATION_ID="smoke-$RUN_ID"

fail() { echo "FAIL: $*" >&2; exit 1; }
step() { echo "--> $*"; }

# json <field path> — reads JSON on stdin, prints one field ("items.0.status").
json() {
  "$PYTHON" -c '
import json, sys
value = json.load(sys.stdin)
for part in sys.argv[1].split("."):
    value = value[int(part)] if isinstance(value, list) else value[part]
print(value)' "$1"
}

# post <url> <body> [extra curl args...] — prints the body; fails on a non-2xx status.
post() {
  local url=$1 body=$2; shift 2
  local response status
  response=$(curl -sS -w '\n%{http_code}' -X POST "$url" \
    -H 'Content-Type: application/json' -H "X-Correlation-Id: $CORRELATION_ID" "$@" -d "$body")
  status=${response##*$'\n'}
  response=${response%$'\n'*}
  [[ $status == 2* ]] || fail "POST $url -> $status: $response"
  printf '%s' "$response"
}

wait_ready() { # name url
  local deadline=$((SECONDS + DEADLINE_SECONDS * 4))
  until curl -sf "$2" > /dev/null; do
    (( SECONDS < deadline )) || fail "$1 not ready at $2"
    sleep 1
  done
  echo "    $1 ready"
}

# poll_status <order id> <expected status> — prints the final order JSON.
poll_status() {
  local order_id=$1 expected=$2 deadline=$((SECONDS + DEADLINE_SECONDS)) order status=""
  while (( SECONDS < deadline )); do
    order=$(curl -sf "$ORDER_URL/api/v1/orders/$order_id") || true
    status=$(printf '%s' "$order" | json status 2>/dev/null || true)
    if [[ $status == "$expected" ]]; then
      printf '%s' "$order"
      return 0
    fi
    sleep 0.5
  done
  fail "order $order_id: expected $expected within ${DEADLINE_SECONDS}s, last status '${status:-none}'"
}

# poll_contains <url> <text>... — waits until the response body contains every text.
poll_contains() {
  local url=$1; shift
  local deadline=$((SECONDS + DEADLINE_SECONDS)) body="" missing
  while (( SECONDS < deadline )); do
    body=$(curl -sf "$url") || true
    missing=""
    for text in "$@"; do [[ $body == *"$text"* ]] || missing+=" $text"; done
    [[ -z $missing ]] && return 0
    sleep 0.5
  done
  fail "$url: still missing${missing} after ${DEADLINE_SECONDS}s"
}

step "waiting for every service to be ready"
wait_ready user-service         "$USER_URL/health/ready"
wait_ready product-service      "$PRODUCT_URL/actuator/health/readiness"
wait_ready order-service        "$ORDER_URL/health/ready"
wait_ready payment-service      "$PAYMENT_URL/health/ready"
wait_ready notification-service "$NOTIFICATION_URL/actuator/health/readiness"

step "creating a user and two products"
USER_ID=$(post "$USER_URL/api/v1/users" \
  "{\"email\":\"smoke-$RUN_ID@example.com\",\"full_name\":\"Smoke Test\",\"password\":\"password123\"}" | json id)
KEYBOARD_ID=$(post "$PRODUCT_URL/api/v1/products" \
  "{\"sku\":\"KB-$RUN_ID\",\"name\":\"Keyboard\",\"price\":49.99,\"currency\":\"GBP\",\"stockQuantity\":10}" | json id)
# Priced so two of them exceed the fake provider's decline limit (1000.00).
MONITOR_ID=$(post "$PRODUCT_URL/api/v1/products" \
  "{\"sku\":\"MON-$RUN_ID\",\"name\":\"Monitor\",\"price\":600.00,\"currency\":\"GBP\",\"stockQuantity\":10}" | json id)

step "happy path: an order is paid through the event flow"
ORDER_ID=$(post "$ORDER_URL/api/v1/orders" \
  "{\"userId\":\"$USER_ID\",\"lines\":[{\"productId\":\"$KEYBOARD_ID\",\"quantity\":2}]}" | json id)
echo "    order $ORDER_ID placed"
poll_status "$ORDER_ID" Paid > /dev/null
echo "    order $ORDER_ID is Paid"

poll_contains "$PAYMENT_URL/api/v1/payments?order_id=$ORDER_ID" Captured
echo "    payment captured"

STOCK=$(curl -sf "$PRODUCT_URL/api/v1/products/$KEYBOARD_ID" | json stockQuantity)
[[ $STOCK == 8 ]] || fail "expected keyboard stock 8 after ordering 2 of 10, got $STOCK"
echo "    stock went from 10 to 8"

poll_contains "$NOTIFICATION_URL/api/v1/notifications?userId=$USER_ID" ORDER_CONFIRMATION PAYMENT_RECEIPT
echo "    confirmation and receipt recorded"

step "decline path: an order over the provider's limit fails"
DECLINED_ID=$(post "$ORDER_URL/api/v1/orders" \
  "{\"userId\":\"$USER_ID\",\"lines\":[{\"productId\":\"$MONITOR_ID\",\"quantity\":2}]}" | json id)
REASON=$(poll_status "$DECLINED_ID" Failed | json statusReason)
[[ $REASON == *card_declined* ]] || fail "expected statusReason to mention card_declined, got '$REASON'"
echo "    order $DECLINED_ID is Failed ($REASON)"

poll_contains "$NOTIFICATION_URL/api/v1/notifications?userId=$USER_ID" PAYMENT_PROBLEM
echo "    payment problem notification recorded"

echo "PASS: checkout works end to end (correlation id $CORRELATION_ID)"
