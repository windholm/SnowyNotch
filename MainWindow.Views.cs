using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using SnowyNotch.Services;
using SnowyNotch.Views;
using SnowyNotch.Widgets;
using SnowyNotch.Localization;

namespace SnowyNotch;

public partial class MainWindow
{
    private enum View { Home, Tray, Widgets, Focus, Artwork, Lyrics, Hud, Agent, Settings, Welcome }

    private const double MinExpandedWidth = 300;
    private static readonly TimeSpan LeaveGrace = TimeSpan.FromMilliseconds(1300);
    private DateTime _leaveGraceUntil;
    private const double SlideDistance = 38;

    private SettingsView? _settingsView;
    private View _view = View.Home;
    private FrameworkElement? _activeEl;
    private readonly List<FrameworkElement> _homeWidgets = new();
    private FrameworkElement? _focusWidget;
    private bool _syncingNav;

    private bool _customizing;
    private List<string> _draft = new();

    private double _wheelAccum;
    private bool _wheelLocked;
    private readonly DispatcherTimer _wheelUnlock = new() { Interval = TimeSpan.FromMilliseconds(180) };

    private void InitViews()
    {
        // У каждой страницы — масштаб и сдвиг: переходы едут, слегка «дышат» размером и пружинят.
        foreach (FrameworkElement v in ViewHost.Children)
        {
            v.RenderTransform = new TransformGroup { Children = { new ScaleTransform(1, 1), new TranslateTransform() } };
            v.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        _wheelUnlock.Tick += (_, _) => { _wheelUnlock.Stop(); _wheelLocked = false; _wheelAccum = 0; };

        // Над сервисами колесо листает только сервисы. Страницы острова — колесом над панелью кнопок под ним,
        // самими кнопками или стрелками. Хвост того же жеста, что привёл на эту страницу, список не крутит.
        WidgetsView.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (_wheelLocked) { _wheelUnlock.Stop(); _wheelUnlock.Start(); return; }
            Dock.Wheel(e.Delta);
        };

        BuildHomeRow();
        _activeEl = HomeView;
        SyncNav();
    }

    /// <summary>
    /// Настройки и сетка сервисов строятся при первом открытии, а не при запуске: большие деревья элементов,
    /// которые во многих сессиях так и не понадобятся, не должны занимать память с самого старта.
    /// </summary>
    private SettingsView EnsureSettingsView()
    {
        if (_settingsView == null)
        {
            _settingsView = new SettingsView(_settings);
            _settingsView.CloseRequested += () => { _hoverActive = false; ClosePinned(); };
            _settingsView.CustomizeRequested += EnterCustomize;
            _settingsView.AudioRequested += () => { OpenFocus("audio"); };
            SettingsHost.Content = _settingsView;
        }
        return _settingsView;
    }

    private void EnsureLazyView(View v)
    {
        if (v == View.Settings) EnsureSettingsView();
        else if (v == View.Widgets && !Dock.HasItems) BuildServiceDock();
    }

    private FrameworkElement ElementOf(View v) => v switch
    {
        View.Tray => TrayView,
        View.Widgets => WidgetsView,
        View.Focus => FocusView,
        View.Artwork => ArtworkHost,
        View.Lyrics => LyricsHost,
        View.Hud => HudView,
        View.Agent => AgentHost,
        View.Settings => SettingsHost,
        View.Welcome => WelcomeView,
        _ => HomeView,
    };

    private static bool IsSwipeView(View v) => v is View.Home or View.Tray or View.Widgets or View.Focus;

    private static int SwipeIndex(View v) => v switch { View.Tray => 1, View.Widgets or View.Focus => 2, _ => 0 };

