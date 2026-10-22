namespace AtmNexus.Devices;

/// <summary>Canonical <see cref="DeviceStatus.Attributes"/> keys/values shared by every adapter (XFS3, XFS4IoT, vendor-native).</summary>
public static class DeviceAttributes
{
    public const string Media = "media";                 // present|notPresent|jammed|...
    public const string RetainBin = "retainBin";         // ok|high|full
    public const string RetainedCards = "retainedCards";
    public const string SafeDoor = "safeDoor";           // open|closed|notSupported|unknown
    public const string Dispenser = "dispenser";         // ok|cuState|cuStop|unknown
    public const string Paper = "paper";                 // full|low|out|jammed
    public const string Toner = "toner";
    public const string Encryptor = "encryptor";
    public const string CabinetDoor = "cabinetDoor";     // open|closed
    public const string Tamper = "tamper";               // active|inactive
    public const string Vibration = "vibration";
    public const string Heat = "heat";
    public const string Alarm = "alarm";
    public const string Open = "open";
    public const string Closed = "closed";
    public const string Active = "active";
    public const string Inactive = "inactive";
}
