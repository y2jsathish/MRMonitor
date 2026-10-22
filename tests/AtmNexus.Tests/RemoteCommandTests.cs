using System.Security.Cryptography;
using AtmNexus.Agent;
using AtmNexus.Agent.Cache;
using AtmNexus.Agent.Commands;
using AtmNexus.Agent.Platform;
using AtmNexus.Simulator;
using Microsoft.Extensions.Time.Testing;

namespace AtmNexus.Tests;

public sealed class RemoteCommandTests : IDisposable
{
    private const string Atm = "ATM-000123";
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2025, 6, 12, 10, 0, 0, TimeSpan.Zero));
    private readonly SqliteAgentCache _cache;
    private readonly FakeServiceController _scm = new();
    private readonly FakeProcessRunner _proc = new();
    private readonly SettableIdleProbe _idle = new();
    private readonly CommandPolicy _policy;
    private readonly RemoteCommandDispatcher _dispatcher;

    public RemoteCommandTests()
    {
        _cache = new SqliteAgentCache(":memory:", _clock);
        _policy = new CommandPolicy
        {
            AtmCode = Atm,
            TrustedKeys = { ["k1"] = Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()) },
            AllowedServices = { "Spooler" },
            AtmApplicationService = "AtmApp",
            XfsServiceName = "XfsMgr"
        };
        var opts = Options.Create(_policy);
        IRemoteCommandHandler[] handlers =
        [
            new RestartWindowsServiceHandler(_scm, _idle, opts), new RestartApplicationHandler(_scm, _idle, opts),
            new RestartXfsHandler(_scm, _idle, opts), new RebootHandler(_proc, _idle), new ShutdownHandler(_proc, _idle)
        ];
        _dispatcher = new RemoteCommandDispatcher(opts, _cache, handlers, _clock, NullLogger<RemoteCommandDispatcher>.Instance);
    }

    private SignedRemoteCommand Sign(RemoteCommandType type, Dictionary<string, string>? p = null, string? approvedBy = null,
        string atm = Atm, string issuedBy = "alice", TimeSpan? lifetime = null, string? nonce = null, string keyId = "k1")
    {
        var now = _clock.GetUtcNow();
        var cmd = new RemoteCommand(Guid.NewGuid(), atm, type, p ?? new(), issuedBy, approvedBy, now, now + (lifetime ?? TimeSpan.FromMinutes(15)),
            nonce ?? Guid.NewGuid().ToString("N"));
        return CommandCrypto.Sign(cmd, _key, keyId);
    }

    [Fact]
    public async Task ValidSignedCommand_Executes()
    {
        var r = await _dispatcher.ExecuteAsync(Sign(RemoteCommandType.RestartWindowsService, new() { ["service"] = "Spooler" }), default);
        Assert.Equal(CommandStatus.Succeeded, r.Status);
        Assert.Contains("Spooler", _scm.Restarted);
    }

    [Fact]
    public async Task TamperedParameters_AreRejected_AndNothingRuns()
    {
        var good = Sign(RemoteCommandType.RestartWindowsService, new() { ["service"] = "Spooler" });
        var evil = good with { Command = good.Command with { Parameters = new Dictionary<string, string> { ["service"] = "WinDefend" } } };
        var r = await _dispatcher.ExecuteAsync(evil, default);
        Assert.Equal(CommandStatus.Rejected, r.Status);
        Assert.Contains("signature", r.Error);
        Assert.Empty(_scm.Restarted);
    }

    [Fact]
    public async Task SignatureFromAnotherKey_IsRejected()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now = _clock.GetUtcNow();
        var cmd = new RemoteCommand(Guid.NewGuid(), Atm, RemoteCommandType.RestartApplication, new Dictionary<string, string>(), "mallory", null, now, now.AddMinutes(5), "n1");
        var r = await _dispatcher.ExecuteAsync(CommandCrypto.Sign(cmd, attacker, "k1"), default);
        Assert.Equal(CommandStatus.Rejected, r.Status);
    }

    [Fact]
    public async Task UnknownKeyId_IsRejected()
    {
        var r = await _dispatcher.ExecuteAsync(Sign(RemoteCommandType.RestartApplication, keyId: "nope"), default);
        Assert.Equal(CommandStatus.Rejected, r.Status);
        Assert.Contains("unknown signing key", r.Error);
    }

    [Fact]
    public async Task CommandForAnotherAtm_IsRejected()
    {
        var r = await _dispatcher.ExecuteAsync(Sign(RemoteCommandType.RestartApplication, atm: "ATM-999999"), default);
        Assert.Equal(CommandStatus.Rejected, r.Status);
        Assert.Contains("different ATM", r.Error);
    }

    [Fact]
    public async Task ExpiredCommand_IsRejectedAsExpired()
    {
        var signed = Sign(RemoteCommandType.RestartApplication, lifetime: TimeSpan.FromMinutes(5));
        _clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(CommandStatus.Expired, (await _dispatcher.ExecuteAsync(signed, default)).Status);
    }

    [Fact]
    public async Task Replay_IsRejected()
    {
        var signed = Sign(RemoteCommandType.RestartApplication);
        Assert.Equal(CommandStatus.Succeeded, (await _dispatcher.ExecuteAsync(signed, default)).Status);
        var again = await _dispatcher.ExecuteAsync(signed, default);
        Assert.Equal(CommandStatus.Rejected, again.Status);
        Assert.Contains("replay", again.Error);
        Assert.Single(_scm.Restarted);
    }

    [Fact]
    public async Task ServiceNotOnAllowList_IsRejected()
    {
        var r = await _dispatcher.ExecuteAsync(Sign(RemoteCommandType.RestartWindowsService, new() { ["service"] = "RemoteRegistry" }), default);
        Assert.Equal(CommandStatus.Rejected, r.Status);
        Assert.Contains("allow-list", r.Error);
    }

    [Fact]
    public async Task BusyAtm_RefusesDisruptiveCommand_UnlessForcedAndApproved()
    {
        _idle.Idle = false;
        var busy = await _dispatcher.ExecuteAsync(Sign(RemoteCommandType.RestartApplication), default);
        Assert.Equal(CommandStatus.Rejected, busy.Status);
        Assert.Contains("busy", busy.Error);

        var forced = await _dispatcher.ExecuteAsync(
            Sign(RemoteCommandType.RestartApplication, new() { ["force"] = "true" }, approvedBy: "bob"), default);
        Assert.Equal(CommandStatus.Succeeded, forced.Status);
    }

    [Fact]
    public async Task Reboot_RequiresDifferentApprover()
    {
        var none = await _dispatcher.ExecuteAsync(Sign(RemoteCommandType.RebootAtm), default);
        Assert.Contains("dual control", none.Error);

        var self = await _dispatcher.ExecuteAsync(Sign(RemoteCommandType.RebootAtm, approvedBy: "alice"), default);
        Assert.Contains("dual control", self.Error);
        Assert.Empty(_proc.Calls);

        var ok = await _dispatcher.ExecuteAsync(Sign(RemoteCommandType.RebootAtm, approvedBy: "bob"), default);
        Assert.Equal(CommandStatus.Succeeded, ok.Status);
        var call = Assert.Single(_proc.Calls);
        Assert.Equal("shutdown.exe", call.File);
        Assert.Equal("/r", call.Args[0]);
    }

    [Fact]
    public async Task LocallyDisabledCommandType_IsRejected_EvenIfSigned()
    {
        _policy.Enabled.Remove(RemoteCommandType.RestartApplication);
        var r = await _dispatcher.ExecuteAsync(Sign(RemoteCommandType.RestartApplication), default);
        Assert.Equal(CommandStatus.Rejected, r.Status);
        Assert.Contains("disabled by local policy", r.Error);
    }

    [Fact]
    public async Task RestartXfs_RestartsXfsThenApplication()
    {
        var r = await _dispatcher.ExecuteAsync(Sign(RemoteCommandType.RestartXfs), default);
        Assert.Equal(CommandStatus.Succeeded, r.Status);
        Assert.Equal(new[] { "XfsMgr", "AtmApp" }, _scm.Restarted.ToArray());
    }

    [Fact]
    public void Canonical_IsStable_AndEscapesDelimiters()
    {
        var now = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var a = new RemoteCommand(Guid.Empty, "A", RemoteCommandType.CollectLogs, new Dictionary<string, string> { ["b"] = "1", ["a"] = "2" }, "u", null, now, now, "n");
        var b = a with { Parameters = new Dictionary<string, string> { ["a"] = "2", ["b"] = "1" } };
        Assert.Equal(a.Canonical(), b.Canonical());                       // order independent

        var inj = a with { Parameters = new Dictionary<string, string> { ["a"] = "2\nb=1" } };
        var plain = a with { Parameters = new Dictionary<string, string> { ["a"] = "2", ["b"] = "1" } };
        Assert.NotEqual(inj.Canonical(), plain.Canonical());              // newline injection cannot forge another parameter
    }

    public void Dispose()
    {
        _cache.Dispose();
        _key.Dispose();
    }
}