    private void ShowView(View v, int direction = 0)
    {
        EnsureLazyView(v);
        var next = ElementOf(v);
        if (direction == 0 && IsSwipeView(v) && IsSwipeView(_view))
            direction = Math.Sign(SwipeIndex(v) - SwipeIndex(_view));

        var prev = _activeEl;
        _view = v;
        _activeEl = next;

        if (prev != next && _state == IslandState.Expanded) _leaveGraceUntil = DateTime.UtcNow + LeaveGrace;

        bool sticky = v is View.Artwork or View.Lyrics or View.Focus or View.Agent;
        if (_sticky && !sticky) _hoverActive = _hoverActive || _mouseOver || Island.IsMouseOver;
        _sticky = sticky;

        if (_state == IslandState.Expanded && prev != next)
        {
            if (prev != null) AnimateOut(prev, direction);
            AnimateIn(next, direction, delayMs: Ms(50));
        }
        else if (prev != next && prev != null)
        {
            HideInstantly(prev);
        }

        if (v == View.Widgets && _state == IslandState.Expanded && prev != next)
            PlayDockEntrance();
        if (v == View.Home && _state == IslandState.Expanded && prev != next)
            PlayHomeEntrance();

        SyncNav();
        ApplyNavigationChrome();
        ActivateWidgets();
        SetTargetsAfterLayout();
    }

    private void StepView(int delta)
    {
        int idx = Math.Clamp(SwipeIndex(_view) + delta, 0, 2);
        if (idx == SwipeIndex(_view)) return;
        ShowView(idx switch { 1 => View.Tray, 2 => View.Widgets, _ => View.Home }, Math.Sign(delta));
    }

    private void RevealActiveView(double delayMs)
    {
        if (_activeEl != null) AnimateIn(_activeEl, 0, delayMs);
        if (_view == View.Widgets) PlayDockEntrance();
        if (_view == View.Home) PlayHomeEntrance(delayMs);
    }

    private void ConcealActiveView()
    {
        if (_activeEl != null) AnimateOut(_activeEl, 0, fast: true);
    }

    private void ResetToHome()
    {
        _sticky = false;
        if (_customizing) ExitCustomize(save: false);
        if (_view != View.Home && !_welcome) ShowView(View.Home);
    }

    private static ScaleTransform ScaleOf(FrameworkElement el) => (ScaleTransform)((TransformGroup)el.RenderTransform).Children[0];
    private static TranslateTransform ShiftOf(FrameworkElement el) => (TranslateTransform)((TransformGroup)el.RenderTransform).Children[1];

    /// <summary>Откуда «вырастает» следующая страница (доля ширины, 0…1): сервис раскрывается из своей иконки в доке.</summary>
    private double? _zoomOriginX;

