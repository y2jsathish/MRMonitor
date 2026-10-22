using System.Text.RegularExpressions;

namespace AtmNexus.Agent.Ej;

public sealed class EjSource
{
    public string Name { get; set; } = "";
    /// <summary>NCR | DieboldNixdorf | KAL | Generic</summary>
    public string Vendor { get; set; } = "Generic";
    public string Directory { get; set; } = "";
    public string FilePattern { get; set; } = "*.jrn";
    /// <summary>Code page of the journal (journals are usually ANSI/UTF-8; UTF-16 journals are not supported by the line reader).</summary>
    public string Encoding { get; set; } = "utf-8";
    public bool DecimalComma { get; set; }
}

public sealed class EjOptions
{
    public const string Section = "Ej";
    public List<EjSource> Sources { get; set; } = new();
    public string AtmTimeZoneId { get; set; } = "UTC";
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);
    public int BatchSize { get; set; } = 500;
    public int MaxLinesPerTransaction { get; set; } = 200;
}

public sealed record EjLine(string File, long StartOffset, long EndOffset, string Text);

public enum EjOutcome { Completed, Cancelled, Failed, Timeout, Incomplete }

/// <summary>Parsed journal transaction. PAN is masked (BIN6 + last4); Lines are sanitised copies of the journal text.</summary>
public sealed record EjTransaction(
    string Id,
    string AtmCode,
    string Vendor,
    string Type,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    string? MaskedPan,
    decimal? Amount,
    string? Currency,
    EjOutcome Outcome,
    IReadOnlyList<string> ErrorCodes,
    string? NotesPresented,
    string File,
    long StartOffset,
    long EndOffset,
    IReadOnlyList<string> Lines);

public interface IEjCollectionTrigger
{
    /// <summary>Re-reads journals whose date falls in [from,to] (ignoring checkpoints; ids are deterministic so re-sends are idempotent).</summary>
    Task<int> CollectAsync(DateOnly from, DateOnly to, CancellationToken ct);
}

/// <summary>Removes anything PCI DSS forbids storing after authorisation: full PAN and magstripe track data.</summary>
public static partial class PanMasker
{
    [GeneratedRegex(@";\d{13,19}=[0-9=]*\??|%B\d{13,19}\^[^?]*\??")]
    private static partial Regex TrackData();

    [GeneratedRegex(@"(?<![\d*])(\d{6})\d{3,9}(\d{4})(?![\d*])")]
    private static partial Regex LongDigitRun();

    /// <summary>Masks every 13-19 digit run (not only Luhn-valid ones: an EJ typo must never leak a real PAN) and drops track data.</summary>
    public static string Sanitize(string text)
    {
        text = TrackData().Replace(text, "[TRACK-REDACTED]");
        return LongDigitRun().Replace(text, m => m.Groups[1].Value + new string('*', m.Length - 10) + m.Groups[2].Value);
    }

    public static string? ForceMask(string? pan)
    {
        if (string.IsNullOrWhiteSpace(pan)) return null;
        var digits = pan.Count(char.IsAsciiDigit);
        if (digits < 13) return pan;                          // already masked by the ATM application
        var d = new string(pan.Where(char.IsAsciiDigit).ToArray());
        return d[..6] + new string('*', d.Length - 10) + d[^4..];
    }
}
