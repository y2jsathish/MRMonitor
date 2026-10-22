using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AtmNexus.Services.Device.Infrastructure;

public sealed record IngestResult(int EventsAccepted, int AlertsCreated, bool StatusChanged);

/// <summary>
/// Command handler for POST /api/agent/telemetry. One database transaction per message: status upsert, components, cash units,
/// alerts (de-duplicated by fingerprint) and the outbox events that describe the change. Idempotent: replays after a network
/// failure only bump counters.
/// </summary>
public sealed class IngestTelemetryHandler(NpgsqlDataSource db, TimeProvider clock, ILogger<IngestTelemetryHandler> log)
{
    public static readonly ActivitySource Source = new("AtmNexus.Device");
    private static readonly Meter Meter = new("AtmNexus.Device");
    private static readonly Counter<long> Messages = Meter.CreateCounter<long>("atm.telemetry.messages");
    private static readonly Counter<long> AlertsCreatedCounter = Meter.CreateCounter<long>("atm.alerts.created");

    /// <summary>Alert code that, when raised, clears the matching "opening" alert on the same source.</summary>
    private static readonly Dictionary<string, string[]> ClearedBy = new()
    {
        ["SAFE_DOOR_CLOSED"] = ["SAFE_DOOR_OPEN"],
        ["CABINET_DOOR_CLOSED"] = ["CABINET_DOOR_OPEN"],
        ["CARD_TAKEN"] = ["CARD_INSERTED"],
    };

