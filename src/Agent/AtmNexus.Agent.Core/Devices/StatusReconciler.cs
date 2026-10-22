namespace AtmNexus.Agent.Devices;

/// <summary>
/// Derives alarms from status deltas between two polls. XFS events can be missed (SP restart, message pump stall,
/// agent restart), so polling is the safety net: the same DeviceEventType is produced and de-duplicated downstream.
/// </summary>
public sealed class StatusReconciler
{
    public IReadOnlyList<DeviceEvent> Reconcile(DeviceStatus? prev, DeviceStatus cur)
    {
        var list = new List<DeviceEvent>();
        string P(string k) => prev?.Attributes.GetValueOrDefault(k, "")?.ToLowerInvariant() ?? "";
        string C(string k) => cur.Attributes.GetValueOrDefault(k, "").ToLowerInvariant();
        bool Changed(string k) => P(k) != C(k);

        void Add(DeviceEventType t, AlertSeverity s, string msg) =>
            list.Add(DeviceEvent.Create(cur.LogicalName, cur.Class, t, s, msg, cur.At,
                new Dictionary<string, string> { ["source"] = "poll" }));

        if (prev is null || prev.State != cur.State)
        {
            switch (cur.State)
            {
                case DeviceState.HardwareError: Add(DeviceEventType.HardwareError, AlertSeverity.Critical, $"{cur.LogicalName} hardware error"); break;
                case DeviceState.FraudAttempt: Add(DeviceEventType.FraudAttempt, AlertSeverity.Critical, $"{cur.LogicalName} fraud attempt"); break;
                case DeviceState.Offline or DeviceState.PowerOff or DeviceState.NoDevice:
                    Add(DeviceEventType.DeviceStatusChanged, AlertSeverity.Major, $"{cur.LogicalName} {cur.State}"); break;
                case DeviceState.Online when prev is not null:
                    Add(DeviceEventType.DeviceStatusChanged, AlertSeverity.Info, $"{cur.LogicalName} recovered (was {prev.State})"); break;
            }
        }

        if (Changed(DeviceAttributes.Media) && C(DeviceAttributes.Media) == "jammed")
            Add(DeviceEventType.CardJam, AlertSeverity.Critical, "Card jammed in reader");
        if (Changed(DeviceAttributes.RetainBin) && C(DeviceAttributes.RetainBin) is "high" or "full")
            Add(DeviceEventType.RetainBinHigh, AlertSeverity.Warning, $"Card retain bin {C(DeviceAttributes.RetainBin)}");

        if (Changed(DeviceAttributes.Paper))
        {
            switch (C(DeviceAttributes.Paper))
            {
                case "jammed": Add(DeviceEventType.PaperJam, AlertSeverity.Major, "Receipt paper jam"); break;
                case "out": Add(DeviceEventType.PaperEmpty, AlertSeverity.Major, "Receipt paper empty"); break;
                case "low": Add(DeviceEventType.PaperLow, AlertSeverity.Warning, "Receipt paper low"); break;
            }
        }

        if (Changed(DeviceAttributes.SafeDoor))
        {
            if (C(DeviceAttributes.SafeDoor) == "open") Add(DeviceEventType.SafeDoorOpen, AlertSeverity.Critical, "Safe door opened");
            else if (C(DeviceAttributes.SafeDoor) == "closed" && P(DeviceAttributes.SafeDoor) == "open") Add(DeviceEventType.SafeDoorClosed, AlertSeverity.Info, "Safe door closed");
        }
        if (Changed(DeviceAttributes.CabinetDoor))
        {
            if (C(DeviceAttributes.CabinetDoor) == "open") Add(DeviceEventType.CabinetDoorOpen, AlertSeverity.Major, "Cabinet door opened");
            else if (P(DeviceAttributes.CabinetDoor) == "open") Add(DeviceEventType.CabinetDoorClosed, AlertSeverity.Info, "Cabinet door closed");
        }
        if (Changed(DeviceAttributes.Tamper) && C(DeviceAttributes.Tamper) == "active")
            Add(DeviceEventType.TamperDetected, AlertSeverity.Critical, "Tamper sensor active");
        if (Changed(DeviceAttributes.Vibration) && C(DeviceAttributes.Vibration) == "active")
            Add(DeviceEventType.VibrationDetected, AlertSeverity.Major, "Vibration sensor active");
        if (Changed(DeviceAttributes.Heat) && C(DeviceAttributes.Heat) == "active")
            Add(DeviceEventType.TemperatureHigh, AlertSeverity.Major, "Heat sensor active");
        if (Changed(DeviceAttributes.Encryptor) && C(DeviceAttributes.Encryptor) == "tampered")
            Add(DeviceEventType.TamperDetected, AlertSeverity.Critical, "PIN pad (EPP) reports tamper");

        return list;
    }

    public IReadOnlyList<DeviceEvent> ReconcileCash(string logicalName, IReadOnlyList<CashUnit>? prev, IReadOnlyList<CashUnit> cur, DateTimeOffset at)
    {
        var list = new List<DeviceEvent>();
        foreach (var u in cur.Where(u => u.Type is CashUnitType.Dispense or CashUnitType.Recycle))
        {
            var p = prev?.FirstOrDefault(x => x.Number == u.Number);
            bool empty = u.Status == CashUnitStatus.Empty || u.Count == 0;
            bool low = !empty && (u.Status == CashUnitStatus.Low || u.Count <= u.Minimum);
            bool wasEmpty = p is not null && (p.Status == CashUnitStatus.Empty || p.Count == 0);
            bool wasLow = p is not null && !wasEmpty && (p.Status == CashUnitStatus.Low || p.Count <= p.Minimum);

            var data = new Dictionary<string, string>
            {
                ["source"] = "poll", ["unit"] = u.Number.ToString(), ["currency"] = u.Currency,
                ["denomination"] = u.Denomination.ToString(), ["count"] = u.Count.ToString()
            };
            if (empty && !wasEmpty)
                list.Add(DeviceEvent.Create(logicalName, DeviceClass.CashDispenser, DeviceEventType.CashEmpty, AlertSeverity.Critical,
                    $"Cash unit {u.Number} ({u.Currency} {u.Denomination}) is empty", at, data));
            else if (low && !wasLow && !wasEmpty)
                list.Add(DeviceEvent.Create(logicalName, DeviceClass.CashDispenser, DeviceEventType.CashLow, AlertSeverity.Major,
                    $"Cash unit {u.Number} ({u.Currency} {u.Denomination}) low: {u.Count}", at, data));
        }
        return list;
    }
}
