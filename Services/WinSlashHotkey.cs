using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace SnowyNotch.Services;

public sealed class WinSlashHotkey : IDisposable
{
    private const int HotkeyId = 0x5E01;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;
    private const int VK_OEM_2 = 0xBF, VK_DIVIDE = 0x6F, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const ushort VK_MASK_KEY = 0xE8;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;

    private readonly Dispatcher _ui;
    private readonly HwndSource? _msgWindow;
    private readonly LowLevelKeyboardProc? _hookProc;
    private readonly IntPtr _hook;
    private bool _lwin, _rwin, _slashHeld;

    public WinSlashHotkey(Dispatcher ui)
    {
        _ui = ui;

        _msgWindow = new HwndSource(new HwndSourceParameters("SnowyNotch.Hotkey")
        {
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3),
        });
        _msgWindow.AddHook(WndProc);

        if (RegisterHotKey(_msgWindow.Handle, HotkeyId, MOD_WIN | MOD_NOREPEAT, VK_OEM_2))
        {
            Mode = "RegisterHotKey";
        }
        else
        {
            _hookProc = HookProc;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
            Mode = _hook != IntPtr.Zero ? "KeyboardHook" : "Unavailable";
        }
        Debug.WriteLine($"[hotkey] Win+/ via {Mode}");
    }

    public string Mode { get; }

    public event Action? Pressed;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;

            if (k.vkCode == VK_LWIN) _lwin = down || (!up && _lwin);
            else if (k.vkCode == VK_RWIN) _rwin = down || (!up && _rwin);
            else if ((k.vkCode == VK_OEM_2 || k.vkCode == VK_DIVIDE) && (_lwin || _rwin))
            {
                if (down && !_slashHeld)
                {
                    _slashHeld = true;
                    _ui.BeginInvoke(() =>
                    {
                        SendMaskKey();
                        Pressed?.Invoke();
                    });
                }
                else if (up)
                {
                    _slashHeld = false;
                }
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void SendMaskKey()
    {
        var inputs = new INPUT[2];
        inputs[0].type = 1;
        inputs[0].ki.wVk = VK_MASK_KEY;
        inputs[1].type = 1;
        inputs[1].ki.wVk = VK_MASK_KEY;
        inputs[1].ki.dwFlags = 0x0002;
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_msgWindow != null)
        {
            UnregisterHotKey(_msgWindow.Handle, HotkeyId);
            _msgWindow.Dispose();
        }
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
        [FieldOffset(8)] public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
