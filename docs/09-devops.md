# 09 - DevOps

Tooling: **GitHub** (source of truth, PR validation, CodeQL, secret scanning) + **Azure DevOps** (environment approvals, deployments to AKS) + **Docker** + **Kubernetes** + **Helm**.
Artefacts in this repo: `.github/workflows/ci.yml`, `azure-pipelines.yml`, `deployment/pipelines/deploy.yml`, `deployment/pipelines/canary-gate.sh`, `deployment/docker/*`, `deployment/helm/*`, `deployment/k8s/device-service.yaml`, `deployment/agent/install-agent.ps1`, `scripts/bootstrap.sh`.

## 1. Branching strategy (trunk-based + release branches)

```
main ───●──●──●──●──●──●──●──●──────●──●──────  always releasable, protected
         \ /   \ /                  \
 feature/*     fix/*                 release/1.4  ──●──(cherry-pick hotfix)──● tag v1.4.2
```
| Rule | Detail |
|---|---|
| Work branches | `feature/<ticket>-slug`, `fix/<ticket>-slug`, <= 2 days, rebased on `main` |
| `main` protection | PR required, 2 approvals (1 CODEOWNER), all checks green (build, test, format, CodeQL, gitleaks, SQL migration applies), linear history (squash), signed commits |
| Release | cut `release/x.y` when a milestone is feature-complete; only bug fixes (cherry-picked **from main first**) after the cut; tag `vX.Y.Z` from the release branch |
| Hotfix | branch from the release tag, fix on `main` first when possible, cherry-pick, tag patch version |
| Feature flags | `Microsoft.FeatureManagement` (config-driven) so incomplete work merges dark; flags removed within 2 releases |
| Commit style | Conventional Commits (`feat:`, `fix:`, `perf:`, `refactor:`, `docs:`, `test:`, `ci:`) - drives changelog and the semver bump |

## 2. CI pipeline (every PR and push)

| # | Stage | Tool | Gate |
|---|---|---|---|
| 1 | Restore (locked) / build | `dotnet build -c Release` (`Directory.Packages.props` central versions) | zero errors, nullable warnings-as-errors on selected rules |
| 2 | Format/analyzers | `dotnet format --verify-no-changes` | clean |
| 3 | DB migration test | apply `db/migrations/*.sql` to empty PostgreSQL 16 service container with `ON_ERROR_STOP` | applies; `DbNamesTests.SqlMigration_*` keep code enums and CHECK constraints in sync |
| 4 | Unit + integration tests | xUnit; **includes the end-to-end agent pipeline on the simulator** (`AgentPipelineTests`) | pass; coverage reported (target 80% for `Agent.Core`, `Devices.*`, `Services.*`) |
| 5 | SAST | CodeQL `security-extended` | no new High |
| 6 | Dependencies | `dotnet list package --vulnerable --include-transitive`, Dependabot | none vulnerable |
| 7 | Secrets | gitleaks | none |
| 8 | Agent package | windows runner: `dotnet publish -r win-x86 --self-contained -p:PublishSingleFile=true`, sign (Trusted Signing) on tags | signature valid |
| 9 | Images | `deployment/docker/build-image.sh`, push to ACR, **Trivy** (HIGH/CRITICAL fixable = fail), SBOM (SPDX) + provenance attestation | scan clean |
| 10 | Helm | `helm lint` + `helm template` with every env values file + `kubeconform` | valid manifests |

## 3. CD pipeline (Azure DevOps stages)

```
Build --> DEV (auto) --> SIT (auto, smoke+regression) --> UAT (approval) --> PROD (CAB + 2 approvers + canary soak)
```
* **Deploy job** (`deployment/pipelines/deploy.yml`): `helm upgrade --install --atomic --timeout 8m` per service with `-f values/<svc>.yaml -f env/values-<env>.yaml`; PROD deploys by **image digest** resolved from ACR (immutable), not by tag.
* **Smoke test** through the gateway `/health/ready`; **regression** in SIT (API contract tests + simulator fleet of 50 ATMs, see 6).
* **Canary soak (PROD)**: after rollout `canary-gate.sh` watches `ApiAvailabilityBurnFast` and `TelemetryIngestStalled` for 15 min; failure -> `helm rollback`.
* **Database migrations**: run as a Helm pre-upgrade Job under the `*_migrator` role. **Expand/contract** only: (1) expand (add nullable column / new table / new index `CONCURRENTLY`), deploy code that writes both, (2) backfill, (3) switch reads, (4) contract in a later release. Never rename/drop in the same release as the code change; always backwards compatible with N-1 code (rolling deploy runs both).
* **Rollback**: `helm rollback <svc> <rev>`; DB changes are N-1 compatible by construction; data fixes are forward-only scripts.
* **Agent releases** are not "deployments to a cluster": see 5 (rings).

## 4. Versioning strategy

