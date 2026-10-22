# 12 - Repository structure

Legend: **[C]** exists in this repo · **[P]** planned (folder reserved, spec in `docs/`).

```
/
├── README.md
├── global.json                      pins SDK 9.0.x
├── Directory.Build.props            net9.0, nullable, implicit usings, deterministic
├── Directory.Packages.props         central package versions (one place to bump / audit)
├── .editorconfig  .gitignore
├── azure-pipelines.yml              CD (DEV -> SIT -> UAT -> PROD)
├── .github/workflows/ci.yml         CI (build, test, CodeQL, secrets, images, agent package)
├── scripts/
│   ├── bootstrap.sh                 creates the .sln, restores, builds, tests
│   └── gen-db-docs.py               regenerates docs/04-database-tables.md from the SQL
├── db/migrations/V001__core_schema.sql
├── src/
│   ├── Shared/
│   │   └── AtmNexus.Contracts/                  [C] DTOs, events, commands, signing
│   ├── Agent/
│   │   ├── AtmNexus.Devices.Abstractions/       [C] IATMDevice & co, cash calculator, ATR/Track2 parsers
│   │   ├── AtmNexus.Devices.Xfs/                [C] XFS 3.x + XFS4IoT, service manager, event processing, device services
│   │   ├── AtmNexus.Agent.Core/                 [C] workers, health, cache, EJ, ProBase, commands, composition
│   │   └── AtmNexus.Agent/                      [C] Windows service host (Win32 platform impls, mTLS token client)
│   ├── Simulator/
│   │   └── AtmNexus.Simulator/                  [C] mock XFS, scenarios, EJ generator, XFS4IoT mock, CLI
│   ├── Services/
│   │   ├── AtmNexus.Services.Device/            [C]
│   │   ├── AtmNexus.Services.Monitoring/        [P]
│   │   ├── AtmNexus.Services.Incident/          [P]
│   │   ├── AtmNexus.Services.Alert/             [P]
│   │   ├── AtmNexus.Services.Deployment/        [P]
│   │   ├── AtmNexus.Services.Reporting/         [P]
│   │   ├── AtmNexus.Services.Notification/      [P]
│   │   ├── AtmNexus.Services.Audit/             [P]
│   │   └── AtmNexus.Services.Auth/              [P]
│   ├── ApiGateway/AtmNexus.ApiGateway/          [P] YARP
│   └── WebPortal/
│       ├── AtmNexus.WebPortal/                  [P] Blazor Web App (server)
│       └── AtmNexus.WebPortal.Client/           [P] WASM components (admin)
├── tests/
│   ├── AtmNexus.Tests/                          [C] 141 test cases (102 facts + 39 theory rows)
│   ├── AtmNexus.Services.IntegrationTests/      [P] Testcontainers (PostgreSQL + Kafka)
│   └── AtmNexus.RegressionTests/                [P] SIT contract + simulator-fleet smoke
├── deployment/
│   ├── docker/      Dockerfile.service.template, Dockerfile.simulator, build-image.sh, docker-compose.dev.yml     [C]
│   ├── k8s/         device-service.yaml (reference manifest)                                                      [C]
│   ├── helm/        atmnexus-service chart, values/<service>.yaml x9, env/values-{dev,sit,uat,prod}.yaml          [C]
│   ├── kafka/       topics.sh                                                                                     [C]
│   ├── elastic/     atm-ej-template.json, atm-ej-ilm-policy.json                                                  [C]
│   ├── observability/ otel-collector.yaml, prometheus.yml, prometheus-rules.yaml                                  [C]
│   ├── pipelines/   deploy.yml (template), canary-gate.sh                                                         [C]
│   └── agent/       install-agent.ps1                                                                             [C]
└── docs/            01..14, api/openapi.yaml, architecture/workspace.dsl
```

## Project reference graph (no cycles; arrows = "depends on")

```
Agent ──► Agent.Core ──► Devices.Xfs ──► Devices.Abstractions ──► Contracts
                    └──────────────────────────────────────────────► Contracts
Simulator ──► Agent.Core
Services.Device ──► Contracts
Tests ──► (all of the above)
```
Rules: `Contracts` has **no** dependencies; `Devices.Abstractions` depends only on `Contracts`; services never reference the Agent; the Agent never references services (they only share `Contracts`).

## Per-project detail

### `AtmNexus.Contracts` (net9.0 library)
**Purpose**: wire contracts shared by agent and platform: enums (`DeviceClass`, `DeviceState`, `HealthLevel`, `AlertSeverity`, `RemoteCommandType`), telemetry (`AtmTelemetry`, `DeviceSnapshot`, `CashUnitSnapshot`, `AgentEvent`), Kafka events + `Topics` + `EventEnvelope<T>`, signed command model + `CommandCrypto`, `ContractJson`.
**Depends on**: nothing. **NuGet**: none (BCL only: `System.Text.Json`, `System.Security.Cryptography`).

