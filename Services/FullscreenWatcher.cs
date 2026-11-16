using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;

namespace SnowyNotch.Services;

public sealed class FullscreenWatcher : IDisposable
{
    private static readonly string[] ShellClasses =
        { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    private static readonly string[] TransientClasses =
        { "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "ForegroundStaging",
          "Windows.UI.Core.CoreWindow", "TaskSwitcherWnd" };

    private readonly IntPtr _ownHwnd;
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly WinEventDelegate _hookProc;
    private readonly IntPtr _hook;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(700) };

    public FullscreenWatcher(IntPtr ownHwnd)
    {
        _ownHwnd = ownHwnd;
        _hookProc = (_, _, _, _, _, _, _) => Check();
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _hookProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        _poll.Tick += (_, _) => Check();
        _poll.Start();
        Check();
    }

    /// <summary>Опрос нужен, только пока включено «Прятать в играх и видео»; при включении — сразу проверка.</summary>
    public bool Enabled
    {
        get => _poll.IsEnabled;
        set
        {
            if (value == _poll.IsEnabled) return;
            if (value) { _poll.Start(); Check(); } // сначала Start: Check может вызвать Changed → снова этот сеттер
            else _poll.Stop();
        }
    }

    public bool IsFullscreen { get; private set; }

    public event Action<bool>? Changed;

    public void Check()
    {
        bool? verdict;
        try { verdict = Evaluate(); }
        catch (Exception ex) { Debug.WriteLine($"[fullscreen] {ex.Message}"); return; }

        if (verdict is not bool fs || fs == IsFullscreen) return;
        IsFullscreen = fs;
        Changed?.Invoke(fs);
    }

    private bool? Evaluate()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == _ownHwnd) return null;

        GetWindowThreadProcessId(fg, out uint pid);
        if (pid == _ownPid) return null;

        string cls = GetClassName(fg);
        if (Array.IndexOf(ShellClasses, cls) >= 0) return false;
        if (Array.IndexOf(TransientClasses, cls) >= 0) return null;

        IntPtr fgMonitor = MonitorFromWindow(fg, MONITOR_DEFAULTTONULL);
        IntPtr ownMonitor = MonitorFromWindow(_ownHwnd, MONITOR_DEFAULTTOPRIMARY);
        if (fgMonitor == IntPtr.Zero || fgMonitor != ownMonitor) return false;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(fgMonitor, ref mi) || !GetWindowRect(fg, out RECT r)) return null;

        var m = mi.rcMonitor;
        bool coversMonitor = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
        if (!coversMonitor) return false;

        long style = GetWindowLongPtr(fg, GWL_STYLE).ToInt64();
        if (IsZoomed(fg) && (style & WS_CAPTION) == WS_CAPTION) return false;

        return true;
    }

    public void Dispose()
    {
        _poll.Stop();
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }

    private readonly StringBuilder _classBuf = new(256); // проверка идёт часто — без новой строки-буфера каждый раз

    private string GetClassName(IntPtr hwnd)
    {
        _classBuf.Clear();
        return GetClassNameW(hwnd, _classBuf, _classBuf.Capacity) > 0 ? _classBuf.ToString() : "";
    }

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint MONITOR_DEFAULTTONULL = 0;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int GWL_STYLE = -16;
    private const long WS_CAPTION = 0x00C00000;

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
}
