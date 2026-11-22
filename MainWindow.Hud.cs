using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SnowyNotch.Services;
using SnowyNotch.Widgets;
using SnowyNotch.Localization;

namespace SnowyNotch;

public partial class MainWindow
{
    private enum HudKind { Volume, Brightness, Device }

    private const double HudSeconds = 1.6;

    private VolumeService _volume = null!;
    private BrightnessService _brightness = null!;
    private BluetoothAudioWatcher _bluetooth = null!;
    private BatteryService _battery = null!;
    private readonly AudioMixerService _mixer = new();

    /// <summary>Горячая клавиша «Звук»: открыть микшер в острове или закрыть, если уже открыт.</summary>
    public void ToggleAudio()
    {
        if (_pinned && _view == View.Focus && _focusWidget is AudioWidget)
        {
            ClosePinned();
            return;
        }
        ShowIsland();
        _welcome = false;
        if (_customizing) ExitCustomize(save: false);
        OpenFocus("audio");
        Pin();
        UpdateState();
    }
    private readonly LyricsService _lyricsService = new();
    private readonly NotesStore _notes = new();
    private LyricsView? _lyricsView;
    private View _beforeLyrics = View.Home;

    private HudKind _hudKind = HudKind.Volume;
    private bool _hudDragging;

    private bool _headsetBubbleShown, _headsetActive;
    private HeadsetInfo? _lastHeadset;
    private readonly DispatcherTimer _headsetHide = new() { Interval = TimeSpan.FromSeconds(9) };

