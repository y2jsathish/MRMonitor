using System.Globalization;

namespace AtmNexus.Simulator;

public enum EjScenario { Completed, Cancelled, Timeout, Failed }

/// <summary>Writes journal lines in the dialect of the simulated vendor so EJ collection/parsing can be exercised end-to-end.</summary>
public sealed class EjGenerator(string directory, string vendor, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    public string FilePath => Path.Combine(directory, $"{_clock.GetUtcNow():yyyyMMdd}.jrn");

    public string WriteTransaction(string type, decimal amount, EjScenario outcome, string pan)
    {
        Directory.CreateDirectory(directory);
        var masked = pan[..6] + new string('*', pan.Length - 10) + pan[^4..];
        var lines = vendor.ToLowerInvariant() switch
        {
            "ncr" => Ncr(type, amount, outcome, masked),
            "dieboldnixdorf" or "dn" or "wincor" or "hyosung" => Dn(type, amount, outcome, masked),
            "kal" => Kal(type, amount, outcome, masked),
            _ => Generic(type, amount, outcome, masked)
        };
        lock (_gate) File.AppendAllLines(FilePath, lines);
        return FilePath;
    }

    private string T(int secs) => _clock.GetUtcNow().AddSeconds(secs).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    private static string A(decimal a) => a.ToString("0.00", CultureInfo.InvariantCulture);

    private List<string> Ncr(string type, decimal amount, EjScenario o, string pan)
    {
        var l = new List<string> { $"[{T(0)}] TRANSACTION START", $"[{T(1)}] CARD INSERTED", $"[{T(2)}] PAN {pan}", $"[{T(6)}] {type} USD {A(amount)}" };
        Tail(l, o, s => $"[{T(s)}] ", "NOTES PRESENTED: 2,0,0,0", "ERROR E7021", "CUSTOMER CANCEL", "TIMEOUT");
        l.Add($"[{T(20)}] TRANSACTION END");
        return l;
    }

    private List<string> Dn(string type, decimal amount, EjScenario o, string pan)
    {
        var l = new List<string> { $"{T(0)} *TRANSACTION START*", $"{T(2)} CARD: {pan}", $"{T(6)} {type} USD {A(amount)}" };
        Tail(l, o, s => $"{T(s)} ", "NOTES PRESENTED: 2x100", "ERROR 7021 Dispenser fault", "CANCEL BY CUSTOMER", "TIMEOUT WAITING CARD TAKEN");
        l.Add($"{T(20)} *TRANSACTION END*");
        return l;
    }

    private List<string> Kal(string type, decimal amount, EjScenario o, string pan)
    {
        string K(int s) => _clock.GetUtcNow().AddSeconds(s).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
        var l = new List<string> { $"{K(0)} SESSION STARTED", $"{K(2)} CARD: {pan}", $"{K(3)} TXN TYPE: {type}", $"{K(6)} AMOUNT: USD {A(amount)}" };
        Tail(l, o, s => $"{K(s)} ", "DISPENSED: 2,0,0,0", "FAULT: E7021", "CANCEL", "TIMEOUT");
        l.Add($"{K(20)} SESSION ENDED");
        return l;
    }

    private List<string> Generic(string type, decimal amount, EjScenario o, string pan)
    {
        var l = new List<string> { $"{T(0)} TRANSACTION START", $"{T(2)} CARD: {pan}", $"{T(6)} {type} AMOUNT: USD {A(amount)}" };
        Tail(l, o, s => $"{T(s)} ", "NOTES: 2", "ERROR: E7021", "CANCEL", "TIMEOUT");
        l.Add($"{T(20)} TRANSACTION END");
        return l;
    }

    private static void Tail(List<string> l, EjScenario o, Func<int, string> p, string ok, string err, string cancel, string timeout)
    {
        switch (o)
        {
            case EjScenario.Completed: l.Add(p(12) + ok); break;
            case EjScenario.Failed: l.Add(p(12) + err); break;
            case EjScenario.Cancelled: l.Add(p(12) + cancel); break;
            case EjScenario.Timeout: l.Add(p(12) + timeout); break;
        }
    }
}
