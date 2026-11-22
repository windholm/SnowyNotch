using System;
using System.Runtime.InteropServices;

namespace SnowyNotch.Services;

/// <summary>
/// Пиковая громкость того, что сейчас звучит из динамиков (0…1), — для эффектов «в такт музыке».
/// Читает системный индикатор уровня (как полоска в микшере громкости): без захвата звука, без задержек.
/// Устройство вывода перечитывается раз в пару секунд — на случай смены наушников/колонок.
/// </summary>
public sealed class AudioMeter : IDisposable
{
    private IMMDeviceEnumerator? _enumerator;
    private IAudioMeterInformation? _meter;
    private DateTime _refreshAt;

    public float Peak()
    {
        try
        {
            if (DateTime.UtcNow >= _refreshAt) Refresh();
            if (_meter == null) return 0;
            return _meter.GetPeakValue(out float p) == 0 ? Math.Clamp(p, 0, 1) : 0;
        }
        catch
        {
            _meter = null;
            return 0;
        }
    }

    private void Refresh()
    {
        _refreshAt = DateTime.UtcNow.AddSeconds(2.5);
        try
        {
            _enumerator ??= (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            if (_enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device) != 0 || device == null)
            {
                _meter = null;
                return;
            }
            var iid = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object o) == 0) _meter = (IAudioMeterInformation)o;
        }
        catch
        {
            _meter = null;
        }
    }

    public void Dispose()
    {
        _meter = null;
        _enumerator = null;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
