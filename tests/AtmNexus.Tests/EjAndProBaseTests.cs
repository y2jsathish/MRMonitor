using AtmNexus.Agent;
using AtmNexus.Agent.Cache;
using AtmNexus.Agent.Ej;
using AtmNexus.Agent.ProBase;
using AtmNexus.Simulator;
using Microsoft.Extensions.Time.Testing;

namespace AtmNexus.Tests;

public class PanMaskerTests
{
    [Fact]
    public void Masks_AnyLongDigitRun_KeepsBin6AndLast4()
    {
        Assert.Equal("card 411111******1111 ok", PanMasker.Sanitize("card 4111111111111111 ok"));
        Assert.Equal("pan 623456*********6789.", PanMasker.Sanitize("pan 6234567890123456789."));     // 19-digit PAN
    }

    [Fact]
    public void Masks_NonLuhnNumbers_Too()
    {
        Assert.DoesNotContain("1234567890123456", PanMasker.Sanitize("pan 1234567890123456"));
    }

    [Fact]
    public void Removes_TrackData()
    {
        var s = PanMasker.Sanitize("raw ;4111111111111111=29122011234567890? end");
        Assert.Contains("[TRACK-REDACTED]", s);
        Assert.DoesNotContain("4111111111111111", s);
    }

    [Fact]
    public void LeavesShortNumbersAndAlreadyMaskedValues()
    {
        Assert.Equal("seq 123456 amount 200.00 pan 411111******1111", PanMasker.Sanitize("seq 123456 amount 200.00 pan 411111******1111"));
    }

    [Fact]
    public void ForceMask_HandlesFullAndMasked()
    {
        Assert.Equal("411111******1111", PanMasker.ForceMask("4111111111111111"));
        Assert.Equal("411111******1111", PanMasker.ForceMask("411111******1111"));
        Assert.Null(PanMasker.ForceMask(null));
    }
}

