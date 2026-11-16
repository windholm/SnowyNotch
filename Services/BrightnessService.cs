using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace SnowyNotch.Services;

public sealed class BrightnessService : IDisposable
{
    private readonly Dispatcher _ui;
    private volatile bool _stop;
    private int _pendingSet = -1;
    private int _setWorkerRunning;

    public BrightnessService(Dispatcher ui)
    {
        _ui = ui;
        var thread = new Thread(Listen) { IsBackground = true, Name = "SnowyNotch.Brightness" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public bool Available { get; private set; }
    public int Level { get; private set; }

    public event Action<int>? Changed;

    private void Listen()
    {
        try
        {
            dynamic services = Connect();
            foreach (dynamic o in services.ExecQuery("SELECT CurrentBrightness FROM WmiMonitorBrightness"))
            {
                Level = Convert.ToInt32(o.CurrentBrightness);
                Available = true;
                break;
            }
            if (!Available) return;

            dynamic events = services.ExecNotificationQuery("SELECT * FROM WmiMonitorBrightnessEvent");
            while (!_stop)
            {
                try
                {
                    dynamic ev = events.NextEvent(1000);
                    int level = Convert.ToInt32(ev.Brightness);
                    _ui.BeginInvoke(() =>
                    {
                        if (level == Level) return;
                        Level = level;
                        Changed?.Invoke(level);
                    });
                }
                catch (COMException)
                {
                }
            }
        }
        catch (Exception ex)
        {
            Available = false;
            Debug.WriteLine($"[brightness] unavailable: {ex.Message}");
        }
    }

    public void SetLevel(int level)
    {
        if (!Available) return;
        Interlocked.Exchange(ref _pendingSet, Math.Clamp(level, 0, 100));
        if (Interlocked.CompareExchange(ref _setWorkerRunning, 1, 0) != 0) return;

        Task.Run(() =>
        {
            try
            {
                dynamic services = Connect();
                int value;
                while ((value = Interlocked.Exchange(ref _pendingSet, -1)) >= 0)
                {
                    foreach (dynamic m in services.ExecQuery("SELECT * FROM WmiMonitorBrightnessMethods"))
                        m.WmiSetBrightness(1, (byte)value);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[brightness] set failed: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _setWorkerRunning, 0);
            }
        });
    }

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator") ?? throw new InvalidOperationException("WMI scripting is unavailable");
        dynamic locator = Activator.CreateInstance(type)!;
        return locator.ConnectServer(".", "root\\WMI");
    }

    public void Dispose() => _stop = true;
}
