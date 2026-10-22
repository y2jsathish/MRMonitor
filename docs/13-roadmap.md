# 13 - Delivery roadmap (12 months, 26 sprints)

## Assumptions
* **Team (8 developers)**: 1 Solution Architect (hands-on), 3 Backend (one is the *agent/XFS lead*), 2 Frontend, 1 DevOps, 1 QA automation. **Product Owner** (not counted in the 8) owns the backlog, vendor/bank liaison and acceptance.
* 2-week sprints x 26 = 12 months. Capacity = 8 people x 12 months = **96 person-months (PM)**.
* **1 story point ~ 1.3 ideal person-days** (reference story: 3 SP = "add an endpoint with validation, tests and audit"). Velocity ramps 35 -> 45 -> 62 SP/sprint (focus factor and ceremonies already included).
* PM per SP = 96 / 1568 = **0.0612**.
* External dependencies that gate the plan (see Risks): lab ATMs from NCR / DN / Hyosung with vendor SDK access, bank PKI + IdP test tenants, Azure subscription landing zone, PCI assessor engagement, ITSM sandbox.

## Effort by epic

| Epic | Scope | Lead roles | Story points | Person-months | Already in this repo (head start, SP) |
|---|---|---|---:|---:|---:|
| E0 | Foundations: repo, CI/CD, IaC, DEV/SIT envs, observability + security baseline | DevOps, SA | 80 | 4.9 | 25 |
| E1 | Contracts, PostgreSQL schema, Device Service: registration, ingest, offline detection, digital twin | BE, SA | 90 | 5.5 | 50 |
| E2 | ATM Agent core: host, SQLite store-and-forward, poller, reconciler, alarm pipeline, health engine, uploader | BE (agent lead) | 130 | 8.0 | 90 |
| E3 | XFS layer: service manager, marshaller, event catalog, CDM/IDC/PTR/PIN/SIU services; certification on NCR, DN, Hyosung | BE (agent lead), QA | 170 | 10.4 | 90 |
| E4 | EJ framework + ProBase integration (vendor dialect profiles, Elasticsearch) | BE (agent), BE | 80 | 4.9 | 55 |
| E5 | Alert Service: rules, escalation, maintenance windows, storm control, alert API | BE | 80 | 4.9 | - |
| E6 | Incident Service: lifecycle, SLA, auto-incident rules, ITSM connector | BE | 90 | 5.5 | - |
| E7 | Notification Service + SignalR realtime | BE, FE | 50 | 3.1 | - |
| E8 | Remote commands end-to-end: signing, dual control, agent handlers, audit | BE, SA | 70 | 4.3 | 45 |
| E9 | Authentication Service (OIDC, enrolment, mTLS tokens), user/role management, Audit Service | BE, SA | 100 | 6.1 | - |
| E10 | Web portal: shell, dashboard, ATM list/detail + twin, alert/incident consoles, admin | FE x2 | 160 | 9.8 | - |
| E11 | Deployment Service, signed packages, agent update/rollback, rollout rings, deployment console | BE, FE, DevOps | 120 | 7.3 | - |
| E12 | Reporting Service: read models, EJ search, scheduled reports | BE, FE | 70 | 4.3 | - |
| E13 | AI: data pipeline, feature store, 3 models, MLOps, Monitoring Service integration | BE, SA | 90 | 5.5 | - |
| E14 | XFS4IoT backends (IDC/PTR/SIU) + migration tooling | BE (agent) | 50 | 3.1 | - |
| E15 | Scale, resilience, DR, security hardening, pen-test remediation, pilot/hypercare | All | 90 | 5.5 | - |
| E16 | QA automation: regression suite, simulator fleet mode, performance + chaos tests | QA, DevOps | 48 | 2.9 | 20 |
| **Total** | | | **1568** | **96.0** | **375** |

*Head start*: code and xUnit/E2E tests (written against the simulator) exist but have **not yet been compiled or executed** (no .NET SDK was available when they were written), certified on hardware, or security-reviewed; planned credit is 375 SP (~24%) **before** the verification effort that remains inside the same epics. It reduces risk and ramp time; it does not remove the vendor-certification work in E3.