public sealed class EjParserTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-ej-" + Guid.NewGuid().ToString("N"));
    public EjParserTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private List<EjTransaction> Parse(string vendor, EjScenario scenario, string type = "WITHDRAWAL", decimal amount = 200m)
    {
        var gen = new EjGenerator(Path.Combine(_dir, vendor + scenario), vendor);
        var file = gen.WriteTransaction(type, amount, scenario, "4111111111111111");
        var parser = new ProfileEjParser(EjProfile.For(vendor), "ATM-1", TimeZoneInfo.Utc);
        return LineFileReader.ReadLines(file, 0, System.Text.Encoding.UTF8).SelectMany(line => parser.Feed(line)).ToList();
    }

    [Theory]
    [InlineData("ncr")]
    [InlineData("dn")]
    [InlineData("kal")]
    [InlineData("generic")]
    public void CompletedWithdrawal_IsParsedInEveryDialect(string vendor)
    {
        var tx = Assert.Single(Parse(vendor, EjScenario.Completed));
        Assert.Equal("WITHDRAWAL", tx.Type);
        Assert.Equal(200m, tx.Amount);
        Assert.Equal("USD", tx.Currency);
        Assert.Equal(EjOutcome.Completed, tx.Outcome);
        Assert.Equal("411111******1111", tx.MaskedPan);
        Assert.NotNull(tx.Start);
        Assert.NotNull(tx.End);
        Assert.All(tx.Lines, l => Assert.DoesNotContain("4111111111111111", l));
    }

    [Theory]
    [InlineData("ncr", EjScenario.Cancelled, EjOutcome.Cancelled)]
    [InlineData("ncr", EjScenario.Timeout, EjOutcome.Timeout)]
    [InlineData("ncr", EjScenario.Failed, EjOutcome.Failed)]
    [InlineData("dn", EjScenario.Cancelled, EjOutcome.Cancelled)]
    [InlineData("dn", EjScenario.Failed, EjOutcome.Failed)]
    [InlineData("kal", EjScenario.Timeout, EjOutcome.Timeout)]
    public void Outcomes_AreClassified(string vendor, EjScenario scenario, EjOutcome expected)
    {
        var tx = Assert.Single(Parse(vendor, scenario));
        Assert.Equal(expected, tx.Outcome);
        if (expected == EjOutcome.Failed) Assert.NotEmpty(tx.ErrorCodes);
    }

    [Fact]
    public void TransactionIds_AreDeterministic()
    {
        var a = Assert.Single(Parse("ncr", EjScenario.Completed));
        Assert.Equal(24, a.Id.Length);
        Assert.Equal(ProfileEjParser.TxId("ATM-1", "x.jrn", 0), ProfileEjParser.TxId("ATM-1", "dir/x.jrn", 0));   // file NAME, not path
        Assert.NotEqual(ProfileEjParser.TxId("ATM-1", "x.jrn", 0), ProfileEjParser.TxId("ATM-1", "x.jrn", 10));
        Assert.NotEqual(ProfileEjParser.TxId("ATM-1", "x.jrn", 0), ProfileEjParser.TxId("ATM-2", "x.jrn", 0));
    }

    [Fact]
    public void OpenTransaction_HoldsSafeOffsetAtItsStart_UntilItEnds()
    {
        var parser = new ProfileEjParser(EjProfile.Ncr, "ATM-1", TimeZoneInfo.Utc);
        Assert.Empty(parser.Feed(new EjLine("f", 0, 10, "idle noise")));
        Assert.Equal(10, parser.SafeOffset);
        Assert.Empty(parser.Feed(new EjLine("f", 10, 60, "[2025-06-12 10:15:31] TRANSACTION START")));
        Assert.Empty(parser.Feed(new EjLine("f", 60, 100, "[2025-06-12 10:15:33] PAN 411111******1111")));
        Assert.Equal(10, parser.SafeOffset);                              // checkpoint must not pass the open transaction
        var done = parser.Feed(new EjLine("f", 100, 150, "[2025-06-12 10:15:40] TRANSACTION END"));
        Assert.Single(done);
        Assert.Equal(150, parser.SafeOffset);
    }

    [Fact]
    public void NewStartWhileOpen_ClosesPreviousAsIncomplete()
    {
        var parser = new ProfileEjParser(EjProfile.Ncr, "ATM-1", TimeZoneInfo.Utc);
        parser.Feed(new EjLine("f", 0, 40, "[2025-06-12 10:15:31] TRANSACTION START"));
        var closed = parser.Feed(new EjLine("f", 40, 80, "[2025-06-12 10:20:31] TRANSACTION START"));
        Assert.Equal(EjOutcome.Incomplete, Assert.Single(closed).Outcome);
    }

    [Fact]
    public void LineReader_YieldsOnlyCompleteLines_WithExactOffsets()
    {
        var f = Path.Combine(_dir, "t.jrn");
        File.WriteAllText(f, "one\r\ntwo\nthree-partial");
        var lines = LineFileReader.ReadLines(f, 0, System.Text.Encoding.UTF8).ToList();
        Assert.Equal(["one", "two"], lines.Select(l => l.Text).ToArray());
        Assert.Equal(5, lines[0].EndOffset);
        Assert.Equal(9, lines[1].EndOffset);
        Assert.Equal("two", LineFileReader.ReadLines(f, 5, System.Text.Encoding.UTF8).First().Text);
    }

    // ---------------------------------------------------------------- collector end-to-end
    private sealed class ListSink : IEjSink
    {
        public List<EjTransaction> Sent { get; } = new();
        public Task SendAsync(IReadOnlyList<EjTransaction> t, CancellationToken ct) { Sent.AddRange(t); return Task.CompletedTask; }
    }

    private (EJCollector, ListSink, EjSource) Collector()
    {
        var src = new EjSource { Name = "t", Vendor = "NCR", Directory = _dir, FilePattern = "*.jrn" };
        var cache = new SqliteAgentCache(":memory:");
        var sink = new ListSink();
        var c = new EJCollector([new NcrEjProvider(), new GenericEjProvider()], new EJStorageService(sink, cache), cache, sink,
            Options.Create(new EjOptions { Sources = { src }, AtmTimeZoneId = "UTC" }),
            Options.Create(new AgentOptions { AtmCode = "ATM-1" }), NullLogger<EJCollector>.Instance);
        return (c, sink, src);
    }

    [Fact]
    public async Task Collector_ShipsEachTransactionExactlyOnce_AcrossPasses()
    {
        var (collector, sink, src) = Collector();
        var gen = new EjGenerator(_dir, "ncr");
        gen.WriteTransaction("WITHDRAWAL", 100m, EjScenario.Completed, "4111111111111111");
        gen.WriteTransaction("WITHDRAWAL", 50m, EjScenario.Cancelled, "4111111111111111");

        Assert.Equal(2, await collector.CollectSourceAsync(src, default));
        Assert.Equal(0, await collector.CollectSourceAsync(src, default));        // nothing new

        gen.WriteTransaction("DEPOSIT", 75m, EjScenario.Completed, "4111111111111111");
        Assert.Equal(1, await collector.CollectSourceAsync(src, default));
        Assert.Equal(3, sink.Sent.Count);
        Assert.Equal(3, sink.Sent.Select(t => t.Id).Distinct().Count());
    }

    [Fact]
    public async Task Collector_DoesNotSplitATransactionThatSpansTwoPasses()
    {
        var (collector, sink, src) = Collector();
        var file = Path.Combine(_dir, "split.jrn");
        File.WriteAllLines(file, new[] { "[2025-06-12 10:15:31] TRANSACTION START", "[2025-06-12 10:15:33] PAN 411111******1111" });

        Assert.Equal(0, await collector.CollectSourceAsync(src, default));        // transaction still open

        File.AppendAllLines(file, new[] { "[2025-06-12 10:15:40] WITHDRAWAL USD 100.00", "[2025-06-12 10:15:50] TRANSACTION END" });
        Assert.Equal(1, await collector.CollectSourceAsync(src, default));

        var tx = Assert.Single(sink.Sent);
        Assert.Equal(EjOutcome.Completed, tx.Outcome);                            // NOT "Incomplete"
        Assert.Equal(100m, tx.Amount);
    }

    [Fact]
    public void BulkBody_UsesMonthlyIndexAndDeterministicIds()
    {
        var tx = Assert.Single(Parse("ncr", EjScenario.Completed));
        var body = EJIndexer.BuildBulkBody([tx]);
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains($"\"_id\":\"{tx.Id}\"", lines[0]);
        Assert.Contains("atm-ej-", lines[0]);
        Assert.Contains("\"masked_pan\":\"411111******1111\"", lines[1]);
    }
}

