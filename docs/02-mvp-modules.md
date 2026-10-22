# 02 - Phase 1 MVP modules

Status legend: **[C]** code in this repository · **[D]** design only (spec complete, not yet coded) · **[C+D]** core coded, rest designed.
Requirement IDs (`FR-*`, `NFR-*`) are test-case anchors: every ID maps to at least one automated test or a manual UAT script.

MVP scope = 8 modules. Out of MVP: deployment rollout engine (Phase 2), AI (Phase 3), reporting beyond the NOC dashboard.

---

## M1. ATM registration **[C+D]**

### Functional requirements
| ID | Requirement |
|---|---|
| FR-REG-01 | Create/update/decommission an ATM with `atm_code` (unique, `^[A-Za-z0-9_-]{3,32}$`), vendor, model, serial, branch, location, IP/MAC, application name/version, tags. |
| FR-REG-02 | Bulk import via CSV/XLSX (<= 5,000 rows) with a dry-run report (per-row errors) before commit. |
| FR-REG-03 | Generate a one-time **enrolment token** (valid 7 days, single use, bound to `atm_code`). |
| FR-REG-04 | Agent enrolment: ATM presents token + CSR -> platform issues a **device certificate** (via bank PKI/SCEP) -> agent obtains OAuth tokens with that certificate. |
| FR-REG-05 | Lifecycle `PLANNED -> ACTIVE -> MAINTENANCE -> DECOMMISSIONED`; decommissioned ATMs reject telemetry and their client certificate is revoked. |
| FR-REG-06 | Device inventory is **self-reported** by the agent (XFS logical services discovered from `HKLM\SOFTWARE\XFS\LOGICAL_SERVICES`) and reconciled against the master record; drift raises a warning. |
| FR-REG-07 | Duplicate detection on (vendor, serial) and on IP within the same branch. |

### Non-functional
| ID | Requirement |
|---|---|
| NFR-REG-01 | Enrol 5,000 ATMs/hour; import of 5,000 rows < 60 s. |
| NFR-REG-02 | Every change audited with before/after JSON diff. |
| NFR-REG-03 | Enrolment token and CSR never logged; certificate private key never leaves the ATM (TPM-backed KSP where present). |

