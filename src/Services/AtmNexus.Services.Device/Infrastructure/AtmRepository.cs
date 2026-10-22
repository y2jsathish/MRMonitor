using System.Text;
using System.Text.Json;
using AtmNexus.Services.Device.Application;

namespace AtmNexus.Services.Device.Infrastructure;

public sealed class AtmRepository(NpgsqlDataSource db)
{
    private const string SummarySelect = """
        SELECT a.atm_id AS id, a.atm_code AS code, v.code AS vendor, a.model, b.code AS branch_code, b.name AS branch, l.city,
               COALESCE(s.health_score, 100)::int AS health_score, COALESCE(s.health_level, 'HEALTHY') AS health_level,
               COALESCE(s.connectivity, 'UNKNOWN') AS connectivity, s.last_seen_at, a.commission_status
        FROM atm a
        JOIN vendor v ON v.vendor_id = a.vendor_id
        LEFT JOIN branch b ON b.branch_id = a.branch_id
        LEFT JOIN location l ON l.location_id = a.location_id
        LEFT JOIN atm_status s ON s.atm_id = a.atm_id
        """;

    public async Task<PagedResult<AtmSummary>> ListAsync(ListAtmsQuery q, CancellationToken ct)
    {
        var where = new StringBuilder(" WHERE a.commission_status <> 'DECOMMISSIONED'");
        var p = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(q.Status)) { where.Append(" AND s.health_level = @status"); p.Add("status", q.Status.ToUpperInvariant()); }
        if (!string.IsNullOrWhiteSpace(q.Connectivity)) { where.Append(" AND s.connectivity = @conn"); p.Add("conn", q.Connectivity.ToUpperInvariant()); }
        if (!string.IsNullOrWhiteSpace(q.Branch)) { where.Append(" AND b.code = @branch"); p.Add("branch", q.Branch); }
        if (!string.IsNullOrWhiteSpace(q.Vendor)) { where.Append(" AND v.code = @vendor"); p.Add("vendor", q.Vendor.ToUpperInvariant()); }
        if (!string.IsNullOrWhiteSpace(q.Search)) { where.Append(" AND (a.atm_code ILIKE @search OR b.name ILIKE @search OR l.city ILIKE @search)"); p.Add("search", $"%{q.Search}%"); }

        int page = Math.Max(1, q.Page), size = Math.Clamp(q.PageSize, 1, 200);
        p.Add("limit", size);
        p.Add("offset", (page - 1) * size);