public class ProBaseTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2025, 6, 12, 10, 0, 0, TimeSpan.Zero));
    private ProBaseLogParser Parser() => new(Options.Create(new ProBaseOptions()), _clock);

    [Fact]
    public void Parses_TransactionLifecycle_Errors_AndServiceState()
    {
        var p = Parser();

        var started = p.Parse("2025-06-12 10:15:31.123 | INFO | TxManager | | TX 1001 STARTED")!;
        Assert.Equal(TxState.Started, started.Tx);
        Assert.Equal("1001", started.TxId);

        var err = p.Parse("2025-06-12 10:15:35.000 | ERROR | Dispenser | E5001 | Dispense failed: jam")!;
        Assert.True(err.IsError);
        Assert.Equal("E5001", err.Code);
        Assert.Equal("Dispenser", err.Module);

        Assert.False(p.Parse("2025-06-12 10:16:00.000 | WARN | Core | | ATM is OUT OF SERVICE")!.InService);
        Assert.True(p.Parse("2025-06-12 10:17:00.000 | INFO | Core | | ATM is IN SERVICE")!.InService);
        Assert.True(p.Parse("2025-06-12 10:17:00.000 | FATAL | Core | F1 | crash")!.IsFatal);
        Assert.Null(p.Parse("this is not a log line"));
    }

    [Fact]
    public void Parser_MasksPanEchoedInMessages()
    {
        var e = Parser().Parse("2025-06-12 10:15:35.000 | ERROR | Auth | E1 | host declined card 4111111111111111")!;
        Assert.DoesNotContain("4111111111111111", e.Message);
        Assert.Contains("411111******1111", e.Message);
    }

    [Fact]
    public void HealthMonitor_TracksErrorRateLastTransactionAndIdleness()
    {
        var state = new AgentStateStore(_clock);
        var m = new ProBaseHealthMonitor(state, Options.Create(new ProBaseOptions()), _clock);
        var p = Parser();
        var now = _clock.GetUtcNow();

        Assert.True(m.IsIdle(TimeSpan.FromSeconds(60)));

        m.Observe(p.Parse("2025-06-12 10:00:00.000 | INFO | Tx | | TX 7 STARTED")! with { At = now });
        Assert.False(m.IsIdle(TimeSpan.FromSeconds(60)));                          // a transaction is open

        _clock.Advance(TimeSpan.FromMinutes(2));
        m.Observe(p.Parse("2025-06-12 10:02:00.000 | INFO | Tx | | TX 7 COMPLETED")! with { At = _clock.GetUtcNow() });
        Assert.False(m.IsIdle(TimeSpan.FromSeconds(60)));                          // just finished: still within quiet period
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(m.IsIdle(TimeSpan.FromSeconds(60)));

        for (int i = 0; i < 6; i++) m.Observe(p.Parse("2025-06-12 10:04:00.000 | ERROR | Tx | E9 | boom")! with { At = _clock.GetUtcNow() });
        var s = m.Evaluate();
        Assert.Equal(6, s.ErrorsPerHour, precision: 3);
        Assert.Equal(TimeSpan.FromMinutes(2), s.SinceLastTransaction);
        Assert.True(s.InService);
    }
}
