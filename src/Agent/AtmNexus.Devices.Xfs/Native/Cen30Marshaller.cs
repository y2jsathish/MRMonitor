using System.Runtime.InteropServices;

namespace AtmNexus.Devices.Xfs.Native;

/// <summary>
/// Reference marshalling for CEN/XFS 3.0 structures (pack 1). Struct generations differ (3.10/3.20/3.30 append members and
/// some vendors add extensions), so everything that reads native memory is isolated here and driven by
/// <see cref="XfsStatusLayouts"/>. Validate against the vendor SDK headers during device certification.
/// </summary>
internal static class Cen30Marshaller
{
    private static int P => IntPtr.Size;

    /// <summary>Reads the leading USHORT members described by <paramref name="layout"/> plus lpszExtra when present.</summary>
    public static XfsStatusData ReadStatus(IntPtr buffer, IReadOnlyList<StatusField> layout)
    {
        var fields = new Dictionary<string, ushort>();
        var arrays = new Dictionary<string, ushort[]>();
        int o = 0;
        foreach (var f in layout)
        {
            if (f.Count == 1)
            {
                fields[f.Name] = (ushort)Marshal.ReadInt16(buffer, o);
                o += 2;
            }
            else
            {
                var arr = new ushort[f.Count];
                for (int i = 0; i < f.Count; i++) arr[i] = (ushort)Marshal.ReadInt16(buffer, o + i * 2);
                arrays[f.Name] = arr;
                o += f.Count * 2;
            }
        }
        return new XfsStatusData(fields, arrays, Extra: null);
    }

    /// <summary>WFSCDMCUINFO { USHORT usTellerID; USHORT usCount; LPWFSCDMCASHUNIT *lppList; }</summary>
    public static XfsCashUnitInfoData ReadCashUnitInfo(IntPtr buffer)
    {
        int count = (ushort)Marshal.ReadInt16(buffer, 2);
        IntPtr list = Marshal.ReadIntPtr(buffer, 4);
        var units = new List<XfsCashUnitData>(count);
        for (int i = 0; i < count; i++)
            units.Add(ReadCashUnit(Marshal.ReadIntPtr(list, i * P)));
        return new XfsCashUnitInfoData(units);
    }

    /// <summary>
    /// WFSCDMCASHUNIT (3.0): usNumber, usType, lpszCashUnitName, cUnitID[5], cCurrencyID[3], ulValues, ulInitialCount,
    /// ulCount, ulRejectCount, ulMinimum, ulMaximum, bAppLock, usStatus, usNumPhysicalCUs, lppPhysical.
    /// </summary>
    public static XfsCashUnitData ReadCashUnit(IntPtr p)
    {
        int o = 0;
        ushort number = (ushort)Marshal.ReadInt16(p, o); o += 2;
        ushort type = (ushort)Marshal.ReadInt16(p, o); o += 2;
        IntPtr namePtr = Marshal.ReadIntPtr(p, o); o += P;
        string name = namePtr == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(namePtr) ?? "";
        string unitId = ReadFixedAnsi(p, o, 5); o += 5;
        string currency = ReadFixedAnsi(p, o, 3); o += 3;
        uint value = (uint)Marshal.ReadInt32(p, o); o += 4;
        uint initial = (uint)Marshal.ReadInt32(p, o); o += 4;
        uint count = (uint)Marshal.ReadInt32(p, o); o += 4;
        uint reject = (uint)Marshal.ReadInt32(p, o); o += 4;
        uint min = (uint)Marshal.ReadInt32(p, o); o += 4;
        uint max = (uint)Marshal.ReadInt32(p, o); o += 4;
        bool appLock = Marshal.ReadInt32(p, o) != 0; o += 4;
        ushort status = (ushort)Marshal.ReadInt16(p, o);
        return new XfsCashUnitData(number, type, name, unitId, currency, value, initial, count, reject, min, max, appLock, status);
    }

    /// <summary>WFSIDCCARDDATA** (null-terminated): wDataSource, wStatus, ulDataLength, lpbData, fwWriteMethod.</summary>
    public static XfsCardDataResult ReadCardData(IntPtr buffer)
    {
        var items = new List<XfsCardDataItem>();
        for (int i = 0; ; i++)
        {
            IntPtr item = Marshal.ReadIntPtr(buffer, i * P);
            if (item == IntPtr.Zero) break;
            ushort source = (ushort)Marshal.ReadInt16(item, 0);
            ushort status = (ushort)Marshal.ReadInt16(item, 2);
            int len = Marshal.ReadInt32(item, 4);
            IntPtr data = Marshal.ReadIntPtr(item, 8);
            var bytes = new byte[Math.Max(0, len)];
            if (len > 0 && data != IntPtr.Zero) Marshal.Copy(data, bytes, 0, len);
            items.Add(new XfsCardDataItem(source, status, bytes));
        }
        return new XfsCardDataResult(items);
    }

    /// <summary>WFSCDMDENOMINATION: cCurrencyID[3], ulAmount, ulCount, lpulValues, ulCashBox.</summary>
    public static IntPtr AllocDispense(XfsCdmDispenseData d, List<IntPtr> allocations)
    {
        IntPtr denom = Alloc(3 + 4 + 4 + P + 4, allocations);
        int o = 0;
        for (int i = 0; i < 3; i++) Marshal.WriteByte(denom, o + i, i < d.Currency.Length ? (byte)d.Currency[i] : (byte)0);
        o += 3;
        Marshal.WriteInt32(denom, o, (int)d.Amount); o += 4;
        Marshal.WriteInt32(denom, o, 0); o += 4;                 // ulCount = 0 -> SP computes the mix from ulAmount
        Marshal.WriteIntPtr(denom, o, IntPtr.Zero); o += P;       // lpulValues
        Marshal.WriteInt32(denom, o, 0);                          // ulCashBox

        // WFSCDMDISPENSE: usTellerID, usMixNumber, lpDenomination, bPresent, fwPosition
        IntPtr disp = Alloc(2 + 2 + P + 4 + 2, allocations);
        o = 0;
        Marshal.WriteInt16(disp, o, (short)d.TellerId); o += 2;
        Marshal.WriteInt16(disp, o, (short)d.MixNumber); o += 2;
        Marshal.WriteIntPtr(disp, o, denom); o += P;
        Marshal.WriteInt32(disp, o, d.Present ? 1 : 0); o += 4;
        Marshal.WriteInt16(disp, o, 0);
        return disp;
    }

    public static IntPtr AllocUShort(ushort value, List<IntPtr> allocations)
    {
        var p = Alloc(2, allocations);
        Marshal.WriteInt16(p, (short)value);
        return p;
    }

    private static IntPtr Alloc(int size, List<IntPtr> allocations)
    {
        var p = Marshal.AllocHGlobal(size);
        for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
        allocations.Add(p);
        return p;
    }

    private static string ReadFixedAnsi(IntPtr p, int offset, int len)
    {
        var chars = new char[len];
        int n = 0;
        for (int i = 0; i < len; i++)
        {
            byte b = Marshal.ReadByte(p, offset + i);
            if (b == 0) break;
            chars[n++] = (char)b;
        }
        return new string(chars, 0, n);
    }
}
