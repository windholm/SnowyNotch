using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Devices.Enumeration;

namespace SnowyNotch.Services;

public sealed record HeadsetInfo(string Name, int? Battery);

public sealed class BluetoothAudioWatcher
{
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const int LowBattery = 15;

    private readonly VolumeService _volume;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _batteryPoll = new() { Interval = TimeSpan.FromMinutes(5) };
    private HashSet<string> _known = new();
    private bool _primed;
    private readonly HashSet<string> _warnedLow = new();

    public BluetoothAudioWatcher(VolumeService volume)
    {
        _volume = volume;
        _poll.Tick += (_, _) => Poll();
        _batteryPoll.Tick += async (_, _) => await CheckBatteriesAsync();
    }

    /// <summary>
    /// Следить за наушниками только когда пузырь включён в настройках: опрос перебирает все аудиоустройства
    /// каждые 2 с, а при выключенной настройке его результат всё равно никуда не идёт.
    /// </summary>
    public bool Enabled
    {
        get => _poll.IsEnabled;
        set
        {
            if (value == _poll.IsEnabled) return;
            if (value)
            {
                _primed = false; // уже подключённые наушники не считаем «только что подключёнными»
                Poll();
                _poll.Start();
                _batteryPoll.Start();
            }
            else
            {
                _poll.Stop();
                _batteryPoll.Stop();
            }
        }
    }

    public event Action<HeadsetInfo>? Connected;
    public event Action<HeadsetInfo>? BatteryLow;

    public IReadOnlyList<AudioDevice> Current { get; private set; } = Array.Empty<AudioDevice>();

    private async void Poll()
    {
        var bt = _volume.ActiveDevices().Where(d => d.IsBluetooth).ToList();
        Current = bt;
        var ids = bt.Select(d => d.Id).ToHashSet();

        if (_primed)
        {
            foreach (var d in bt.Where(d => !_known.Contains(d.Id)))
            {
                int? battery = await QueryBatteryAsync(d.Name);
                Connected?.Invoke(new HeadsetInfo(CleanName(d.Name), battery));
            }
        }
        _known = ids;
        _primed = true;
    }

    private async Task CheckBatteriesAsync()
    {
        foreach (var d in Current)
        {
            int? battery = await QueryBatteryAsync(d.Name);
            if (battery is int b && b <= LowBattery && _warnedLow.Add(d.Id))
                BatteryLow?.Invoke(new HeadsetInfo(CleanName(d.Name), b));
            if (battery is int ok && ok > LowBattery) _warnedLow.Remove(d.Id);
        }
    }

    public static async Task<int?> QueryBatteryAsync(string endpointName)
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync("", new[] { BatteryKey }, DeviceInformationKind.Device);
            foreach (var d in devices)
            {
                if (!d.Properties.TryGetValue(BatteryKey, out var value) || value == null) continue;
                if (string.IsNullOrWhiteSpace(d.Name)) continue;
                if (endpointName.Contains(d.Name, StringComparison.OrdinalIgnoreCase) ||
                    d.Name.Contains(CleanName(endpointName), StringComparison.OrdinalIgnoreCase))
                    return Convert.ToInt32(value);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[bluetooth] battery query failed: {ex.Message}");
        }
        return null;
    }

    public static string CleanName(string endpointName)
    {
        int open = endpointName.IndexOf('('), close = endpointName.LastIndexOf(')');
        return open >= 0 && close > open ? endpointName[(open + 1)..close].Trim() : endpointName;
    }
}
