namespace AtmNexus.Devices;

public static class CashCalculator
{
    /// <summary>A unit that can contribute notes to a dispense right now.</summary>
    public static bool IsDispensable(CashUnit u) =>
        u.Type is CashUnitType.Dispense or CashUnitType.Recycle
        && !u.AppLocked
        && u.Count > 0
        && u.Denomination > 0
        && u.Status is not (CashUnitStatus.Empty or CashUnitStatus.Inoperative or CashUnitStatus.Missing
            or CashUnitStatus.Manipulated or CashUnitStatus.NoValue or CashUnitStatus.NoReference);

    public static long RemainingCash(IEnumerable<CashUnit> units, string currency) =>
        units.Where(u => IsDispensable(u) && u.Currency == currency).Sum(u => (long)u.Count * u.Denomination);

    public static CashSummary Summarize(IEnumerable<CashUnit> units)
    {
        var list = units.ToList();
        var byCurrency = list
            .Where(IsDispensable)
            .GroupBy(u => u.Currency)
            .ToDictionary(
                g => g.Key,
                g => new CurrencyCash(
                    g.Key,
                    g.Sum(u => (long)u.Count * u.Denomination),
                    g.Sum(u => u.Count),
                    g.GroupBy(u => u.Denomination).ToDictionary(d => d.Key, d => d.Sum(u => u.Count))));

        var dispenseUnits = list.Where(u => u.Type is CashUnitType.Dispense or CashUnitType.Recycle).ToList();
        return new CashSummary(
            byCurrency,
            LowUnits: dispenseUnits.Count(u => u.Status == CashUnitStatus.Low || (u.Count > 0 && u.Count <= u.Minimum)),
            EmptyUnits: dispenseUnits.Count(u => u.Status == CashUnitStatus.Empty || u.Count == 0),
            FaultyUnits: dispenseUnits.Count(u => u.Status is CashUnitStatus.Inoperative or CashUnitStatus.Missing or CashUnitStatus.Manipulated));
    }

    /// <summary>
    /// Bounded-coin reachability: can <paramref name="amount"/> be paid out exactly using the notes currently available?
    /// O(units * amount / gcd) - 50k ATMs x this is trivial since it only runs on the ATM and on cash-change events.
    /// </summary>
    public static bool CanDispense(IEnumerable<CashUnit> units, string currency, long amount)
    {
        if (amount <= 0) return false;
        var usable = units.Where(u => IsDispensable(u) && u.Currency == currency).ToList();
        if (usable.Count == 0) return false;

        long g = usable.Select(u => (long)u.Denomination).Aggregate(Gcd);
        if (amount % g != 0) return false;

        long target = amount / g;
        if (target > 1_000_000) throw new ArgumentOutOfRangeException(nameof(amount), "Amount too large for feasibility check.");
        int s = (int)target;

        var reach = new bool[s + 1];
        reach[0] = true;
        foreach (var u in usable)
        {
            int d = (int)(u.Denomination / g);
            var used = new int[s + 1];
            for (int i = d; i <= s; i++)
            {
                if (!reach[i] && reach[i - d] && used[i - d] < u.Count)
                {
                    reach[i] = true;
                    used[i] = used[i - d] + 1;
                }
            }
        }
        return reach[s];
    }

    private static long Gcd(long a, long b) => b == 0 ? a : Gcd(b, a % b);
}
