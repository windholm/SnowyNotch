using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SnowyNotch.Services;

public sealed class MiddleClickHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MBUTTONDOWN = 0x0207, WM_MBUTTONUP = 0x0208;
    private static readonly TimeSpan LongPressTime = TimeSpan.FromMilliseconds(600);

    private readonly Func<int, int, bool> _isOverIsland;
    private readonly LowLevelMouseProc _proc;
    private readonly IntPtr _hook;
    private readonly DispatcherTimer _longPress = new() { Interval = LongPressTime };
    private bool _down, _longFired;

    public MiddleClickHook(Func<int, int, bool> isOverIsland)
    {
        _isOverIsland = isOverIsland;
        _proc = HookProc;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Debug.WriteLine("[middle] hook failed");
        _longPress.Tick += (_, _) =>
        {
            _longPress.Stop();
            if (!_down) return;
            _longFired = true;
            LongPress?.Invoke();
        };
    }

    public bool Enabled { get; set; } = true;

    public event Action? Click;
    public event Action? LongPress;

    public event Action<int, int>? ButtonDown;

    private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204;

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() is WM_LBUTTONDOWN or WM_RBUTTONDOWN && ButtonDown != null)
        {
            var down = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            try { ButtonDown(down.pt.x, down.pt.y); } catch {  }
        }

        if (nCode >= 0 && Enabled)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_MBUTTONDOWN or WM_MBUTTONUP)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                bool ours = msg == WM_MBUTTONUP ? _down : SafeIsOver(info.pt.x, info.pt.y);
                if (ours)
                {
                    if (msg == WM_MBUTTONDOWN)
                    {
                        _down = true;
                        _longFired = false;
                        _longPress.Stop();
                        _longPress.Start();
                    }
                    else
                    {
                        _down = false;
                        _longPress.Stop();
                        if (!_longFired) Click?.Invoke();
                    }
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool SafeIsOver(int x, int y)
    {
        try { return _isOverIsland(x, y); }
        catch { return false; }
    }

    public void Dispose()
    {
        _longPress.Stop();
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
