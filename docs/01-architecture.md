# 01 - System architecture (C4)

Model-as-code: `docs/architecture/workspace.dsl` (Structurizr DSL: `structurizr-cli export -w workspace.dsl -f mermaid`).
The Mermaid below renders on GitHub/Azure DevOps wikis.

## 1. Layers and the contract between them

| # | Layer | Tech | Owns | Talks to (protocol) |
|---|---|---|---|---|
| 1 | **ATM Agent** | .NET 9 Worker Service, win-x86, SQLite WAL cache | Device status, XFS events, EJ, inventory, command execution, store-and-forward | API Gateway (HTTPS, mTLS + OAuth2 client-credentials, **outbound only**) |
| 2 | **API Gateway** | YARP on AKS behind Azure Front Door/App Gateway WAF (on-prem: NGINX/Envoy) | TLS/mTLS termination, JWT pre-validation, rate limiting, routing, request-size limits | Backend services (HTTP/2, mTLS via mesh) |
| 3 | **Backend services** | ASP.NET Core 9 minimal APIs, Clean Architecture + CQRS | Business logic per bounded context (9 services) | PostgreSQL (SQL), Kafka (events), each other only via events |
| 4 | **Database layer** | PostgreSQL 16 (zone-redundant HA), Elasticsearch 8 (EJ + log search), Redis (optional cache) | System of record (PG), search (ES) | - |
| 5 | **Messaging layer** | Apache Kafka (Strimzi / Azure Event Hubs Kafka endpoint / Confluent) | Event backbone, 8 topics + DLQs | Producers: outbox relays; Consumers: services, AI, Notification |
| 6 | **Monitoring layer** | OpenTelemetry Collector, Prometheus, Grafana, Elastic (logs), Tempo (traces) | Platform observability (not ATM monitoring) | OTLP from every service and (optionally) agents |
| 7 | **AI layer** | Azure ML (training, registry, managed online endpoints), Azure AI Search + Azure OpenAI (RCA retrieval), ADLS Gen2 (feature store) | Classification, failure prediction, RCA suggestions | Monitoring service (HTTPS, circuit-broken, rules fallback) |
| 8 | **Notification layer** | Notification service, SignalR hub (UI push), SMTP/SMS/Teams/webhook adapters, ITSM connector | Fan-out with retry + DLQ | Kafka in; adapters out |

**Hard rules** (enforced in review and by `tests`):
1. Agents never accept inbound connections. Everything is pull/push **from** the ATM.
2. Services own their tables. Cross-service reads go through events or APIs, never SQL.
3. State change and its event are written in **one transaction** (outbox); Kafka outage never loses or blocks writes.
4. No cardholder data (PAN, track, PIN, keys) is stored or transmitted unmasked. The agent masks at source (`PanMasker`).
5. Disruptive actions require dual control and a signature that the **ATM itself** verifies.

## 2. Level 1 - System context

```mermaid
flowchart LR
  noc([NOC operator]) -->|HTTPS OIDC| nexus
  eng([Field engineer]) -->|HTTPS OIDC| nexus
  sup([Supervisor / approver]) -->|HTTPS OIDC| nexus
  aud([Auditor]) -->|HTTPS OIDC| nexus
  subgraph nexus [ATM Nexus platform]
    direction TB
    platform[Monitoring, management,<br/>security and update platform]
  end
  atm[[ATM fleet<br/>NCR / DN / Hyosung / GRG / Hitachi / KAL / XFS4IoT]] -->|mTLS HTTPS outbound only| nexus
  nexus -->|SAML/OIDC| idp[(Bank IdP / Entra ID)]
  nexus -->|tickets webhook / REST| itsm[(ServiceNow / Jira SM)]
  nexus -->|syslog / CEF| siem[(SIEM)]
  nexus -->|SMTP / SMS / Teams| notify[(Notification channels)]
  nexus -->|SCEP / ACME| pki[(Bank PKI)]
  host[(Card switch / host)] -. no data flow: out of PCI scope .- nexus
```