    private void InitHud()
    {
        _volume.Changed += (_, _, external) => OnLevelChanged(HudKind.Volume, external);

        _brightness = new BrightnessService(Dispatcher);
        _brightness.Changed += _ => OnLevelChanged(HudKind.Brightness, external: !_hudDragging);

        _bluetooth = new BluetoothAudioWatcher(_volume);
        _bluetooth.Connected += info => OnHeadset(info, L.T("Подключено"));
        _bluetooth.BatteryLow += info => OnHeadset(info, L.T("Низкий заряд"));

        _battery = new BatteryService();
        _battery.PlugChanged += charging => OnBattery(charging ? L.T("Зарядка") : L.T("От батареи"));
        _battery.Low += () => OnBattery(L.T("Низкий заряд"));

        // Опросы устройств работают, только пока соответствующий показ включён.
        void SyncDeviceWatchers()
        {
            _bluetooth.Enabled = _settings.ShowBluetooth;
            _battery.Enabled = _settings.ShowBattery;
        }
        SyncDeviceWatchers();
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.ShowBluetooth) or nameof(AppSettings.ShowBattery)) SyncDeviceWatchers();
        };

        _headsetHide.Tick += (_, _) =>
        {
            _headsetHide.Stop();
            _headsetActive = false;
            UpdateBubbles();
        };

        Closed += (_, _) =>
        {
            _volume.Dispose();
            _brightness.Dispose();
            _battery.Dispose();
            _notes.Flush();
        };
    }

    private void OnLevelChanged(HudKind kind, bool external)
    {
        if (!_settings.ShowHud || IsGhosted) return;
        if (_view == View.Hud && _hudKind == kind) RefreshHud(animate: true);
        if (!external) return;

        if (_state == IslandState.Expanded && _view != View.Hud && (_hoverActive || _pinned || _customizing)) return;

        _hudKind = kind;
        RefreshHud(animate: _view == View.Hud);
        if (_view != View.Hud) ShowView(View.Hud);
        Peek(HudSeconds);
    }

    private void RefreshHud(bool animate)
    {
        bool device = _hudKind == HudKind.Device;
        HudLevelRow.Visibility = device ? Visibility.Collapsed : Visibility.Visible;
        HudDeviceRow.Visibility = device ? Visibility.Visible : Visibility.Collapsed;
        if (device) return;

        double level;
        if (_hudKind == HudKind.Volume)
        {
            level = _volume.Muted ? 0 : _volume.Volume;
            HudIcon.Data = (Geometry)FindResource(
                _volume.Muted || level < 0.005 ? "IconSpeakerMute" : level < 0.34 ? "IconSpeaker" : level < 0.67 ? "IconSpeakerLow" : "IconSpeakerHigh");
            HudValue.Text = _volume.Muted ? "—" : $"{level * 100:0}";
            HudIconButton.ToolTip = _volume.Muted ? L.T("Включить звук") : L.T("Выключить звук");
        }
        else
        {
            level = _brightness.Level / 100.0;
            HudIcon.Data = (Geometry)FindResource("IconSun");
            HudValue.Text = $"{_brightness.Level}";
            HudIconButton.ToolTip = null;
        }

        double width = HudTrack.ActualWidth > 0 ? HudTrack.ActualWidth : 230;
        double target = Math.Clamp(level, 0, 1) * width;
        if (animate && !_hudDragging)
            HudFill.BeginAnimation(WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else
        {
            HudFill.BeginAnimation(WidthProperty, null);
            HudFill.Width = target;
        }
    }

    private void HudIcon_Click(object sender, RoutedEventArgs e)
    {
        if (_hudKind == HudKind.Volume) _volume.ToggleMute();
        Peek(HudSeconds);
    }

    private void HudTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _hudDragging = true;
        HudTrack.CaptureMouse();
        ApplyHudLevel(e.GetPosition(HudTrack).X);
        e.Handled = true;
    }

    private void HudTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (_hudDragging) ApplyHudLevel(e.GetPosition(HudTrack).X);
    }

    private void HudTrack_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_hudDragging) return;
        _hudDragging = false;
        HudTrack.ReleaseMouseCapture();
        Peek(HudSeconds);
    }

    private void ApplyHudLevel(double x)
    {
        double f = Math.Clamp(x / Math.Max(1, HudTrack.ActualWidth), 0, 1);
        if (_hudKind == HudKind.Volume) _volume.SetVolume((float)f);
        else if (_hudKind == HudKind.Brightness) _brightness.SetLevel((int)Math.Round(f * 100));
        HudFill.BeginAnimation(WidthProperty, null);
        HudFill.Width = f * HudTrack.ActualWidth;
        HudValue.Text = $"{f * 100:0}";
        _peekTimer.Stop();
        _peekTimer.Start();
    }

    private void OnHeadset(HeadsetInfo info, string subtitle)
    {
        if (!_settings.ShowBluetooth) return;
        _lastHeadset = info;

        HeadsetRing.Progress = (info.Battery ?? 0) / 100.0;
        HeadsetRing.RingBrush = BatteryBrush(info.Battery);
        _headsetActive = true;
        _headsetHide.Stop();
        _headsetHide.Start();
        UpdateBubbles();

        if (IsGhosted) return;
        if (_state == IslandState.Expanded && _view != View.Hud && (_hoverActive || _pinned || _customizing)) return;
        ShowDeviceHud(info, subtitle);
        Peek(3.2);
    }

    /// <summary>Ноутбук: зарядку подключили/отключили или заряд низкий — HUD со значком батареи.</summary>
    private void OnBattery(string title)
    {
        if (!_settings.ShowBattery || !_battery.HasBattery || IsGhosted) return;
        if (_state == IslandState.Expanded && _view != View.Hud && (_hoverActive || _pinned || _customizing)) return;

        _hudKind = HudKind.Device;
        HudDeviceIcon.Data = (Geometry)FindResource(_battery.Charging ? "IconBolt" : "IconBatteryLow");
        HudDeviceIcon.Filled = _battery.Charging;
        var tint = _battery.Charging ? Color.FromRgb(0x30, 0xD1, 0x58) : _battery.Percent <= 20 ? Color.FromRgb(0xFF, 0x45, 0x3A) : Colors.White;
        HudDeviceIcon.Foreground = new SolidColorBrush(tint);
        HudDeviceIconBg.Background = new SolidColorBrush(Color.FromArgb(0x2E, tint.R, tint.G, tint.B));
        HudDeviceName.Text = title;
        HudDeviceSub.Text = _battery.Charging ? L.T("Батарея заряжается") : _battery.Percent <= 20 ? L.T("Подключите зарядку") : L.T("Ноутбук работает от батареи");
        HudBatteryPanel.Visibility = Visibility.Visible;
        HudBatteryRing.Visibility = Visibility.Collapsed;
        HudBatteryGlyph.Visibility = Visibility.Visible;
        HudBatteryGlyph.Level = _battery.Percent / 100.0;
        HudBatteryGlyph.Charging = _battery.Charging;
        HudBatteryText.Text = $"{_battery.Percent}%";
        RefreshHud(animate: false);
        if (_view != View.Hud) ShowView(View.Hud);
        Peek(3);
    }

    private void ShowDeviceHud(HeadsetInfo info, string subtitle)
    {
        _hudKind = HudKind.Device;
        HudDeviceIcon.Data = (Geometry)FindResource("IconHeadphones");
        HudDeviceIcon.Filled = false;
        HudDeviceIcon.Foreground = Brushes.White;
        HudDeviceIconBg.Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
        HudBatteryRing.Visibility = Visibility.Visible;
        HudBatteryGlyph.Visibility = Visibility.Collapsed;
        HudDeviceName.Text = info.Name;
        HudDeviceSub.Text = subtitle;
        HudBatteryPanel.Visibility = info.Battery.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (info.Battery is int b)
        {
            HudBatteryRing.Progress = b / 100.0;
            HudBatteryRing.RingBrush = BatteryBrush(b);
            HudBatteryText.Text = $"{b}%";
        }
        RefreshHud(animate: false);
        if (_view != View.Hud) ShowView(View.Hud);
    }

    private static Brush BatteryBrush(int? battery) => battery switch
    {
        null => new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
        <= 15 => new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)),
        <= 35 => new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)),
        _ => new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)),
    };

    private void HeadsetBubble_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_lastHeadset == null) return;
        ShowDeviceHud(_lastHeadset, _lastHeadset.Battery.HasValue ? L.T("Заряд наушников") : L.T("Подключены"));
        Peek(3);
    }

    private LyricsView EnsureLyricsView()
    {
        if (_lyricsView == null)
        {
            _lyricsView = new LyricsView(_lyricsService, _settings);
            _lyricsView.BackRequested += CloseLyrics;
            _lyricsView.SeekRequested += t => _ = _media.SeekAsync(t);
            _lyricsView.NotFound += () => { if (_view == View.Lyrics) CloseLyrics(); };
            LyricsHost.Content = _lyricsView;
        }
        return _lyricsView;
    }

    private void OpenLyrics()
    {
        EnsureLyricsView();
        _lyricsView!.Update(_snap);
        if (_view != View.Lyrics) _beforeLyrics = _view == View.Artwork ? View.Artwork : View.Home;
        ShowView(View.Lyrics);
    }

    private void CloseLyrics() => ShowView(_beforeLyrics, -1);}