    public async Task<IngestResult> HandleAsync(AtmTelemetry t, string authenticatedAtmCode, CancellationToken ct)
    {
        if (!string.Equals(t.AtmCode, authenticatedAtmCode, StringComparison.OrdinalIgnoreCase))
            throw new Application.ForbiddenException("Agents may only report telemetry for their own ATM.");
        if (t.HealthScore is < 0 or > 100) throw new Application.ValidationFailedException("healthScore must be 0-100.");
        if (t.Events.Count > 500) throw new Application.ValidationFailedException("At most 500 events per message.");

        using var activity = Source.StartActivity("telemetry.ingest");
        activity?.SetTag("atm.code", t.AtmCode);
        var now = clock.GetUtcNow();

        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        var atmId = await conn.ExecuteScalarAsync<Guid?>(Sql.Cmd(
            "SELECT atm_id FROM atm WHERE atm_code = @code AND commission_status <> 'DECOMMISSIONED'", new { code = t.AtmCode }, tx, ct))
            ?? throw new Application.NotFoundException($"ATM '{t.AtmCode}' is not registered.");

        // ---- status (row lock serialises concurrent messages of the same ATM) ----
        var prev = await conn.QuerySingleOrDefaultAsync<PreviousStatus>(Sql.Cmd(
            "SELECT health_level AS level, connectivity FROM atm_status WHERE atm_id = @atmId FOR UPDATE", new { atmId }, tx, ct));
        string level = DbNames.Snake(t.Level.ToString());
        bool levelChanged = prev is null || prev.Level != level;

        await conn.ExecuteAsync(Sql.Cmd("""
            INSERT INTO atm_status (atm_id, health_score, health_level, connectivity, in_service, last_seen_at, last_state_change_at, components, reasons, updated_at)
            VALUES (@atmId, @score, @level, 'ONLINE', @inService, @now, @now, @components::jsonb, @reasons::jsonb, @now)
            ON CONFLICT (atm_id) DO UPDATE SET
              health_score = EXCLUDED.health_score, health_level = EXCLUDED.health_level, connectivity = 'ONLINE',
              in_service = EXCLUDED.in_service, last_seen_at = EXCLUDED.last_seen_at,
              last_state_change_at = CASE WHEN atm_status.health_level <> EXCLUDED.health_level THEN EXCLUDED.last_state_change_at ELSE atm_status.last_state_change_at END,
              components = EXCLUDED.components, reasons = EXCLUDED.reasons, updated_at = EXCLUDED.updated_at
            """,
            new
            {
                atmId, score = t.HealthScore, level, inService = true, now = now.UtcDateTime,
                components = Sql.Json(t.Metrics.Where(m => m.Key.StartsWith("health.", StringComparison.Ordinal))
                    .ToDictionary(m => m.Key[7..], m => (int)m.Value)),
                reasons = Sql.Json(t.Events.Where(e => e.Severity >= AlertSeverity.Major).Select(e => e.Message).Take(10))
            }, tx, ct));

        // History is sampled (level change, or at most one point per 15 min): 50k ATMs x 1 msg/min would otherwise be ~72M rows/day.
        await conn.ExecuteAsync(Sql.Cmd("""
            INSERT INTO atm_status_history (atm_id, recorded_at, health_score, health_level, connectivity)
            SELECT @atmId::uuid, @now::timestamptz, @score::smallint, @level::varchar, 'ONLINE'
            WHERE @levelChanged::boolean OR NOT EXISTS (
                SELECT 1 FROM atm_status_history h WHERE h.atm_id = @atmId::uuid AND h.recorded_at > @now::timestamptz - interval '15 minutes')
            ON CONFLICT DO NOTHING
            """, new { atmId, now = now.UtcDateTime, score = t.HealthScore, level, levelChanged }, tx, ct));

        await conn.ExecuteAsync(Sql.Cmd("UPDATE atm SET agent_version = @v, updated_at = now() WHERE atm_id = @atmId AND agent_version IS DISTINCT FROM @v",
            new { atmId, v = t.AgentVersion }, tx, ct));

        if (levelChanged)
        {
            await Sql.EnqueueEventAsync(conn, tx, Topics.AtmStatusChanged, atmId, "atm.status.changed", new AtmStatusChanged(
                t.AtmCode, ParseLevel(prev?.Level), t.Level, t.HealthScore,
                t.Events.Where(e => e.Severity >= AlertSeverity.Major).Select(e => e.Message).Take(5).ToList()), clock, ct);
        }

        // ---- components & cash units ----
        foreach (var d in t.Devices)
        {
            await conn.ExecuteAsync(Sql.Cmd("""
                INSERT INTO atm_component (atm_id, logical_name, device_class, state, vendor, model, firmware, attributes, last_state_change_at, updated_at)
                VALUES (@atmId, @name, @cls, @state, @vendor, @model, @fw, @attrs::jsonb, @now, @now)
                ON CONFLICT (atm_id, logical_name) DO UPDATE SET
                  device_class = EXCLUDED.device_class, state = EXCLUDED.state, attributes = EXCLUDED.attributes, updated_at = EXCLUDED.updated_at,
                  vendor = COALESCE(EXCLUDED.vendor, atm_component.vendor), model = COALESCE(EXCLUDED.model, atm_component.model),
                  firmware = COALESCE(EXCLUDED.firmware, atm_component.firmware),
                  last_state_change_at = CASE WHEN atm_component.state <> EXCLUDED.state THEN EXCLUDED.updated_at ELSE atm_component.last_state_change_at END
                """,
                new
                {
                    atmId, name = d.LogicalName, cls = DbNames.Snake(d.Class.ToString()), state = DbNames.Snake(d.State.ToString()),
                    vendor = d.Vendor, model = d.Model, fw = d.Firmware, attrs = Sql.Json(d.Attributes), now = now.UtcDateTime
                }, tx, ct));
        }

        foreach (var u in t.CashUnits)
        {
            await conn.ExecuteAsync(Sql.Cmd("""
                INSERT INTO atm_cash_unit (atm_id, unit_number, unit_id, unit_type, currency, denomination, note_count, initial_count, reject_count, minimum, maximum, status, updated_at)
                VALUES (@atmId, @n, @id, @type, NULLIF(@cur,''), @den, @cnt, @init, @rej, @min, @max, @status, @now)
                ON CONFLICT (atm_id, unit_number) DO UPDATE SET
                  unit_id = EXCLUDED.unit_id, unit_type = EXCLUDED.unit_type, currency = EXCLUDED.currency, denomination = EXCLUDED.denomination,
                  note_count = EXCLUDED.note_count, initial_count = EXCLUDED.initial_count, reject_count = EXCLUDED.reject_count,
                  minimum = EXCLUDED.minimum, maximum = EXCLUDED.maximum, status = EXCLUDED.status, updated_at = EXCLUDED.updated_at
                """,
                new
                {
                    atmId, n = (short)u.Number, id = u.UnitId, type = u.Type, cur = u.Currency, den = u.Denomination, cnt = u.Count, init = u.InitialCount,
                    rej = u.RejectCount, min = u.Minimum, max = u.Maximum, status = u.Status, now = now.UtcDateTime
                }, tx, ct));

            // a refilled unit clears its CASH_LOW / CASH_EMPTY alerts
            if (u.Status is "Ok" or "High" or "Full" && u.Count > u.Minimum)
                await conn.ExecuteAsync(Sql.Cmd("""
                    UPDATE atm_alert SET state = 'CLEARED', cleared_at = @now
                    WHERE atm_id = @atmId AND state IN ('OPEN','ACKNOWLEDGED') AND code IN ('CASH_LOW','CASH_EMPTY') AND data->>'unit' = @unit
                    """, new { atmId, unit = u.Number.ToString(), now = now.UtcDateTime }, tx, ct));
        }

        // ---- device recovery auto-clear (state no longer matches the fault) ----
        foreach (var d in t.Devices)
        {
            string A(string k) => d.Attributes.GetValueOrDefault(k, "").ToLowerInvariant();
            var recovered = new List<string>();
            if (A("media") != "jammed") recovered.Add("CARD_JAM");
            if (A("paper") is "full") { recovered.Add("PAPER_LOW"); recovered.Add("PAPER_EMPTY"); recovered.Add("PAPER_JAM"); }
            if (A("safedoor") == "closed") recovered.Add("SAFE_DOOR_OPEN");
            if (d.State == DeviceState.Online) recovered.Add("HARDWARE_ERROR");
            if (recovered.Count > 0)
                await conn.ExecuteAsync(Sql.Cmd("""
                    UPDATE atm_alert SET state = 'CLEARED', cleared_at = @now
                    WHERE atm_id = @atmId AND state IN ('OPEN','ACKNOWLEDGED') AND source = @src AND code = ANY(@codes)
                    """, new { atmId, src = d.LogicalName, codes = recovered.ToArray(), now = now.UtcDateTime }, tx, ct));
        }

        // ---- events -> alerts ----
        int created = 0;
        foreach (var e in t.Events)
        {
            if (ClearedBy.TryGetValue(e.Code, out var clears))
            {
                await conn.ExecuteAsync(Sql.Cmd(
                    "UPDATE atm_alert SET state='CLEARED', cleared_at=@now WHERE atm_id=@atmId AND state IN ('OPEN','ACKNOWLEDGED') AND source=@src AND code = ANY(@codes)",
                    new { atmId, src = e.Source, codes = clears, now = now.UtcDateTime }, tx, ct));
                continue;
            }
            if (e.Severity < AlertSeverity.Warning) continue;

            string fingerprint = $"{e.Code}|{e.Source}|{e.Data?.GetValueOrDefault("unit")}|{e.Data?.GetValueOrDefault("service")}";
            var row = await conn.QuerySingleAsync<(Guid AlertId, bool Inserted)>(Sql.Cmd("""
                INSERT INTO atm_alert (alert_id, atm_id, code, severity, source, message, data, fingerprint, first_raised_at, last_raised_at)
                VALUES (@alertId, @atmId, @code, @sev, @src, @msg, @data::jsonb, @fp, @at, @at)
                ON CONFLICT (atm_id, fingerprint) WHERE state IN ('OPEN','ACKNOWLEDGED') DO UPDATE SET
                  last_raised_at = EXCLUDED.last_raised_at, occurrences = atm_alert.occurrences + 1,
                  severity = GREATEST(atm_alert.severity, EXCLUDED.severity), message = EXCLUDED.message
                RETURNING alert_id, (xmax = 0) AS inserted
                """,
                new
                {
                    alertId = e.EventId, atmId, code = e.Code, sev = (short)e.Severity, src = Trunc(e.Source, 64), msg = Trunc(e.Message, 1000),
                    data = Sql.Json(e.Data ?? new Dictionary<string, string>()), fp = Trunc(fingerprint, 160), at = e.At.UtcDateTime
                }, tx, ct));

            if (row.Inserted)
            {
                created++;
                await Sql.EnqueueEventAsync(conn, tx, Topics.AlertCreated, atmId, "atm.alert.created",
                    new AlertCreated(row.AlertId, t.AtmCode, e.Code, e.Severity, e.Source, e.Message, e.At, e.Data), clock, ct);
            }
        }

        await tx.CommitAsync(ct);
        Messages.Add(1);
        if (created > 0) AlertsCreatedCounter.Add(created);
        log.LogDebug("Telemetry {AtmCode}: score={Score} level={Level} events={Events} newAlerts={Created}", t.AtmCode, t.HealthScore, t.Level, t.Events.Count, created);
        return new IngestResult(t.Events.Count, created, levelChanged);
    }

    private sealed class PreviousStatus
    {
        public string Level { get; set; } = "";
        public string Connectivity { get; set; } = "";
    }

    private static string Trunc(string s, int max) => s.Length <= max ? s : s[..max];

    private static HealthLevel ParseLevel(string? dbLevel) =>
        dbLevel is null ? HealthLevel.Healthy : Enum.TryParse<HealthLevel>(dbLevel, ignoreCase: true, out var l) ? l : HealthLevel.Healthy;
}
