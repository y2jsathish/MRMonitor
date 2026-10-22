#!/usr/bin/env bash
# Creates the platform topics. Sizing assumes 50,000 ATMs (see docs/05-events-kafka.md for the partition math).
# Usage: BOOTSTRAP=kafka:9092 RF=3 deployment/kafka/topics.sh
set -euo pipefail
B="${BOOTSTRAP:-localhost:9092}"; RF="${RF:-3}"; K="${KAFKA_BIN:-kafka-topics.sh}"
mk() { # name partitions retention.ms extra...
  local n="$1" p="$2" r="$3"; shift 3
  "$K" --bootstrap-server "$B" --create --if-not-exists --topic "$n" --partitions "$p" --replication-factor "$RF" \
       --config "retention.ms=$r" --config min.insync.replicas=2 --config compression.type=zstd "$@"
}
DAY=86400000
mk atm.status.changed        48 $((7*DAY))
mk atm.offline               24 $((7*DAY))
mk atm.alert.created         48 $((14*DAY))
mk atm.incident.created      12 $((30*DAY))
mk deployment.started        12 $((30*DAY))
mk deployment.completed      12 $((30*DAY))
mk atm.ej.batch              96 $((3*DAY))
mk atm.inventory.reported    12 $((3*DAY))
# dead-letter topics: one per consumer group that can poison-pill
for g in monitoring-service incident-service alert-service notification-service reporting-service audit-service deployment-service; do
  mk "dlq.$g" 6 $((30*DAY))
done
