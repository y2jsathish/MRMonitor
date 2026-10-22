using System.Runtime.InteropServices;

namespace AtmNexus.Devices.Xfs.Native;

/// <summary>
/// P/Invoke surface of msxfs.dll. NOTE: the XFS manager shipped on virtually all ATMs is 32-bit, so the Agent must be
/// published for win-x86 (see AtmNexus.Agent.csproj). All XFS structures are packed (#pragma pack(1)).
/// </summary>
internal static class NativeMethods
{
    private const string Xfs = "msxfs.dll";

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Ansi)]
    internal struct WfsVersion
    {
        public ushort Version;
        public ushort LowVersion;
        public ushort HighVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string SystemStatus;
    }

    // WFSRESULT (pack 1): ULONG RequestID(0) | HSERVICE hService(4) | SYSTEMTIME ts(6, 16 bytes) | HRESULT hResult(22) | DWORD u(26) | LPVOID lpBuffer(30)
    internal const int ResultOffsetHService = 4;
    internal const int ResultOffsetHResult = 22;
    internal const int ResultOffsetEventOrCommand = 26;
    internal const int ResultOffsetBuffer = 30;

    [DllImport(Xfs, EntryPoint = "WFSStartUp")]
    internal static extern int WFSStartUp(uint dwVersionsRequired, out WfsVersion lpWFSVersion);

    [DllImport(Xfs, EntryPoint = "WFSCleanUp")]
    internal static extern int WFSCleanUp();

    [DllImport(Xfs, EntryPoint = "WFSOpen", CharSet = CharSet.Ansi)]
    internal static extern int WFSOpen(string lpszLogicalName, IntPtr hApp, string lpszAppId, uint dwTraceLevel,
        uint dwTimeOut, uint dwSrvcVersionsRequired, out WfsVersion lpSrvcVersion, out WfsVersion lpSPIVersion,
        out ushort lphService);

    [DllImport(Xfs, EntryPoint = "WFSClose")]
    internal static extern int WFSClose(ushort hService);

    [DllImport(Xfs, EntryPoint = "WFSRegister")]
    internal static extern int WFSRegister(ushort hService, uint dwEventClass, IntPtr hWndReg);

    [DllImport(Xfs, EntryPoint = "WFSDeregister")]
    internal static extern int WFSDeregister(ushort hService, uint dwEventClass, IntPtr hWndReg);

    [DllImport(Xfs, EntryPoint = "WFSLock")]
    internal static extern int WFSLock(ushort hService, uint dwTimeOut, out IntPtr lppResult);

    [DllImport(Xfs, EntryPoint = "WFSUnlock")]
    internal static extern int WFSUnlock(ushort hService);

    [DllImport(Xfs, EntryPoint = "WFSGetInfo")]
    internal static extern int WFSGetInfo(ushort hService, uint dwCategory, IntPtr lpQueryDetails, uint dwTimeOut, out IntPtr lppResult);

    [DllImport(Xfs, EntryPoint = "WFSExecute")]
    internal static extern int WFSExecute(ushort hService, uint dwCommand, IntPtr lpCmdData, uint dwTimeOut, out IntPtr lppResult);

    [DllImport(Xfs, EntryPoint = "WFSFreeResult")]
    internal static extern int WFSFreeResult(IntPtr lpResult);

    // ---- user32 / kernel32 for the hidden message-only window that receives WFS_*_EVENT messages ----
    internal delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    internal static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] internal static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] internal static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] internal static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] internal static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr GetModuleHandle(string? moduleName);
}
