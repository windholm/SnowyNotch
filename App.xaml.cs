using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using SnowyNotch.Services;
using Forms = System.Windows.Forms;
using SnowyNotch.Localization;

namespace SnowyNotch;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private Forms.NotifyIcon? _tray;
    private MainWindow? _island;
    private WinSlashHotkey? _hotkey;
    private GlobalHotkey? _audioHotkey;

    /// <summary>Сочетание, которое реально открывает «Звук» (для настроек).</summary>
    public static string? AudioHotkeyLabel { get; private set; }

    /// <summary>Режим съёмки скриншотов для README (только Debug, --shots=папка): чистые настройки, демо-данные, ничего не сохраняется.</summary>
    public static bool ShotMode { get; private set; }
    private readonly ToolsService _tools = new();
    private readonly TrayService _files = new();
    private AppSettings _settings = new();
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromMilliseconds(400) };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Codex вызывает notify как «SnowyNotch.exe --codex-notify <json>»: переслать в работающий остров и выйти.
        int notifyAt = Array.IndexOf(e.Args, CodexIntegration.NotifyArg);
        if (notifyAt >= 0)
        {
            ForwardCodexNotify(notifyAt + 1 < e.Args.Length ? e.Args[^1] : "");
            Shutdown();
            return;
        }

#if DEBUG
        // Скриншоты для README: настройки по умолчанию на английском, без трея, хоткеев и сохранения.
        string? shots = Array.Find(e.Args, a => a.StartsWith("--shots="));
        if (shots != null)
        {
            ShotMode = true;
            // Главная как у нового пользователя: по умолчанию там только музыка.
            _settings = new AppSettings { FirstRunDone = true, Language = "en", HideInFullscreen = false };
            Localization.L.Init(_settings.Language);
            _island = new MainWindow(_settings, _tools, _files);
            _island.Show();
            string dir = shots["--shots=".Length..];
            Dispatcher.InvokeAsync(() => _island.RunShots(dir, _files, _tools), DispatcherPriority.ApplicationIdle);
            return;
        }
#endif

        _singleInstance = new Mutex(true, @"Local\SnowyNotch.SingleInstance", out bool isFirst);
        // Перезапуск (смена языка): прежний остров ещё закрывается — ждём, пока он отпустит мьютекс.
        if (!isFirst && Array.IndexOf(e.Args, RelaunchArg) >= 0)
        {
            try { isFirst = _singleInstance.WaitOne(TimeSpan.FromSeconds(8)); }
            catch (AbandonedMutexException) { isFirst = true; }
        }
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            LogError(args.Exception);
            args.Handled = true;
        };

        _settings = AppSettings.Load();
        Localization.L.Init(_settings.Language);
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); _settings.Save(); };
        _settings.PropertyChanged += (_, _) => { _saveDebounce.Stop(); _saveDebounce.Start(); };

        _island = new MainWindow(_settings, _tools, _files);
        _island.Show();

#if DEBUG
        foreach (var arg in e.Args)
        {
            if (arg.StartsWith("--preview="))
            {
                string what = arg["--preview=".Length..];
                Dispatcher.InvokeAsync(() =>
                {
                    try { _island.DebugPreview(what); }
                    catch (Exception ex) { LogError(ex); }
                }, DispatcherPriority.ApplicationIdle);
            }
            if (arg.StartsWith("--demo-files="))
                _files.Add(arg["--demo-files=".Length..].Split('|'));
            if (arg == "--demo-tools")
            {
                _tools.Timer.SetDuration(TimeSpan.FromMinutes(5));
                _tools.Timer.Start();
                _tools.Stopwatch.Toggle();
            }
        }
#endif

        _hotkey = new WinSlashHotkey(Dispatcher);
        _hotkey.Pressed += () => _island?.ToggleSettings();

        // «Звук»: Win+Alt+V (V — volume); если занято — запасные сочетания.
        const uint V = 0x56;
        _audioHotkey = new GlobalHotkey(
            (GlobalHotkey.Win | GlobalHotkey.Alt, V, "Win + Alt + V"),
            (GlobalHotkey.Ctrl | GlobalHotkey.Alt | GlobalHotkey.Shift, V, "Ctrl + Alt + Shift + V"),
            (GlobalHotkey.Win | GlobalHotkey.Ctrl | GlobalHotkey.Alt, V, "Win + Ctrl + Alt + V"));
        _audioHotkey.Pressed += () => _island?.ToggleAudio();
        AudioHotkeyLabel = _audioHotkey.Label;

        CreateTrayIcon();

        // После перезапуска из настроек (смена языка) — вернуть пользователя туда же.
        int settingsAt = Array.IndexOf(e.Args, "--settings");
        if (settingsAt >= 0 && settingsAt + 1 < e.Args.Length)
        {
            string section = e.Args[settingsAt + 1];
            Dispatcher.InvokeAsync(() => _island?.OpenSettings(section), DispatcherPriority.ApplicationIdle);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_saveDebounce.IsEnabled) _settings.Save();
        _hotkey?.Dispose();
        _audioHotkey?.Dispose();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        try { _singleInstance?.ReleaseMutex(); } catch { /* не владели — второй экземпляр */ }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OpenSettings() => _island?.OpenSettings();

    private const string RelaunchArg = "--relaunch";

    /// <summary>
    /// Перезапустить остров (язык применяется при запуске): сохранить настройки, запустить новый экземпляр,
    /// который откроет настройки на разделе <paramref name="section"/>, и закрыться.
    /// </summary>
    public static void Relaunch(string section)
    {
        if (Current is not App app) return;
        app._settings.Save();
        string? exe = Environment.ProcessPath;
        if (exe == null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"{RelaunchArg} --settings {section}") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            LogError(ex);
            return;
        }
        app.Shutdown();
    }

    private static void ForwardCodexNotify(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
            http.PostAsync(AgentBridge.CodexUrl, content).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogError(ex); // остров не запущен — ничего страшного, Codex это не ломает
        }
    }

    internal static void LogError(Exception ex)
    {
        try
        {
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnowyNotch");
            System.IO.Directory.CreateDirectory(dir);
            string log = System.IO.Path.Combine(dir, "errors.log");
            // Журнал не растёт бесконечно: больше 512 КБ — прежний уходит в errors.old.log (одна копия).
            var info = new System.IO.FileInfo(log);
            if (info.Exists && info.Length > 512 * 1024)
                System.IO.File.Move(log, System.IO.Path.Combine(dir, "errors.old.log"), overwrite: true);
            System.IO.File.AppendAllText(log, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch {  }
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(L.T("Настройки        Win + /"), null, (_, _) => OpenSettings());
        menu.Items.Add(L.T("Показать остров"), null, (_, _) => _island?.ShowIsland());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(L.T("Выход"), null, (_, _) => Shutdown());

        _tray = new Forms.NotifyIcon
        {
            Icon = CreatePillIcon(),
            Text = "SnowyNotch",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) OpenSettings();
        };
    }

    private static Icon CreatePillIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(System.Drawing.Color.Transparent);
            using var path = new GraphicsPath();
            var r = new RectangleF(2, 10, 28, 12);
            float d = r.Height;
            path.AddArc(r.X, r.Y, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            path.CloseFigure();
            using var fill = new SolidBrush(System.Drawing.Color.Black);
            using var pen = new Pen(System.Drawing.Color.FromArgb(200, 255, 255, 255), 1.5f);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }
        // Icon.FromHandle не владеет дескриптором: делаем собственную копию и освобождаем системный HICON.
        IntPtr hIcon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
