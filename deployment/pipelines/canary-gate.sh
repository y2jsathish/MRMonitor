#!/usr/bin/env bash
# Polls Prometheus for the multi-window burn-rate alert and the telemetry-ingest drop alert for N seconds after a PROD rollout.
set -euo pipefail
PROM="$1"; SOAK="${2:-900}"; end=$((SECONDS+SOAK))
while [ $SECONDS -lt $end ]; do
  fired=$(curl -sG "$PROM/api/v1/query" --data-urlencode 'query=ALERTS{alertname=~"ApiAvailabilityBurnFast|TelemetryIngestStalled",alertstate="firing"}' | python3 -c 'import sys,json; print(len(json.load(sys.stdin)["data"]["result"]))')
  [ "$fired" -eq 0 ] || { echo "SLO alert firing during canary soak: failing the release (helm --atomic already rolled this revision's health gate; run 'helm rollback')"; exit 1; }
  sleep 30
done
echo "canary soak passed"
