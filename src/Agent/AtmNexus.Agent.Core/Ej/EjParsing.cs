using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AtmNexus.Agent.Ej;

/// <summary>
/// Regex profile describing one journal dialect. The built-in profiles below are ILLUSTRATIVE reference dialects:
/// capture real journals from each ATM model/application release (APTRA Advance NDC, Wincor ProTopas/ProBase, Kalignite)
/// during onboarding and tune the expressions - parsing is data, so no recompilation is needed (profiles can be overridden in config).
/// </summary>
public sealed class EjProfile
{
    public required string Vendor { get; init; }
    public required Regex Start { get; init; }
    public required Regex End { get; init; }
    public Regex? Timestamp { get; init; }              // (?<ts>...)
    public string[] TimestampFormats { get; init; } = ["yyyy-MM-dd HH:mm:ss"];
    public Regex? Type { get; init; }                   // (?<type>...)
    public Regex? Card { get; init; }                   // (?<pan>...)
    public Regex? Amount { get; init; }                 // (?<ccy>...)?(?<amt>...)
    public Regex? Notes { get; init; }                  // (?<notes>...)
    public Regex? Error { get; init; }                  // (?<code>...)
    public Regex? Cancel { get; init; }
    public Regex? Timeout { get; init; }

    private const RegexOptions O = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private static Regex R(string p) => new(p, O, TimeSpan.FromMilliseconds(200));

    public static readonly EjProfile Ncr = new()
    {
        Vendor = "NCR",
        Start = R(@"TRANSACTION START"),
        End = R(@"TRANSACTION END"),
        Timestamp = R(@"^\[(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\]"),
        Type = R(@"(?<type>WITHDRAWAL|BALANCE INQUIRY|DEPOSIT|TRANSFER|PIN CHANGE|MINI STATEMENT)"),
        Card = R(@"\bPAN[:=\s]+(?<pan>[\d*Xx]{12,19})"),
        Amount = R(@"(?:WITHDRAWAL|DEPOSIT|TRANSFER)\s+(?<ccy>[A-Z]{3})\s+(?<amt>[\d.,]+)"),
        Notes = R(@"NOTES PRESENTED[:\s]+(?<notes>[\d,x ]+)"),
        Error = R(@"\bERR(?:OR)?[\s:#]+(?<code>[A-Z0-9-]{3,10})"),
        Cancel = R(@"CUSTOMER CANCEL|CANCELLED BY CUSTOMER"),
        Timeout = R(@"TIME-?OUT")
    };

    public static readonly EjProfile DieboldNixdorf = new()
    {
        Vendor = "DieboldNixdorf",
        Start = R(@"\*TRANSACTION START\*"),
        End = R(@"\*TRANSACTION END\*"),
        Timestamp = R(@"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})"),
        Type = R(@"\b(?<type>WITHDRAWAL|BALANCE|DEPOSIT|TRANSFER|PIN CHANGE)\b"),
        Card = R(@"CARD[:=\s]+(?<pan>[\d*Xx]{12,19})"),
        Amount = R(@"(?:WITHDRAWAL|DEPOSIT|TRANSFER)\s+(?<ccy>[A-Z]{3})\s+(?<amt>[\d.,]+)"),
        Notes = R(@"NOTES PRESENTED[:\s]+(?<notes>[\d,x ]+)"),
        Error = R(@"\bERROR\s+(?<code>\d{3,6})"),
        Cancel = R(@"CANCEL"),
        Timeout = R(@"TIMEOUT")
    };

    public static readonly EjProfile Kal = new()
    {
        Vendor = "KAL",
        Start = R(@"SESSION STARTED"),
        End = R(@"SESSION ENDED"),
        Timestamp = R(@"^(?<ts>\d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2})"),
        TimestampFormats = ["dd/MM/yyyy HH:mm:ss"],
        Type = R(@"TXN TYPE[:=\s]+(?<type>[A-Z ]+?)\s*$"),
        Card = R(@"CARD[:=\s]+(?<pan>[\d*Xx]{12,19})"),
        Amount = R(@"AMOUNT[:=\s]+(?<ccy>[A-Z]{3})?\s*(?<amt>[\d.,]+)"),
        Notes = R(@"DISPENSED[:\s]+(?<notes>[\d,x ]+)"),
        Error = R(@"FAULT[:\s]+(?<code>[A-Z0-9-]{3,10})"),
        Cancel = R(@"CANCEL"),
        Timeout = R(@"TIMEOUT")
    };

    public static readonly EjProfile Generic = new()
    {
        Vendor = "Generic",
        Start = R(@"\b(?:TRANSACTION|TXN|SESSION)\s+START"),
        End = R(@"\b(?:TRANSACTION|TXN|SESSION)\s+END"),
        Timestamp = R(@"(?<ts>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2})"),
        TimestampFormats = ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss"],
        Type = R(@"\b(?<type>WITHDRAWAL|BALANCE|DEPOSIT|TRANSFER)\b"),
        Card = R(@"(?:CARD|PAN)[:=\s]+(?<pan>[\d*Xx]{12,19})"),
        Amount = R(@"(?:AMOUNT|WITHDRAWAL|DEPOSIT)[:=\s]+(?<ccy>[A-Z]{3})?\s*(?<amt>[\d.,]+)"),
        Error = R(@"\bERROR[:\s#]+(?<code>[A-Z0-9-]{3,10})"),
        Cancel = R(@"CANCEL"),
        Timeout = R(@"TIME-?OUT")
    };