### Technical design
* Master data owned by **Device Service** (`atm`, `location`, `branch`, `vendor`). Enrolment owned by **Authentication Service** (tokens, CA integration).
* Enrolment flow: portal creates token -> installer script `deployment/agent/install-agent.ps1` takes `-AtmCode` + `-CertThumbprint` (cert pre-issued by the bank's PKI automation: SCEP/EST) -> first token request proves possession.
* Bulk import: parsed in the Blazor server circuit, validated with the same FluentValidation-style rules as the API, committed in one transaction per 500 rows; idempotent on `atm_code`.
* Revocation: Authentication Service publishes `atm.decommissioned`; gateway denies the certificate serial immediately (CRL/OCSP stapling + deny-list cache of 60 s).

### REST APIs
| Method | Path | Permission | Notes |
|---|---|---|---|
| POST | `/api/atms` | `atm.manage` | 201 + `Location`. 409 on duplicate code/serial. |
| PUT | `/api/atms/{id}` | `atm.manage` | `If-Match: <rowVersion>` optimistic concurrency. |
| POST | `/api/atms/import?dryRun=true` | `atm.manage` | multipart CSV; returns per-row result. |
| POST | `/api/atms/{id}/enrolment-token` | `atm.manage` | returns token once. |
| POST | `/api/atms/{id}/decommission` | `atm.manage` | revokes cert, status -> DECOMMISSIONED. |
| GET | `/api/atms`, `/api/atms/{id}` | `atm.read` | **[C]** implemented |

### Database
`vendor`, `location`, `branch`, `atm`, `atm_component` (see `docs/04-database-tables.md`). Constraints that carry business rules: `uq_atm_code`, `uq_atm_serial (vendor_id, serial_number)`, `ck_atm_commission`, `ck_atm_code_fmt`.

### UI screens
ATM List (filters, bulk actions), ATM Create/Edit wizard (3 steps: identity -> placement -> devices), Import dialog with error grid, ATM Detail -> Enrolment tab (token, certificate status/expiry, agent version).

### Backend services
Device Service (CRUD, import), Authentication Service (enrolment, revocation), Audit Service.

---

## M2. ATM monitoring **[C]**

### Functional requirements
| ID | Requirement |
|---|---|
| FR-MON-01 | Agent reports every 60 s (configurable 15-300 s): per-device state + canonical attributes, cash units, health score/components, queued events. |
| FR-MON-02 | Device classes: card reader, cash dispenser/recycler, depository, receipt printer, PIN pad (status only), SIU (doors, tamper, vibration, heat), camera/barcode/passport/NFC/biometric (status). |
| FR-MON-03 | XFS events (`WFS_EXECUTE/SERVICE/USER/SYSTEM_EVENT`) are mapped to domain events within 1 s and flushed to the platform within 10 s for severity >= Major. |
| FR-MON-04 | Polling reconciles missed events (state deltas produce the same alarms). |
| FR-MON-05 | Windows service/process health, ATM application health (ProBase log + journal), Windows Error/Critical events. |
| FR-MON-06 | Software and patch inventory daily; patch compliance (>=1 hotfix in 90 days) feeds the security component. |
| FR-MON-07 | Offline detection: no heartbeat for 3 intervals -> `connectivity=OFFLINE`, `atm.offline` event, exactly once per outage. |
| FR-MON-08 | EJ collection, parsed and indexed within 2 minutes of journal write; PAN masked at source. |
| FR-MON-09 | Store-and-forward: >= 7 days of telemetry survive network loss (50,000-row outbox cap, oldest Info dropped first). |

### Non-functional
| ID | Requirement |
|---|---|
| NFR-MON-01 | Agent CPU < 2% average, < 8% peak; RSS < 120 MB; disk < 300 MB incl. cache; **never on the transaction path** (all device calls are on worker threads, bounded timeouts, circuit of retries only for idempotent calls). |
| NFR-MON-02 | Ingest p99 < 250 ms at 833 msg/s; zero loss under platform restarts (agent retry + idempotent upserts). |
| NFR-MON-03 | Status freshness: 95% of ATMs < 90 s old. |
| NFR-MON-04 | No unbounded memory: all queues bounded (`DropOldest`), outbox capped. |

### Technical design
Implemented end-to-end; see `docs/14-xfs-integration.md` for device/XFS details.
* Ingest = **one transaction per message** (`IngestTelemetryHandler`): row-lock `atm_status` -> upsert status/components/cash -> auto-clear recovered alerts -> upsert alerts by fingerprint -> outbox events -> commit.
* Idempotency: re-sent messages only update `last_seen_at` and bump `occurrences`; EJ ids are deterministic hashes (`ProfileEjParser.TxId`).
* Alert **fingerprint** = `code|source|unit|service`; partial unique index `uq_alert_active_fingerprint` guarantees one active alert per fingerprint.

### REST APIs
`POST /api/agent/telemetry`, `GET /api/atms/{id}/status`, `GET /api/atms/{id}/twin` **[C]** · `POST /api/agent/ej`, `/inventory`, `PUT /api/agent/logs/{label}` **[C]** (accept + fan out).

### Database
`atm_status`, `atm_status_history` (sampled), `atm_component`, `atm_cash_unit`, `outbox_event`.

### UI screens
ATM Detail (device tree, cash cassettes, live event timeline, health breakdown, EJ search tab).

### Backend services
Device Service (ingest), Monitoring Service (rules/trends/AI client - Phase 3), Reporting Service (EJ search).

---

## M3. Health dashboard **[C+D]**

### Functional requirements
| ID | Requirement |
|---|---|
| FR-DSH-01 | Fleet tiles: total active, by health level (Healthy/Degraded/Critical/Down), offline, open alerts by severity, cash low/empty units, open incidents. |
| FR-DSH-02 | "Worst 10" ATMs with top reasons; click-through to detail. |
| FR-DSH-03 | Filters: region, branch, vendor/model, application version; filter state is part of the URL (shareable). |
| FR-DSH-04 | Map view (clustered markers coloured by health). |
| FR-DSH-05 | Near-real-time: changes visible < 15 s (SignalR push, 30 s polling fallback). |
| FR-DSH-06 | Health score drill-down: 6 components with weights and reasons. |
| FR-DSH-07 | Wallboard mode (auto-rotating, large type, no interaction). |

### Non-functional
Dashboard API p95 < 300 ms for 50k ATMs (read model: `atm_status` + partial indexes; heavy aggregates cached 5 s). Works at 1920x1080 wallboard and 1366x768 laptops. WCAG 2.1 AA, colour-blind-safe palette (never colour-only: icons + text).

### Technical design
* `GET /api/dashboard` **[C]** aggregates with 6 indexed queries (`ix_status_level`, `ix_alert_open`, `ix_cash_status`, `ix_incident_open`); at >20k ATMs move to a materialised read model refreshed by `atm.status.changed` consumers (Reporting Service).
* Health formula: see `docs/14-xfs-integration.md#health-score`. Thresholds: Healthy >= 85, Degraded 60-84, Critical 30-59, Down < 30.
* Push: Notification Service consumes `atm.status.changed` and broadcasts to SignalR groups (`region:SOUTH`, `branch:BR-0001`, `atm:ATM-000123`); clients subscribe only to what they display.

### REST APIs
`GET /api/dashboard` **[C]**, `GET /api/atms?status=&connectivity=&branch=&vendor=&search=&page=` **[C]**, `GET /api/dashboard/map` [D].

### Database
Reads `atm`, `atm_status`, `atm_alert`, `atm_cash_unit`, `atm_incident`.

### UI screens / Backend
NOC Dashboard, Wallboard; Device Service (read), Notification Service (push).

---

## M4. Alerting **[C+D]**

### Functional requirements
| ID | Requirement |
|---|---|
| FR-ALR-01 | Alert lifecycle `OPEN -> ACKNOWLEDGED -> CLEARED` (+ `SUPPRESSED`). Auto-clear when the condition recovers (card jam cleared, door closed, cassette refilled). |
| FR-ALR-02 | De-duplication by fingerprint with `occurrences` counter and `last_raised_at`. |
| FR-ALR-03 | Severity: Info/Warning/Major/Critical; default mapping per event type is data (catalog), override per vendor/ATM group. |
| FR-ALR-04 | Rules: thresholds (cash low by denomination and branch type), compound conditions (>=3 `CARD_JAM` in 1 h), rate-of-change (cash burn rate predicts empty before next CIT visit). |
| FR-ALR-05 | Escalation timers: unacknowledged Critical -> notify L2 after 10 min, L3 after 30 min. |
| FR-ALR-06 | Maintenance windows and planned-work suppression (per ATM/branch, with audit). |
| FR-ALR-07 | Storm control: >5% of a region's ATMs alerting the same code in 10 min -> one **group alert** (network/carrier/power) and child alerts folded. |
| FR-ALR-08 | Manual alert creation (`POST /api/alerts`) for field-reported faults. |
| FR-ALR-09 | Notification routing by severity, region, device class, time of day (on-call calendars). |

### Non-functional
Alert created -> notification sent < 30 s p95. No lost alerts under broker/DB failover (outbox). Flapping suppression: same fingerprint > 6 times/10 min emits one `FLAPPING` alert.

### Technical design
* Raised **at the edge** (agent `AlarmPipeline`: dedup window 5 min, flap detection) and de-duplicated again **centrally** (unique partial index) - two layers because edge dedup protects the uplink and central dedup protects against multiple agents/restarts.
* Alert Service consumes `atm.alert.created`, applies rules (rule definitions in `alert_rule` table, evaluated with a small expression tree; no scripting engine), publishes `atm.alert.escalated` / `atm.incident.requested`.
* Auto-clear rules are code + data: see `IngestTelemetryHandler` (device recovery, refilled cassettes, counterpart events).

### REST APIs
| Method | Path | Permission |
|---|---|---|
| GET | `/api/alerts?state=&severity=&atm=&from=&to=` | `alert.read` |
| POST | `/api/alerts` | `alert.ack` (manual raise) |
| POST | `/api/alerts/{id}/acknowledge` | `alert.ack` |
| POST | `/api/alerts/{id}/clear` | `alert.ack` (reason required) |
| GET/POST/PUT | `/api/alert-rules` | `atm.manage` |

### Database
`atm_alert` (+ `alert_rule`, `maintenance_window` in Alert Service schema [D]).

### UI / Backend
Alert Console; Alert Service, Notification Service.

---

## M5. Incident management **[D]**

### Functional requirements
| ID | Requirement |
|---|---|
| FR-INC-01 | Auto-create incidents from alerts by rule (e.g. `CARD_JAM`, `CASH_EMPTY` on > 50% of cassettes, `TAMPER_DETECTED` -> P1 security). |
| FR-INC-02 | Manual creation; link N alerts to one incident; at most one open incident per (ATM, category) (`uq_incident_open_per_atm_category`). |
| FR-INC-03 | States `NEW -> ASSIGNED -> IN_PROGRESS -> PENDING -> RESOLVED -> CLOSED`; transition rules enforced server-side; reopen within 72 h. |
| FR-INC-04 | Priority P1-P4 with SLA clocks (response/resolution), business-hours calendars per region, pause on `PENDING`. |
| FR-INC-05 | Assignment to groups/users; field engineer mobile view (read incident, add notes/photos, request remote command). |
| FR-INC-06 | Auto-resolve when all linked alerts clear (configurable; requires confirmation for P1). |
| FR-INC-07 | ITSM sync (ServiceNow/Jira SM): outbound webhook + inbound updates, idempotent by external id. |
| FR-INC-08 | Resolution codes (hardware replaced, cash replenished, software restart, no fault found) for analytics and AI labelling. |

### Non-functional
SLA timers survive restarts (stored timestamps, evaluated by a scheduler). Incident number format `INC-YYYY-NNNNNN` (sequence). All transitions audited.

### Technical design
State machine table-driven (`from,to,permission,required_fields`); optimistic concurrency via `rowVersion`; `incident_event` append-only timeline table [D]. Incident Service consumes `atm.alert.created`, publishes `atm.incident.created` / `atm.incident.updated`.

### REST APIs
`POST /api/incidents` (201), `GET /api/incidents`, `GET /api/incidents/{id}`, `POST /api/incidents/{id}/transition`, `POST /api/incidents/{id}/notes`, `POST /api/incidents/{id}/alerts/{alertId}`.

### Database
`atm_incident` (+ `incident_event`, `incident_note`, `sla_policy` [D]).

### UI / Backend
Incident Console, Field-engineer mobile page; Incident Service, Notification Service.

---

## M6. Remote commands **[C]**

### Functional requirements
| ID | Requirement |
|---|---|
| FR-CMD-01 | Commands: RestartApplication, RestartXfs (XFS manager then app), RestartDevice (XFS RESET), RestartWindowsService (allow-listed), CollectLogs, CollectEj, RunDiagnostics, RebootAtm, ShutdownAtm. |
| FR-CMD-02 | Every command requires a **reason** (incident id or free text) and is audited (requested, approved, delivered, result). |
| FR-CMD-03 | Disruptive commands (Reboot, Shutdown) need **dual control**: requester != approver, approver holds `atm.command.approve`. |
| FR-CMD-04 | Commands expire (15 min default); one in-flight command per (ATM, type). |
| FR-CMD-05 | The agent refuses while a customer transaction is open (idle gate) unless `force=true` **and** approved. |
| FR-CMD-06 | Command result (status, output <= 8 KB, error) visible in portal and incident timeline. |
| FR-CMD-07 | Local kill switches: each command type can be disabled in the agent's local policy regardless of what the server signs. |

### Non-functional
Delivery latency < 2 s for a waiting agent (NOTIFY wake-up). Signature verification failures are **security events** (alert + audit). No arbitrary script execution, ever: only fixed handlers with validated parameters.

### Technical design
Full security model in `docs/14-xfs-integration.md#remote-command-execution` and `docs/07-security.md`. Implemented: `CommandService`, `PemCommandSigner`, `CommandNotifier` (server); `RemoteCommandDispatcher` + 9 handlers + `RemoteCommandWorker` (agent); `CommandCrypto` (shared canonicalisation + ECDSA P-256).

### REST APIs **[C]**
`POST /api/atms/{id}/restart`, `POST /api/commands/{id}/approve`, `GET /api/agent/commands/next`, `POST /api/agent/commands/{id}/result`; [D] `GET /api/commands?atm=&state=`, `POST /api/atms/{id}/commands` (logs/EJ/diagnostics).

### Database
`remote_command` with `ck_cmd_four_eyes`, `ck_cmd_signed`, `uq_cmd_nonce`.

### UI / Backend
ATM Detail -> Actions panel (command catalogue with confirmation dialog, reason, approval request); Device Service, Audit Service.

---

## M7. User management **[D]**

### Functional requirements
| ID | Requirement |
|---|---|
| FR-USR-01 | Authentication delegated to the bank IdP (OIDC, MFA). Users are provisioned just-in-time on first login (`app_user`); disabled in the IdP = denied everywhere within 10 min (token lifetime). |
| FR-USR-02 | RBAC: 6 seeded roles (NOC_VIEWER, NOC_OPERATOR, FIELD_ENGINEER, SUPERVISOR, ADMIN, AUDITOR) over 15 permissions; custom roles. |
| FR-USR-03 | Region scoping: a role assignment can be limited to a `region_code` (row-level filter on branch). |
| FR-USR-04 | Admin UI: list/search users, assign/revoke roles, deactivate, view effective permissions, access review export. |
| FR-USR-05 | Separation of duties: ADMIN cannot hold SUPERVISOR/AUDITOR; requester != approver is enforced by data constraints. |
| FR-USR-06 | Service accounts / API clients with scopes; agent identities are devices, not users. |

### Non-functional
Permission changes effective <= 10 min (token lifetime) or immediately via back-channel revocation list. All role changes audited with granter.

### Technical design
Authentication Service issues tokens with a `permissions` array claim computed from `user_role` x `role_permission` (+ `scope_region` claim). Services authorise by **permission**, never by role name (`RequireClaim("permissions", "atm.read")`). Region scoping applied as a mandatory `WHERE b.region_code = ANY(@regions)` in repository queries (to be added with the user module; currently all-region).

### REST APIs
`GET/POST /api/users`, `PUT /api/users/{id}/roles`, `POST /api/users/{id}/deactivate`, `GET/POST /api/roles`, `GET /api/permissions`.

### Database
`app_user`, `role`, `permission`, `role_permission`, `user_role`.

### UI / Backend
Administration Portal -> Users, Roles, Access review; Authentication Service, Audit Service.

---

## M8. Audit logging **[C+D]**

### Functional requirements
| ID | Requirement |
|---|---|
| FR-AUD-01 | Audit: authentication events, authorisation denials, every create/update/delete of master data, role changes, command lifecycle, deployment approvals, data exports, configuration changes. |
| FR-AUD-02 | Record who (sub, name, IP), what (action, entity), when (UTC, microseconds), outcome, correlation/trace id, structured details. |
| FR-AUD-03 | Tamper evidence: append-only, **hash chain** (`prev_hash`, `row_hash`), UPDATE/DELETE blocked by trigger, periodic chain verification job, off-box export to SIEM/WORM storage. |
| FR-AUD-04 | Search/filter/export (CSV, signed JSON) for auditors; retention >= 12 months online, 7 years archive. |
| FR-AUD-05 | **Failure to write an audit record fails the audited operation** for privileged actions. |

### Non-functional
Write p99 < 20 ms; throughput 2,000/s (advisory lock serialises the chain - shard the chain per partition or per service if sustained rate exceeds this). Clock sync (NTP) monitored. Cardholder data never in `details`.

### Technical design
* **[C]** `audit_append()` PL/pgSQL (advisory-lock-serialised chain), `AuditWriter` in Device Service, trigger `audit_immutable`.
* **[D]** Audit Service: consumes `audit.record` events from services that cannot write the DB directly, verifies the chain every 5 minutes (recomputes `row_hash` over the last N rows), exports to SIEM (CEF over TLS), raises `AUDIT_CHAIN_BROKEN` (page).

### REST APIs
`GET /api/audit?actor=&action=&entityType=&entityId=&atm=&from=&to=&page=` (`audit.read`), `GET /api/audit/verify` (`audit.read`), `POST /api/audit/export`.

### Database
`audit_log` (monthly partitions, `ix_audit_*`), function `audit_append`, trigger `trg_audit_no_update`.

### UI / Backend
Administration Portal -> Audit viewer; Audit Service.
