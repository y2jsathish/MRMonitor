namespace AtmNexus.Simulator;

/// <summary>
/// High-level fault/behaviour injection. Each scenario both mutates the physical model (so the next poll sees it) and raises
/// the XFS events a real service provider would raise (so the event path is exercised too): card insert, dispense, jam,
/// cash empty, safe door open, tamper, paper, vibration, EJ messages.
/// </summary>
public sealed class ScenarioEngine(SimulatedAtm atm, MockXfsApi xfs, EjGenerator ej, SiuSensorMap? map = null)
{
    private readonly SiuSensorMap _map = map ?? SiuSensorMap.Default;

    public void InsertCard()
    {
        lock (atm.Gate) atm.CardPresent = true;
        xfs.Raise(XfsEventScope.Idc, XfsEventKind.Execute, 103);                 // WFS_EXEE_IDC_MEDIAINSERTED
    }

    public void TakeCard()
    {
        lock (atm.Gate) atm.CardPresent = false;
        xfs.Raise(XfsEventScope.Idc, XfsEventKind.Service, 101);                 // WFS_SRVE_IDC_MEDIAREMOVED
    }

    public void RetainCard()
    {
        lock (atm.Gate) { atm.CardPresent = false; atm.RetainedCards++; }
        xfs.Raise(XfsEventScope.Idc, XfsEventKind.Service, 102);                 // WFS_SRVE_IDC_CARDRETAINED
    }

    public void JamCard()
    {
        lock (atm.Gate) atm.CardJammed = true;
        xfs.Raise(XfsEventScope.Idc, XfsEventKind.System, 2, hResult: -14);      // WFS_SYSE_HARDWARE_ERROR
    }

    public void ChipFailure(bool on) { lock (atm.Gate) atm.ChipFailure = on; }

    public void CashEmpty(int unit) => SetCount(unit, 0);

    public void CashLow(int unit)
    {
        uint min;
        lock (atm.Gate) min = atm.Units.First(u => u.Number == unit).Minimum;
        SetCount(unit, min);
    }

    public void Refill(int unit)
    {
        uint initial;
        lock (atm.Gate) initial = atm.Units.First(u => u.Number == unit).Initial;
        SetCount(unit, initial);
    }

    private void SetCount(int unit, uint count)
    {
        XfsCashUnitData data;
        lock (atm.Gate)
        {
            var u = atm.Units.First(x => x.Number == unit);
            u.Count = count;
            data = new XfsCashUnitData(u.Number, u.XfsType, u.Name, u.UnitId, u.Currency, u.Value, u.Initial, u.Count, u.Reject,
                u.Minimum, u.Maximum, false, u.Status);
        }
        xfs.Raise(XfsEventScope.Cdm, XfsEventKind.User, 201, data);              // WFS_USRE_CDM_CASHUNITTHRESHOLD
    }

    public void SafeDoor(bool open)
    {
        lock (atm.Gate) atm.SafeDoorOpen = open;
        xfs.Raise(XfsEventScope.Cdm, XfsEventKind.Service, open ? 201u : 202u);  // WFS_SRVE_CDM_SAFEDOOROPEN/CLOSED
        PortEvent(SensorKind.SafeDoor, open);
    }

    public void CabinetDoor(bool open)
    {
        lock (atm.Gate) atm.CabinetOpen = open;
        PortEvent(SensorKind.CabinetDoor, open);
    }

    public void Tamper(bool active)
    {
        lock (atm.Gate) atm.TamperActive = active;
        PortEvent(SensorKind.Tamper, active);
    }

    public void Vibration(bool active)
    {
        lock (atm.Gate) atm.VibrationActive = active;
        PortEvent(SensorKind.Vibration, active);
    }

    public void Paper(ushort level)
    {
        lock (atm.Gate) atm.PaperLevel = level;
        xfs.Raise(XfsEventScope.Ptr, XfsEventKind.User, 301, new XfsUShortData(level));   // WFS_USRE_PTR_PAPERTHRESHOLD
    }

    public void EppTamper()
    {
        lock (atm.Gate) atm.EppState = 6;
        xfs.Raise(XfsEventScope.Pin, XfsEventKind.System, 9);                    // WFS_SYSE_FRAUD_ATTEMPT
    }

    public void HardwareFault()
    {
        lock (atm.Gate) atm.DeviceState = 4;
        xfs.Raise(XfsEventScope.Cdm, XfsEventKind.System, 2, hResult: -14);
    }

    public void Recover()
    {
        lock (atm.Gate) { atm.DeviceState = 0; atm.CardJammed = false; atm.PaperJammed = false; atm.EppState = 0; atm.TamperActive = false; }
    }

    /// <summary>Writes a journal transaction in the configured vendor dialect.</summary>
    public string EjTransaction(string type, decimal amount, EjScenario outcome = EjScenario.Completed) =>
        ej.WriteTransaction(type, amount, outcome, atm.TestPan);

    private void PortEvent(SensorKind kind, bool active)
    {
        var e = _map.All.FirstOrDefault(x => x.Kind == kind);
        if (e is null) return;
        ushort status = active ? e.ActiveMask : (e.PortType == 2 ? (ushort)0x0001 : (ushort)0x0002);
        xfs.Raise(XfsEventScope.Siu, XfsEventKind.Service, 801, new XfsSiuPortEventData(e.PortType, e.PortIndex, status));
    }
}
