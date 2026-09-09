#!/usr/bin/env bash
set -euo pipefail

until /opt/kafka/bin/kafka-broker-api-versions.sh --bootstrap-server kafka:9092 >/dev/null 2>&1; do
  sleep 2
done

while IFS= read -r topic; do
  [[ -z "$topic" || "$topic" == \#* ]] && continue
  /opt/kafka/bin/kafka-topics.sh \
    --bootstrap-server kafka:9092 \
    --create --if-not-exists \
    --topic "$topic" \
    --partitions 3 \
    --replication-factor 1 \
    --config retention.ms=604800000
done < <(tr -d '\r' < /topics.txt)