    private void AnimateIn(FrameworkElement el, int dir, double delayMs)
    {
        el.Visibility = Visibility.Visible;
        el.IsHitTestVisible = true;
        var tt = ShiftOf(el);
        var st = ScaleOf(el);
        var begin = TimeSpan.FromMilliseconds(delayMs);
        var fadeEase = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Раскрытие из иконки: страница растёт из точки, где была иконка. Обычный переход: едет сбоку
        // и чуть подрастает; при раскрытии острова — опускается сверху.
        bool zoom = _zoomOriginX != null;
        double fromScale = zoom ? 0.72 : dir == 0 ? 0.94 : 0.965;
        el.RenderTransformOrigin = new Point(zoom ? _zoomOriginX!.Value : 0.5, zoom ? 0.62 : dir == 0 ? 0 : 0.5);
        _zoomOriginX = null;

        var move = TimeSpan.FromMilliseconds(Ms(zoom ? 520 : 460));
        var spring = zoom ? SnowyNotch.Animation.SpringEase.Lively : SnowyNotch.Animation.SpringEase.Soft;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Ms(220))) { BeginTime = begin, EasingFunction = fadeEase });
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(zoom ? 0 : dir * SlideDistance, 0, move) { BeginTime = begin, EasingFunction = spring });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dir == 0 && !zoom ? -8 : 0, 0, move) { BeginTime = begin, EasingFunction = spring });
        var grow = new DoubleAnimation(fromScale, 1, move) { BeginTime = begin, EasingFunction = spring };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        // Проявление из размытия — для лёгких страниц. Док сервисов размывать каждый кадр слишком дорого
        // (иконки с градиентами), у него своё каскадное появление.
        if (el == WidgetsView) { el.Effect = null; return; }
        var blur = new BlurEffect { Radius = 16, RenderingBias = RenderingBias.Performance };
        el.Effect = blur;
        var unblur = new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(Ms(260))) { BeginTime = begin, EasingFunction = fadeEase };
        unblur.Completed += (_, _) => { if (el.Effect == blur) el.Effect = null; };
        blur.BeginAnimation(BlurEffect.RadiusProperty, unblur);
    }

    private void AnimateOut(FrameworkElement el, int dir, bool fast = false)
    {
        el.IsHitTestVisible = false;
        var tt = ShiftOf(el);
        var st = ScaleOf(el);
        var blur = new BlurEffect { Radius = 0, RenderingBias = RenderingBias.Performance };
        el.Effect = el == WidgetsView ? null : blur;
        var dur = TimeSpan.FromMilliseconds(Ms(fast ? 90 : 150));
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };

        var fade = new DoubleAnimation(0, dur);
        fade.Completed += (_, _) =>
        {
            if (_activeEl == el && _state == IslandState.Expanded) return;
            el.Visibility = Visibility.Hidden;
            if (el.Effect == blur) el.Effect = null;
        };
        el.BeginAnimation(OpacityProperty, fade);
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-dir * SlideDistance * 0.7, dur) { EasingFunction = ease });
        var shrink = new DoubleAnimation(dir == 0 ? 0.97 : 0.955, dur) { EasingFunction = ease };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(12, dur));
    }

    private static void HideInstantly(FrameworkElement el)
    {
        el.BeginAnimation(OpacityProperty, null);
        el.Opacity = 0;
        el.Visibility = Visibility.Hidden;
        el.IsHitTestVisible = false;
        el.Effect = null;
    }

    private double TopTabsHeight =>
        TopTabs.Visibility == Visibility.Visible && IsSwipeView(_view) && !_customizing && !_welcome ? 44 : 0;

    private Size ActiveViewSize()
    {
        var el = _activeEl ?? HomeView;
        el.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var ds = el.DesiredSize;
        double w = Math.Max(MinExpandedWidth, ds.Width);
        double tabsW = TopTabsHeight > 0 ? 330 : 0;
        return new Size(Math.Max(w, tabsW), Math.Max(60, ds.Height) + TopTabsHeight);
    }

    private void LayoutViews(double bodyWidth)
    {
        double top = TopTabsHeight;
        foreach (FrameworkElement v in ViewHost.Children)
        {
            double w = v.DesiredSize.Width;
            Canvas.SetLeft(v, Math.Round((bodyWidth - w) / 2));
            Canvas.SetTop(v, top);
        }
    }

    private void SyncNav()
    {
        _syncingNav = true;
        int i = SwipeIndex(_view);
        bool nav = IsSwipeView(_view);
        NavHome.IsChecked = nav && i == 0;
        NavTray.IsChecked = nav && i == 1;
        NavWidgets.IsChecked = nav && i == 2;
        TabHome.IsChecked = nav && i == 0;
        TabTray.IsChecked = nav && i == 1;
        TabWidgets.IsChecked = nav && i == 2;
        _syncingNav = false;
        Dispatcher.InvokeAsync(() => MoveNavIndicator(), DispatcherPriority.Loaded);
    }

    private void MoveNavIndicator(bool animate = true)
    {
        RadioButton? active = NavHome.IsChecked == true ? NavHome
                            : NavTray.IsChecked == true ? NavTray
                            : NavWidgets.IsChecked == true ? NavWidgets : null;
        if (active == null || active.ActualWidth <= 0)
        {
            Fade(NavIndicator, 0, Ms(120));
            return;
        }

        double x = active.TranslatePoint(new Point(0, 0), NavGrid).X;
        double w = active.ActualWidth;
        bool jump = !animate || NavIndicator.Opacity < 0.05 || NavBar.Opacity < 0.05;
        if (jump)
        {
            NavIndicatorShift.BeginAnimation(TranslateTransform.XProperty, null);
            NavIndicator.BeginAnimation(WidthProperty, null);
            NavIndicatorShift.X = x;
            NavIndicator.Width = w;
        }
        else
        {
            var ease = SnowyNotch.Animation.SpringEase.Lively;
            var dur = TimeSpan.FromMilliseconds(Ms(520));
            NavIndicatorShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, dur) { EasingFunction = ease });
            NavIndicator.BeginAnimation(WidthProperty, new DoubleAnimation(w, dur) { EasingFunction = ease });
        }
        Fade(NavIndicator, 1, Ms(160));
    }

    private void NavTab_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingNav) return;
        if (sender == NavHome || sender == TabHome) ShowView(View.Home);
        else if (sender == NavTray || sender == TabTray) ShowView(View.Tray);
        else if (sender == NavWidgets || sender == TabWidgets) ShowView(View.Widgets);
    }

    /// <summary>
    /// Повторное нажатие на уже выбранную кнопку «Сервисы»: если открыт какой-то сервис (секундомер, таймер,
    /// звук, заметки…), возвращаемся к панели сервисов. Checked в этом случае не срабатывает — кнопка уже выбрана.
    /// </summary>
    private void NavWidgets_Click(object sender, RoutedEventArgs e)
    {
        if (_view != View.Focus) return;
        if (_focusWidget is IIslandWidget w) w.SetActive(false);
        ShowView(View.Widgets, -1);
    }

    /// <summary>Кнопка «назад» в шапке сервиса: сначала внутренние страницы сервиса (заметка → список), потом панель.</summary>
    private void FocusBack_Click(object sender, RoutedEventArgs e)
    {
        if (_focusWidget is IBackNavigable nav && nav.TryGoBack()) return;
        if (_focusWidget is IIslandWidget w) w.SetActive(false);
        ShowView(View.Widgets, -1);
    }

    private void ApplyNavigationChrome()
    {
        bool expanded = _state == IslandState.Expanded;
        bool wanted = expanded && _settings.ButtonsEnabled && IsSwipeView(_view) && !_customizing && !_welcome;
        bool bar = wanted && _settings.NavButtonStyle == NavButtonStyle.BottomBar;
        bool tabs = wanted && _settings.NavButtonStyle == NavButtonStyle.TopTabs;

        bool barShown = NavBar.IsHitTestVisible;
        if (bar)
        {
            NavHitArea.Visibility = Visibility.Visible;
            NavHitArea.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        }
        else
        {
            NavHitArea.Background = Brushes.Transparent;
            if (_state != IslandState.Expanded) NavHitArea.Visibility = Visibility.Collapsed;
        }
        if (bar && !barShown) Dispatcher.InvokeAsync(() => MoveNavIndicator(animate: false), DispatcherPriority.Loaded);
        if (bar != barShown)
        {
            NavBar.IsHitTestVisible = bar;
            Fade(NavBar, bar ? 1 : 0, Ms(bar ? 220 : 100), delayMs: bar ? Ms(140) : 0);
            NavBarShift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(bar ? 0 : -6, TimeSpan.FromMilliseconds(Ms(260)))
                {
                    BeginTime = TimeSpan.FromMilliseconds(bar ? Ms(140) : 0),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }

        bool tabsShown = TopTabs.Visibility == Visibility.Visible && TopTabs.IsHitTestVisible;
        if (tabs != tabsShown)
        {
            if (tabs) TopTabs.Visibility = Visibility.Visible;
            TopTabs.IsHitTestVisible = tabs;
            Fade(TopTabs, tabs ? 1 : 0, Ms(tabs ? 220 : 90), delayMs: tabs ? Ms(90) : 0);
        }
        TopTabs.Visibility = _settings.ButtonsEnabled && _settings.NavButtonStyle == NavButtonStyle.TopTabs
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Island_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || !_settings.ScrollEnabled || _state != IslandState.Expanded || !IsSwipeView(_view) || _customizing) return;
        e.Handled = true;
        OnSwipe(-e.Delta);
    }

    private void OnSwipe(double delta)
    {
        if (!_settings.ScrollEnabled || _state != IslandState.Expanded || !IsSwipeView(_view) || _customizing) return;
        if (_wheelLocked)
        {
            _wheelUnlock.Stop();
            _wheelUnlock.Start();
            return;
        }
        _wheelAccum += delta;
        if (Math.Abs(_wheelAccum) < 60) return;
        StepView(_wheelAccum > 0 ? 1 : -1);
        _wheelAccum = 0;
        _wheelLocked = true;
        _wheelUnlock.Start();
    }

    private FrameworkElement CreateWidget(string id)
    {
        switch (id)
        {
            case "media":
                var mw = new MediaWidget(_media, _settings);
                mw.ArtworkClicked += OpenArtwork;
                mw.ArtworkDropped = ArtworkDropped;
                mw.LyricsClicked += OpenLyrics;
                return mw;
            case "notes":
                var nw = new NotesWidget(_notes);
                nw.KeyboardRequested += Pin;
                nw.SizeChanged += (_, _) => { if (_state == IslandState.Expanded) SetTargetsAfterLayout(); };
                return nw;
            case "clock":
                var cw = new ClockWidget();
                cw.SizeChanged += (_, _) => { if (_state == IslandState.Expanded) SetTargetsAfterLayout(); };
                return cw;
            case "timer": return new TimerWidget(_tools.Timer);
            case "stopwatch": return new StopwatchWidget(_tools.Stopwatch);
            case "system": return new SystemWidget();
            case "audio":
                var aw = new AudioWidget(_mixer, _volume);
                aw.SizeChanged += (_, _) => { if (_state == IslandState.Expanded) SetTargetsAfterLayout(); };
                return aw;
            default: return new TextBlock { Text = id };
        }
    }

    private IEnumerable<IMediaView> MediaWidgets()
    {
        foreach (var w in _homeWidgets.OfType<IMediaView>()) yield return w;
        if (_focusWidget is IMediaView fm) yield return fm;
        if (_hero != null) yield return _hero;
        if (_lyricsView != null) yield return _lyricsView;
        if (_fullscreenPlayer != null) yield return _fullscreenPlayer;
    }

    private MediaHero? _hero;
    private View _beforeArtwork = View.Home;

    private void OpenArtwork()
    {
        if (_snap == null) return;
        if (_hero == null)
        {
            _hero = new MediaHero(_media, _settings);
            _hero.CloseRequested += CloseArtwork;
            _hero.LyricsClicked += OpenLyrics;
            _hero.ArtworkDropped = ArtworkDropped;
            _hero.FullscreenRequested += rect => OpenFullscreen(new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect);
            ArtworkHost.Content = _hero;
        }
        _hero.Update(_snap);
        _beforeArtwork = _view == View.Artwork ? View.Home : _view;
        ShowView(View.Artwork);
        _hero.PlayEnterAnimation(_settings.AnimationSpeed);
    }

    private void CloseArtwork() => ShowView(_beforeArtwork, -1);

    private void TickMediaWidgets()
    {
        foreach (var mw in MediaWidgets()) mw.Tick();
    }

    private void ActivateWidgets()
    {
        bool expanded = _state == IslandState.Expanded;
        // Позицию трека показывают раскрытый остров и полноэкранный плеер.
        _media.PositionPolling = expanded || _fullscreenPlayer != null;
        foreach (var w in _homeWidgets.OfType<IIslandWidget>()) w.SetActive(expanded && _view == View.Home);
        if (_focusWidget is IIslandWidget f) f.SetActive(expanded && _view == View.Focus);
        _lyricsView?.SetActive(expanded && _view == View.Lyrics);
    }

    private void BuildHomeRow()
    {
        foreach (var w in _homeWidgets.OfType<IIslandWidget>()) w.SetActive(false);
        _homeWidgets.Clear();
        HomeRow.Children.Clear();

        var ids = _customizing ? _draft : _settings.HomeWidgets;
        if (ids.Count == 0)
        {
            HomeRow.Children.Add(BuildEmptyHome());
        }
        else
        {
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) HomeRow.Children.Add(new Border { Width = 1, Margin = new Thickness(18, 4, 18, 4), Background = (Brush)FindResource("Hairline") });
                var widget = CreateWidget(ids[i]);
                _homeWidgets.Add(widget);
                HomeRow.Children.Add(_customizing ? WrapForEditing(widget, ids[i]) : widget);
            }
        }

        if (_customizing) BuildDock();
        ActivateWidgets();
        foreach (var mw in _homeWidgets.OfType<MediaWidget>()) mw.Update(_snap);
        SetTargetsAfterLayout();
    }

    private UIElement BuildEmptyHome()
    {
        var add = new Button { Style = (Style)FindResource("PillButton"), Content = L.T("Добавить виджеты"), Margin = new Thickness(0, 10, 0, 0) };
        add.Click += (_, _) => EnterCustomize();
        return new StackPanel
        {
            Width = 260,
            Children =
            {
                new TextBlock { Text = L.T("На главной пусто"), FontSize = 13, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = L.T("Музыка, время, таймер — что хотите"), FontSize = 11.5, Foreground = (Brush)FindResource("TextSecondary"), HorizontalAlignment = HorizontalAlignment.Center },
                add,
            },
        };
    }

    private UIElement WrapForEditing(FrameworkElement widget, string id)
    {
        widget.IsHitTestVisible = false;
        var minus = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 20, Height = 20,
            Background = (Brush)FindResource("DangerRed"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -12, -12, 0),
            ToolTip = L.T("Убрать"),
            Content = new SnowyNotch.Controls.IconGlyph
            {
                Data = (Geometry)FindResource("IconMinus"), Foreground = Brushes.White,
                StrokeWidth = 2.2, Width = 16, Height = 16,
            },
        };
        minus.Click += (_, _) => { _draft.Remove(id); BuildHomeRow(); };

        var frame = new Rectangle
        {
            RadiusX = 12, RadiusY = 12,
            Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1.3,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            Margin = new Thickness(-8),
            IsHitTestVisible = false,
        };
        var slot = new Grid { Children = { widget, frame, minus } };

        slot.RenderTransformOrigin = new Point(0.5, 0.5);
        var st = new ScaleTransform(0.96, 0.96);
        slot.RenderTransform = st;
        Scale(st, 1, Ms(260));
        return slot;
    }

    private void BuildServiceDock()
    {
        foreach (var info in WidgetCatalog.All)
            Dock.Add(info.Id, info.Title, info.Icon.Create(SnowyNotch.Controls.ServiceDock.IconSize), info.Icon.Top);
        Dock.Invoked += (id, centerX) =>
        {
            // Сервис раскрывается из своей иконки.
            double left = Dock.TranslatePoint(new Point(centerX, 0), WidgetsView).X;
            _zoomOriginX = WidgetsView.ActualWidth > 0 ? Math.Clamp(left / WidgetsView.ActualWidth, 0, 1) : 0.5;
            if (id == "agents") OpenAgents(); else OpenFocus(id);
        };
        UpdateDockStatus();
    }

    /// <summary>Точки под иконками: какие сервисы сейчас работают.</summary>
    private void UpdateDockStatus()
    {
        if (!Dock.HasItems) return;
        Dock.SetActive("media", _snap?.IsPlaying == true);
        Dock.SetActive("timer", _tools.Timer.IsActive);
        Dock.SetActive("stopwatch", _tools.Stopwatch.IsActive);
        Dock.SetActive("agents", _claudeBubbleWanted);
    }

    private void PlayDockEntrance()
    {
        UpdateDockStatus();
        Dock.ResetScroll();
        Dock.PlayEntrance();
    }

    /// <summary>Виджеты главной появляются по очереди: каждый чуть поднимается и проявляется.</summary>
    private void PlayHomeEntrance(double delayMs = 0)
    {
        if (_customizing) return;
        int i = 0;
        foreach (FrameworkElement child in HomeRow.Children)
        {
            if (child.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                child.RenderTransform = tt;
            }
            var begin = TimeSpan.FromMilliseconds(delayMs + Ms(40 + i * 55));
            child.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Ms(260))) { BeginTime = begin });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(Ms(520)))
            {
                BeginTime = begin, EasingFunction = SnowyNotch.Animation.SpringEase.Lively,
            });
            i++;
        }
    }

    private void EditHome_Click(object sender, RoutedEventArgs e) => EnterCustomize();

    private Button MakeTile(string title, string iconKey, Brush background) => new()
    {
        Style = (Style)FindResource("WidgetTile"),
        Content = title,
        Tag = FindResource(iconKey),
        Background = background,
    };

    private static Brush TileBrush(Color tint)
    {
        var b = new LinearGradientBrush(
            Color.FromRgb((byte)Math.Min(255, tint.R + 40), (byte)Math.Min(255, tint.G + 40), (byte)Math.Min(255, tint.B + 40)),
            tint, 90);
        b.Freeze();
        return b;
    }

    private void OpenFocus(string id)
    {
        if (_focusWidget is IIslandWidget old) old.SetActive(false);
        _focusWidget = CreateWidget(id);
        FocusHost.Content = _focusWidget;
        var info = WidgetCatalog.Find(id);
        FocusTitle.Text = info?.Title ?? "";
        FocusIconHost.Content = info?.Icon.Create(26, glow: false);
        if (_focusWidget is MediaWidget mw) mw.Update(_snap);
        if (_focusWidget is ClockWidget cw) cw.EnableCalendar();
        ShowView(View.Focus, 1);
    }

    private void EnterCustomize()
    {
        _welcome = false;
        _customizing = true;
        _draft = new List<string>(_settings.HomeWidgets);
        CustomizeDock.Visibility = Visibility.Visible;
        BuildHomeRow();
        ShowView(View.Home);
        Pin();
        UpdateState();
    }

    private void BuildDock()
    {
        DockTiles.Children.Clear();
        bool full = _draft.Count >= AppSettings.MaxHomeWidgets;
        foreach (var info in WidgetCatalog.All.Where(w => !_draft.Contains(w.Id)))
        {
            var tile = MakeTile(info.Title, info.IconKey, TileBrush(info.Tint));
            tile.IsEnabled = !full;
            tile.ToolTip = full ? L.F("На главной помещается {0} виджета", AppSettings.MaxHomeWidgets) : L.T("Добавить на главную");
            tile.Click += (_, _) =>
            {
                if (_draft.Count >= AppSettings.MaxHomeWidgets) return;
                _draft.Add(info.Id);
                BuildHomeRow();
            };
            DockTiles.Children.Add(tile);
        }
        if (DockTiles.Children.Count == 0)
            DockTiles.Children.Add(new TextBlock { Text = L.T("Все виджеты уже на главной"), FontSize = 12, Foreground = (Brush)FindResource("TextSecondary"), VerticalAlignment = VerticalAlignment.Center });
    }

    private void ExitCustomize(bool save)
    {
        if (!_customizing) return;
        _customizing = false;
        CustomizeDock.Visibility = Visibility.Collapsed;
        if (save) _settings.HomeWidgets = new List<string>(_draft);
        BuildHomeRow();
        ApplyNavigationChrome();
    }

    private void CustomizeDone_Click(object sender, RoutedEventArgs e)
    {
        ExitCustomize(save: true);
        _hoverActive = true;
        ClosePinned();
    }

    private void CustomizeCancel_Click(object sender, RoutedEventArgs e)
    {
        ExitCustomize(save: false);
        _hoverActive = true;
        ClosePinned();
    }

