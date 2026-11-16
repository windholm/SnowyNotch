using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace SnowyNotch.Services;

/// <summary>
/// Глобальная горячая клавиша через RegisterHotKey: система сама отказывает, если сочетание уже занято
/// (Windows или другой программой), — тогда берём следующее из списка. <see cref="Label"/> — то, что реально работает.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    public const uint Alt = 0x1, Ctrl = 0x2, Shift = 0x4, Win = 0x8;
    private const uint NoRepeat = 0x4000;
    private const int WM_HOTKEY = 0x0312;

    private static int _nextId = 0x6A00;
    private readonly HwndSource _window;
    private readonly int _id = _nextId++;

    public GlobalHotkey(params (uint Mods, uint Key, string Label)[] candidates)
    {
        _window = new HwndSource(new HwndSourceParameters("SnowyNotch.Hotkey." + _id) { WindowStyle = 0, ParentWindow = new IntPtr(-3) });
        _window.AddHook(WndProc);
        foreach (var (mods, key, label) in candidates)
        {
            if (!RegisterHotKey(_window.Handle, _id, mods | NoRepeat, key)) continue;
            Label = label;
            break;
        }
        Debug.WriteLine($"[hotkey] {(Label ?? "none")}");
    }

    /// <summary>Какое сочетание удалось занять (null — все заняты).</summary>
    public string? Label { get; }

    public event Action? Pressed;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterHotKey(_window.Handle, _id);
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