## 3. Level 2 - Containers

```mermaid
flowchart TB
  subgraph atm [ATM - Windows 10 IoT / 11 IoT]
    app[ATM application<br/>APTRA / ProTopas / ProBase / Kalignite]
    xfs[XFS manager + vendor SPs<br/>msxfs.dll]
    agent[ATM Agent<br/>.NET 9 Worker Service x86]
    cache[(SQLite WAL<br/>outbox, checkpoints, nonces)]
    app --- xfs
    agent -->|WFS* P/Invoke / XFS4IoT WebSocket| xfs
    agent --- cache
    agent -.->|tails journals + logs| app
  end

  subgraph edge [Edge / DMZ]
    waf[WAF + Front Door]
    gw[API Gateway - YARP]
  end

  subgraph aks [AKS - 3 zones]
    dev[Device Service]
    mon[Monitoring Service]
    alert[Alert Service]
    inc[Incident Service]
    dep[Deployment Service]
    rep[Reporting Service]
    notif[Notification Service]
    audit[Audit Service]
    auth[Authentication Service]
    portal[Blazor Web App<br/>+ SignalR hub]
  end

  subgraph data [Data tier]
    pg[(PostgreSQL 16<br/>zone-redundant HA)]
    es[(Elasticsearch<br/>EJ + logs)]
    kafka{{Kafka<br/>RF3 minISR2}}
    blob[(Blob / ADLS<br/>packages, log bundles, features)]
  end

  subgraph ai [AI - Azure]
    aml[Azure ML endpoints]
    search[Azure AI Search + OpenAI]
  end

  subgraph obs [Observability]
    otel[OTel Collector] --> prom[(Prometheus)] --> graf[Grafana]
    otel --> tempo[(Tempo)]
    otel --> es
  end

  agent -->|HTTPS mTLS| waf --> gw
  gw --> dev & dep & auth & rep & inc & alert & portal
  dev & mon & alert & inc & dep & rep & notif & audit & auth --> pg
  dev & alert & inc & dep & mon & notif & audit & rep <--> kafka
  rep --> es
  dep --> blob
  mon -->|score / classify / rca| aml & search
  notif -->|push| portal
  aks -. OTLP .-> otel
```

## 4. Level 3 - Components

### 4.1 Device Service (implemented: `src/Services/AtmNexus.Services.Device`)

```mermaid
flowchart LR
  subgraph endpoints [Endpoints layer]
    ae[AtmEndpoints<br/>/api/atms, /status, /twin, /restart, /dashboard]
    ag[AgentEndpoints<br/>/api/agent/telemetry, /commands/next, /result, /ej, /inventory]
  end
  subgraph app [Application: CQRS handlers]
    q[Queries: ListAtms, GetAtm, GetStatus, GetTwin, GetDashboard]
    c1[IngestTelemetryHandler]
    c2[CommandService: queue / approve / dequeue / complete]
    tw[DigitalTwinBuilder - pure]
  end
  subgraph infra [Infrastructure]
    repo[AtmRepository - Dapper]
    sign[PemCommandSigner - ECDSA P-256]
    ntf[CommandNotifier - LISTEN/NOTIFY]
    obx[OutboxRelayService]
    off[OfflineDetectorService]
    aud[AuditWriter - audit_append]
  end
  ae --> q --> repo
  ae --> c2 --> sign
  c2 --> ntf
  ag --> c1 --> repo
  ag --> c2
  q --> tw
  c1 -->|same txn| obx
  off -->|same txn| obx
  c2 --> aud
  repo --> pg[(PostgreSQL)]
  obx --> k{{Kafka}}
```

### 4.2 ATM Agent (implemented: `src/Agent/*`)

