# Documentation index

| # | Document | Read when you are... |
|---|---|---|
| 01 | [Architecture (C4, deployment, sequences, capacity)](01-architecture.md) | designing / reviewing the system |
| 02 | [MVP modules](02-mvp-modules.md) | planning Phase 1 backlog |
| 03 | [Microservices](03-microservices.md) | starting a new service |
| 04 | [Database tables (generated)](04-database-tables.md) · [`V001__core_schema.sql`](../db/migrations/V001__core_schema.sql) | writing queries / migrations |
| 05 | [Kafka events](05-events-kafka.md) | producing or consuming events |
| - | [OpenAPI](api/openapi.yaml) | building clients / contract tests |
| 07 | [Security](07-security.md) | security review, PCI |
| 08 | [UI design](08-ui.md) | building the Blazor portal |
| 09 | [DevOps](09-devops.md) | CI/CD, environments, releases |
| 10 | [Observability](10-observability.md) | on-call, dashboards |
| 11 | [AI](11-ai.md) | Phase 3 |
| 12 | [Repository structure](12-repo-structure.md) | navigating the code |
| 13 | [Roadmap](13-roadmap.md) | planning, estimating |
| 14 | [ATM / XFS integration](14-xfs-integration.md) | agent, devices, vendors, simulator |
| - | [C4 workspace (Structurizr DSL)](architecture/workspace.dsl) | rendering diagrams |

Suggested reading order for a new engineer: 01 -> 14 -> 12 -> 02 -> 05 -> 07.
Doc 06 does not exist: the numbering skips it because the OpenAPI file replaced the planned `06-api.md`.