#if DEBUG
    public void DebugPreview(string what)
    {
        _welcome = false;
        switch (what)
        {
            case "tray": ShowView(View.Tray); break;
            case "widgets": ShowView(View.Widgets); break;
            case "settings": ShowView(View.Settings); break;
            case "artwork": _peeking = true; UpdateState(); OpenArtwork(); break;
            case "hud": _hudKind = HudKind.Volume; RefreshHud(false); ShowView(View.Hud); break;
            case "agent": OpenAgents(); break;
            case "headset": ShowDeviceHud(new HeadsetInfo(L.T("Наушники (демо)"), 72), L.T("Подключено"));
                _headsetActive = true; break;
            case "lyrics-demo":
                EnsureLyricsView().ShowDemo(_snap);
                ShowView(View.Lyrics);
                break;
            case "lyrics-shot": ShowView(View.Lyrics); break; // текст уже подставлен через ShowDemo
            case "look": EnsureSettingsView().ShowSection("look"); ShowView(View.Settings); break;
            case "customize":
                _customizing = true;
                _draft = new List<string>(_settings.HomeWidgets);
                CustomizeDock.Visibility = Visibility.Visible;
                BuildHomeRow();
                ShowView(View.Home);
                break;
            default:
                if (what.StartsWith("focus:")) OpenFocus(what[6..]);
                else ShowView(View.Home);
                break;
        }
        _peeking = true;
        _peekTimer.Stop();
        _peekTimer.Interval = TimeSpan.FromSeconds(25);
        _peekTimer.Start();
        UpdateState();
    }
#endif
}