## Effort by role (96 PM)
| Role | Headcount | PM | Main epics |
|---|---:|---:|---|
| Solution Architect | 1 | 12 | E0 E1 E8 E9 E13, design authority, security reviews |
| Backend (3) | 3 | 36 | E1 E2 E3 E4 E5 E6 E7 E8 E9 E11 E12 E13 E14 |
| Frontend (2) | 2 | 24 | E7 E10 E11 E12 |
| DevOps | 1 | 12 | E0 E11 E15 E16 |
| QA automation | 1 | 12 | E3 certification, E16, UAT |
| **Total** | **8** | **96** | |

## Quarterly milestones
| Quarter | Months | Sprints | Theme | Exit criteria (demonstrable) |
|---|---|---|---|---|
| **Q1** | 1-3 | S1-S6 | Walking skeleton | 5 lab ATMs (NCR, DN, Hyosung) report to DEV; health score + twin visible; agent < 2% CPU in 72 h soak; CI/CD to DEV green; schema V001 applied by pipeline |
| **Q2** | 4-6 | S7-S13 | MVP feature complete | All 8 MVP modules in UAT; dual-control reboot demonstrated; hash-chained audit verified; simulator fleet 5,000 in SIT meets ingest p99 < 250 ms; pilot ring 0 (20 ATMs); PCI scoping approved |
| **Q3** | 7-9 | S14-S19 | Production pilot + deployment | 500 ATMs in PROD at >= 99.9% measured availability; signed agent update with automatic rollback proven on a ring; DR drill passed (RTO 30 min); runbooks signed off by NOC |
| **Q4** | 10-12 | S20-S26 | Scale, AI, XFS4IoT, 99.99% | 5,000 ATMs in PROD; 50,000 simulated ATMs validated; AI assist live (classification + prediction); XFS4IoT migration tooling proven in shadow; pen-test findings closed; 99.99% SLO + error-budget policy in force; 1.0 handover |

## Sprint breakdown
| Sprint | Weeks | Goal | Planned SP |
|---|---|---|---:|
| S1 | 1-2 | Walking skeleton: repo, CI, DEV cluster, schema V001, Contracts, agent host + SQLite outbox | 35 |
| S2 | 3-4 | Device Service ingest + registration APIs; agent poller/health on simulator; first telemetry in DEV | 45 |
| S3 | 5-6 | XFS service manager + CDM/IDC services on lab NCR ATM; event catalog; reconciler | 62 |
| S4 | 7-8 | SIU/PTR/PIN services; DN + Hyosung lab ATMs reporting; EJ framework (NCR dialect) | 62 |
| S5 | 9-10 | Offline detection, digital twin API, dashboard API; auth against dev IdP; portal shell | 62 |
| S6 | 11-12 | Q1 demo: 5 lab ATMs (3 vendors) live in DEV; health score on dashboard; hardening of agent resource use | 62 |
| S7 | 13-14 | Alert Service (rules, dedup, ack/clear) + Alert Console; notification skeleton | 62 |
| S8 | 15-16 | Incident Service (lifecycle, SLA) + auto-incident from alerts; Incident Console | 62 |
| S9 | 17-18 | Remote commands E2E (signing, dual control, handlers) + command UI; audit hash chain + Audit Service | 62 |
| S10 | 19-20 | Authentication Service (enrolment, mTLS tokens) + user/role admin UI; ProBase integration | 62 |
| S11 | 21-22 | Notification channels + SignalR realtime; EJ -> Elasticsearch -> search UI; portal ATM detail/twin | 62 |
| S12 | 23-24 | SIT with simulator fleet 5,000; performance tuning; security review round 1 | 62 |
| S13 | 25-26 | MVP feature complete: UAT build; pilot ring 0 (20 ATMs); PCI scoping pack | 62 |
| S14 | 27-28 | UAT fixes; pilot expansion; runbooks; DR design; Deployment Service foundation (packages, signing) | 62 |
| S15 | 29-30 | Agent update + rollback + rings; deployment console; vendor profile delivery as signed config | 62 |
| S16 | 31-32 | Reporting read models + scheduled reports; ITSM connector (ServiceNow/Jira SM) | 62 |
| S17 | 33-34 | Production go-live of 500-ATM tier (pilot ring 1-2); hypercare; SLO dashboards live | 62 |
| S18 | 35-36 | DR drill #1; chaos tests (PG failover, Kafka outage, AZ drain); 20k simulated ATMs load test | 62 |
| S19 | 37-38 | Q3 exit: GA for 500-ATM tier at 99.9% measured; backlog burn-down; vendor onboarding of GRG/Hitachi/KAL profiles | 62 |
| S20 | 39-40 | AI data pipeline + feature store; shadow-mode classification | 62 |
| S21 | 41-42 | Failure-prediction models (dispenser/reader); XFS4IoT CDM in production-ready state | 62 |
| S22 | 43-44 | RCA retrieval (AI Search) + assist mode; XFS4IoT IDC/PTR/SIU backends | 62 |
| S23 | 45-46 | Scale to 5,000 ATMs production; 50k simulated validation; capacity + cost review | 62 |
| S24 | 47-48 | Pen-test remediation; key rotation drill; migration tooling XFS3 -> XFS4IoT shadow mode | 62 |
| S25 | 49-50 | 99.99% SLO validation (error-budget policy), final DR drill, documentation + training | 62 |
| S26 | 51-52 | Release 1.0 hardening, handover, support model; Phase 4 backlog grooming | 62 |
| | | **Total** | **1568** |

