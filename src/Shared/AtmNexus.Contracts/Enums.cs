namespace AtmNexus.Contracts;

/// <summary>Vendor-neutral device classes. Maps to CEN/XFS service classes IDC, CDM, DEP, CIM/CRM, PTR, PIN, SIU, ...</summary>
public enum DeviceClass
{
    CardReader,       // IDC
    CashDispenser,    // CDM
    CashRecycler,     // CRM / CIM+CDM
    Depository,       // DEP
    Printer,          // PTR
    PinPad,           // PIN (EPP)
    Sensors,          // SIU
    Camera,           // CAM
    BarcodeReader,    // BCR
    PassportReader,   // CAM/IDC variant
    NfcReader,        // IDC contactless
    Biometric         // BIO
}

/// <summary>Normalised device state (XFS fwDevice / XFS4IoT common.status.device).</summary>
public enum DeviceState
{
    Online, Offline, PowerOff, NoDevice, HardwareError, UserError, Busy, FraudAttempt, Unknown
}

public enum HealthLevel { Healthy, Degraded, Critical, Down }

public enum AlertSeverity { Info, Warning, Major, Critical }

public enum RemoteCommandType
{
    RestartApplication,
    RestartXfs,
    RestartDevice,
    RestartWindowsService,
    CollectLogs,
    CollectEj,
    RunDiagnostics,
    RebootAtm,
    ShutdownAtm
}

public enum CommandStatus { Pending, Delivered, Running, Succeeded, Failed, Rejected, Expired }
