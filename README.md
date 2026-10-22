# ATM Nexus

Vendor-neutral ATM enterprise monitoring and management platform: monitor, diagnose, secure and update ATMs from NCR, Diebold Nixdorf/Wincor, Hyosung, GRG, Hitachi, KAL and custom XFS / XFS4IoT devices, at 500 / 5,000 / 50,000+ ATMs, targeting 99.99% platform availability.

> **Read this first - honest status.** This repository was produced in a sandbox with **no .NET SDK, no physical ATM and no access to NuGet**.
>
> **Not verified (your first tasks):**
> * **The C# has never been compiled or run.** Expect a handful of compile errors and failing tests on the first `dotnet build` / `dotnet test` - run `scripts/bootstrap.sh` first. (All 141 test expectations were cross-checked arithmetically, but not executed.)
> * **XFS constants are reference values, not verified.** Struct layouts, event/command/error numbers and SIU wiring follow CEN/XFS 3.x from memory and are isolated in data or one file each. They **must be validated against each vendor's SDK headers on a real ATM** (`docs/14-xfs-integration.md` section 17).
> * **Only the Device Service and the ATM stack are coded.** The other 8 microservices, the API gateway and the Blazor portal are fully specified, not implemented (`docs/12-repo-structure.md` marks every project [C] code / [P] planned).
> * Helm templates were not rendered, Dockerfiles not built, PowerShell/Structurizr files not parsed.
>
> **Verified mechanically (with real engines):**
> * **PostgreSQL 16**: `V001__core_schema.sql` applies cleanly (`ON_ERROR_STOP`); all 38 SQL statements embedded in the Device Service prepare successfully against it; 27 behavioural checks pass (telemetry upsert, alert de-duplication/occurrence counting, auto-clear, history sampling, exactly-once offline transition, command dequeue with `SKIP LOCKED`, four-eyes/signature/nonce constraints, hash-chained append-only audit, one-open-incident-per-ATM).
> * **SQLite** agent cache SQL executed (outbox, checkpoints, replay nonces, trim semantics).
> * **EJ parsing**: the NCR / DN / KAL / generic regex dialects extracted from the C# were run over generated journals for all four outcomes.
> * **Health score + cash feasibility**: independently re-implemented and checked against every expected value used in the tests and in the docs' worked example.
> * **Syntax**: all 69 C# files parse (tree-sitter), plus a namespace-import cross-check (it found and fixed two missing `using`s); all YAML/JSON parse; the OpenAPI document parses, every `$ref` resolves, and all requested endpoints are present; all 12 Mermaid diagrams parse with Mermaid 10.9; shell scripts pass `bash -n`; all intra-repo Markdown links resolve.

## What is in the box

| Area | Where | Status |
|---|---|---|
| Architecture (C4, deployment, sequences, capacity, 99.99% design, ADRs) | `docs/01-architecture.md`, `docs/architecture/workspace.dsl` | design |
| MVP modules (8) with FR/NFR, design, APIs, schema, UI, services | `docs/02-mvp-modules.md` | design (+ code where marked) |
| 9 microservices (responsibility, DB, endpoints, events, K8s, Docker) | `docs/03-microservices.md`, `deployment/helm/values/*` | Device = code; others design + Helm values |
| PostgreSQL schema (20 tables, indexes, constraints, hash-chained audit, outbox, seed) | `db/migrations/V001__core_schema.sql`, `docs/04-database-tables.md` (generated) | **code** |
| Kafka topics, schemas, JSON examples, consumer pattern | `docs/05-events-kafka.md`, `deployment/kafka/topics.sh` | spec + script |
| OpenAPI 3.1 (all requested endpoints + agent API) | `docs/api/openapi.yaml` | spec (validated) |
| Security (OIDC/JWT/RBAC, token lifecycle, key rotation, secrets, PCI DSS mapping, STRIDE) | `docs/07-security.md` | design + code |
| UI design (7 screens, state management, samples) | `docs/08-ui.md` | design |
| DevOps (branching, CI/CD, versioning, environments, rings) | `docs/09-devops.md`, `.github/`, `azure-pipelines.yml`, `deployment/` | pipelines + design |
| Observability (metrics, logs, traces, dashboards, health, runbooks) | `docs/10-observability.md`, `deployment/observability/` | design + configs |
| AI (classification, failure prediction, RCA) | `docs/11-ai.md` | design |
| Repo structure + NuGet per project | `docs/12-repo-structure.md` | - |
| 12-month roadmap, 26 sprints, story points, person-months | `docs/13-roadmap.md` | plan |
| **ATM/XFS integration** (device abstraction, XFS manager, CDM, IDC, SIU, EJ, ProBase, commands, health score, events, XFS4IoT, digital twin, simulator) | `docs/14-xfs-integration.md` + `src/Agent`, `src/Simulator` | **code + 141 tests** |

## Quick start

```bash
# prerequisites: .NET SDK 9.0.x, Docker (optional), PostgreSQL 15+ (optional)
scripts/bootstrap.sh                                   # creates AtmNexus.sln, restore, build, test

# run the ATM simulator (real agent pipeline on a mock XFS provider; works on Linux/macOS/Windows)
dotnet run --project src/Simulator/AtmNexus.Simulator -- --vendor ncr --atm SIM-0001
#   > card | jam | chip-fault on | dispense 170 | empty 3 | safe open | tamper on | paper out | health | status

# local platform stack + Device Service
docker compose -f deployment/docker/docker-compose.dev.yml up -d postgres kafka
psql postgresql://atmnexus:atmnexus@localhost:5432/atmnexus -v ON_ERROR_STOP=1 -f db/migrations/V001__core_schema.sql
```

Agent on a real ATM: publish `win-x86` (the XFS manager is 32-bit) and install with `deployment/agent/install-agent.ps1` (see `docs/07-security.md` for the certificate/enrolment model).

## Design principles
1. Agents are **outbound-only** and **never on the transaction path**; they store and forward through a local SQLite outbox.
2. **Vendor differences are data** (profiles: status layouts, event catalog, SIU maps, EJ regexes, error tables) - onboarding a model is configuration plus certification, not a fork.
3. **No cardholder data**: masked at source (`PanMasker`), PIN pad status only, track data zeroed after parse.
4. Disruptive actions need **dual control** and a **signature verified by the ATM itself**.
5. State change and event are one transaction (outbox); consumers are idempotent.

See `docs/README.md` for the reading order.
