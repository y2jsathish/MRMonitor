using System.Runtime.InteropServices;

namespace AtmNexus.Devices.Xfs.Native;

/// <summary>
/// Dedicated STA-free thread owning a message-only window. XFS delivers WFS_SERVICE/USER/SYSTEM/EXECUTE events
/// by PostMessage(hWnd, WM_USER+10..12 / +9, 0, lpWFSRESULT); the receiver MUST call WFSFreeResult on lParam.
/// </summary>
internal sealed class XfsMessagePump : IDisposable
{
    internal const uint WM_USER = 0x0400;
    internal const uint WFS_EXECUTE_EVENT = WM_USER + 9;
    internal const uint WFS_SERVICE_EVENT = WM_USER + 10;
    internal const uint WFS_USER_EVENT = WM_USER + 11;
    internal const uint WFS_SYSTEM_EVENT = WM_USER + 12;
    private const uint WM_QUIT = 0x0012;

    private readonly Func<uint, IntPtr, bool> _onMessage;
    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private NativeMethods.WndProc? _proc;   // keep delegate alive
    private uint _threadId;

    public IntPtr Handle { get; private set; }

    /// <param name="onMessage">Return true if the message was an XFS event that was handled.</param>
    public XfsMessagePump(Func<uint, IntPtr, bool> onMessage, ILogger log)
    {
        _onMessage = onMessage;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "xfs-message-pump" };
    }

    public void Start()
    {
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(5)) || Handle == IntPtr.Zero)
            throw new InvalidOperationException("XFS message window could not be created.");
    }

    private void Run()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        _proc = WndProc;
        var hInstance = NativeMethods.GetModuleHandle(null);
        const string className = "ATMNexusXfsSink";
        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInstance,
            lpszClassName = className
        };
        NativeMethods.RegisterClassEx(ref wc);
        Handle = NativeMethods.CreateWindowEx(0, className, "ATMNexus XFS sink", 0, 0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
        _ready.Set();

        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
        if (Handle != IntPtr.Zero) NativeMethods.DestroyWindow(Handle);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg is >= WM_USER + 1 and <= WFS_SYSTEM_EVENT)
        {
            try { _onMessage(msg, lParam); }
            catch (Exception ex) { _log.LogError(ex, "Unhandled exception processing XFS message {Msg}", msg); }
            finally
            {
                if (lParam != IntPtr.Zero) NativeMethods.WFSFreeResult(lParam);
            }
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) NativeMethods.PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