```mermaid
flowchart TB
  subgraph devices [Device abstraction - AtmNexus.Devices.Abstractions]
    iface[IATMDevice / ICardReader / ICashDispenser / ICashRecycler / IPinPad / IPrinter / ISensor / IDepository]
    fac[IDeviceAdapterFactory plugins]
  end
  subgraph xfsl [XFS layer - AtmNexus.Devices.Xfs]
    mgr[XfsServiceManager<br/>Open Register Lock Unlock Execute GetInfo Close]
    api[IXfsApi]
    win[Win32XfsApi + message pump]
    mock[MockXfsApi - simulator/tests]
    x4[Xfs4IotClient + Xfs4IotCdmBackend]
    evp[XfsEventProcessor<br/>Execute/Service/User/System handlers]
    svc[CardReaderService / CashDispenserService / PrinterService / PinpadService / SiuService]
  end
  subgraph core [Agent.Core - workers]
    dm[DeviceManager]
    poll[DevicePollerService + StatusReconciler]
    lst[XfsEventListener]
    ap[AlarmProcessor -> AlarmPipeline]
    hs[DeviceHealthService + HealthScoreEngine]
    sm[ServiceMonitorWorker]
    we[WindowsEventWorker]
    inv[InventoryWorker]
    ej[EJCollector -> Parser -> Indexer -> Storage]
    pb[ProBase: FileWatcher, LogParser, HealthMonitor, EventPublisher]
    rc[RemoteCommandWorker -> RemoteCommandDispatcher -> handlers]
    up[TelemetryUploader]
  end
  win --> api
  mock --> api
  mgr --> api
  svc --> mgr
  x4 --> svc
  fac --> svc
  dm --> fac
  dm --> iface
  lst --> mgr
  lst --> evp --> bus((IDeviceEventBus))
  poll --> dm
  poll --> bus
  bus --> ap --> out[(SQLite outbox)]
  hs --> out
  sm --> out
  we --> out
  inv --> out
  ej --> out
  pb --> out
  up --> out
  up -->|HTTPS| gw[Gateway]
  rc -->|long-poll| gw
```

## 5. Deployment diagrams

### 5.1 Azure (reference deployment, 50k ATMs)

```mermaid
flowchart TB
  internet((ATMs + users)) --> fd[Azure Front Door Premium + WAF]
  fd --> pls[Private Link]
  subgraph hub [Hub VNet]
    fw[Azure Firewall] --- bastion[Bastion]
  end
  subgraph spoke [Spoke VNet - atmnexus-prod, 3 availability zones]
    subgraph aks [AKS - system pool 3 nodes + user pool 6-30 nodes, zones 1/2/3]
      ing[Ingress NGINX / YARP gateway]
      svcs[9 microservices + portal<br/>HPA, PDB, topology spread]
      kafkaK[Strimzi Kafka 3 brokers + 3 KRaft controllers<br/>or Event Hubs Premium]
      otelK[OTel collector, Prometheus, Grafana, Tempo]
    end
    pg[(Azure DB for PostgreSQL Flexible<br/>zone-redundant HA, PITR 35d)]
    es[(Elastic Cloud on Azure / ECK)]
    kv[(Key Vault Premium HSM)]
    st[(Storage: GRS, immutable, CMK)]
    acr[(ACR geo-replicated)]
  end
  subgraph dr [DR region - paired]
    pg2[(PG geo-replica)]
    aks2[AKS warm standby, scaled to 0 services / 1 node]
    st2[(GRS replica)]
  end
  fd --> ing --> svcs
  svcs --> pg & es & kafkaK & kv & st
  pg -. async replication RPO 5 min .-> pg2
  st -. GRS .-> st2
  aml[Azure ML + AI Search + OpenAI<br/>private endpoints] <--> svcs
```

### 5.2 Deployment model variants

