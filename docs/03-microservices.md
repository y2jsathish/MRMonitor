# 03 - Microservice design

Status: **Device Service [C]** is implemented and tested; the other eight follow the identical template and are specified here so teams can start in parallel. The Helm values for all nine exist (`deployment/helm/values/*.yaml`).

## 0. Common template (every service)

```
AtmNexus.Services.<Name>/
  Program.cs                 composition root: auth, OTel, health checks, problem details
  Endpoints/                 minimal-API groups; thin: bind -> authorise -> call handler -> map result
  Application/               CQRS: Query/Command records + handlers; DTOs; validation; no I/O frameworks here
  Domain/                    (when the service has real invariants: Incident state machine, Deployment state machine)
  Infrastructure/            Dapper/EF repositories, Kafka producer/consumers, outbox relay, HTTP clients
  appsettings.json
```

| Concern | Standard |
|---|---|
| Database | One PostgreSQL **schema per service** (`device`, `alert`, `incident`, ...) with its own login role (`<svc>_app`, DML only; migrations run by a separate `<svc>_migrator` role). Shared physical cluster in MVP, separable later. |
| Events out | Transactional outbox table in the service schema + relay (same code as Device Service). |
| Events in | `Confluent.Kafka` consumer, `EnableAutoCommit=false`, commit after DB transaction; idempotency table `processed_event(event_id PK)`; 3 retries (1 s/5 s/30 s) then `dlq.<group>`. |
| Auth | JWT bearer; policy per permission; service-to-service uses client-credentials with `svc.<name>` scope. |
| Health | `/health/live` (process), `/health/ready` (DB + Kafka metadata), `startup` probe. |
| Observability | OTel traces/metrics via OTLP, JSON logs, `traceparent` propagated in Kafka headers. |
| Container | `deployment/docker/Dockerfile.service.template` (chiseled, non-root, read-only FS). |
| Kubernetes | `deployment/helm/atmnexus-service` + `values/<name>.yaml` + `env/values-<env>.yaml`. |

