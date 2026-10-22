# 05 - Event-driven architecture (Kafka)

Create topics: `BOOTSTRAP=... deployment/kafka/topics.sh`. C# contracts: `src/Shared/AtmNexus.Contracts/Events.cs` (`Topics`, `EventEnvelope<T>`, payload records). Serialisation: `ContractJson.Options` (camelCase, enums as strings, ISO-8601, nulls omitted).

## Conventions

| Item | Rule |
|---|---|
| Key | `atmId` (uuid string) -> all events of one ATM are ordered within a partition |
| Value | JSON `EventEnvelope<T>` (below) |
| Headers | `traceparent` (W3C), `content-type: application/json`, `schema: atm.alert.created/1` |
| Producer | `acks=all`, `enable.idempotence=true`, `compression=zstd`, `linger.ms=5`, **only via the transactional outbox relay** |
| Consumer | manual commit after DB commit; idempotent on `eventId` (`processed_event` table); 3 retries (1 s/5 s/30 s) then `dlq.<group>` |
| Delivery | at-least-once; consumers must be idempotent |
| Evolution | schema registry (Apicurio/Confluent), compatibility **BACKWARD**: add optional fields only; breaking change = new topic version `atm.alert.created.v2` with dual-publish for one release |
| Retention | status 7 d, alerts 14 d, incidents/deployments 30 d (replay window), EJ 3 d (Elasticsearch is the store) |
| PII/CHD | none; EJ batches carry masked PAN only |

