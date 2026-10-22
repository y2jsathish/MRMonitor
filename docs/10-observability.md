# 10 - Observability

Stack: **OpenTelemetry** (SDK in every service and in the agent) -> **OTel Collector** -> **Prometheus** (metrics) / **Tempo** (traces) / **Elastic** (logs). **Grafana** for dashboards and alert routing. Configs: `deployment/observability/{otel-collector.yaml,prometheus.yml,prometheus-rules.yaml}`; local stack: `deployment/docker/docker-compose.dev.yml`.

> Two different things are observed: the **platform** (this document) and the **ATMs** (the product itself - health score, alerts, EJ). Platform observability must stay alive when ATM monitoring is noisy, so they use separate pipelines and storage.

## 1. Metrics

### Implemented in code
| Metric (OTel name -> Prometheus) | Type | Source | Labels |
|---|---|---|---|
| `xfs.calls` -> `xfs_calls_total` | counter | `XfsServiceManager` (agent) | `op`, `device` |
| `xfs.errors` -> `xfs_errors_total` | counter | same | `op`, `device` |
| `xfs.call.duration` -> `xfs_call_duration_milliseconds` | histogram | same | `op`, `device` |
| `atm.telemetry.messages` -> `atm_telemetry_messages_total` | counter | `IngestTelemetryHandler` | - |
| `atm.alerts.created` -> `atm_alerts_created_total` | counter | same | - |
| ASP.NET Core / HttpClient / .NET runtime | histograms/gauges | OTel instrumentation | `http.route`, `http.response.status_code` |
| Npgsql pool/command metrics | histogram/gauge | Npgsql meter | `db.system`, pool name |

### Specified (to add with each service; names reserved)
| Metric | Meaning | Alert use |
|---|---|---|
| `atmnexus_atms_active`, `atmnexus_atms_offline`, `atmnexus_atms_by_health{level}` | fleet gauges (Reporting, scraped every 15 s from the read model) | FleetOfflineSpike |
| `atmnexus_outbox_pending`, `atmnexus_outbox_oldest_age_seconds` | outbox relay backlog | OutboxLag |
| `atmnexus_command_queue_depth`, `atmnexus_command_latency_seconds{type}` | queue -> delivered -> result | slow delivery |
| `atmnexus_command_signature_failures_total` (from agents via telemetry) | agent rejected a command | security page |
| `kafka_consumergroup_lag` (Kafka exporter) | consumer lag | KafkaConsumerLag |
| `atmnexus_ai_request_duration_seconds`, `atmnexus_ai_fallback_total` | AI endpoint health / rules fallback usage | ticket |
| `atmnexus_agent_version{version}` | fleet version distribution | rollout progress |
| `atmnexus_longpoll_connections` | open agent long-polls per pod | capacity |

### Agent-side (reported inside telemetry `metrics`, not scraped)
`health.devices|cash|application|network|security|history`, outbox depth, last successful upload age, XFS error rate, EJ lag. Stored per ATM in `atm_status.components` and in the time-series (`atm_status_history`).

## 2. Logs
* **Format**: JSON, one event per line (`AddJsonConsole` in services; agent uses Windows Event Log + file under `%ProgramData%\ATMNexus\logs` with rolling 50 MB x 5).
* **Mandatory fields**: `timestamp` (UTC), `level`, `message`, `service.name`, `service.version`, `trace_id`, `span_id`, and `atm.code` where applicable (structured logging templates: `"Telemetry {AtmCode}: score={Score}"`).
* **Never logged**: PAN, track data, PIN, tokens, signatures, connection strings (OTel `transform/redact` masks 13-19 digit runs as a last line of defence; tests keep it out at the source).
* **Pipeline**: stdout -> Fluent Bit/OTel -> Elasticsearch `atmnexus-logs-*` (ILM: 14 d hot, 30 d warm, delete 90 d; audit-relevant events are in `audit_log`, not here).
* **Levels**: Information for state changes, Warning for recoverable failures (retry), Error for operation failure, Critical for loss of invariants. Debug off in PROD (per-ATM debug can be enabled for one ATM through a signed config command for 1 h).

