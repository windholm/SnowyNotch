using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SnowyNotch.Animation;
using SnowyNotch.Interop;
using SnowyNotch.Services;

namespace SnowyNotch;

public partial class MainWindow : Window
{
    private enum IslandState { Idle, Compact, Expanded }

    private const double IdleHoverGrow = 12;

    private readonly AppSettings _settings;
    private readonly ToolsService _tools;
    private readonly TrayService _tray;
    private readonly MediaService _media;

    private readonly Spring _w, _h, _topR, _bottomR, _ear, _top;
    private readonly Spring[] _springs;

    private readonly DispatcherTimer _hoverDelay = new();
    private readonly DispatcherTimer _leaveDelay = new();
    private readonly DispatcherTimer _peekTimer = new();
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _topmostTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _resetViewTimer = new() { Interval = TimeSpan.FromMilliseconds(320) };

    private IslandProfile _profile;
    private IslandState _state = IslandState.Idle;
    private MediaSnapshot? _snap;

    private bool _mouseOver;
    private bool _hoverActive;
    private bool _peeking;
    private bool _pinned;
    private bool _welcome;
    private bool _dragOpen;
    private bool _sticky;

    private bool _animating;
    private TimeSpan _lastFrame;
    private IntPtr _hwnd;
    private FullscreenWatcher? _fullscreen;
    private bool _suppressed;
    private bool _idleHidden;
    private string _fillKey = "";

