#!/usr/bin/env bash
# Creates the platform's topics. Run by the one-shot `kafka-init` container on every
# `docker compose up`; --if-not-exists makes a rerun a no-op.
#
# Partition counts and retention are decided in docs/decisions/0001-message-broker.md.
# Auto topic creation is disabled on the broker, so a topic missing here does not exist.
set -euo pipefail

BOOTSTRAP=${BOOTSTRAP:-kafka:9092}

create() { # topic partitions retention_ms
  kafka-topics --bootstrap-server "$BOOTSTRAP" --create --if-not-exists \
    --topic "$1" --partitions "$2" --replication-factor 1 --config retention.ms="$3"
}

create orders       3 604800000   # 7 days
create payments     3 604800000   # 7 days
create orders.dlq   1 2592000000  # 30 days — humans read these, give them time
create payments.dlq 1 2592000000

kafka-topics --bootstrap-server "$BOOTSTRAP" --list
