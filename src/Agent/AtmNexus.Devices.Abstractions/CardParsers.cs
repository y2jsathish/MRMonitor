using System.Text;

namespace AtmNexus.Devices;

public sealed record AtrInfo(bool InverseConvention, IReadOnlyList<int> Protocols, int HistoricalBytes, bool EmvCompatible);

/// <summary>ISO 7816-3 ATR validation + EMV 4.x basic ATR constraints (TS, T=0/T=1, TCK).</summary>
public static class AtrParser
{
    public static bool TryParse(ReadOnlySpan<byte> atr, out AtrInfo? info)
    {
        info = null;
        if (atr.Length < 2 || atr.Length > 33) return false;
        byte ts = atr[0];
        if (ts != 0x3B && ts != 0x3F) return false;

        byte t0 = atr[1];
        int k = t0 & 0x0F;
        int yi = t0 >> 4;
        int i = 2;
        var protocols = new List<int>();
        bool tckRequired = false;

        while (true)
        {
            i += (yi & 0x1) != 0 ? 1 : 0;   // TAi
            i += (yi & 0x2) != 0 ? 1 : 0;   // TBi
            i += (yi & 0x4) != 0 ? 1 : 0;   // TCi
            if ((yi & 0x8) == 0) break;
            if (i >= atr.Length) return false;
            byte td = atr[i++];
            int t = td & 0x0F;
            protocols.Add(t);
            if (t != 0) tckRequired = true;
            yi = td >> 4;
        }
        if (protocols.Count == 0) protocols.Add(0);

        int expected = i + k + (tckRequired ? 1 : 0);
        if (expected != atr.Length) return false;
        if (tckRequired)
        {
            byte x = 0;
            for (int j = 1; j < atr.Length; j++) x ^= atr[j];
            if (x != 0) return false;
        }

        info = new AtrInfo(ts == 0x3F, protocols, k, protocols.All(p => p is 0 or 1));
        return true;
    }
}

public sealed record Track2Info(string MaskedPan, string ServiceCode, bool LuhnValid, bool ChipCardPerServiceCode);

/// <summary>Extracts only non-sensitive facts from Track 2: masked PAN, service code. The raw buffer is never retained.</summary>
public static class Track2Parser
{
    public static bool TryParse(ReadOnlySpan<byte> track2, out Track2Info? info)
    {
        info = null;
        var s = Encoding.ASCII.GetString(track2).Trim();
        if (s.StartsWith(';')) s = s[1..];
        if (s.EndsWith('?')) s = s[..^1];

        int sep = s.IndexOfAny(['=', 'D']);
        if (sep < 12 || sep > 19 || s.Length < sep + 8) return false;

        var pan = s[..sep];
        if (!pan.All(char.IsAsciiDigit)) return false;
        var svc = s.Substring(sep + 5, 3);
        if (!svc.All(char.IsAsciiDigit)) return false;

        var masked = pan[..6] + new string('*', pan.Length - 10) + pan[^4..];
        info = new Track2Info(masked, svc, Luhn(pan), svc[0] is '2' or '6');
        return true;
    }

    public static bool Luhn(string digits)
    {
        int sum = 0; bool dbl = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d; dbl = !dbl;
        }
        return sum % 10 == 0;
    }
}