### Envelope schema

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://schemas.atmnexus.example/envelope/1.json",
  "type": "object",
  "required": ["eventId", "eventType", "schemaVersion", "occurredAt", "atmId", "payload"],
  "properties": {
    "eventId":       { "type": "string", "format": "uuid" },
    "eventType":     { "type": "string", "pattern": "^[a-z]+(\\.[a-z_]+)+$" },
    "schemaVersion": { "type": "integer", "minimum": 1 },
    "occurredAt":    { "type": "string", "format": "date-time" },
    "atmId":         { "type": "string", "format": "uuid" },
    "correlationId": { "type": ["string", "null"], "maxLength": 64 },
    "payload":       { "type": "object" }
  },
  "additionalProperties": false
}
```

### Partition sizing (50,000 ATMs)
Partitions >= max consumer parallelism and >= peak MB/s / 5 MB/s-per-partition. `atm.status.changed`: level changes are rare (~2/ATM/day = ~1.2 msg/s), but 48 partitions give headroom for 48 parallel consumers during a fleet-wide event and room to grow without re-keying. `atm.ej.batch`: 9 M tx/day in batches of <=500 -> small count but 5 MB messages -> 96 partitions for throughput.

---

## 1. `atm.status.changed`

| | |
|---|---|
| Producer | Device Service (`IngestTelemetryHandler`) when `health_level` changes (and on first telemetry) |
| Consumers | Monitoring (trend/AI), Reporting (read model), Notification (SignalR push), Deployment (wave health gate) |
| Partitions / key | 48 / atmId |

```json
{
  "$id": "https://schemas.atmnexus.example/atm.status.changed/1.json",
  "type": "object",
  "required": ["atmCode", "previousLevel", "currentLevel", "healthScore", "reasons"],
  "properties": {
    "atmCode":       { "type": "string" },
    "previousLevel": { "enum": ["Healthy", "Degraded", "Critical", "Down"] },
    "currentLevel":  { "enum": ["Healthy", "Degraded", "Critical", "Down"] },
    "healthScore":   { "type": "integer", "minimum": 0, "maximum": 100 },
    "reasons":       { "type": "array", "items": { "type": "string" }, "maxItems": 5 }
  }
}
```
```json
{
  "eventId": "6f2b9f4e-0c58-4f1b-9d5c-0f6d8e6b2a11",
  "eventType": "atm.status.changed",
  "schemaVersion": 1,
  "occurredAt": "2025-06-12T10:15:42.318Z",
  "atmId": "dddddddd-dddd-dddd-dddd-dddddddddddd",
  "correlationId": "0af7651916cd43dd8448eb211c80319c",
  "payload": {
    "atmCode": "ATM-000124",
    "previousLevel": "Healthy",
    "currentLevel": "Critical",
    "healthScore": 38,
    "reasons": ["IDC30 (CardReader) HardwareError [media=Jammed]", "Cash unit 2 (INR 2000) low: 90"]
  }
}
```

## 2. `atm.offline`

| | |
|---|---|
| Producer | Device Service `OfflineDetectorService` (exactly once per outage: the `UPDATE ... WHERE connectivity='ONLINE'` transition) |
| Consumers | Alert (correlation/storm control), Incident, Monitoring, Notification |
| Partitions / key | 24 / atmId |

```json
{
  "$id": "https://schemas.atmnexus.example/atm.offline/1.json",
  "type": "object",
  "required": ["atmCode", "lastSeenAt", "missedHeartbeats"],
  "properties": {
    "atmCode": { "type": "string" },
    "lastSeenAt": { "type": "string", "format": "date-time" },
    "missedHeartbeats": { "type": "integer", "minimum": 1 }
  }
}
```
```json
{
  "eventId": "b1c7a0d2-93a8-4a63-86a0-5a8c1b5b4e90",
  "eventType": "atm.offline",
  "schemaVersion": 1,
  "occurredAt": "2025-06-12T10:18:30.002Z",
  "atmId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
  "correlationId": null,
  "payload": { "atmCode": "ATM-000123", "lastSeenAt": "2025-06-12T10:15:12Z", "missedHeartbeats": 3 }
}
```

## 3. `atm.alert.created`

| | |
|---|---|
| Producer | Device Service when a **new** active fingerprint is inserted (re-raises only bump `occurrences`, no event). After the Alert Service split: Alert Service. |
| Consumers | Alert (rules/escalation), Incident (auto-create), Notification, Monitoring (AI classification features), Reporting |
| Partitions / key | 48 / atmId |

```json
{
  "$id": "https://schemas.atmnexus.example/atm.alert.created/1.json",
  "type": "object",
  "required": ["alertId", "atmCode", "code", "severity", "source", "message", "raisedAt"],
  "properties": {
    "alertId":  { "type": "string", "format": "uuid" },
    "atmCode":  { "type": "string" },
    "code":     { "type": "string", "examples": ["CASH_LOW", "CARD_JAM", "SAFE_DOOR_OPEN", "TAMPER_DETECTED", "SERVICE_NOT_RUNNING"] },
    "severity": { "enum": ["Info", "Warning", "Major", "Critical"] },
    "source":   { "type": "string", "description": "XFS logical name or subsystem (services, windows-events, probase, health)" },
    "message":  { "type": "string", "maxLength": 1000 },
    "raisedAt": { "type": "string", "format": "date-time" },
    "data":     { "type": ["object", "null"], "additionalProperties": { "type": "string" } }
  }
}
```
```json
{
  "eventId": "8e5a6c8b-8f0e-4b27-a8f4-2a3f6a6b1d2c",
  "eventType": "atm.alert.created",
  "schemaVersion": 1,
  "occurredAt": "2025-06-12T10:15:40.771Z",
  "atmId": "dddddddd-dddd-dddd-dddd-dddddddddddd",
  "correlationId": "0af7651916cd43dd8448eb211c80319c",
  "payload": {
    "alertId": "3a0c5b1e-6a2d-4f2a-9f60-3b0f2c9b9a10",
    "atmCode": "ATM-000124",
    "code": "CASH_LOW",
    "severity": "Major",
    "source": "CashDispenser",
    "message": "Cash unit 2 (USD 50) low: 90 notes",
    "raisedAt": "2025-06-12T10:15:38.120Z",
    "data": { "device": "CashDispenser", "deviceClass": "CashDispenser", "unit": "2", "currency": "USD", "denomination": "50", "count": "90", "status": "Low", "xfs.event": "WFS_USRE_CDM_CASHUNITTHRESHOLD" }
  }
}
```

## 4. `atm.incident.created`

| | |
|---|---|
| Producer | Incident Service |
| Consumers | Notification, Reporting, Monitoring (labels for AI), ITSM connector, Deployment (pause rollouts on fleet incident) |
| Partitions / key | 12 / atmId |

```json
{
  "$id": "https://schemas.atmnexus.example/atm.incident.created/1.json",
  "type": "object",
  "required": ["incidentId", "number", "atmCode", "title", "priority", "category", "createdAt"],
  "properties": {
    "incidentId": { "type": "string", "format": "uuid" },
    "number":     { "type": "string", "pattern": "^INC-\\d{4}-\\d{6}$" },
    "atmCode":    { "type": "string" },
    "title":      { "type": "string", "maxLength": 200 },
    "priority":   { "enum": ["P1", "P2", "P3", "P4"] },
    "category":   { "enum": ["HARDWARE", "CASH", "SOFTWARE", "NETWORK", "SECURITY", "OTHER"] },
    "sourceAlertId": { "type": ["string", "null"], "format": "uuid" },
    "assignedGroup": { "type": ["string", "null"] },
    "createdAt":  { "type": "string", "format": "date-time" }
  }
}
```
```json
{
  "eventId": "1d0f6f5e-3c0a-4c53-bf8a-9e3a1f2a7b55",
  "eventType": "atm.incident.created",
  "schemaVersion": 1,
  "occurredAt": "2025-06-12T10:16:02.004Z",
  "atmId": "dddddddd-dddd-dddd-dddd-dddddddddddd",
  "correlationId": "0af7651916cd43dd8448eb211c80319c",
  "payload": {
    "incidentId": "7c4b3a8e-2f1d-4c6a-8f0e-5d2b1a9c3e77",
    "number": "INC-2025-000123",
    "atmCode": "ATM-000124",
    "title": "Card jammed in reader - ATM-000124 (Airport T1)",
    "priority": "P2",
    "category": "HARDWARE",
    "sourceAlertId": "3a0c5b1e-6a2d-4f2a-9f60-3b0f2c9b9a10",
    "assignedGroup": "FIELD-SOUTH-ATM",
    "createdAt": "2025-06-12T10:16:02Z"
  }
}
```

## 5. `deployment.started`

| | |
|---|---|
| Producer | Deployment Service (per ATM when the install command is queued) |
| Consumers | Notification, Reporting, Audit, Monitoring (suppress expected restarts/health dips during the window) |
| Partitions / key | 12 / atmId |

```json
{
  "$id": "https://schemas.atmnexus.example/deployment.started/1.json",
  "type": "object",
  "required": ["deploymentId", "packageId", "packageVersion", "atmCode", "strategy", "startedBy", "startedAt"],
  "properties": {
    "deploymentId":   { "type": "string", "format": "uuid" },
    "packageId":      { "type": "string", "format": "uuid" },
    "packageVersion": { "type": "string" },
    "atmCode":        { "type": "string" },
    "strategy":       { "enum": ["IMMEDIATE", "MAINTENANCE_WINDOW", "CANARY"] },
    "startedBy":      { "type": "string" },
    "startedAt":      { "type": "string", "format": "date-time" }
  }
}
```
```json
{
  "eventId": "a41c4a76-1a8a-4b0f-8e1b-7a0d6b8c5e12",
  "eventType": "deployment.started",
  "schemaVersion": 1,
  "occurredAt": "2025-06-14T01:00:03.512Z",
  "atmId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
  "correlationId": "rollout-2025-06-14-agent-0.2.0",
  "payload": {
    "deploymentId": "f2f9c3b0-5b5e-4c7e-9b0d-2c1e8a7d6f45",
    "packageId": "9b8d7c6e-1a2b-4c3d-8e9f-0a1b2c3d4e5f",
    "packageVersion": "0.2.0",
    "atmCode": "ATM-000123",
    "strategy": "CANARY",
    "startedBy": "release.manager1",
    "startedAt": "2025-06-14T01:00:03Z"
  }
}
```

## 6. `deployment.completed`

| | |
|---|---|
| Producer | Deployment Service (on agent result or timeout) |
| Consumers | Device (update agent/app version), Notification, Reporting, Monitoring, Deployment (wave gate) |
| Partitions / key | 12 / atmId |

```json
{
  "$id": "https://schemas.atmnexus.example/deployment.completed/1.json",
  "type": "object",
  "required": ["deploymentId", "atmCode", "result", "durationSeconds", "completedAt"],
  "properties": {
    "deploymentId":    { "type": "string", "format": "uuid" },
    "atmCode":         { "type": "string" },
    "result":          { "enum": ["SUCCEEDED", "FAILED", "ROLLED_BACK", "CANCELLED"] },
    "durationSeconds": { "type": "integer", "minimum": 0 },
    "failureReason":   { "type": ["string", "null"], "maxLength": 1000 },
    "completedAt":     { "type": "string", "format": "date-time" }
  }
}
```
```json
{
  "eventId": "0d3e9a1b-7c2f-4a5d-b6e8-9f0a1b2c3d4e",
  "eventType": "deployment.completed",
  "schemaVersion": 1,
  "occurredAt": "2025-06-14T01:12:41.900Z",
  "atmId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
  "correlationId": "rollout-2025-06-14-agent-0.2.0",
  "payload": {
    "deploymentId": "f2f9c3b0-5b5e-4c7e-9b0d-2c1e8a7d6f45",
    "atmCode": "ATM-000123",
    "result": "ROLLED_BACK",
    "durationSeconds": 758,
    "failureReason": "Health probe failed after install: XFS manager service not running for 90 s",
    "completedAt": "2025-06-14T01:12:41Z"
  }
}
```

## Additional topics (internal)

| Topic | Producer | Consumer | Purpose |
|---|---|---|---|
| `atm.ej.batch` | Device (agent intake) | Reporting EJ indexer | `{atmCode, transactions[]}` envelope payload; masked PAN only |
| `atm.inventory.reported` | Device | Reporting, Deployment (compatibility) | software + patches + compliance flag |
| `dlq.<group>` | any consumer | operations | poison messages with original headers + `x-exception` |

## Consumer skeleton (C#)

```csharp
// Infrastructure/Kafka/IdempotentConsumer.cs (template used by all services)
while (!ct.IsCancellationRequested)
{
    var cr = consumer.Consume(ct);
    var env = JsonSerializer.Deserialize<EventEnvelope<AlertCreated>>(cr.Message.Value, ContractJson.Options)!;
    for (int attempt = 1; ; attempt++)
    {
        try
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            // 1) idempotency: INSERT INTO processed_event(event_id) ... ON CONFLICT DO NOTHING; if 0 rows => already processed
            // 2) business logic + outbox rows
            await tx.CommitAsync(ct);
            consumer.Commit(cr);                       // commit offset AFTER the DB commit
            break;
        }
        catch (Exception ex) when (attempt < 3) { await Task.Delay(Backoff(attempt), ct); }
        catch (Exception ex) { await dlq.ProduceAsync($"dlq.{group}", cr.Message, ct); consumer.Commit(cr); break; }
    }
}
```