Sprint ceremonies: planning (Mon wk1), refinement (Wed wk1), review + retro (Fri wk2). Every sprint ends with a deployable DEV build; every 3rd sprint a SIT release; UAT/PROD releases follow the stage gates in `docs/09-devops.md`.

## Release plan
| Release | Sprint | Content | Audience |
|---|---|---|---|
| 0.1 | S6 | skeleton: ingest, agent, health, 5 lab ATMs | internal demo |
| 0.5 | S10 | alerts, incidents, commands, audit, portal consoles | SIT |
| 0.9 (MVP RC) | S13 | MVP complete, UAT | bank UAT, pilot ring 0 |
| 1.0 | S17 | production go-live, 500 ATMs | PROD rings 1-2 |
| 1.1 | S19 | deployments, reporting, ITSM | PROD |
| 1.5 | S23 | AI assist, XFS4IoT, 5,000-ATM scale | PROD |
| 2.0 | S26 | 99.99% validated, 50k-ready | PROD |

## Definition of done (every story)
Code reviewed (2 approvers), unit + integration tests, **simulator scenario** for any device/agent behaviour, OpenAPI/Kafka schema updated, observability (metrics/logs/traces) added, security checklist (authz, input validation, no CHD), migration is expand/contract, runbook/doc updated, deployed to DEV by the pipeline, demo-able to the PO.

## Top risks and mitigations
| # | Risk | Impact | Mitigation |
|---|---|---|---|
| R1 | Vendor SDK/lab ATM access delayed (NCR/DN/Hyosung) | E3 slips; certification late | PO escalates in month 1; simulator keeps development unblocked; vendor profile data-driven so certification is configuration, not code |
| R2 | XFS struct/event/error constants differ per SP release (3.0 vs 3.30, vendor extensions) | wrong status/event mapping | `docs/14` section 17 checklist; per-profile overrides; contract tests per profile; 72 h soak on each model |
| R3 | Agent impacts ATM transaction performance | bank veto | agent off transaction path, resource caps, perf budget gates (CPU/RSS), kill-switch policy, ring rollout |
| R4 | PCI scope creep (if CHD leaks into logs/EJ) | assessment cost | masking at source, strict ES mapping, redaction tests, CHD scanner in CI on fixtures |
| R5 | EJ/ProBase dialect variance | parser gaps | profile-as-data, capture real journals in onboarding, fail-open (unparsed lines kept sanitised) |
| R6 | 50k scale unproven until late | rework | simulator fleet in SIT from S12; load tests every 3 sprints; capacity model in `docs/01` |
| R7 | Bank PKI/IdP integration lead time | mTLS enrolment late | dev IdP + test CA from S1; integration spike in S5 |
| R8 | Key-person risk (agent/XFS lead) | single point of failure | pair on E3, documented vendor-profile playbook, SA as backup |
