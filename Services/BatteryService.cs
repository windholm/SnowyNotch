using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace SnowyNotch.Services;

/// <summary>
/// Батарея ноутбука через GetSystemPowerStatus: есть ли она вообще, процент, идёт ли зарядка.
/// Сообщает о подключении/отключении зарядки и о низком заряде (20 % и 10 %).
/// </summary>
public sealed class BatteryService : IDisposable
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool? _lastCharging;
    private int _lastWarned = 101;

    public BatteryService()
    {
        Read();
        _lastCharging = Charging;
        _poll.Tick += (_, _) => Tick();
    }

    /// <summary>Опрашивать только когда показ батареи включён и батарея вообще есть (на ПК — никогда).</summary>
    public bool Enabled
    {
        get => _poll.IsEnabled;
        set
        {
            value &= HasBattery;
            if (value == _poll.IsEnabled) return;
            if (value)
            {
                Read();
                _lastCharging = Charging; // не объявлять «зарядку подключили» про то, что было и раньше
                _lastWarned = 101;
                _poll.Start();
            }
            else _poll.Stop();
        }
    }

    /// <summary>У компьютера есть батарея (ноутбук/планшет).</summary>
    public bool HasBattery { get; private set; }
    public int Percent { get; private set; }
    public bool Charging { get; private set; }

    /// <summary>Зарядку подключили (true) или отключили (false).</summary>
    public event Action<bool>? PlugChanged;
    /// <summary>Заряд опустился до порога предупреждения.</summary>
    public event Action? Low;

    public static bool DetectBattery() => GetSystemPowerStatus(out var s) && HasBatteryFlag(s);

    private void Tick()
    {
        Read();
        if (!HasBattery) return;
        if (_lastCharging != Charging)
        {
            _lastCharging = Charging;
            _lastWarned = 101;
            PlugChanged?.Invoke(Charging);
            return;
        }
        if (!Charging)
        {
            int threshold = Percent <= 10 ? 10 : Percent <= 20 ? 20 : 101;
            if (threshold < _lastWarned)
            {
                _lastWarned = threshold;
                Low?.Invoke();
            }
        }
    }

    private void Read()
    {
        if (!GetSystemPowerStatus(out var s)) { HasBattery = false; return; }
        HasBattery = HasBatteryFlag(s);
        Percent = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : 0;
        Charging = s.ACLineStatus == 1;
    }

    // 128 — батареи нет, 255 — неизвестно.
    private static bool HasBatteryFlag(SYSTEM_POWER_STATUS s) => s.BatteryFlag != 128 && s.BatteryFlag != 255 && s.BatteryLifePercent <= 100;

    public void Dispose() => _poll.Stop();

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