### Dockerfile structure (all services)
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build       # restore (cached) -> publish --no-restore, UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:9.0-noble-chiseled AS runtime   # no shell, UID 1654
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0
USER 1654
ENTRYPOINT ["dotnet", "AtmNexus.Services.<Name>.dll"]
```
Render with `deployment/docker/build-image.sh <Name> <semver>`.

### Kubernetes deployment (shape common to all)
Deployment (3 replicas, zone spread, `maxUnavailable: 0`, read-only root FS, drop ALL caps, seccomp RuntimeDefault, startup/live/ready probes, preStop 15 s, grace 60 s) + Service + HPA (CPU 65%) + PDB + NetworkPolicy (ingress from gateway only; egress to PG/Kafka/OTel/DNS) + SecretProviderClass (Key Vault CSI). Concrete example: `deployment/k8s/device-service.yaml`.

---

## 1. Device Service **[C]**

| | |
|---|---|
| **Responsibility** | ATM master data and live state: registration, status/component/cash read models, telemetry ingest, offline detection, digital twin, remote-command queue + signing, agent ingestion endpoints (EJ/inventory/log intake). |
| **Database** | schema `device`: `vendor, location, branch, atm, atm_status, atm_status_history, atm_component, atm_cash_unit, remote_command, outbox_event` (+ `audit_log` via function). |
| **Endpoints** | `GET /api/atms`, `/api/atms/{id}`, `/{id}/status`, `/{id}/twin`; `GET /api/dashboard`; `POST /api/atms/{id}/restart`; `POST /api/commands/{id}/approve`; agent: `POST /api/agent/telemetry`, `GET /api/agent/commands/next`, `POST /api/agent/commands/{id}/result`, `POST /api/agent/ej`, `/inventory`, `PUT /api/agent/logs/{label}`; `/health/*`. |
| **Publishes** | `atm.status.changed` (on level change), `atm.offline`, `atm.alert.created` (when a new fingerprint opens), `atm.ej.batch`, `atm.inventory.reported`. |
| **Consumes** | `atm.decommissioned` (Auth), `deployment.completed` (to refresh agent/app versions) [D]. |
| **Kubernetes** | `values/device.yaml`: 3-30 replicas, 500m/512Mi req, 1Gi limit; ~50k held connections spread across pods; terminationGrace 60 s for long-polls. |
| **Dockerfile** | template, `__PROJECT__=src/Services/AtmNexus.Services.Device/...csproj`. |

## 2. Monitoring Service **[D]**

| | |
|---|---|
| **Responsibility** | Cross-ATM analytics on the event stream: trend detection (cash burn rate -> time-to-empty), peer comparison (ATM vs same model/firmware cohort), regional anomaly detection, AI client (classification/prediction/RCA) with circuit breaker and rules fallback, writes `atm_prediction` and `atm_insight`. |
| **Database** | schema `monitoring`: `atm_prediction, atm_insight, cohort_baseline, processed_event`. |
| **Endpoints** | `GET /api/atms/{id}/insights`, `GET /api/atms/{id}/predictions`, `POST /api/ai/feedback` (human accept/reject), `GET /api/monitoring/cohorts`. |
| **Publishes** | `atm.prediction.created`, `atm.insight.created`. |
| **Consumes** | `atm.status.changed`, `atm.alert.created`, `atm.offline`, `atm.incident.created`. |
| **K8s** | `values/monitoring.yaml` (3 replicas, one consumer per partition up to 48). |

## 3. Incident Service **[D]**

| | |
|---|---|
| **Responsibility** | Incident lifecycle, SLA clocks, assignment, ITSM sync, auto-incident rules, reopen/resolve logic. |
| **Database** | schema `incident`: `atm_incident, incident_event, incident_note, sla_policy, incident_rule, processed_event, outbox_event`. |
| **Endpoints** | `POST /api/incidents`, `GET /api/incidents`, `GET /api/incidents/{id}`, `POST /api/incidents/{id}/transition`, `POST /api/incidents/{id}/notes`, `POST /api/incidents/{id}/alerts/{alertId}`. |
| **Publishes** | `atm.incident.created`, `atm.incident.updated`, `atm.incident.sla_breached`. |
| **Consumes** | `atm.alert.created`, `atm.alert.cleared`, `atm.offline` (group correlation). |
| **K8s** | `values/incident.yaml`. |

## 4. Alert Service **[D]**

| | |
|---|---|
| **Responsibility** | Alert rules, severity overrides, escalation, maintenance windows, storm control/group alerts, acknowledge/clear API, manual alerts. |
| **Database** | schema `alert`: `alert_rule, maintenance_window, escalation_policy, atm_alert (owned here in the split topology), processed_event, outbox_event`. In MVP the table lives in `device`; ownership moves with the extraction (documented migration). |
| **Endpoints** | `GET /api/alerts`, `POST /api/alerts`, `POST /api/alerts/{id}/acknowledge`, `POST /api/alerts/{id}/clear`, `GET/POST/PUT /api/alert-rules`, `GET/POST /api/maintenance-windows`. |
| **Publishes** | `atm.alert.escalated`, `atm.alert.cleared`, `atm.alert.group.created`. |
| **Consumes** | `atm.alert.created`, `atm.offline`, `atm.status.changed`. |
| **K8s** | `values/alert.yaml`. |

## 5. Deployment Service **[D]** (Phase 2)

| | |
|---|---|
| **Responsibility** | Package registry (signed artefacts), rollout orchestration (immediate / maintenance window / canary waves), four-eyes approval, per-ATM state machine, rollback policy, compatibility matrix (OS/vendor app/agent versions). |
| **Database** | schema `deployment`: `deployment_package, deployment_history, rollout_plan, rollout_wave, compatibility_rule, processed_event, outbox_event`. |
| **Endpoints** | `POST /api/atms/{id}/deploy`, `POST /api/rollouts`, `GET /api/rollouts/{id}`, `POST /api/rollouts/{id}/approve|pause|resume|abort`, `GET/POST /api/packages`. |
| **Publishes** | `deployment.started`, `deployment.completed`, `deployment.wave.completed`. |
| **Consumes** | `atm.status.changed` (health gate between waves), `atm.offline`, `atm.incident.created` (halt rollout if fleet-wide incident). |
| **K8s** | `values/deployment.yaml` (2-8 replicas); Key Vault: package signing key (HSM, sign-only). |

## 6. Reporting Service **[D]**

| | |
|---|---|
| **Responsibility** | Read models for dashboards at scale, scheduled reports (availability %, MTTR, MTBF, cash-out minutes, transaction success by ATM/branch), EJ search API (Elasticsearch), CSV/XLSX/PDF export, EJ indexer consumer (`atm.ej.batch` -> `ElasticEjSink`). |
| **Database** | schema `reporting`: materialised views + `report_definition, report_run`; Elasticsearch `atm-ej-*`. |
| **Endpoints** | `GET /api/reports`, `POST /api/reports/{id}/run`, `GET /api/ej/search?atm=&from=&to=&q=&outcome=`, `GET /api/ej/transactions/{id}`. |
| **Publishes** | `report.completed`. |
| **Consumes** | `atm.status.changed`, `atm.alert.created`, `atm.incident.created`, `atm.ej.batch`. |
| **K8s** | `values/reporting.yaml` (extra egress to `elastic:9200`). |

## 7. Notification Service **[D]**

| | |
|---|---|
| **Responsibility** | Templates, routing (severity x region x on-call), channel adapters (SMTP, SMS, Teams, webhook, ITSM), retry + DLQ, delivery receipts, **SignalR hub** for portal push (broadcast of status changes to groups). |
| **Database** | schema `notification`: `notification_template, routing_rule, oncall_schedule, delivery_log, processed_event`. |
| **Endpoints** | `GET/PUT /api/notification-rules`, `GET /api/notifications?atm=`, `POST /api/notifications/test`, WebSocket `/hubs/status`. |
| **Publishes** | `notification.sent`, `notification.failed`. |
| **Consumes** | `atm.alert.created`, `atm.alert.escalated`, `atm.incident.created`, `atm.incident.sla_breached`, `atm.status.changed`, `deployment.completed`. |
| **K8s** | `values/notification.yaml` (sticky not needed: SignalR uses Redis/Azure SignalR backplane). |

## 8. Audit Service **[D]**

| | |
|---|---|
| **Responsibility** | Central audit sink for services without direct DB access, hash-chain verification, SIEM export (CEF/TLS), audit search/export API, access reviews. |
| **Database** | schema `audit`: `audit_log` (the table in V001), verification state `audit_chain_checkpoint`. |
| **Endpoints** | `GET /api/audit`, `GET /api/audit/verify`, `POST /api/audit/export`. |
| **Publishes** | `audit.chain_broken` (page). |
| **Consumes** | `audit.record` (from all services). |
| **K8s** | `values/audit.yaml` (2-6 replicas; single writer per chain: leader election via PG advisory lock). |

## 9. Authentication Service **[D]**

| | |
|---|---|
| **Responsibility** | OIDC provider facade/broker (federates the bank IdP, or Duende IdentityServer/Keycloak when none), user JIT provisioning, role/permission claim issuance, **device enrolment**, mTLS-bound OAuth2 client-credentials for agents (RFC 8705), token revocation, JWKS publishing and key rotation. |
| **Database** | schema `auth`: `app_user, role, permission, role_permission, user_role, enrolment_token, device_certificate, revoked_token, signing_key`. |
| **Endpoints** | `/.well-known/openid-configuration`, `/.well-known/jwks.json`, `/connect/authorize|token|revocation|userinfo`, `POST /api/enrolment/token`, `POST /api/enrolment/complete`, `GET/POST /api/users|roles`. |
| **Publishes** | `atm.enrolled`, `atm.decommissioned`, `user.role_changed`. |
| **Consumes** | `atm.registered`. |
| **K8s** | `values/auth.yaml` (3-15 replicas; most latency-sensitive: every agent re-authenticates every 15 min => 50k/15 min ~ 55 tokens/s). |

---

## Event matrix

| Topic | Producer | Consumers |
|---|---|---|
| `atm.status.changed` | Device | Monitoring, Reporting, Notification (SignalR), Deployment (wave gate) |
| `atm.offline` | Device | Alert, Incident, Monitoring, Notification |
| `atm.alert.created` | Device (MVP) -> Alert (after split) | Alert, Incident, Notification, Monitoring, Reporting |
| `atm.incident.created` | Incident | Notification, Reporting, Monitoring, ITSM connector |
| `deployment.started` | Deployment | Notification, Reporting, Audit |
| `deployment.completed` | Deployment | Device (versions), Notification, Reporting, Monitoring |
| `atm.ej.batch` | Device (intake) | Reporting (EJ indexer) |