## 3. Traces
* **Propagation**: W3C `traceparent` from portal -> gateway -> services; Kafka producers add `traceparent` header from the outbox row (`correlationId` stores the trace id at write time) so consumer spans link to the producing request; agent HTTP calls carry `traceparent` (agent `HttpClient` instrumentation) when OTLP is enabled.
* **Spans**: `telemetry.ingest` (custom, `AtmNexus.Device`), `xfs.open/WFSGetInfo/WFSExecute` (agent), ASP.NET Core server spans, Npgsql command spans, Kafka produce/consume.
* **Sampling**: tail sampling in the collector: keep 100% errors and >1 s, 5% of the rest (ingest is ~833 req/s, so head sampling at 100% would be 70M spans/day).

## 4. Dashboards (Grafana, provisioned from JSON in repo)

| Dashboard | Panels |
|---|---|
| **Platform SLO** | availability SLI (5 m/1 h/30 d), error budget remaining, burn rate (fast/slow), p50/p95/p99 latency by route, 5xx by route, saturation (CPU, memory, DB pool, thread pool) |
| **Ingest pipeline** | telemetry msg/s, ingest latency histogram, 4xx/429 by reason, alerts created/min, DB txn time, outbox pending/age, Kafka produce latency, `atm_telemetry_messages_total` vs 1 h ago |
| **Fleet** | ATMs by health level (stacked area), offline count + top regions, version distribution, cash low/empty units, top 20 alert codes, heatmap branch x hour of alerts |
| **Commands & security** | commands by type/state, delivery latency, signature failures, authz denials, token issuance rate, audit append rate/latency, chain-verification status |
| **Kafka** | per-topic in/out msg/s, consumer lag per group, DLQ depth, broker disk/ISR |
| **PostgreSQL** | TPS, locks/wait events, replication lag, bloat on hot tables (`atm_status`, `atm_component`), partition sizes, slow queries (pg_stat_statements) |
| **XFS health (fleet-wide)** | `xfs_errors_total` rate by device class and **vendor/model** (spots a bad SP release), call latency p95, top error codes (feeds vendor profile tuning) |
| **AI** | scoring latency, fallback rate, prediction precision/recall (from feedback), drift indicators |

## 5. Health checks

| Probe | Path | Checks | Failure action |
|---|---|---|---|
| startup | `/health/live` (generous `failureThreshold: 30`) | process up | restart |
| liveness | `/health/live` | event loop responsive only - **no dependencies** (a dead DB must not restart-loop every pod) | restart |
| readiness | `/health/ready` | PostgreSQL (`AddNpgSql`), Kafka metadata (planned), signing key loaded | remove from Service endpoints, keep process |
| Deep (synthetic) | external prober every 30 s | register a canary ATM identity -> POST telemetry -> assert it appears in `/status` within 5 s | page (end-to-end SLI) |
| Agent self-check | in telemetry | cache writable, outbox age, XFS handles open | `AGENT_DEGRADED` alert |

## 6. Alerts (rules in `prometheus-rules.yaml`)
Multi-window multi-burn-rate on the 99.99% SLO (fast 14.4x/5 m page; slow 6x/30 m page; 1x/6 h ticket), ingest drop vs 1 h ago, outbox lag, fleet offline spike (>5%), XFS error rate, Kafka lag, DLQ non-empty, certificate expiry, audit chain broken, PG replication lag > 30 s.

## 7. Runbooks (summary)

| Symptom | First checks | Typical cause / action |
|---|---|---|
| **Ingest stalled** | gateway 5xx? IdP token latency? PG connections/locks? | IdP down -> agents hold tokens 15 min, queue locally; PG failover -> wait <2 min; scale Device pods if CPU-bound |
| **Outbox lag** | `outbox_event` pending count, Kafka reachability, relay logs | Kafka outage (writes unaffected); restart relay; do **not** delete rows |
| **>5% fleet offline in one region** | network/carrier status, gateway reachability from that region | one network incident, suppress per-ATM incidents (storm control) |
| **Command signature failures** | agent log `invalid signature`, key id in command vs `TrustedKeys` | key rotation not fully rolled out: re-deliver trust-anchor config; if unexplained -> treat as attack, freeze command queue |
| **Audit chain broken** | `GET /api/audit/verify` first bad `audit_id` | page security; preserve DB; compare with SIEM copy |
| **XFS error spike on one model** | XFS dashboard by vendor/model, recent profile/SP release | roll back vendor SP/profile; adjust error table overrides |
| **PG failover** | replication lag, app reconnect | pods reconnect automatically; verify outbox continues; review long transactions |
