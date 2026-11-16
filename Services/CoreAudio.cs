using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using SnowyNotch.Localization;

namespace SnowyNotch.Services;

public sealed record AudioDevice(string Id, string Name, bool IsBluetooth);

public sealed class VolumeService : IDisposable
{
    private static readonly Guid OurContext = Guid.NewGuid();

    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _deviceCheck = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly VolumeCallback _callback;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;
    private string? _deviceId;

    public VolumeService(Dispatcher ui)
    {
        _ui = ui;
        _callback = new VolumeCallback(this);
        try
        {
            // Через CLSID, а не ComImport-класс: перечислитель — общий объект, и приведение к «своему» классу
            // ломается, если его первым создал кто-то другой в процессе.
            _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            AttachDefault();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[volume] init failed: {ex.Message}");
        }
        _deviceCheck.Tick += (_, _) => AttachDefault();
        _deviceCheck.Start();
    }

    public float Volume { get; private set; }
    public bool Muted { get; private set; }
    public bool Available => _endpoint != null;

    public event Action<float, bool, bool>? Changed;

    public void SetVolume(float level)
    {
        if (_endpoint == null) return;
        level = Math.Clamp(level, 0, 1);
        var ctx = OurContext;
        _endpoint.SetMasterVolumeLevelScalar(level, ref ctx);
        if (Muted && level > 0) _endpoint.SetMute(false, ref ctx);
    }

    public void ToggleMute()
    {
        if (_endpoint == null) return;
        var ctx = OurContext;
        _endpoint.SetMute(!Muted, ref ctx);
    }

    public List<AudioDevice> ActiveDevices()
    {
        var list = new List<AudioDevice>();
        if (_enumerator == null) return list;
        try
        {
            _enumerator.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE_ACTIVE, out var collection);
            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                collection.Item(i, out var device);
                device.GetId(out var id);
                device.OpenPropertyStore(0 , out var store);
                string name = ReadString(store, PKEY_Device_FriendlyName) ?? L.T("Устройство");
                string enumerator = ReadString(store, PKEY_Device_EnumeratorName) ?? "";
                list.Add(new AudioDevice(id, name, enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase)));
                Marshal.ReleaseComObject(store);
                Marshal.ReleaseComObject(device);
            }
            Marshal.ReleaseComObject(collection);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[volume] enumerate failed: {ex.Message}");
        }
        return list;
    }

    private void AttachDefault()
    {
        if (_enumerator == null) return;
        try
        {
            if (_enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device) != 0) return;
            device.GetId(out var id);
            if (id == _deviceId) { Marshal.ReleaseComObject(device); return; }

            Detach();
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
            _endpoint = (IAudioEndpointVolume)obj;
            _endpoint.RegisterControlChangeNotify(_callback);
            _deviceId = id;
            _endpoint.GetMasterVolumeLevelScalar(out float v);
            _endpoint.GetMute(out bool m);
            Volume = v;
            Muted = m;
            Marshal.ReleaseComObject(device);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[volume] attach failed: {ex.Message}");
        }
    }

    private void Detach()
    {
        if (_endpoint == null) return;
        try { _endpoint.UnregisterControlChangeNotify(_callback); } catch {  }
        Marshal.ReleaseComObject(_endpoint);
        _endpoint = null;
    }

    private void OnNotify(Guid context, bool muted, float level)
    {
        _ui.BeginInvoke(() =>
        {
            bool changed = Math.Abs(level - Volume) > 0.0005f || muted != Muted;
            Volume = level;
            Muted = muted;
            if (changed) Changed?.Invoke(level, muted, context != OurContext);
        });
    }

    public void Dispose()
    {
        _deviceCheck.Stop();
        Detach();
    }

    private static string? ReadString(IPropertyStore store, PROPERTYKEY key)
    {
        var k = key;
        if (store.GetValue(ref k, out var pv) != 0) return null;
        try { return pv.vt == 31  ? Marshal.PtrToStringUni(pv.pointer) : null; }
        finally { PropVariantClear(ref pv); }
    }

    private const uint DEVICE_STATE_ACTIVE = 1;
    private const uint CLSCTX_ALL = 23;

    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly PROPERTYKEY PKEY_Device_EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    [ComVisible(true)]
    private sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly VolumeService _owner;
        public VolumeCallback(VolumeService owner) => _owner = owner;

        public int OnNotify(IntPtr data)
        {
            var ctx = Marshal.PtrToStructure<Guid>(data);
            bool muted = Marshal.ReadInt32(data, 16) != 0;
            float level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
            _owner.OnNotify(ctx, muted, level);
            return 0;
        }
    }

    private enum EDataFlow { eRender, eCapture, eAll }
    private enum ERole { eConsole, eMultimedia, eCommunications }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PROPERTYKEY
    {
        public readonly Guid fmtid;
        public readonly uint pid;
        public PROPERTYKEY(Guid f, uint p) { fmtid = f; pid = p; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);


    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr notifyData);
    }
}