| Artefact | Scheme | Source |
|---|---|---|
| Services/images | **SemVer** `MAJOR.MINOR.PATCH` from Git tags (GitVersion/`git describe`), pre-release `-rc.N` on release branches, `+sha` metadata | tag `v1.4.2` -> image `device-service:1.4.2` |
| REST API | URL major only when breaking (`/api/v2/...`); additive changes are non-breaking; `Sunset` + `Deprecation` headers for 6 months | OpenAPI `info.version` |
| Event schemas | `schemaVersion` integer + topic suffix for breaking (`.v2`), BACKWARD compatibility in registry | `docs/05-events-kafka.md` |
| DB | `V001__description.sql` ordered, immutable once released; repeatable `R__views.sql` | `db/migrations` |
| Agent | SemVer; **compatibility matrix**: agent N supports server N and N-1; server supports agent N, N-1, N-2 (fleet cannot upgrade atomically); `agentVersion` in every telemetry message drives the dashboard "version distribution" | `atm.agent_version` |
| Helm chart | chart version bumps only with template changes; `appVersion` = service version | `Chart.yaml` |
| Vendor profiles | `xfs_profile` id + version (e.g. `ncr-aptra-xfs3@1.2`) delivered as signed config packages, independent of agent releases | `vendor.xfs_profile` |

## 5. Environment strategy

| | DEV | SIT | UAT | PROD |
|---|---|---|---|---|
| Purpose | developer integration | system/integration, regression, performance smoke | business acceptance, vendor-hardware pilots | live |
| Deploys | every merge to `main` | auto after DEV green | approval (QA lead) | CAB ticket + 2 approvers + freeze-window check |
| Topology | 1 AKS node pool, single-AZ, PG burstable, Kafka 1 broker | 2 zones, PG GP 4 vCPU, Kafka 3 | prod-like (3 zones), PG GP 8 vCPU | 3 zones, PG zone-redundant 16+ vCPU, Kafka 3+3, DR region |
| Data | synthetic + simulator (200 ATMs) | simulator fleet 5,000 | 20-100 real pilot ATMs + simulator | real |
| Identity | dev IdP (Keycloak) | bank IdP test tenant | bank IdP UAT | bank IdP prod |
| Secrets | `kv-atmnexus-dev` | `-sit` | `-uat` | `-prod` (Managed HSM) |
| Observability | shared Grafana | own | own | own + paging |
| Real ATMs | none | none | pilot ring | rings 0-4 |

**Agent rollout rings** (PROD): ring 0 = 10 lab ATMs (every vendor/model), ring 1 = 1% (branch staff ATMs), ring 2 = 10%, ring 3 = 50%, ring 4 = 100%. Gate between rings: 24 h with `deployment.completed` success >= 99%, no `HEALTH_LEVEL` regression vs cohort, no `FLAPPING` alerts, ingest p99 unchanged. Automatic rollback in the agent (health probe failure) and automatic halt in Deployment Service (2 failures in a wave).

## 6. Test strategy and load test
| Layer | What | Where |
|---|---|---|
| Unit | cash calculator, health score, reconciler, parsers, event mapping, signing | `tests/AtmNexus.Tests` |
| Component | device services on `MockXfsApi`, XFS4IoT bridge on `MockXfs4IotServer`, SQLite cache | same |
| End-to-end (hosted) | full agent pipeline on simulator; scenario -> XFS event -> alert in outbox -> health drop | `AgentPipelineTests` |
| Contract | OpenAPI examples validated against handlers; Kafka schemas against registry | [planned Sprint 9] |
| Hardware certification | per vendor/model on a physical ATM or vendor test bench: struct layouts, event IDs, error codes, SIU maps (see `docs/14-xfs-integration.md` 12) | UAT |
| Performance | **simulator fleet**: `docker run atmnexus-simulator` x N pods (each hosting up to ~200 virtual ATMs in a later `--fleet` mode); target 50,000 virtual ATMs at 1 msg/min; SLO assertions: ingest p99 < 250 ms, zero loss, outbox lag < 5 s | k6/NBomber + simulator, nightly in SIT |
| Chaos | kill PG primary, Kafka broker, AZ drain, gateway cut; agents must queue and drain without loss | monthly in UAT, game-day in PROD |
| Security | SAST/DAST (ZAP), pen test annually, agent fuzzing of command parser/EJ parser | CI + yearly |

## 7. Infrastructure as code
Terraform (or Bicep) modules: `network` (hub/spoke, NSGs, Private Link), `aks` (zonal pools, workload identity, Defender), `postgres` (Flexible Server, ZRS, PITR 35 d, geo-replica), `kafka` (Strimzi via Helm or Event Hubs), `keyvault` (HSM keys, access policies via RBAC), `acr`, `storage` (GRS, immutability policies), `observability`. State in a locked remote backend; plan/apply via pipeline with the same approval gates as application deploys. Drift detection nightly.

## 8. Operations
* **Runbooks** (`docs/10-observability.md`): ingest stalled, outbox lag, Kafka lag, PG failover, fleet-wide offline, command signature failures, audit chain broken, certificate expiry wave.
* **On-call**: platform team primary/secondary; NOC is the first line for ATM issues; PagerDuty/Teams routing from Alertmanager.
* **Backups**: PG PITR 35 d + weekly logical dump to immutable storage; ES snapshots daily; restore drill quarterly (RTO 30 min proven, not assumed).
* **Change freeze**: configurable calendar blocks PROD deploys (month-end cash peaks, holiday trading); enforced by an environment check.