| Model | Compute | PostgreSQL | Kafka | Object store | Secrets | Notes |
|---|---|---|---|---|---|---|
| **On-premise** | Kubernetes (RKE2/OpenShift), 3 racks/zones | Patroni 3-node (sync replica) + pgBackRest | Strimzi 3+3 | MinIO (erasure-coded) | HashiCorp Vault + CSI | Air-gapped option: mirror registry, no AI layer or on-prem ONNX scoring |
| **Private cloud** | Same Helm charts on tenant K8s | Managed PG or Patroni | Strimzi / Redpanda | S3-compatible | Vault | Same artefacts, only values files differ |
| **Azure** | AKS | Flexible Server ZRS | Event Hubs Kafka or Strimzi | Blob (GRS) | Key Vault | Reference above |
| **Hybrid** | Data plane on-prem (PG, Kafka, ES, gateway in DMZ); optional control/analytics plane in Azure | On-prem | On-prem Kafka + MirrorMaker 2 -> Event Hubs for AI | Both | Vault + Key Vault | ATMs only ever reach the on-prem gateway; only **de-identified** features leave the bank network |

## 6. Sequence diagrams

### 6.1 Telemetry -> alert -> incident -> notification

```mermaid
sequenceDiagram
  autonumber
  participant X as XFS SP
  participant A as ATM Agent
  participant G as Gateway
  participant D as Device Service
  participant P as PostgreSQL
  participant K as Kafka
  participant AL as Alert Service
  participant I as Incident Service
  participant N as Notification Service
  participant U as Portal (SignalR)

  X->>A: WFS_USER_EVENT CASHUNITTHRESHOLD (unit 2 low)
  A->>A: XfsEventProcessor -> CashLow(Major), AlarmPipeline dedup, write SQLite outbox
  A->>G: POST /api/agent/telemetry (mTLS + Bearer) - woken early because severity>=Major
  G->>D: forward (JWT validated, rate limited)
  D->>P: BEGIN, upsert atm_status/components/cash, upsert atm_alert (fingerprint), INSERT outbox_event x2, COMMIT
  D-->>A: 200 {alertsCreated:1}
  A->>A: ack outbox rows
  D->>K: OutboxRelay -> atm.alert.created (key=atmId)
  K->>AL: consume (group alert-service)
  AL->>AL: evaluate rules (CASH_LOW + branch calendar + CIT schedule)
  AL->>K: publish alert.escalated / or no-op
  K->>I: consume atm.alert.created
  I->>P: create incident (unique open per ATM+category) + outbox
  I->>K: atm.incident.created
  K->>N: consume -> render template -> email/SMS to CIT replenishment queue
  N->>U: push via SignalR (NOC console refresh)
```

### 6.2 Remote restart with dual control (reboot) and local verification

```mermaid
sequenceDiagram
  autonumber
  participant O as Operator (atm.command.execute)
  participant S as Supervisor (atm.command.approve)
  participant D as Device Service
  participant P as PostgreSQL
  participant A as ATM Agent
  participant W as Windows / ATM app

  O->>D: POST /api/atms/{id}/restart {target:"atm", reason:"INC-2025-000123"}
  D->>P: INSERT remote_command state=PENDING_APPROVAL (no signature yet), audit_append
  D-->>O: 202 PENDING_APPROVAL
  S->>D: POST /api/commands/{id}/approve
  D->>D: approver != requester? , sign canonical(cmd incl. ApprovedBy) with ECDSA P-256 (Key Vault HSM)
  D->>P: UPDATE state=PENDING, pg_notify('cmd_queued', atmId), audit_append
  A->>D: GET /api/agent/commands/next?waitSeconds=30 (long poll, already waiting)
  D-->>A: SignedRemoteCommand (woken by NOTIFY)
  A->>A: verify signature (pinned key) -> ATM code -> expiry -> local policy -> approver!=issuer -> nonce replay -> idle check
  alt ATM busy
    A-->>D: result REJECTED "busy with customer transaction"
  else idle
    A->>W: shutdown.exe /r /t 60 (no shell, fixed arg list)
    A-->>D: POST result SUCCEEDED "reboot scheduled"
  end
  D->>P: UPDATE state, audit_append(atm.command.result)
```

