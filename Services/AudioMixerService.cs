using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using SnowyNotch.Localization;

namespace SnowyNotch.Services;

/// <summary>Одна программа в микшере (несколько её аудиосессий сведены в одну строку).</summary>
public sealed class MixerApp
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public ImageSource? Icon { get; init; }
    public float Volume { get; set; }
    public bool Muted { get; set; }
    public float Peak { get; set; }
    public bool Active { get; set; }
    public bool IsSystem { get; init; }
    internal List<object> Sessions { get; } = new();
}

/// <summary>
/// Громкость по программам — то же, что «Микшер громкости» Windows: аудиосессии устройства вывода по умолчанию
/// (IAudioSessionManager2), их громкость/без звука (ISimpleAudioVolume) и текущий уровень (IAudioMeterInformation).
/// Опрашивается, только пока панель открыта.
/// </summary>
public sealed class AudioMixerService
{
    private static readonly Guid OurContext = Guid.NewGuid();
    private readonly Dictionary<string, (string Name, ImageSource? Icon)> _identity = new();

    /// <summary>Снимок программ, которые сейчас (или недавно) выводят звук. Сначала играющие.</summary>
    private List<MixerApp> _last = new();

    public List<MixerApp> Snapshot()
    {
        // COM-объекты прошлого снимка больше не нужны.
        foreach (var o in _last.SelectMany(a => a.Sessions))
            try { Marshal.ReleaseComObject(o); } catch { }
        _last = new();

        var apps = new Dictionary<string, MixerApp>();
        try
        {
            // Не «new MMDeviceEnumeratorCom()»: Windows отдаёт перечислитель как общий объект, и если его уже
            // создала служба громкости через свой ComImport-класс, приведение к нашему классу падало
            // (InvalidCastException) — микшер всегда был пуст. Приведение только к интерфейсу работает всегда.
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(MMDeviceEnumeratorClsid)!)!;
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0) return new();
            var iid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref iid, 23, IntPtr.Zero, out var obj);
            var manager = (IAudioSessionManager2)obj;
            manager.GetSessionEnumerator(out var sessions);
            sessions.GetCount(out int count);

            for (int i = 0; i < count; i++)
            {
                sessions.GetSession(i, out var control);
                if (control is not IAudioSessionControl2 c2) continue;
                c2.GetState(out int state);
                if (state == 2) continue; // истёкшая

                c2.GetProcessId(out uint pid);
                bool system = c2.IsSystemSoundsSession() == 0;
                var (key, name, icon) = Identify(pid, system, c2);
                if (key.Length == 0) continue;

                var vol = (ISimpleAudioVolume)control;
                vol.GetMasterVolume(out float level);
                vol.GetMute(out bool muted);
                float peak = 0;
                if (control is IAudioMeterInformation meter) meter.GetPeakValue(out peak);

                if (!apps.TryGetValue(key, out var app))
                {
                    app = new MixerApp { Key = key, Name = name, Icon = icon, Volume = level, Muted = muted, IsSystem = system };
                    apps[key] = app;
                }
                app.Sessions.Add(control);
                app.Peak = Math.Max(app.Peak, peak);
                app.Active |= state == 1;
                if (state == 1) { app.Volume = level; app.Muted = muted; }
            }
            Marshal.ReleaseComObject(sessions);
            Marshal.ReleaseComObject(manager);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[mixer] snapshot failed: {ex.Message}");
        }
        _last = apps.Values.OrderByDescending(a => a.Active).ThenBy(a => a.IsSystem).ThenBy(a => a.Name).ToList();
        return _last;
    }

    public void SetVolume(MixerApp app, float level)
    {
        level = Math.Clamp(level, 0, 1);
        var ctx = OurContext;
        foreach (var o in app.Sessions)
        {
            try
            {
                if (o is not ISimpleAudioVolume s) continue;
                s.SetMasterVolume(level, ref ctx);
                if (app.Muted && level > 0) s.SetMute(false, ref ctx);
            }
            catch { /* сессия уже закрылась */ }
        }
        app.Volume = level;
        if (level > 0) app.Muted = false;
    }

    public void ToggleMute(MixerApp app)
    {
        var ctx = OurContext;
        bool mute = !app.Muted;
        foreach (var o in app.Sessions)
        {
            try { if (o is ISimpleAudioVolume s) s.SetMute(mute, ref ctx); } catch { }
        }
        app.Muted = mute;
    }

    private (string Key, string Name, ImageSource? Icon) Identify(uint pid, bool system, IAudioSessionControl2 c2)
    {
        if (system) return ("system", L.T("Системные звуки"), null);
        if (pid == 0) return ("", "", null);
        string cacheKey = pid.ToString();
        if (_identity.TryGetValue(cacheKey, out var known)) return (KeyOf(known.Name), known.Name, known.Icon);

        string name = "", path = "";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            name = p.ProcessName;
            try
            {
                path = p.MainModule?.FileName ?? "";
                var desc = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) name = desc!;
            }
            catch { /* чужой или защищённый процесс — останется имя процесса */ }
        }
        catch { return ("", "", null); }

        if (c2.GetDisplayName(out string display) == 0 && !string.IsNullOrWhiteSpace(display) && !display.StartsWith('@')) name = display;
        name = Friendly(name);
        ImageSource? icon = path.Length > 0 ? TrayThumbnails.Shell(path, 48, iconOnly: true) : null;
        _identity[cacheKey] = (name, icon);
        if (_identity.Count > 200) _identity.Clear();
        return (KeyOf(name), name, icon);
    }

    private static string KeyOf(string name) => name.ToLowerInvariant();

    private static string Friendly(string name) => name switch
    {
        "msedge" or "Microsoft Edge" => "Edge",
        "chrome" or "Google Chrome" => "Chrome",
        "firefox" => "Firefox",
        "Spotify" or "spotify" => "Spotify",
        _ => name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : name,
    };

    // ---------- COM ----------

    private static readonly Guid MMDeviceEnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

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

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object session);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
        [PreserveSig] int GetGroupingParam(out Guid param);
        [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid ctx);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
