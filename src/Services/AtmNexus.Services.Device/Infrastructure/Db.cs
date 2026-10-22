using System.Text.Json;
using System.Text.RegularExpressions;

namespace AtmNexus.Services.Device.Infrastructure;

public static partial class DbNames
{
    /// <summary>CashDispenser -> CASH_DISPENSER, HardwareError -> HARDWARE_ERROR</summary>
    public static string Snake(string pascal) => Camel().Replace(pascal, "$1_$2").ToUpperInvariant();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex Camel();
}

public static class Sql
{
    public static CommandDefinition Cmd(string sql, object? p, NpgsqlTransaction? tx, CancellationToken ct) =>
        new(sql, p, tx, cancellationToken: ct);

    public static string Json(object? value) => JsonSerializer.Serialize(value, ContractJson.Options);

    /// <summary>Writes an event in the SAME transaction as the state change (transactional outbox).</summary>
    public static Task EnqueueEventAsync<T>(NpgsqlConnection conn, NpgsqlTransaction tx, string topic, Guid atmId, string eventType,
        T payload, TimeProvider clock, CancellationToken ct, string? correlationId = null)
    {
        // the W3C trace id of the request that caused the change travels with the event so consumer spans link to the producer
        correlationId ??= System.Diagnostics.Activity.Current?.TraceId.ToString();
        var envelope = new EventEnvelope<T>(Guid.NewGuid(), eventType, 1, clock.GetUtcNow(), atmId, correlationId, payload);
        return conn.ExecuteAsync(Cmd(
            "INSERT INTO outbox_event(topic, event_key, payload) VALUES (@topic, @key, @payload::jsonb)",
            new { topic, key = atmId.ToString(), payload = Json(envelope) }, tx, ct));
    }
}