### 6.3 Heartbeat loss -> offline

```mermaid
sequenceDiagram
  participant O as OfflineDetectorService (every 30 s, N replicas)
  participant P as PostgreSQL
  participant K as Kafka
  participant AL as Alert Service
  O->>P: UPDATE atm_status SET connectivity='OFFLINE' WHERE connectivity='ONLINE' AND last_seen_at < now()-3min RETURNING ...
  Note over O,P: row-level: only one replica wins each ATM => exactly-once transition
  O->>P: INSERT outbox_event(atm.offline) (same txn)
  P-->>K: relay
  K->>AL: atm.offline -> correlate: >5% fleet offline in same region => ONE network incident, not 2,500 ATM incidents
```

### 6.4 Software deployment (canary)

```mermaid
sequenceDiagram
  participant U as Release manager
  participant DS as Deployment Service
  participant K as Kafka
  participant A as ATM Agent
  participant B as Blob (signed package)
  U->>DS: POST /api/atms/{id}/deploy {packageId, strategy:CANARY}
  DS->>DS: four-eyes approval, select 1% canary cohort, schedule in maintenance window
  DS->>K: deployment.started
  A->>DS: command: InstallPackage (signed, includes sha256 + package signature)
  A->>B: download over mTLS (resume-capable)
  A->>A: verify sha256 + signature -> stop ATM app at idle -> install -> health probe 10 min
  alt healthy
    A-->>DS: result SUCCEEDED
  else regress
    A->>A: automatic rollback to previous version
    A-->>DS: result ROLLED_BACK
  end
  DS->>K: deployment.completed
  DS->>DS: canary success rate >= 99% for 24 h -> widen to 10% -> 50% -> 100%, halt on 2 failures
```

### 6.5 Authentication

```mermaid
sequenceDiagram
  autonumber
  participant B as Browser (Blazor)
  participant P as Portal BFF
  participant I as IdP / Auth Service
  participant G as Gateway
  participant S as Device Service
  B->>P: GET /
  P-->>B: 302 /authorize (code + PKCE S256, state, nonce)
  B->>I: login (MFA)
  I-->>B: 302 /signin-oidc?code=...
  B->>P: code
  P->>I: token exchange (code_verifier, client auth private_key_jwt)
  I-->>P: id_token + access_token(10 min) + refresh_token(rotating)
  P-->>B: HttpOnly Secure SameSite=Strict cookie (tokens stay server-side)
  B->>P: SignalR / API call
  P->>G: Bearer access_token
  G->>S: JWT validated (iss, aud, exp, signature via JWKS), permission claim checked per endpoint

  Note over I: Agent flow: mTLS client auth to /connect/token (RFC 8705), grant client_credentials, scope atm.agent, 15 min token, client_id = ATM code
```

## 7. Capacity model (50,000 ATMs)

| Quantity | Calculation | Result |
|---|---|---|
| Telemetry messages | 50,000 / 60 s | **~833/s** steady, ~2,500/s peak after a network outage (agents jitter retries; backlog drains from SQLite) |
| Message size | 5 devices + 4 cassettes + <=10 events | 3-6 KB -> 2.5-5 MB/s ingress |
| Long-poll connections | 1 per ATM | 50,000 held open: 6 pods x ~8,000 (Kestrel handles >50k per pod; spread for failure domains) |
| PG write TPS | status upsert + components + cash + (alerts) | ~833 txn/s x ~12 statements = 10k stmt/s - fits a 16 vCPU Flexible Server; hot rows use fillfactor 70-80 for HOT updates |
| `atm_status_history` rows | sampled: level change or 1 per 15 min | 50k x 96/day = **4.8M/day**, ~430M rows / 90 days, partitioned monthly (unsampled would be 72M/day) |
| Kafka partitions | per-ATM ordering by key; consumers scale to partitions | `atm.status.changed` 48, `atm.alert.created` 48, `atm.ej.batch` 96 |
| EJ volume | ~150 tx/ATM/day x 50k x ~1.2 KB indexed | 9 M docs/day ~ 11 GB/day raw (3-4 GB compressed), ILM hot 30d -> warm -> snapshot |
| Service replicas | Device 6-12, others 2-6 | HPA on CPU 65% |