    public static EjProfile For(string vendor) => vendor.ToLowerInvariant() switch
    {
        "ncr" => Ncr,
        "dieboldnixdorf" or "dn" or "wincor" => DieboldNixdorf,
        "kal" => Kal,
        _ => Generic
    };
}

public interface IEJParser
{
    string Vendor { get; }
    /// <summary>Feeds one line; returns transactions completed by it.</summary>
    IReadOnlyList<EjTransaction> Feed(EjLine line);
    /// <summary>Offset that is safe to checkpoint: the start of the currently open transaction, or the end of the last line.</summary>
    long SafeOffset { get; }
}

/// <summary>Stateful, per-file transaction assembler driven by an <see cref="EjProfile"/>.</summary>
public sealed class ProfileEjParser(EjProfile profile, string atmCode, TimeZoneInfo atmTimeZone, bool decimalComma = false, int maxLines = 200) : IEJParser
{
    private sealed class Open(string file, long start)
    {
        public string File { get; } = file;
        public long StartOffset { get; } = start;
        public long EndOffset;
        public DateTimeOffset? Start, End;
        public string Type = "UNKNOWN";
        public string? Pan, Ccy, Notes;
        public decimal? Amount;
        public bool Cancelled, TimedOut;
        public readonly List<string> Errors = new();
        public readonly List<string> Lines = new();
    }

    private Open? _open;
    private long _lastEnd;

    public string Vendor => profile.Vendor;
    public long SafeOffset => _open?.StartOffset ?? _lastEnd;

    public IReadOnlyList<EjTransaction> Feed(EjLine line)
    {
        var done = new List<EjTransaction>();
        string text = PanMasker.Sanitize(line.Text);
        bool isStart = profile.Start.IsMatch(text);

        if (isStart)
        {
            if (_open is not null) done.Add(Close(EjOutcome.Incomplete));
            _open = new Open(line.File, line.StartOffset) { Start = ParseTs(text) };
        }
        if (_open is null) { _lastEnd = line.EndOffset; return done; }

        _open.EndOffset = line.EndOffset;
        if (_open.Lines.Count < maxLines) _open.Lines.Add(text);

        if (profile.Type?.Match(text) is { Success: true } t && _open.Type == "UNKNOWN") _open.Type = t.Groups["type"].Value.ToUpperInvariant();
        if (profile.Card?.Match(text) is { Success: true } c) _open.Pan = PanMasker.ForceMask(c.Groups["pan"].Value);
        if (profile.Amount?.Match(text) is { Success: true } a && TryAmount(a.Groups["amt"].Value, out var amt))
        {
            _open.Amount = amt;
            if (a.Groups["ccy"].Success) _open.Ccy = a.Groups["ccy"].Value.ToUpperInvariant();
        }
        if (profile.Notes?.Match(text) is { Success: true } n) _open.Notes = n.Groups["notes"].Value.Trim();
        if (profile.Error?.Match(text) is { Success: true } e) _open.Errors.Add(e.Groups["code"].Value);
        if (profile.Cancel?.IsMatch(text) == true) _open.Cancelled = true;
        if (profile.Timeout?.IsMatch(text) == true) _open.TimedOut = true;

        if (!isStart && profile.End.IsMatch(text))
        {
            _open.End = ParseTs(text) ?? _open.Start;
            done.Add(Close(null));
            _lastEnd = line.EndOffset;
        }
        return done;
    }

    private EjTransaction Close(EjOutcome? forced)
    {
        var o = _open!;
        _open = null;
        _lastEnd = o.EndOffset;

        var outcome = forced
            ?? (o.TimedOut ? EjOutcome.Timeout
                : o.Cancelled ? EjOutcome.Cancelled
                : o.Errors.Count > 0 && o.Amount > 0 && o.Notes is null ? EjOutcome.Failed
                : EjOutcome.Completed);

        return new EjTransaction(
            Id: TxId(atmCode, o.File, o.StartOffset), AtmCode: atmCode, Vendor: profile.Vendor, Type: o.Type,
            Start: o.Start, End: o.End, MaskedPan: o.Pan, Amount: o.Amount, Currency: o.Ccy, Outcome: outcome,
            ErrorCodes: o.Errors, NotesPresented: o.Notes, File: Path.GetFileName(o.File),
            StartOffset: o.StartOffset, EndOffset: o.EndOffset, Lines: o.Lines);
    }

    /// <summary>Deterministic id => re-reading the same journal range is idempotent in Elasticsearch.</summary>
    public static string TxId(string atmCode, string file, long startOffset)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{atmCode}|{Path.GetFileName(file)}|{startOffset}"));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    private DateTimeOffset? ParseTs(string text)
    {
        var m = profile.Timestamp?.Match(text);
        if (m is not { Success: true }) return null;
        if (!DateTime.TryParseExact(m.Groups["ts"].Value, profile.TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;
        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), atmTimeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private bool TryAmount(string raw, out decimal amount)
    {
        raw = decimalComma ? raw.Replace(".", "").Replace(',', '.') : raw.Replace(",", "");
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }
}