        await using var conn = await db.OpenConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<long>(Sql.Cmd(
            "SELECT count(*) FROM atm a JOIN vendor v ON v.vendor_id=a.vendor_id LEFT JOIN branch b ON b.branch_id=a.branch_id " +
            "LEFT JOIN location l ON l.location_id=a.location_id LEFT JOIN atm_status s ON s.atm_id=a.atm_id" + where, p, null, ct));
        var items = (await conn.QueryAsync<AtmSummary>(Sql.Cmd(
            SummarySelect + where + " ORDER BY s.health_score ASC NULLS LAST, a.atm_code LIMIT @limit OFFSET @offset", p, null, ct))).ToList();
        return new PagedResult<AtmSummary>(items, page, size, total);
    }

    public async Task<AtmDetail?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<AtmDetail>(Sql.Cmd("""
            SELECT a.atm_id AS id, a.atm_code AS code, v.code AS vendor, a.model, b.code AS branch_code, b.name AS branch, l.city,
                   COALESCE(s.health_score, 100)::int AS health_score, COALESCE(s.health_level, 'HEALTHY') AS health_level,
                   COALESCE(s.connectivity, 'UNKNOWN') AS connectivity, s.last_seen_at, a.commission_status,
                   a.serial_number, host(a.ip_address) AS ip_address, a.os_version, a.agent_version, a.xfs_version,
                   a.application_name, a.application_version, a.install_date::timestamp AS install_date, a.tags::text AS tags_json,
                   l.latitude::float8 AS latitude, l.longitude::float8 AS longitude
            FROM atm a
            JOIN vendor v ON v.vendor_id = a.vendor_id
            LEFT JOIN branch b ON b.branch_id = a.branch_id
            LEFT JOIN location l ON l.location_id = a.location_id
            LEFT JOIN atm_status s ON s.atm_id = a.atm_id
            WHERE a.atm_id = @id
            """, new { id }, null, ct));
    }

    public async Task<StatusRow?> GetStatusAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<StatusRow>(Sql.Cmd("""
            SELECT a.atm_id, a.atm_code, v.code AS vendor, a.model,
                   COALESCE(s.health_score, 100)::int AS health_score, COALESCE(s.health_level,'HEALTHY') AS health_level,
                   COALESCE(s.connectivity,'UNKNOWN') AS connectivity, COALESCE(s.in_service, true) AS in_service, s.last_seen_at,
                   COALESCE(s.components,'{}'::jsonb)::text AS components_json, COALESCE(s.reasons,'[]'::jsonb)::text AS reasons_json
            FROM atm a JOIN vendor v ON v.vendor_id = a.vendor_id LEFT JOIN atm_status s ON s.atm_id = a.atm_id
            WHERE a.atm_id = @id
            """, new { id }, null, ct));
    }

    public async Task<IReadOnlyList<ComponentRow>> GetComponentsAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<ComponentRow>(Sql.Cmd(
            "SELECT logical_name, device_class, state, vendor, model, firmware, attributes::text AS attributes_json, last_state_change_at " +
            "FROM atm_component WHERE atm_id = @id ORDER BY logical_name", new { id }, null, ct))).ToList();
    }

    public async Task<IReadOnlyList<CashUnitRow>> GetCashUnitsAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<CashUnitRow>(Sql.Cmd(
            "SELECT unit_number, unit_id, unit_type, currency::text AS currency, denomination, note_count, initial_count, minimum, maximum, status " +
            "FROM atm_cash_unit WHERE atm_id = @id ORDER BY unit_number", new { id }, null, ct))).ToList();
    }

    public async Task<IReadOnlyList<AlertRow>> GetOpenAlertsAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        return (await conn.QueryAsync<AlertRow>(Sql.Cmd(
            "SELECT alert_id, code, severity::int AS severity, source, message, state, data::text AS data_json, first_raised_at, occurrences " +
            "FROM atm_alert WHERE atm_id = @id AND state IN ('OPEN','ACKNOWLEDGED') ORDER BY severity DESC, first_raised_at", new { id }, null, ct))).ToList();
    }

    public async Task<DashboardDto> GetDashboardAsync(CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);

        var levels = (await conn.QueryAsync<(string Level, int Count)>(Sql.Cmd(
            "SELECT s.health_level AS level, count(*)::int AS count FROM atm a JOIN atm_status s ON s.atm_id=a.atm_id " +
            "WHERE a.commission_status='ACTIVE' GROUP BY s.health_level", null, null, ct))).ToDictionary(x => x.Level, x => x.Count);

        var offline = await conn.ExecuteScalarAsync<int>(Sql.Cmd(
            "SELECT count(*)::int FROM atm a JOIN atm_status s ON s.atm_id=a.atm_id WHERE a.commission_status='ACTIVE' AND s.connectivity='OFFLINE'", null, null, ct));

        var alerts = (await conn.QueryAsync<(int Severity, int Count)>(Sql.Cmd(
            "SELECT severity::int AS severity, count(*)::int AS count FROM atm_alert WHERE state IN ('OPEN','ACKNOWLEDGED') GROUP BY severity", null, null, ct)))
            .ToDictionary(x => x.Severity switch { 3 => "CRITICAL", 2 => "MAJOR", 1 => "WARNING", _ => "INFO" }, x => x.Count);

        var cash = await conn.QuerySingleAsync<(int Low, int Empty)>(Sql.Cmd(
            "SELECT count(*) FILTER (WHERE status='Low')::int AS low, count(*) FILTER (WHERE status='Empty')::int AS empty FROM atm_cash_unit", null, null, ct));

        var incidents = await conn.ExecuteScalarAsync<int>(Sql.Cmd(
            "SELECT count(*)::int FROM atm_incident WHERE state NOT IN ('RESOLVED','CLOSED')", null, null, ct));

        var worst = (await conn.QueryAsync<AtmSummary>(Sql.Cmd(
            SummarySelect + " WHERE a.commission_status='ACTIVE' ORDER BY s.health_score ASC NULLS LAST LIMIT 10", null, null, ct))).ToList();

        return new DashboardDto(levels.Values.Sum(), levels, offline, alerts, cash.Low, cash.Empty, incidents, worst, DateTime.UtcNow);
    }

    public static IReadOnlyDictionary<string, string> ParseAttributes(string json) =>
        (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new())
            .ToDictionary(k => k.Key, k => k.Value.ValueKind == JsonValueKind.String ? k.Value.GetString() ?? "" : k.Value.ToString());
}