## 8. Availability design (99.99% = 52.6 min/year)

| Failure | Mitigation | Customer-visible effect |
|---|---|---|
| Pod / node loss | >=3 replicas, zone spread, PDB minAvailable 2, readiness gating, `maxUnavailable: 0` | none |
| AZ loss | 3-zone AKS, PG zone-redundant HA (sync standby, failover ~60-120 s), Kafka RF3 minISR2 | in-flight requests retried by agents |
| PostgreSQL failover | Npgsql retry on transient, outbox + agents buffer, `Maximum Pool Size` tuned | ingest pauses <=2 min, no data loss |
| Kafka outage | Outbox keeps writing; relay resumes in order | alerts/incidents delayed, ingest unaffected |
| Platform outage | **Agent keeps monitoring and queueing (50k rows = days); ATMs keep serving customers** - the agent is never on the transaction path | none for cardholders |
| Region loss | Warm standby in paired region; PG async replica RPO <=5 min, RTO <=30 min (runbook + quarterly drill); agents fail over via DNS/Front Door | NOC degraded ~30 min |
| Bad release | Canary + `helm --atomic` + SLO burn gate + one-command rollback; DB migrations expand/contract | none |
| Poison message | Retry 3x with backoff -> `dlq.<group>`; alert on DLQ depth | one event delayed |
| Thundering herd after outage | Agent jitter (+/-20%), exponential backoff to 15 min, token-bucket rate limit per client | smoothed ingest |

Error budget: 4.3 min/month. Burn-rate alerts (14.4x/5 min page, 6x/30 min page, 1x/6 h ticket) are in `deployment/observability/prometheus-rules.yaml`.

## 9. Technology decisions (ADR summary)

| ID | Decision | Alternatives | Why |
|---|---|---|---|
| ADR-1 | Agent is **x86**, .NET 9 self-contained single file | x64, .NET Framework 4.8 | msxfs.dll is 32-bit on nearly all ATMs; .NET 9 gives a modern runtime without GAC/installer dependencies |
| ADR-2 | Pull model + outbound HTTPS only | MQTT/gRPC streaming, inbound agent port | Bank firewalls allow outbound 443 only; long-poll reaches the same latency with no inbound surface |
| ADR-3 | Dapper + hand-written SQL on hot paths | EF Core everywhere | Explicit upserts/`SKIP LOCKED`/`LISTEN`; predictable plans at 10k stmt/s. EF Core remains fine for admin CRUD services |
| ADR-4 | Transactional outbox | Dual write, CDC (Debezium) | Simple, portable to on-prem; Debezium is the migration path if relay becomes a bottleneck |
| ADR-5 | Postgres LISTEN/NOTIFY for command wake-up | Redis pub/sub, SignalR to agents | No extra infrastructure; falls back to timed re-check if the LISTEN connection drops |
| ADR-6 | Signed commands verified **on the ATM** | Trust the TLS channel only | Compromise of the gateway/API cannot execute anything on 50k ATMs |
| ADR-7 | Health score computed on the ATM **and** re-derivable server-side | Server only | Works while offline, deterministic, same code in both places |
| ADR-8 | Vendor differences are **data** (profiles: status layouts, event catalog, SIU maps, EJ regex) | Per-vendor code branches | Onboarding a new ATM model = capture logs + edit JSON, not a release |
| ADR-9 | Blazor Web App (Interactive Server for NOC, WASM for admin) | React SPA | One language/team; SignalR push built in |
| ADR-10 | Kafka event backbone, JSON + schema registry (BACKWARD compat) | Avro/Protobuf | JSON is debuggable by NOC engineers; schemas still enforced centrally |