    public MainWindow(AppSettings settings, ToolsService tools, TrayService tray)
    {
        InitializeComponent();
        _settings = settings;
        _tools = tools;
        _tray = tray;
        _media = new MediaService(Dispatcher) { MusicOnly = settings.MusicOnly };
        settings.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppSettings.MusicOnly)) _media.MusicOnly = _settings.MusicOnly; };

        _profile = IslandProfile.For(settings.Preset);
        var idle = CollapsedTarget(IslandState.Idle);
        _w = new Spring(idle.W);
        _h = new Spring(idle.H);
        _topR = new Spring(idle.TopR);
        _bottomR = new Spring(idle.BottomR);
        _ear = new Spring(idle.Ear);
        _top = new Spring(TopOffsetTarget());
        _springs = new[] { _w, _h, _topR, _bottomR, _ear, _top };

        _hoverDelay.Tick += (_, _) => { _hoverDelay.Stop(); _hoverActive = true; UpdateState(); };
        _leaveDelay.Tick += (_, _) =>
        {
            _leaveDelay.Stop();
            if (_mouseOver) return;
            if (DateTime.UtcNow < _leaveGraceUntil)
            {
                _leaveDelay.Start();
                return;
            }
            _mouseOver = Island.IsMouseOver || NavHitArea.IsMouseOver;
            if (_mouseOver) return;
            _hoverActive = false;
            UpdateState();
        };
        _peekTimer.Tick += (_, _) =>
        {
            _peekTimer.Stop();
            _peeking = false;
            if (Island.IsMouseOver || NavHitArea.IsMouseOver) _hoverActive = true;
            UpdateState();
        };
        _progressTimer.Tick += (_, _) => TickMediaWidgets();
        _topmostTimer.Tick += (_, _) => { if (_hwnd != IntPtr.Zero && IsVisible) NativeMethods.BringToTop(_hwnd); };
        _resetViewTimer.Tick += (_, _) =>
        {
            _resetViewTimer.Stop();
            if (_state != IslandState.Expanded) ResetToHome();
        };

        StopwatchBubble.DataContext = tools;
        TimerBubble.DataContext = tools;

        // Громкость нужна раньше видов: виджет «Звук» может стоять на главном экране и создаётся в InitViews.
        _volume = new VolumeService(Dispatcher);
        InitViews();
        InitTray();
        InitTools();
        InitHud();
        InitGhost();
        InitAgents();

        ApplySettings(null);
        ApplyGeometry();
        _settings.PropertyChanged += OnSettingChanged;

        Loaded += OnLoaded;
        Deactivated += (_, _) => { if (_pinned) ClosePinned(); };
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) =>
        {
            _settings.PropertyChanged -= OnSettingChanged;
            _fullscreen?.Dispose();
            _media.Dispose();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.MakeOverlay(_hwnd);
        PositionAtTopCenter();
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _topmostTimer.Start();

        _fullscreen = new FullscreenWatcher(_hwnd);
        _fullscreen.Changed += _ => ApplyFullscreenState();
        Dispatcher.InvokeAsync(ApplyFullscreenState, DispatcherPriority.Loaded);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0, WM_SETTINGCHANGE = 0x001A, WM_MOUSEHWHEEL = 0x020E;
        if (msg is WM_DISPLAYCHANGE or WM_DPICHANGED or WM_SETTINGCHANGE)
            Dispatcher.InvokeAsync(PositionAtTopCenter, DispatcherPriority.Background);
        if (msg == WM_MOUSEHWHEEL && (Island.IsMouseOver || NavHitArea.IsMouseOver))
        {
            int delta = unchecked((short)((long)wParam >> 16));
            // Над сервисами горизонтальный жест листает только сервисы (у края — отскок, страница не меняется).
            if (_view == View.Widgets && WidgetsView.IsMouseOver && _state == IslandState.Expanded) Dock.ScrollBy(delta * 0.9);
            else OnSwipe(-delta);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void PositionAtTopCenter()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_settings.FirstRunDone)
        {
            _welcome = true;
            ShowView(View.Welcome);
            UpdateState();
        }

        if (App.ShotMode) return; // съёмка скриншотов: только демо-трек, реальный плеер не слушаем
        _media.Changed += OnMediaChanged;
        try
        {
            await _media.StartAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[island] media init failed: {ex}");
        }
    }

    public void ToggleSettings()
    {
        if (_pinned && _view == View.Settings) ClosePinned();
        else OpenSettings();
    }

    public void OpenSettings(string? section = null)
    {
        ShowIsland();
        _welcome = false;
        if (_customizing) ExitCustomize(save: false);
        if (section != null) EnsureSettingsView().ShowSection(section);
        ShowView(View.Settings);
        Pin();
        UpdateState();
    }

    private void Pin()
    {
        if (!_pinned)
        {
            _pinned = true;
            NativeMethods.SetNoActivate(_hwnd, false);
            NativeMethods.ForceForeground(_hwnd);
            Activate();
        }
        ApplyFullscreenState();
    }

    private void ClosePinned()
    {
        if (_customizing) ExitCustomize(save: false);
        if (_pinned)
        {
            _pinned = false;
            NativeMethods.SetNoActivate(_hwnd, true);
        }
        _hoverActive = _mouseOver && _hoverActive;
        if (_view == View.Settings && _hoverActive) ShowView(View.Home, -1);
        ApplyFullscreenState();
        UpdateState();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when _view == View.Artwork && !_pinned:
                CloseArtwork();
                e.Handled = true;
                break;
            case Key.Escape when _view == View.Lyrics && !_pinned:
                CloseLyrics();
                e.Handled = true;
                break;
            case Key.Escape:
                _hoverActive = false;
                ClosePinned();
                e.Handled = true;
                break;
            case Key.Left when IsSwipeView(_view) && Keyboard.FocusedElement is not System.Windows.Controls.TextBox:
                StepView(-1);
                e.Handled = true;
                break;
            case Key.Right when IsSwipeView(_view) && Keyboard.FocusedElement is not System.Windows.Controls.TextBox:
                StepView(1);
                e.Handled = true;
                break;
        }
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e) => ApplySettings(e.PropertyName);

    private void ApplySettings(string? changed)
    {
        bool all = changed == null;

        if (all || changed == nameof(AppSettings.Preset))
            _profile = IslandProfile.For(_settings.Preset);
        if (all || changed == nameof(AppSettings.Scale))
            ClusterScale.ScaleX = ClusterScale.ScaleY = _settings.Scale;
        if (all || changed == nameof(AppSettings.AccentColor))
            ApplyAccentColor();

        _hoverDelay.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _settings.HoverDelayMs));
        _leaveDelay.Interval = TimeSpan.FromMilliseconds(_settings.CollapseDelayMs);

        if (all || changed is nameof(AppSettings.ShowVisualizer) or nameof(AppSettings.AccentFromArtwork)
                 or nameof(AppSettings.ShowShuffleButton) or nameof(AppSettings.ShowRepeatButton) or nameof(AppSettings.LyricsEnabled)
                 or nameof(AppSettings.ShowSource) or nameof(AppSettings.WavyProgress) or nameof(AppSettings.FullscreenStyle))
        {
            CompactBars.Visibility = _settings.ShowVisualizer ? Visibility.Visible : Visibility.Collapsed;
            ApplyCompactAccent();
            foreach (var mw in MediaWidgets()) mw.ApplySettings();
        }
        if (all || changed is nameof(AppSettings.Navigation) or nameof(AppSettings.NavButtonStyle)) ApplyNavigationChrome();
        if (all || changed == nameof(AppSettings.HomeWidgets)) { if (!_customizing) BuildHomeRow(); }
        if (all || changed == nameof(AppSettings.HideWhenIdle)) ApplyIdleVisibility();
        if (changed == nameof(AppSettings.MiddleClickGhost) && _middleHook != null)
        {
            _middleHook.Enabled = _settings.MiddleClickGhost;
            if (!_settings.MiddleClickGhost) ShowIsland();
        }
        if (all || changed == nameof(AppSettings.HideInFullscreen)) ApplyFullscreenState();
        if (all || changed is nameof(AppSettings.Finish) or nameof(AppSettings.GlassIntensity) or nameof(AppSettings.SubtleOutline))
        {
            _fillKey = "";
            ApplyGeometry();
        }

        if (!_settings.PeekOnTrackChange && _peeking)
        {
            _peeking = false;
            _peekTimer.Stop();
            UpdateState();
        }

        SetTargetsAfterLayout();
    }

    private void ApplyAccentColor()
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(_settings.AccentColor);
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            Application.Current.Resources["AccentBrush"] = brush;
        }
        catch (FormatException) {  }
    }

    private double TopOffsetTarget() => _profile.Attached ? 0 : _settings.TopOffset;

    private ShapeSize CollapsedTarget(IslandState state)
    {
        double len = _settings.Length;
        var s = state == IslandState.Compact
            ? _profile.Compact with { W = Math.Max(120, _profile.Compact.W * len) }
            : _profile.Idle with { W = _profile.Idle.W * len };

        if (state == IslandState.Idle && _mouseOver)
            s = s with { W = s.W + IdleHoverGrow, H = s.H + 2 };
        return s;
    }

    private ShapeSize Target()
    {
        if (_state != IslandState.Expanded) return CollapsedTarget(_state);
        var size = ActiveViewSize();
        return _profile.Expanded with { W = size.Width, H = size.Height };
    }

    private void ApplyFullscreenState()
    {
        if (_fullscreen != null) _fullscreen.Enabled = _settings.HideInFullscreen;
        bool shouldHide = _settings.HideInFullscreen && _fullscreen?.IsFullscreen == true && !_pinned;
        if (shouldHide == _suppressed) return;
        _suppressed = shouldHide;

        if (shouldHide)
        {
            _hoverDelay.Stop(); _leaveDelay.Stop(); _peekTimer.Stop();
            _mouseOver = _hoverActive = _peeking = false;
            UpdateState();

            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (_, _) =>
            {
                if (_suppressed) Hide();
            };
            Root.BeginAnimation(OpacityProperty, fade);
        }
        else
        {
            if (!IsVisible)
            {
                Root.BeginAnimation(OpacityProperty, null);
                Root.Opacity = 0;
                Show();
                NativeMethods.BringToTop(_hwnd);
            }
            Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
    }

    private void ApplyIdleVisibility()
    {
        bool hide = _settings.HideWhenIdle && _state == IslandState.Idle && !_mouseOver && !AnyToolActive && _tray.Items.Count == 0;
        if (hide == _idleHidden) return;
        _idleHidden = hide;
        Fade(Island, hide ? 0 : 1, hide ? 200 : 160);
        Island.IsHitTestVisible = !hide;
    }

    private void UpdateState()
    {
        IslandState target = (_welcome || _pinned || _sticky || _hoverActive || _peeking || _dragOpen)
            ? IslandState.Expanded
            : _snap != null ? IslandState.Compact : IslandState.Idle;

        if (target != _state)
        {
            var from = _state;
            _state = target;
            OnStateChanged(from, target);
        }

        SetTargets();
    }

    private void OnStateChanged(IslandState from, IslandState to)
    {
        UseSpringFeel(growing: to == IslandState.Expanded);

        if (to == IslandState.Expanded)
        {
            _resetViewTimer.Stop();
            FadeVisibility(CompactView, 0, Ms(80));
            CompactView.IsHitTestVisible = false;
            RevealActiveView(delayMs: Ms(90));
            _progressTimer.Start();
        }
        else
        {
            ConcealActiveView();
            _progressTimer.Stop();
            FadeVisibility(CompactView, to == IslandState.Compact ? 1 : 0, Ms(200), delayMs: from == IslandState.Expanded ? Ms(150) : Ms(40));
            if (from == IslandState.Expanded) _resetViewTimer.Start();
        }

        ApplyNavigationChrome();
        ActivateWidgets();
        UpdateBubbles();
        ApplyIdleVisibility();
    }

    private void UseSpringFeel(bool growing)
    {
        double s = _settings.AnimationSpeed;
        double fast = 560 * s * s, slow = 330 * s * s, mid = 420 * s * s;
        // Раскрытие — упругое: остров чуть «перелетает» размер и садится на место (как Dynamic Island).
        // Схлопывание — плотнее, с едва заметной отдачей: маленькая пилюля не должна дрожать.
        double zeta = growing ? 0.7 : 0.82;
        Configure(_w, growing ? fast : slow, zeta);
        Configure(_h, growing ? slow : fast, zeta);
        foreach (var sp in new[] { _topR, _bottomR, _ear, _top }) Configure(sp, mid, 0.86);

        static void Configure(Spring sp, double k, double zeta)
        {
            sp.Stiffness = k;
            sp.Damping = 2 * zeta * Math.Sqrt(k);
        }
    }

    private double Ms(double ms) => ms / _settings.AnimationSpeed;

    private void SetTargets()
    {
        var size = Target();
        _w.Target = size.W;
        _h.Target = size.H;
        _topR.Target = size.TopR;
        _bottomR.Target = size.BottomR;
        _ear.Target = size.Ear;
        _top.Target = TopOffsetTarget();
        EnsureWindowFits(_top.Target + size.H);

        double ch = _profile.Compact.H;
        CompactView.Height = ch;
        double art = ch - 12;
        CompactArt.Width = CompactArt.Height = art;
        CompactArt.CornerRadius = new CornerRadius(art * 0.26);

        StartAnimating();
    }

    // Под островом ещё висит панель навигации: отступ 10 + высота ~42 + отступ 16, с запасом.
    private const double NavBarReserve = 80;

    /// <summary>
    /// Окно прозрачное и фиксированного размера; если раскрытый остров (например, три ряда виджетов)
    /// вместе с панелью навигации не помещается, окно подрастает — иначе низ обрезается.
    /// Только растёт: прозрачные области кликам не мешают, а лишние ресайзы layered-окна дороги.
    /// </summary>
    private void EnsureWindowFits(double islandBottom)
    {
        double need = Math.Ceiling((islandBottom + NavBarReserve) * _settings.Scale) + 8;
        need = Math.Min(need, SystemParameters.PrimaryScreenHeight);
        if (need > Height) Height = need;
    }

    private void SetTargetsAfterLayout()
    {
        SetTargets();
        Dispatcher.InvokeAsync(SetTargets, DispatcherPriority.Loaded);
    }

    private void StartAnimating()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : (now - _lastFrame).TotalSeconds;
        if (dt <= 0) return;
        _lastFrame = now;
        dt = Math.Min(dt, 0.05);

        bool settled = true;
        foreach (var s in _springs)
        {
            s.Step(dt);
            settled &= s.IsSettled;
        }

        if (settled)
        {
            foreach (var s in _springs) s.Snap();
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
        }

        ApplyGeometry();
    }

    private void ApplyGeometry()
    {
        double w = Math.Max(1, _w.Value);
        double h = Math.Max(1, _h.Value);
        double ear = Math.Max(0, _ear.Value);
        double top = Math.Max(0, _top.Value);

        Island.Width = w + 2 * ear;
        Island.Height = h;
        Island.Margin = new Thickness(0, top, 0, 0);

        Shape.Data = IslandGeometry.Build(w, h, _topR.Value, _bottomR.Value, ear, includeEars: true);
        Shape.Fill = IslandFill(h);

        Outline.Visibility = _settings.SubtleOutline ? Visibility.Visible : Visibility.Collapsed;
        if (_settings.SubtleOutline)
            Outline.Data = IslandGeometry.BuildOutline(w, h, _topR.Value, _bottomR.Value, ear, _profile.Attached);

        Body.Width = w;
        Body.Height = h;
        Body.Margin = new Thickness(ear, 0, 0, 0);
        Body.Clip = IslandGeometry.Build(w, h, _topR.Value, _bottomR.Value, 0, includeEars: false);

        LayoutViews(w);
        PositionBubbles(w + 2 * ear, top);
    }

    private Brush IslandFill(double h)
    {
        var finish = _settings.Finish;
        if (finish == IslandFinish.Black) { _fillKey = "black"; return Brushes.Black; }

        double collapsedH = _profile.Compact.H;
        double expandedH = Math.Max(collapsedH + 1, _state == IslandState.Expanded ? _h.Target : collapsedH + 120);
        double open = Math.Clamp((h - collapsedH) / (expandedH - collapsedH), 0, 1);
        double k = _settings.GlassIntensity * (_view == View.Settings || _customizing ? 0.12 : _view == View.Artwork ? 0.4 : 1);

        double alpha = finish == IslandFinish.DynamicGlass
            ? 1 - 0.62 * k * open
            : 1 - 0.58 * k * (0.3 + 0.7 * open);
        alpha = Math.Round(alpha, 2);

        string key = $"{finish}:{alpha:0.00}";
        if (key == _fillKey && Shape.Fill is Brush cached) return cached;
        _fillKey = key;

        if (alpha >= 0.995) return Brushes.Black;

        Brush brush;
        if (finish == IslandFinish.DynamicGlass)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            g.GradientStops.Add(new GradientStop(Colors.Black, 0));
            g.GradientStops.Add(new GradientStop(Colors.Black, 0.5));
            g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 255), 16, 16, 20), 1));
            brush = g;
        }
        else
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(Math.Min(1, alpha + 0.12) * 255), 22, 22, 28), 0));
            g.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(alpha * 255), 22, 22, 28), 1));
            brush = g;
        }
        brush.Freeze();
        return brush;
    }

    private static void Fade(UIElement el, double to, double ms, double delayMs = 0) =>
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, DoubleAnimation> LastFade = new();

    /// <summary>
    /// Как <see cref="Fade"/>, но погасший элемент ещё и скрывается (Visibility.Hidden). Визуально то же самое,
    /// зато анимированные контролы внутри (Clawd, глифы, эквалайзер) видят IsVisible = false и останавливают
    /// свои кадровые циклы — иначе невидимый пузырь перерисовывал прозрачное окно 60 раз в секунду.
    /// </summary>
    private static void FadeVisibility(UIElement el, double to, double ms, double delayMs = 0)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        LastFade.AddOrUpdate(el, anim);
        if (to > 0) el.Visibility = Visibility.Visible;
        else anim.Completed += (_, _) =>
        {
            // Пока гасли, могли начать показывать снова — тогда не прячем.
            if (LastFade.TryGetValue(el, out var last) && last == anim) el.Visibility = Visibility.Hidden;
        };
        el.BeginAnimation(OpacityProperty, anim);
    }

    private static void Scale(ScaleTransform t, double to, double ms, double delayMs = 0)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 },
        };
        t.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        t.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void Hover_Enter(object sender, MouseEventArgs e)
    {
        _mouseOver = true;
        _leaveDelay.Stop();
        if (!_hoverActive && _settings.ExpandOnHover) _hoverDelay.Start();
        if (_state == IslandState.Idle) SetTargets();
        ApplyIdleVisibility();
    }

    private void Hover_Leave(object sender, MouseEventArgs e)
    {
        _mouseOver = Island.IsMouseOver || NavHitArea.IsMouseOver;
        if (_mouseOver) return;
        _hoverDelay.Stop();
        if (_hoverActive) _leaveDelay.Start();
        if (_state == IslandState.Idle) SetTargets();
        ApplyIdleVisibility();
    }

    private void Island_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_state != IslandState.Expanded)
        {
            _hoverDelay.Stop();
            _hoverActive = true;
            UpdateState();
        }
    }

    private void Island_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenSettings();
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void WelcomeStart_Click(object sender, RoutedEventArgs e)
    {
        _settings.FirstRunDone = true;
        _welcome = false;
        ShowView(View.Home);
        _hoverActive = _mouseOver;
        UpdateState();
    }

    private void WelcomeSettings_Click(object sender, RoutedEventArgs e)
    {
        _settings.FirstRunDone = true;
        OpenSettings();
    }
}
