using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace SnowyNotch.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IslandPreset
{
    DynamicIsland,
    MacBookNotch,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IslandFinish
{
    DynamicGlass,
    Glass,
    Black,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NavigationMode
{
    Scroll,
    Buttons,
    Both,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NavButtonStyle
{
    BottomBar,
    TopTabs,
}

public sealed class AppSettings : INotifyPropertyChanged
{
    public const int MaxHomeWidgets = 3;

    public static readonly string[] AccentPresets =
    {
        "#0A84FF", "#5E5CE6", "#BF5AF2", "#FF375F", "#FF453A", "#FF9F0A",
        "#FFD60A", "#30D158", "#63E6E2", "#64D2FF", "#AC8E68", "#F2F2F7",
    };

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnowyNotch", "settings.json");

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "SnowyNotch";

    private IslandPreset _preset = IslandPreset.MacBookNotch;
    private IslandFinish _finish = IslandFinish.DynamicGlass;
    private double _glassIntensity = 0.4;
    private bool _subtleOutline = true;
    private string _accentColor = "#0A84FF";
    private double _length = 1.0;
    private double _scale = 1.0;
    private double _topOffset = 8;
    private double _animationSpeed = 1.0;

    private bool _expandOnHover = true;
    private double _hoverDelayMs = 120;
    private double _collapseDelayMs = 300;
    private bool _peekOnTrackChange = true;
    private double _peekDurationSec = 3.5;
    private bool _hideWhenIdle;
    private bool _hideInFullscreen = true;

    private bool _showVisualizer = true;
    private bool _accentFromArtwork = true;
    private bool _showShuffleButton;
    private bool _showRepeatButton;
    private bool _showSource = true;

    private bool _middleClickGhost = true;
    private bool _showHud = true;
    private bool _showBluetooth = true;
    private bool _lyricsEnabled = true;
    private bool _musicOnly = true;
    private bool _showBattery;
    private bool _showAgents = true;
    private bool _codexEnabled;
    private bool _claudeUsageEnabled;

    // По умолчанию на главной только музыка; остальное пользователь добавляет сам.
    private List<string> _homeWidgets = new() { "media" };
    private NavigationMode _navigation = NavigationMode.Both;
    private NavButtonStyle _navButtonStyle = NavButtonStyle.BottomBar;
    private bool _firstRunDone;

    public IslandPreset Preset { get => _preset; set => Set(ref _preset, value); }
    public IslandFinish Finish { get => _finish; set => Set(ref _finish, value); }

    public double GlassIntensity { get => _glassIntensity; set => Set(ref _glassIntensity, Math.Clamp(value, 0, 1)); }

    public bool SubtleOutline { get => _subtleOutline; set => Set(ref _subtleOutline, value); }

    public string AccentColor { get => _accentColor; set => Set(ref _accentColor, value); }

    /// <summary>Язык интерфейса: код ("en", "ru", "uk", …) или "" — как в системе.</summary>
    public string Language { get => _language; set => Set(ref _language, value ?? ""); }
    private string _language = "";

    public double Length { get => _length; set => Set(ref _length, Math.Clamp(value, 0.6, 1.6)); }

    public double Scale { get => _scale; set => Set(ref _scale, Math.Clamp(value, 0.8, 1.3)); }

    public double TopOffset { get => _topOffset; set => Set(ref _topOffset, Math.Clamp(value, 0, 40)); }

    public double AnimationSpeed { get => _animationSpeed; set => Set(ref _animationSpeed, Math.Clamp(value, 0.6, 1.6)); }

    public bool ExpandOnHover { get => _expandOnHover; set => Set(ref _expandOnHover, value); }
    public double HoverDelayMs { get => _hoverDelayMs; set => Set(ref _hoverDelayMs, Math.Clamp(value, 0, 800)); }
    public double CollapseDelayMs { get => _collapseDelayMs; set => Set(ref _collapseDelayMs, Math.Clamp(value, 100, 2000)); }
    public bool PeekOnTrackChange { get => _peekOnTrackChange; set => Set(ref _peekOnTrackChange, value); }
    public double PeekDurationSec { get => _peekDurationSec; set => Set(ref _peekDurationSec, Math.Clamp(value, 1, 10)); }
    public bool HideWhenIdle { get => _hideWhenIdle; set => Set(ref _hideWhenIdle, value); }
    public bool HideInFullscreen { get => _hideInFullscreen; set => Set(ref _hideInFullscreen, value); }

    public bool ShowVisualizer { get => _showVisualizer; set => Set(ref _showVisualizer, value); }
    public bool AccentFromArtwork { get => _accentFromArtwork; set => Set(ref _accentFromArtwork, value); }
    public bool ShowShuffleButton { get => _showShuffleButton; set => Set(ref _showShuffleButton, value); }
    public bool ShowRepeatButton { get => _showRepeatButton; set => Set(ref _showRepeatButton, value); }

    /// <summary>Показывать рядом с исполнителем, откуда играет музыка (Spotify, SoundCloud, браузер).</summary>
    public bool ShowSource { get => _showSource; set => Set(ref _showSource, value); }

    public bool MiddleClickGhost { get => _middleClickGhost; set => Set(ref _middleClickGhost, value); }

    public bool ShowHud { get => _showHud; set => Set(ref _showHud, value); }

    public bool ShowBluetooth { get => _showBluetooth; set => Set(ref _showBluetooth, value); }

    public bool LyricsEnabled { get => _lyricsEnabled; set => Set(ref _lyricsEnabled, value); }

    /// <summary>Плеер только для музыки: музыкальные приложения и сайты, а не YouTube, видео и любой звук.</summary>
    public bool MusicOnly { get => _musicOnly; set => Set(ref _musicOnly, value); }

    /// <summary>Ноутбук: показывать зарядку и низкий заряд. По умолчанию выключено.</summary>
    public bool ShowBattery { get => _showBattery; set => Set(ref _showBattery, value); }

    [JsonIgnore] public bool HasBattery => BatteryService.DetectBattery();

    public bool ShowAgents { get => _showAgents; set => Set(ref _showAgents, value); }

    /// <summary>Интеграция Codex: читать журналы сессий ~/.codex/sessions (и notify, если подключён).</summary>
    public bool CodexEnabled { get => _codexEnabled; set => Set(ref _codexEnabled, value); }

    /// <summary>Показывать лимиты Claude: запрос к api.anthropic.com со входом Claude Code. Только по согласию.</summary>
    public bool ClaudeUsageEnabled { get => _claudeUsageEnabled; set => Set(ref _claudeUsageEnabled, value); }

    public List<string> HomeWidgets
    {
        get => _homeWidgets;
        set
        {
            // Виджет «claude» стал общим «agents».
            var clean = (value ?? new List<string>()).Select(id => id == "claude" ? "agents" : id)
                .Distinct().Take(MaxHomeWidgets).ToList();
            if (clean.SequenceEqual(_homeWidgets)) return;
            _homeWidgets = clean;
            OnPropertyChanged();
        }
    }

    public NavigationMode Navigation { get => _navigation; set => Set(ref _navigation, value); }
    public NavButtonStyle NavButtonStyle { get => _navButtonStyle; set => Set(ref _navButtonStyle, value); }
    public bool FirstRunDone { get => _firstRunDone; set => Set(ref _firstRunDone, value); }

    [JsonIgnore] public bool ScrollEnabled => Navigation is NavigationMode.Scroll or NavigationMode.Both;
    [JsonIgnore] public bool ButtonsEnabled => Navigation is NavigationMode.Buttons or NavigationMode.Both;

    [JsonIgnore]
    public bool LaunchAtStartup
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) != null;
        }
        set
        {
            if (value == LaunchAtStartup) return;
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (value) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
                else key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ResetToDefaults()
    {
        var d = new AppSettings();
        foreach (var p in typeof(AppSettings).GetProperties())
        {
            if (!p.CanWrite || p.Name is nameof(FirstRunDone) or nameof(LaunchAtStartup)) continue;
            if (p.GetCustomAttributes(typeof(JsonIgnoreAttribute), true).Length > 0) continue;
            p.SetValue(this, p.GetValue(d));
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath).Replace("\"ClassicNotch\"", "\"MacBookNotch\"");
                var loaded = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                // Настройки из версии, где был только русский: оставляем русский, а не переключаем на язык системы.
                // Новые установки (файла нет) по умолчанию берут язык системы.
                if (!json.Contains("\"Language\"")) loaded.Language = "ru";
                return loaded;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[settings] load failed: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[settings] save failed: {ex.Message}");
        }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