### `AtmNexus.Devices.Abstractions` (library)
**Purpose**: vendor-neutral device model: `IATMDevice`, `ICardReader`, `ICashDispenser`, `ICashRecycler`, `IPinPad`, `IPrinter`, `ISensor`, `IDepository`, marker interfaces, plugin contracts (`IDeviceAdapterFactory`, `IDeviceManager`), DTOs, `CashCalculator`, `AtrParser`, `Track2Parser`, `SensitiveBytes`, `DeviceAttributes`.
**Depends on**: Contracts. **NuGet**: none.

### `AtmNexus.Devices.Xfs` (library)
**Purpose**: XFS integration: `IXfsApi` seam, `XfsServiceManager`, `Win32XfsApi` (P/Invoke + message pump + marshaller), event catalog/handlers/processor, device services (card reader, cash dispenser bridge, printer, PIN pad, SIU), XFS4IoT client/backend, adapter factories.
**Depends on**: Devices.Abstractions. **NuGet**: `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.DependencyInjection.Abstractions`.

### `AtmNexus.Agent.Core` (library)
**Purpose**: everything the agent does that is not Windows-specific: workers (poller, event listener, alarm pipeline, health, service monitor, Windows events, inventory, uploader), `DeviceManager`, `StatusReconciler`, `HealthScoreEngine`, `AgentStateStore`, SQLite cache (outbox/checkpoints/nonces), EJ framework, ProBase integration, remote-command framework, platform abstractions, `AddNexusAgentCore`.
**Depends on**: Contracts, Devices.Abstractions, Devices.Xfs. **NuGet**: `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.Http`, `Microsoft.Extensions.Options`, `Microsoft.Extensions.Options.ConfigurationExtensions`, `Microsoft.Data.Sqlite`.

### `AtmNexus.Agent` (Worker Service exe, `win-x86`)
**Purpose**: production host: Windows service lifetime, Win32 implementations (SCM, event log, registry/WMI inventory, process runner, TCP probe, XFS registry discovery, mTLS HTTP + token provider, sanitising log bundler), `appsettings.json`.
**Depends on**: Agent.Core. **NuGet**: `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Hosting.WindowsServices`, `Microsoft.Extensions.Logging.EventLog`, `System.ServiceProcess.ServiceController`, `System.Diagnostics.EventLog`, `System.Management`, `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.Runtime`.

### `AtmNexus.Simulator` (console exe)
**Purpose**: ATM simulator + mock XFS/XFS4IoT; reuses `AddNexusAgentCore`.
**Depends on**: Agent.Core. **NuGet**: `Microsoft.Extensions.Hosting`.

### `AtmNexus.Services.Device` (ASP.NET Core web)
**Purpose**: ATM master/live state, ingest, offline detection, digital twin, command queue/signing, dashboard, outbox relay.
**Depends on**: Contracts. **NuGet**: `Npgsql`, `Dapper`, `Confluent.Kafka`, `Microsoft.AspNetCore.Authentication.JwtBearer`, `AspNetCore.HealthChecks.NpgSql`, `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.AspNetCore|Http|Runtime`.

### Planned services (same template; `docs/03-microservices.md`)
Each = ASP.NET Core web project + `Contracts` + `Npgsql`/`Dapper` (or EF Core for admin-style CRUD in Auth/Notification) + `Confluent.Kafka` + JwtBearer + OTel + health checks. Shared infrastructure that is duplicated across services in the first cut (outbox relay, `Sql` helpers, `AuditWriter`, problem-details handler, OTel/auth wiring) should be extracted to `AtmNexus.Services.Common` **when the second service is written** (not before: the second use proves the abstraction).

### `AtmNexus.ApiGateway` [P]
YARP reverse proxy: routes `/api/agent/**` (mTLS/JWT), `/api/**` (user JWT), rate limit policies, request size limits, JWT pre-validation, health aggregation. **NuGet**: `Yarp.ReverseProxy`, `Microsoft.AspNetCore.Authentication.JwtBearer`, OTel.

### `AtmNexus.WebPortal` [P]
Blazor Web App. **NuGet**: `Microsoft.AspNetCore.Authentication.OpenIdConnect`, `Microsoft.AspNetCore.SignalR.Client`, `Microsoft.FluentUI.AspNetCore.Components` (or `MudBlazor`), `NSwag.MSBuild` (typed client from `openapi.yaml`), `Polly`/`Microsoft.Extensions.Http.Resilience`, OTel.

### `AtmNexus.Tests` (xUnit)
**Purpose**: unit, component and hosted end-to-end tests (see `docs/09-devops.md` 6).
**Depends on**: all [C] src projects. **NuGet**: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `coverlet.collector`, `Microsoft.Extensions.TimeProvider.Testing`, `Microsoft.Extensions.Hosting`.

## Conventions
* One central place for versions (`Directory.Packages.props`); `dotnet list package --vulnerable` in CI.
* Namespaces mirror folders; file-scoped namespaces; nullable enabled; implicit usings + per-project global usings in the csproj.
* Public API surface of `Contracts` is a **compatibility boundary** (agents lag servers by up to two releases): add members, never rename or remove; obsolete for >= 2 releases first.
* Anything vendor-specific is configuration or an adapter plugin, never an `if (vendor == ...)` in shared code.
