using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SnowyNotch.Animation;
using SnowyNotch.Localization;
using SnowyNotch.Services;
using SnowyNotch.Widgets;

namespace SnowyNotch.Views;

/// <summary>
/// Плеер во весь экран. Четыре стиля (<see cref="FullscreenStyle"/>):
/// • Классический — обложка слева, синхронный текст справа;
/// • Кино — кашетирование, плёночное зерно, луч проектора с пылью, крупный текст по центру, обложка в углу;
/// • В центре — большая обложка по центру, текущая строка — субтитром;
/// • Винил — обложка становится этикеткой крутящейся пластинки, тонарм опускается при игре.
/// Фон — размытая «живая» обложка с цветными пятнами в её тон; свечение, пятна и частицы пульсируют в такт музыке.
/// Выход — Esc или стрелка. Пробел — пауза, ← / → — перемотка на 5 с, S — сменить стиль. Строку текста можно нажать.
/// </summary>
public partial class FullscreenPlayer : Window, IMediaView
{
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(120);
    private const int EffectRange = 5;
    private const int ParticleCount = 46;

    private readonly MediaService _media;
    private readonly LyricsService _lyrics;
    private readonly AppSettings _settings;
    private readonly AudioMeter _meter = new();
    private readonly Point _screenPx;
    private readonly Rect? _fromPx;

    private MediaSnapshot? _snap;
    private string _trackKey = "";
    private (object? Art, string Title) _artKey;
    private int _loadVersion;
    private bool _demo, _closing, _entered, _backB, _pausedShown = true;
    private FullscreenStyle _style;

    private enum LyricsState { Off, Searching, Synced, Plain, None }
    private LyricsState _lyricsState = LyricsState.Searching;

    private readonly List<LineVisual> _lines = new();
    private IReadOnlyList<LyricLine> _lyricLines = Array.Empty<LyricLine>();
    private int _current = -2;
    private double _lyricFont = 40;
    private bool _captionA;

    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(2.6) };
    private readonly DispatcherTimer _toastHide = new() { Interval = TimeSpan.FromSeconds(1.4) };
    private Point _lastMouse;
    private bool _chromeShown;

    // Покадровые эффекты.
    private TimeSpan _lastFrame;
    private double _time, _level, _spin, _spinSpeed;
    private double _aurora = 0.55, _beam, _particles = 0.5, _grainTick;
    private readonly Random _rnd = new();
    private readonly (TranslateTransform T, double X, double Y, double Speed, double Phase)[] _dust = new (TranslateTransform, double, double, double, double)[ParticleCount];
    private readonly TranslateTransform _grainShift = new();

    private sealed class LineVisual
    {
        public required TextBlock Text;
        public required TranslateTransform Shift;
        public required ScaleTransform Scale;
        public required BlurEffect Blur;
        public required DropShadowEffect Glow;
        public double BaseY, TargetOpacity = 0.2, TargetBlur = 4;
    }

    /// <param name="screenPx">Точка на нужном мониторе (пиксели экрана).</param>
    /// <param name="fromPx">Откуда подлетает обложка (пиксели экрана); null — просто появляется.</param>
    public FullscreenPlayer(MediaService media, LyricsService lyrics, AppSettings settings, Point screenPx, Rect? fromPx)
    {
        InitializeComponent();
        _media = media;
        _lyrics = lyrics;
        _settings = settings;
        _screenPx = screenPx;
        _fromPx = fromPx;
        _style = settings.FullscreenStyle;

        BackArtB.RenderTransform = BackArtA.RenderTransform; // оба слоя фона дрейфуют вместе
        Disc.Fill = VinylGrooves;
        Grain.Fill = new ImageBrush(Noise)
        {
            TileMode = TileMode.Tile, Stretch = Stretch.None, ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 192, 192), Transform = _grainShift,
        };
        BuildParticles();

        SourceInitialized += (_, _) => CoverMonitor();
        SizeChanged += (_, _) => Relayout();
        LyricsPane.MouseWheel += LyricsPane_MouseWheel;
        LyricsPane.SizeChanged += (_, e) => LyricsMask.EndPoint = new Point(0, e.NewSize.Height); // маска — по видимой высоте
        _browseEnd.Tick += (_, _) => EndBrowse();
        ContentRendered += (_, _) => PlayEnter();
        PreviewKeyDown += OnKey;
        MouseMove += OnMouseMove;
        _idle.Tick += (_, _) => { _idle.Stop(); HideChrome(); };
        _toastHide.Tick += (_, _) => { _toastHide.Stop(); StyleToast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400))); };
        Closed += (_, _) => { CompositionTarget.Rendering -= OnFrame; _idle.Stop(); _browseEnd.Stop(); _meter.Dispose(); };
        ApplyStyleVisuals(animate: false);
        ApplySettings();
    }

    // ======================= окно =======================

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Растянуть окно ровно на монитор (в пикселях — корректно при любом масштабе экрана).</summary>
    private void CoverMonitor()
    {
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)_screenPx.X, (int)_screenPx.Y));
        var b = screen.Bounds;
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, new IntPtr(-1), b.X, b.Y, b.Width, b.Height, 0x0040 /* SWP_SHOWWINDOW */);
    }

    private bool HasLyricsColumn => _lyricsState is LyricsState.Searching or LyricsState.Synced or LyricsState.Plain;

    /// <summary>Раскладка под стиль и размер экрана: где обложка, сколько места тексту, какие размеры шрифтов.</summary>
    private void Relayout()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        bool lyrics = HasLyricsColumn;
        double bars = _style == FullscreenStyle.Cinematic ? Math.Round(h * 0.085) : 0;

        double s;
        // Иммерсивные стили с текстом: обложка уходит в левый нижний угол, экран — тексту.
        bool corner = lyrics && Immersive;
        if (corner) s = Math.Round(h * 0.16);
        else if (Immersive || _style == FullscreenStyle.Spotlight) s = Math.Round(Math.Min(h * 0.46, w * 0.33));
        else s = Math.Round(lyrics ? Math.Min(h * 0.44, w * 0.27) : Math.Min(h * 0.52, w * 0.36));

        CoverHost.Width = CoverHost.Height = s;
        CoverHost.HorizontalAlignment = corner ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        LeftCol.Width = corner ? Math.Max(s, 420) : Math.Max(s, _style == FullscreenStyle.Spotlight ? Math.Min(w * 0.5, 640) : s);
        LeftCol.HorizontalAlignment = corner ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        LeftCol.VerticalAlignment = corner ? VerticalAlignment.Bottom : VerticalAlignment.Center;
        LeftCol.Margin = corner ? new Thickness(w * 0.045, 0, 0, bars + h * 0.045) : new Thickness(0);
        TitleRow.Margin = new Thickness(0, corner ? 18 : 40, 0, 0);

        double titleBase = corner ? 0.15 : 0.072;
        TitleText.FontSize = Math.Clamp(s * titleBase, 22, 40);
        ArtistText.FontSize = Math.Clamp(TitleText.FontSize * 0.62, 15, 24);

        // Колонки и место текста.
        bool centeredText = CenteredLines;
        bool ownHost = _style is FullscreenStyle.Spotlight or FullscreenStyle.Kinetic or FullscreenStyle.Float;
        if (!lyrics || ownHost)
        {
            RightColDef.Width = new GridLength(0);
            LyricsPane.Visibility = Visibility.Collapsed;
        }
        else
        {
            LyricsPane.Visibility = Visibility.Visible;
            if (centeredText)
            {
                RightColDef.Width = new GridLength(0);
                Grid.SetColumn(LyricsPane, 0);
                Grid.SetColumnSpan(LyricsPane, 2);
                LyricsPane.Margin = _style == FullscreenStyle.Depth
                    ? new Thickness(w * 0.1, h * 0.02, w * 0.1, h * 0.2)
                    : new Thickness(w * 0.14, bars + h * 0.04, w * 0.14, bars + h * 0.24);
            }
            else
            {
                RightColDef.Width = new GridLength(1.15, GridUnitType.Star);
                Grid.SetColumn(LyricsPane, 1);
                Grid.SetColumnSpan(LyricsPane, 1);
                LyricsPane.Margin = new Thickness(0, 0, w * 0.06, 0);
            }
        }

        _lyricFont = Math.Clamp(h * (_style == FullscreenStyle.Depth ? 0.064 : centeredText ? 0.058 : 0.04), 24, 72);
        KineticHost.Margin = new Thickness(w * 0.1, h * 0.06, w * 0.1, h * 0.26);
        PlainText.FontSize = _lyricFont * 0.66;
        PlainText.LineHeight = _lyricFont;
        PlainText.TextAlignment = centeredText ? TextAlignment.Center : TextAlignment.Left;
        LyricsStatus.HorizontalAlignment = centeredText ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        CaptionA.FontSize = CaptionB.FontSize = Math.Clamp(h * 0.03, 20, 34);

        if (_entered)
        {
            var bar = new DoubleAnimation(bars, TimeSpan.FromMilliseconds(700)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } };
            BarTop.BeginAnimation(HeightProperty, bar);
            BarBottom.BeginAnimation(HeightProperty, bar);
        }
        else BarTop.Height = BarBottom.Height = bars;

        // Цветные пятна — по углам и центру экрана; частицы — по всему экрану.
        Canvas.SetLeft(BlobA, -w * 0.12); Canvas.SetTop(BlobA, -h * 0.2);
        Canvas.SetLeft(BlobB, w * 0.55); Canvas.SetTop(BlobB, h * 0.35);
        Canvas.SetLeft(BlobC, w * 0.3); Canvas.SetTop(BlobC, -h * 0.1);

        LayoutVinyl(s);
        foreach (var v in _lines) v.Text.FontSize = _lyricFont;
        Dispatcher.InvokeAsync(() =>
        {
            RelayoutLines();
            if (_current >= -1) Highlight(_current, animate: false);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Пластинка под размер обложки: этикетка, ось, тонарм (поворачивается вокруг стойки справа сверху).</summary>
    private void LayoutVinyl(double s)
    {
        double label = s * 0.36;
        Label.Width = Label.Height = LabelRing.Width = LabelRing.Height = label;
        Spindle.Width = Spindle.Height = Math.Max(8, s * 0.028);
        var pivot = new Point(s * 0.95, s * 0.02);
        var elbow = new Point(s * 0.9, s * 0.42);
        var end = new Point(s * 0.75, s * 0.71); // игла — на бороздках, а не на этикетке
        ArmPath.Data = new PathGeometry(new[]
        {
            new PathFigure(pivot, new PathSegment[] { new LineSegment(elbow, true), new LineSegment(end, true) }, false),
        });
        ArmPath.StrokeThickness = Math.Max(3, s * 0.014);
        double pv = Math.Max(20, s * 0.09);
        ArmPivot.Width = ArmPivot.Height = pv;
        Canvas.SetLeft(ArmPivot, pivot.X - pv / 2); Canvas.SetTop(ArmPivot, pivot.Y - pv / 2);
        ArmHead.Width = Math.Max(14, s * 0.06); ArmHead.Height = Math.Max(9, s * 0.036);
        Canvas.SetLeft(ArmHead, end.X - ArmHead.Width / 2); Canvas.SetTop(ArmHead, end.Y - ArmHead.Height / 2);
        ArmAngle.CenterX = pivot.X;
        ArmAngle.CenterY = pivot.Y;
    }

    // ======================= стили =======================

    private static string StyleName(FullscreenStyle s) => s switch
    {
        FullscreenStyle.Cinematic => L.T("Кино"),
        FullscreenStyle.Spotlight => L.T("В центре"),
        FullscreenStyle.Vinyl => L.T("Винил"),
        FullscreenStyle.Depth => L.T("Глубина"),
        FullscreenStyle.Kinetic => L.T("Кинетика"),
        FullscreenStyle.Float => L.T("Парящий"),
        _ => L.T("Классический"),
    };

    /// <summary>Погружение: обложка с управлением уходят в угол, управление прячется вместе с курсором.</summary>
    private bool Immersive => _style is FullscreenStyle.Cinematic or FullscreenStyle.Depth or FullscreenStyle.Kinetic or FullscreenStyle.Float;

    /// <summary>Строки текста по центру экрана (а не колонкой справа).</summary>
    private bool CenteredLines => _style is FullscreenStyle.Cinematic or FullscreenStyle.Depth;

    /// <summary>Параметры фона и декора стиля. Пятна, луч и частицы подтягиваются к ним плавно (покадрово).</summary>
    private void ApplyStyleVisuals(bool animate)
    {
        var dur = TimeSpan.FromMilliseconds(animate ? 700 : 0);
        bool cinema = _style == FullscreenStyle.Cinematic, vinyl = _style == FullscreenStyle.Vinyl, spot = _style == FullscreenStyle.Spotlight;

        double blur = _style switch
        {
            FullscreenStyle.Cinematic => 64, FullscreenStyle.Spotlight => 120, FullscreenStyle.Kinetic => 100,
            FullscreenStyle.Float => 120, FullscreenStyle.Depth => 140, _ => 160,
        };
        BlurA.Radius = BlurB.Radius = blur;
        Scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(cinema ? 0.75 : _style == FullscreenStyle.Kinetic ? 1.15 : 1, dur));
        Vignette.BeginAnimation(OpacityProperty, new DoubleAnimation(cinema || _style == FullscreenStyle.Depth ? 1 : spot ? 0.85 : 0.7, dur));
        Grain.BeginAnimation(OpacityProperty, new DoubleAnimation(cinema ? 0.085 : 0, dur));

        SquareArt.Visibility = vinyl ? Visibility.Collapsed : Visibility.Visible;
        VinylArt.Visibility = vinyl ? Visibility.Visible : Visibility.Collapsed;
        Caption.Visibility = spot ? Visibility.Visible : Visibility.Collapsed;
        KineticHost.Visibility = _style == FullscreenStyle.Kinetic ? Visibility.Visible : Visibility.Collapsed;
        FloatHost.Visibility = _style == FullscreenStyle.Float ? Visibility.Visible : Visibility.Collapsed;
        if (_style != FullscreenStyle.Float) FloatHost.Clear();
        if (_style != FullscreenStyle.Kinetic) KineticHost.Children.Clear();
        _kineticIdx = _floatIdx = -2;
        Bars.Visibility = _settings.ShowVisualizer && _style is FullscreenStyle.Classic or FullscreenStyle.Vinyl ? Visibility.Visible : Visibility.Collapsed;
        TitleStack.Margin = new Thickness(0, 0, spot ? 0 : 44, 0);
        TitleText.TextAlignment = ArtistText.TextAlignment = spot ? TextAlignment.Center : TextAlignment.Left;
        StyleLabel.Text = StyleName(_style);

        // Вне иммерсивных стилей управление всегда видно.
        if (!Immersive && _entered)
        {
            ProgressRow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, dur));
            ControlsRow.BeginAnimation(OpacityProperty, new DoubleAnimation(1, dur));
        }
    }

    private double AuroraTarget => _style switch
    {
        FullscreenStyle.Cinematic => 0.3, FullscreenStyle.Spotlight => 0.7, FullscreenStyle.Vinyl => 0.45,
        FullscreenStyle.Kinetic => 0.65, FullscreenStyle.Float => 0.6, FullscreenStyle.Depth => 0.5, _ => 0.55,
    };
    private double BeamTarget => _style == FullscreenStyle.Cinematic ? 0.55 : _style == FullscreenStyle.Depth ? 0.25 : 0;
    private double ParticlesTarget => _style switch
    {
        FullscreenStyle.Cinematic => 0.9, FullscreenStyle.Depth => 0.85, FullscreenStyle.Spotlight => 0.6,
        FullscreenStyle.Float => 0.5, FullscreenStyle.Kinetic => 0.3, _ => 0.4,
    };

    private const int StyleCount = 7;

    private void CycleStyle() => ChooseStyle((FullscreenStyle)(((int)_style + 1) % StyleCount));

    private void ChooseStyle(FullscreenStyle style)
    {
        _settings.FullscreenStyle = style; // ApplySettings придёт следом и переключит
        if (_style != style) TransitionTo(style);
    }

    /// <summary>Меню стилей под кнопкой: все стили списком, текущий отмечен.</summary>
    private void OpenStyleMenu()
    {
        StyleList.Children.Clear();
        for (int i = 0; i < StyleCount; i++)
        {
            var style = (FullscreenStyle)i;
            bool current = style == _style;
            var row = new Button
            {
                Style = (Style)FindResource("PillButton"), Background = current ? new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 1, 0, 1),
                Content = new Grid
                {
                    Children =
                    {
                        new TextBlock { Text = StyleName(style), FontSize = 15, FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Brushes.White },
                        new TextBlock { Text = current ? "✓" : "", FontSize = 15, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Right },
                    },
                },
            };
            row.Click += (_, _) => { StyleMenu.IsOpen = false; ChooseStyle(style); };
            StyleList.Children.Add(row);
        }
        StyleMenu.IsOpen = true;
        _idle.Stop();
    }

    /// <summary>Нажата кнопка «Текст поверх экрана»: выключен — включить (плеер закроется), включён — выключить.</summary>
    public event Action? OverlayToggleRequested;

    private void Overlay_Click(object sender, RoutedEventArgs e) => OverlayToggleRequested?.Invoke();

    /// <summary>Показать на кнопке, включён ли «Текст поверх экрана». <paramref name="announce"/> — всплывающая подпись.</summary>
    public void SetOverlayActive(bool active, bool announce = false)
    {
        OverlayButton.Background = new SolidColorBrush(active ? Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        OverlayGlyph.Foreground = active ? Brushes.Black : Brushes.White;
        OverlayButton.ToolTip = active ? L.T("Выключить текст поверх экрана") : L.T("Текст поверх экрана");
        if (announce) ShowToast(active ? L.T("Текст поверх экрана включён") : L.T("Текст поверх экрана выключен"));
    }
    /// <summary>Смена стиля: сцена гаснет, перестраивается и проявляется с пружинкой; вверху — название стиля.</summary>
    private void TransitionTo(FullscreenStyle style)
    {
        if (style == _style) return;
        _style = style;
        ShowToast(StyleName(style));
        if (!_entered)
        {
            ApplyStyleVisuals(false);
            Relayout();
            return;
        }
        var outDur = TimeSpan.FromMilliseconds(170);
        var fade = new DoubleAnimation(0, outDur) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            ApplyStyleVisuals(true);
            Relayout();
            Stage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)));
            var grow = new DoubleAnimation(0.95, 1, TimeSpan.FromMilliseconds(650)) { EasingFunction = SpringEase.Lively };
            StageScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            StageScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            Shine();
        };
        Stage.BeginAnimation(OpacityProperty, fade);
        var shrink = new DoubleAnimation(0.97, outDur);
        StageScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        StageScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    private void ShowToast(string text)
    {
        StyleToastText.Text = text;
        StyleToast.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        ShowChrome();
        _toastHide.Stop();
        _toastHide.Start();
    }

    /// <summary>Блик пробегает по обложке — при появлении, смене трека и стиля.</summary>
    private void Shine()
    {
        CoverShine.Opacity = 1;
        var run = new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(1100)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } };
        run.Completed += (_, _) => CoverShine.Opacity = 0;
        ShineMove.BeginAnimation(TranslateTransform.XProperty, run);
    }

    // ======================= появление и выход =======================

    private void PlayEnter()
    {
        if (_entered) return;
        _entered = true;
        Activate();
        Focus();

        Backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(900)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        StartAmbient();

        // Обложка подлетает с места, где её отпустили, и пружинисто встаёт на своё место.
        CoverHost.Opacity = 1;
        var final = CoverHost.TransformToAncestor(Root).TransformBounds(new Rect(0, 0, CoverHost.ActualWidth, CoverHost.ActualHeight));
        double fromScale = 0.6;
        Vector fromShift = new(0, 40);
        if (_fromPx is Rect r && final.Width > 0)
        {
            var a = Root.PointFromScreen(r.TopLeft);
            var b = Root.PointFromScreen(r.BottomRight);
            var from = new Rect(a, b);
            fromScale = Math.Clamp(from.Width / final.Width, 0.05, 1.5);
            fromShift = new Vector(from.X + from.Width / 2 - (final.X + final.Width / 2), from.Y + from.Height / 2 - (final.Y + final.Height / 2));
        }
        var fly = TimeSpan.FromMilliseconds(820);
        var grow = new DoubleAnimation(fromScale, 1, fly) { EasingFunction = SpringEase.Lively };
        FlyScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        FlyScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        FlyShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromShift.X, 0, fly) { EasingFunction = SpringEase.Lively });
        FlyShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromShift.Y, 0, fly) { EasingFunction = SpringEase.Lively });
        CoverGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, _pausedShown ? 0.35 : 0.85, TimeSpan.FromMilliseconds(1200)) { BeginTime = TimeSpan.FromMilliseconds(300) });

        // Кашетирование въезжает, как занавес.
        if (_style == FullscreenStyle.Cinematic)
        {
            var h = BarTop.Height;
            var bar = new DoubleAnimation(0, h, TimeSpan.FromMilliseconds(900)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } };
            BarTop.BeginAnimation(HeightProperty, bar);
            BarBottom.BeginAnimation(HeightProperty, bar);
        }

        int i = 0;
        foreach (FrameworkElement el in new FrameworkElement[] { TitleRow, ProgressRow, ControlsRow })
            Rise(el, 240 + i++ * 70);
        LyricsPane.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700)) { BeginTime = TimeSpan.FromMilliseconds(360) });
        LyricsShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(70, 0, TimeSpan.FromMilliseconds(900)) { BeginTime = TimeSpan.FromMilliseconds(360), EasingFunction = SpringEase.Soft });
        Dispatcher.InvokeAsync(Shine, DispatcherPriority.Background);
        ShowChrome();

        CompositionTarget.Rendering += OnFrame;
    }

    private static void Rise(FrameworkElement el, double delayMs)
    {
        var begin = TimeSpan.FromMilliseconds(delayMs);
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420)) { BeginTime = begin });
        ((TranslateTransform)el.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(700)) { BeginTime = begin, EasingFunction = SpringEase.Lively });
    }

    /// <summary>Медленное «дыхание» фона: размытая обложка дрейфует и поворачивается, пятна плывут.</summary>
    private void StartAmbient()
    {
        var slow = new SineEase { EasingMode = EasingMode.EaseInOut };
        DriftScale.BeginAnimation(ScaleTransform.ScaleXProperty, Loop(1.12, 1.3, 23, slow));
        DriftScale.BeginAnimation(ScaleTransform.ScaleYProperty, Loop(1.12, 1.3, 23, slow));
        DriftRotate.BeginAnimation(RotateTransform.AngleProperty, Loop(-6, 6, 31, slow));
        BlobAMove.BeginAnimation(TranslateTransform.XProperty, Loop(0, 260, 17, slow));
        BlobAMove.BeginAnimation(TranslateTransform.YProperty, Loop(0, 180, 21, slow));
        BlobBMove.BeginAnimation(TranslateTransform.XProperty, Loop(0, -320, 25, slow));
        BlobBMove.BeginAnimation(TranslateTransform.YProperty, Loop(0, -160, 19, slow));
        BlobCMove.BeginAnimation(TranslateTransform.XProperty, Loop(0, 200, 29, slow));
        BlobCMove.BeginAnimation(TranslateTransform.YProperty, Loop(0, 260, 23, slow));
    }

    private static DoubleAnimation Loop(double from, double to, double seconds, IEasingFunction ease) =>
        new(from, to, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };

    public void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        var dur = TimeSpan.FromMilliseconds(260);
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var shrink = new DoubleAnimation(0.94, dur) { EasingFunction = ease };
        StageScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        StageScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        Stage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, dur));
        TopBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        TopRight.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease };
        fade.Completed += (_, _) => Close();
        Backdrop.BeginAnimation(OpacityProperty, fade);
    }

    // ======================= покадровые эффекты =======================

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : Math.Clamp((now - _lastFrame).TotalSeconds, 0, 0.05);
        _lastFrame = now;
        if (dt <= 0) return;
        _time += dt;
        Tick();

        // Громкость: быстро вверх, плавно вниз — пульс в такт музыке.
        bool playing = _snap?.IsPlaying == true;
        double peak = playing ? _meter.Peak() : 0;
        _level = Math.Max(peak, _level - dt * 1.6);

        double k = 1 - Math.Exp(-dt * 3);
        _aurora += (AuroraTarget - _aurora) * k;
        _beam += (BeamTarget - _beam) * k;
        _particles += (ParticlesTarget - _particles) * k;
        Aurora.Opacity = Math.Min(1, _aurora + 0.3 * _level);
        Beam.Opacity = _beam <= 0.005 ? 0 : _beam * (0.85 + 0.1 * Math.Sin(_time * 23) + 0.05 * Math.Sin(_time * 61)) + 0.25 * _level * _beam;
        Particles.Opacity = _particles;
        GlowScale.ScaleX = GlowScale.ScaleY = 1 + 0.12 * _level;

        // Частицы плывут вверх, покачиваясь; в такт — быстрее.
        double w = ActualWidth, h = ActualHeight, boost = 1 + 2.2 * _level;
        for (int i = 0; i < _dust.Length; i++)
        {
            var d = _dust[i];
            d.Y -= d.Speed * dt * boost;
            if (d.Y < -0.04) { d.Y = 1.04; d.X = _rnd.NextDouble(); }
            double x = d.X + 0.012 * Math.Sin(_time * 0.7 + d.Phase);
            d.T.X = x * w;
            d.T.Y = d.Y * h;
            _dust[i] = d;
        }

        // Пластинка: 33⅓ об/мин при игре, плавный разгон и остановка.
        if (_style == FullscreenStyle.Vinyl)
        {
            _spinSpeed += ((playing ? 200 : 0) - _spinSpeed) * (1 - Math.Exp(-dt * 2.2));
            _spin = (_spin + _spinSpeed * dt) % 360;
            DiscSpin.Angle = _spin;
        }

        // Плёночное зерно дрожит ~24 раза в секунду.
        if (_style == FullscreenStyle.Cinematic && (_grainTick += dt) > 1 / 24.0)
        {
            _grainTick = 0;
            _grainShift.X = _rnd.Next(192);
            _grainShift.Y = _rnd.Next(192);
        }
    }

    private void BuildParticles()
    {
        for (int i = 0; i < ParticleCount; i++)
        {
            double size = 2 + _rnd.NextDouble() * 5;
            var brush = new RadialGradientBrush(Color.FromArgb((byte)(120 + _rnd.Next(120)), 255, 250, 240), Color.FromArgb(0, 255, 250, 240));
            brush.Freeze();
            var t = new TranslateTransform();
            Particles.Children.Add(new Ellipse { Width = size * 2.4, Height = size * 2.4, Fill = brush, RenderTransform = t, Opacity = 0.3 + _rnd.NextDouble() * 0.7 });
            _dust[i] = (t, _rnd.NextDouble(), _rnd.NextDouble(), 0.012 + _rnd.NextDouble() * 0.03, _rnd.NextDouble() * Math.PI * 2);
        }
    }

    private static readonly Brush VinylGrooves = MakeGrooves();

    private static Brush MakeGrooves()
    {
        var b = new RadialGradientBrush();
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x1A, 0x1C), 0));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x10, 0x10, 0x12), 0.4));
        bool dark = true;
        for (double o = 0.42; o < 0.985; o += 0.011)
        {
            byte v = (byte)(dark ? 0x0C : 0x19);
            b.GradientStops.Add(new GradientStop(Color.FromRgb(v, v, (byte)(v + 2)), o));
            dark = !dark;
        }
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x22, 0x22, 0x25), 0.99));
        b.GradientStops.Add(new GradientStop(Color.FromRgb(0x05, 0x05, 0x06), 1));
        b.Freeze();
        return b;
    }

    private static readonly BitmapSource Noise = MakeNoise();

    private static BitmapSource MakeNoise()
    {
        const int n = 192;
        var px = new byte[n * n * 4];
        var r = new Random(7);
        for (int i = 0; i < n * n; i++)
        {
            byte a = (byte)r.Next(0, 90);
            byte v = (byte)(r.Next(0, 256) * a / 255); // Pbgra: цвет домножен на прозрачность
            px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = v;
            px[i * 4 + 3] = a;
        }
        var bmp = BitmapSource.Create(n, n, 96, 96, PixelFormats.Pbgra32, null, px, n * 4);
        bmp.Freeze();
        return bmp;
    }

    // ======================= ввод =======================

    private async void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                CloseAnimated();
                break;
            case Key.Space:
                e.Handled = true;
                await TogglePlay();
                break;
            case Key.S:
                e.Handled = true;
                CycleStyle();
                break;
            case Key.Left or Key.Right when _snap is { Duration.TotalSeconds: > 0 }:
                e.Handled = true;
                var to = _snap.CurrentPosition + TimeSpan.FromSeconds(e.Key == Key.Left ? -5 : 5);
                await _media.SeekAsync(TimeSpan.FromSeconds(Math.Clamp(to.TotalSeconds, 0, _snap.Duration.TotalSeconds)));
                break;
        }
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if ((p - _lastMouse).Length < 2) return;
        _lastMouse = p;
        ShowChrome();
    }

    private void ShowChrome()
    {
        Root.Cursor = null;
        _idle.Stop();
        _idle.Start();
        if (_chromeShown) return;
        _chromeShown = true;
        var show = new DoubleAnimation(1, TimeSpan.FromMilliseconds(220));
        TopBar.BeginAnimation(OpacityProperty, show);
        TopRight.BeginAnimation(OpacityProperty, show);
        if (Immersive && _entered)
        {
            ProgressRow.BeginAnimation(OpacityProperty, show);
            ControlsRow.BeginAnimation(OpacityProperty, show);
        }
    }

    /// <summary>Мышь не двигается — прячем кнопки и курсор (в «Кино» — и управление): остаётся только музыка.</summary>
    private void HideChrome()
    {
        if (TopBar.IsMouseOver || TopRight.IsMouseOver || ControlsRow.IsMouseOver || ProgressRow.IsMouseOver) { _idle.Start(); return; }
        _chromeShown = false;
        Root.Cursor = Cursors.None;
        var hide = new DoubleAnimation(0, TimeSpan.FromMilliseconds(700));
        TopBar.BeginAnimation(OpacityProperty, hide);
        TopRight.BeginAnimation(OpacityProperty, hide);
        if (Immersive)
        {
            ProgressRow.BeginAnimation(OpacityProperty, hide);
            ControlsRow.BeginAnimation(OpacityProperty, hide);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseAnimated();
    private void Style_Click(object sender, RoutedEventArgs e) => OpenStyleMenu();
    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await TogglePlay();
    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void Prev_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    private async System.Threading.Tasks.Task TogglePlay()
    {
        // Кнопка откликается сразу; настоящее состояние придёт от плеера.
        if (_snap != null) ShowPlaying(!_snap.IsPlaying);
        await _media.TogglePlayPauseAsync();
    }

    private async void Track_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_snap == null || _snap.Duration <= TimeSpan.Zero || Track.ActualWidth <= 0) return;
        e.Handled = true;
        double f = Math.Clamp(e.GetPosition(Track).X / Track.ActualWidth, 0, 1);
        await _media.SeekAsync(TimeSpan.FromSeconds(_snap.Duration.TotalSeconds * f));
    }

    // ======================= данные =======================

    public void ApplySettings()
    {
        Wave.IsWavy = _settings.WavyProgress;
        Bars.Visibility = _settings.ShowVisualizer && _style is FullscreenStyle.Classic or FullscreenStyle.Vinyl ? Visibility.Visible : Visibility.Collapsed;
        if (_settings.FullscreenStyle != _style) TransitionTo(_settings.FullscreenStyle);
    }

    public void Update(MediaSnapshot? snap)
    {
        _snap = snap;
        if (snap == null)
        {
            Bars.IsPlaying = false;
            Wave.IsPlaying = false;
            ShowPlaying(false);
            return;
        }

        TitleText.Text = snap.Title;
        ArtistText.Text = snap.Artist;
        Bars.IsPlaying = snap.IsPlaying;
        Bars.BarColor = Brighten(snap.Accent);
        Wave.IsPlaying = snap.IsPlaying;
        ShowPlaying(snap.IsPlaying);

        var key = (snap.Artwork as object, snap.Title);
        if (key != _artKey)
        {
            bool first = _artKey == default;
            _artKey = key;
            ApplyArt(snap, animate: !first);
        }

        string trackKey = $"{snap.Artist}\u0001{snap.Title}";
        if (trackKey != _trackKey)
        {
            _trackKey = trackKey;
            _demo = false;
            _ = LoadLyricsAsync(snap);
        }
        Tick();
    }

    private void ShowPlaying(bool playing)
    {
        PlayGlyph.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        PauseGlyph.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
        SetPaused(!playing);
    }

    /// <summary>Новая обложка: плавно сменяет старую и на переднем плане, и в размытом фоне; цвета пятен — из неё.</summary>
    private void ApplyArt(MediaSnapshot snap, bool animate)
    {
        var brush = MediaVisuals.ArtBrush(snap);
        var c = snap.Accent;
        var dur = TimeSpan.FromMilliseconds(animate ? 900 : 0);

        if (animate)
        {
            CoverNext.Background = brush;
            var show = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(450));
            show.Completed += (_, _) => { Cover.Background = brush; CoverNext.BeginAnimation(OpacityProperty, null); CoverNext.Opacity = 0; };
            CoverNext.BeginAnimation(OpacityProperty, show);
            var pop = new DoubleAnimation(0.93, 1, TimeSpan.FromMilliseconds(600)) { EasingFunction = SpringEase.Bouncy };
            FlyScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            FlyScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            Shine();
        }
        else Cover.Background = brush;
        Label.Fill = brush;

        var (front, back) = _backB ? (BackArtA, BackArtB) : (BackArtB, BackArtA);
        _backB = !_backB;
        front.Background = brush;
        front.BeginAnimation(OpacityProperty, new DoubleAnimation(1, dur));
        back.BeginAnimation(OpacityProperty, new DoubleAnimation(0, dur));

        Tint(CoverGlowIn, CoverGlowOut, Brighten(c), 0x90, dur);
        Tint(BlobAIn, BlobAOut, Hue(c, 0), 0xB0, dur);
        Tint(BlobBIn, BlobBOut, Hue(c, 40), 0xA0, dur);
        Tint(BlobCIn, BlobCOut, Hue(c, -50), 0x90, dur);
    }

    private static void Tint(GradientStop inner, GradientStop outer, Color c, byte alpha, TimeSpan dur)
    {
        inner.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(alpha, c.R, c.G, c.B), dur));
        outer.BeginAnimation(GradientStop.ColorProperty, new ColorAnimation(Color.FromArgb(0, c.R, c.G, c.B), dur));
    }

    /// <summary>На паузе обложка отходит назад (как в Apple Music), тонарм поднимается; при игре — всё возвращается с пружинкой.</summary>
    private void SetPaused(bool paused)
    {
        if (paused == _pausedShown) return;
        _pausedShown = paused;
        var anim = new DoubleAnimation(paused ? 0.86 : 1, TimeSpan.FromMilliseconds(paused ? 500 : 650))
        {
            EasingFunction = paused ? new CubicEase { EasingMode = EasingMode.EaseOut } : SpringEase.Lively,
        };
        PauseScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        PauseScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        if (_entered) CoverGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(paused ? 0.35 : 0.85, TimeSpan.FromMilliseconds(500)));
        ArmAngle.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(paused ? -24 : 0, TimeSpan.FromMilliseconds(paused ? 700 : 900))
        {
            EasingFunction = paused ? new CubicEase { EasingMode = EasingMode.EaseInOut } : SpringEase.Soft,
        });
    }

    public void Tick()
    {
        if (_snap == null) return;
        var dur = _snap.Duration;
        var pos = _snap.CurrentPosition;
        bool has = dur > TimeSpan.Zero;
        Wave.Progress = has ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1) : 0;
        string el = has ? MediaVisuals.Format(pos) : "", rem = has ? "-" + MediaVisuals.Format(dur - pos) : "";
        if (ElapsedText.Text != el) ElapsedText.Text = el;
        if (RemainingText.Text != rem) RemainingText.Text = rem;

        if (_demo || _lyricLines.Count == 0) return;
        var p = pos + Lead;
        int idx = -1;
        for (int i = 0; i < _lyricLines.Count; i++)
        {
            if (_lyricLines[i].Time <= p) idx = i;
            else break;
        }
        if (idx != _current) Highlight(idx, animate: _current != -2);
    }

    // ======================= текст песни =======================

    private async System.Threading.Tasks.Task LoadLyricsAsync(MediaSnapshot snap)
    {
        int version = ++_loadVersion;
        ClearLines();
        if (!_settings.LyricsEnabled) { SetLyricsState(LyricsState.Off); return; }
        LyricsStatus.Text = L.T("Ищу текст…");
        LyricsStatus.Visibility = Visibility.Visible;
        SetLyricsState(LyricsState.Searching);
        LyricsResult result;
        try { result = await _lyrics.GetAsync(snap.Title, snap.Artist, snap.Duration); }
        catch { result = LyricsResult.None; }
        if (version != _loadVersion) return;
        ShowLyrics(result);
    }

    /// <summary>Для скриншотов: свой текст вместо поиска в сети, подсвечена строка <paramref name="current"/>.</summary>
    public void ShowDemo(IReadOnlyList<string> text, int current)
    {
        _loadVersion++;
        var lines = new List<LyricLine>();
        for (int i = 0; i < text.Count; i++) lines.Add(new LyricLine(TimeSpan.FromSeconds(i * 3), text[i]));
        ShowLyrics(new LyricsResult(lines, null));
        _demo = true;
        Dispatcher.InvokeAsync(() => { RelayoutLines(); Highlight(current, animate: false); }, DispatcherPriority.Loaded);
    }

    private void ShowLyrics(LyricsResult result)
    {
        ClearLines();
        LyricsStatus.Visibility = Visibility.Collapsed;
        if (result.IsSynced)
        {
            var lines = new List<LyricLine>(result.Lines);
            // Длинное вступление: до первой строки горит «♪», а не весь текст тусклый.
            if (lines[0].Time > TimeSpan.FromSeconds(1.5))
                lines.Insert(0, new LyricLine(TimeSpan.Zero, ""));
            _lyricLines = lines;
            BuildLines();
            SetLyricsState(LyricsState.Synced);
            Tick();
        }
        else if (!string.IsNullOrWhiteSpace(result.Plain))
        {
            PlainText.Text = result.Plain;
            PlainScroll.ScrollToTop();
            PlainScroll.Visibility = Visibility.Visible;
            SetLyricsState(LyricsState.Plain);
        }
        else SetLyricsState(LyricsState.None); // текста нет — обложка с управлением встаёт по центру
    }

    private void SetLyricsState(LyricsState state)
    {
        bool before = HasLyricsColumn;
        _lyricsState = state;
        if (before != HasLyricsColumn && _entered)
        {
            Relayout();
            Rise(LeftCol, 0);
        }
        else Relayout();
        if (state != LyricsState.Synced) SetCaption("");
        UpdateNoLyricsNote();
    }

    /// <summary>
    /// Подсказка внизу: стили (свечение строк, «Кинетика», «Парящий», субтитр…) живут на синхронизированном тексте.
    /// Его нет — эффекты видны не полностью, и об этом лучше сказать, чем оставить человека гадать.
    /// </summary>
    private void UpdateNoLyricsNote()
    {
        string? text = _demo ? null : _lyricsState switch
        {
            LyricsState.None => L.T("Для этой песни не нашлось текста — эффекты стиля видны не в полной мере"),
            LyricsState.Off => L.T("Текст песни выключен в настройках — эффекты стиля видны не в полной мере"),
            LyricsState.Plain => L.T("Текст без привязки ко времени — эффекты стиля видны не в полной мере"),
            _ => null,
        };
        if (text != null) NoLyricsText.Text = text;
        bool show = text != null;
        if (show == (NoLyricsNote.Visibility == Visibility.Visible && NoLyricsNote.Tag is true)) return;
        NoLyricsNote.Tag = show;
        if (show) NoLyricsNote.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 500 : 250))
        {
            BeginTime = show ? TimeSpan.FromMilliseconds(400) : TimeSpan.Zero,
        };
        if (!show) anim.Completed += (_, _) => { if (NoLyricsNote.Tag is false) NoLyricsNote.Visibility = Visibility.Collapsed; };
        NoLyricsNote.BeginAnimation(OpacityProperty, anim);
    }

    private void ClearLines()
    {
        _lyricLines = Array.Empty<LyricLine>();
        _lines.Clear();
        _browsing = false;
        _browse = 0;
        FloatHost.Clear();
        KineticHost.Children.Clear();
        _kineticIdx = _floatIdx = -2;
        LinesCanvas.Children.Clear();
        PlainScroll.Visibility = Visibility.Collapsed;
        _current = -2;
    }

    private void BuildLines()
    {
        foreach (var line in _lyricLines)
        {
            var scale = new ScaleTransform(0.94, 0.94);
            var shift = new TranslateTransform();
            var blur = new BlurEffect { Radius = 4, RenderingBias = RenderingBias.Performance };
            var glow = new DropShadowEffect { Color = Colors.White, BlurRadius = 28, ShadowDepth = 0, Opacity = 0.32, RenderingBias = RenderingBias.Performance };
            var tb = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text,
                FontSize = _lyricFont, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White, Opacity = 0.2, Background = Brushes.Transparent, Cursor = Cursors.Hand,
                RenderTransform = new TransformGroup { Children = { scale, shift } },
                Effect = blur,
            };
            var v = new LineVisual { Text = tb, Shift = shift, Scale = scale, Blur = blur, Glow = glow };
            var time = line.Time;
            // Наведение проясняет строку, нажатие перематывает к ней.
            tb.MouseEnter += (_, _) =>
            {
                tb.BeginAnimation(OpacityProperty, new DoubleAnimation(Math.Max(v.TargetOpacity, 0.8), TimeSpan.FromMilliseconds(140)));
                if (tb.Effect == v.Blur) v.Blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
            };
            tb.MouseLeave += (_, _) =>
            {
                tb.BeginAnimation(OpacityProperty, new DoubleAnimation(v.TargetOpacity, TimeSpan.FromMilliseconds(260)));
                if (tb.Effect == v.Blur) v.Blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(v.TargetBlur, TimeSpan.FromMilliseconds(260)));
            };
            tb.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                if (_demo || _snap == null) return;
                await _media.SeekAsync(time);
            };
            LinesCanvas.Children.Add(tb);
            _lines.Add(v);
        }
        RelayoutLines();
    }

    private void RelayoutLines()
    {
        double width = Math.Max(200, LyricsPane.ActualWidth - 10);
        bool center = CenteredLines;
        double y = 0, gap = _lyricFont * 0.55;
        foreach (var v in _lines)
        {
            v.Text.Width = width;
            v.Text.TextAlignment = center ? TextAlignment.Center : TextAlignment.Left;
            v.Text.RenderTransformOrigin = new Point(center ? 0.5 : 0, 0.5);
            v.Glow.BlurRadius = center ? 42 : 28;
            v.Glow.Opacity = center ? 0.45 : 0.32;
            v.Text.Measure(new Size(width, double.PositiveInfinity));
            v.BaseY = y;
            y += v.Text.DesiredSize.Height + gap;
        }
    }

    private int _kineticIdx = -2, _floatIdx = -2;

    /// <summary>
    /// Текущая строка — яркая, крупная, со свечением; соседние — тусклее и размыты тем сильнее, чем дальше.
    /// «Глубина»: прошлые строки уменьшаются и улетают вдаль, будущие — крупные, ближе к зрителю, не в фокусе.
    /// Ручная прокрутка: все строки читаемые, сдвинуты на прокрученное расстояние.
    /// </summary>
    private void Highlight(int idx, bool animate)
    {
        _current = idx;
        string text = idx >= 0 && idx < _lines.Count ? _lines[idx].Text.Text : "";
        SetCaption(text);
        if (_style == FullscreenStyle.Kinetic && idx != _kineticIdx) { _kineticIdx = idx; ShowKinetic(idx); }
        if (_style == FullscreenStyle.Float && idx != _floatIdx) { _floatIdx = idx; FloatHost.ShowLine(text); }
        if (_lines.Count == 0) return;
        double paneH = LyricsPane.ActualHeight;
        if (paneH <= 0) return; // ещё не разложено — подсветка придёт из Relayout
        bool depth = _style == FullscreenStyle.Depth && !_browsing;
        double anchorY = paneH * (_style == FullscreenStyle.Depth ? 0.5 : _style == FullscreenStyle.Cinematic ? 0.42 : 0.38);
        double focusY = idx >= 0 && idx < _lines.Count ? _lines[idx].BaseY : _lines[0].BaseY - _lyricFont;
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };

        // «Глубина»: где стоит строка — накопленные высоты уменьшенных (прошлых) и увеличенных (будущих) строк.
        var depthY = new double[_lines.Count];
        var depthS = new double[_lines.Count];
        if (depth)
        {
            int c = Math.Clamp(idx, -1, _lines.Count - 1);
            double hc = c >= 0 ? _lines[c].Text.DesiredSize.Height : _lyricFont;
            double upY = anchorY - hc / 2, downY = anchorY + hc / 2;
            for (int i = c - 1; i >= 0; i--)
            {
                double sc = Math.Max(0.32, 1 - 0.17 * (c - i));
                double hh = _lines[i].Text.DesiredSize.Height * sc;
                upY -= hh * 0.92;
                depthY[i] = upY + hh / 2;
                depthS[i] = sc;
            }
            for (int i = Math.Max(0, c + 1); i < _lines.Count; i++)
            {
                double sc = 1 + 0.09 * Math.Min(4, i - c);
                double hh = _lines[i].Text.DesiredSize.Height * sc;
                depthY[i] = downY + hh / 2 + _lyricFont * 0.25;
                downY += hh + _lyricFont * 0.25;
                depthS[i] = sc;
            }
            if (c >= 0) { depthY[c] = anchorY; depthS[c] = 1; }
        }

        for (int i = 0; i < _lines.Count; i++)
        {
            var v = _lines[i];
            int d = idx < 0 ? i + 1 : i - idx;
            int dist = Math.Abs(d);
            double opacity = d == 0 ? 1 : dist == 1 ? 0.42 : dist == 2 ? 0.27 : 0.16;
            if (d < 0) opacity *= 0.75;
            double blur = d == 0 ? 0 : Math.Min(7, 1.4 + (dist - 1) * 1.8);
            double scale = d == 0 ? 1 : 0.94;
            double targetY = v.BaseY - focusY + anchorY;

            if (depth)
            {
                targetY = depthY[i] - v.Text.DesiredSize.Height / 2;
                scale = depthS[i];
                opacity = d == 0 ? 1 : d < 0 ? Math.Max(0, 0.55 - 0.12 * dist) : Math.Max(0, 0.38 - 0.12 * dist);
                blur = d == 0 ? 0 : d < 0 ? Math.Min(6, 0.8 * dist) : Math.Min(10, 3 * dist);
            }
            if (_browsing)
            {
                targetY += _browse;
                opacity = d == 0 ? 1 : 0.6;
                blur = 0;
            }
            v.TargetOpacity = opacity;
            v.TargetBlur = blur;

            var begin = TimeSpan.FromMilliseconds(animate && d > 0 && !_browsing ? Math.Min(6, d) * 35 : 0);
            var dur = TimeSpan.FromMilliseconds(!animate ? 0 : _browsing ? 260 : 700);
            IEasingFunction moveEase = !animate || _browsing ? ease : SpringEase.Soft;
            v.Shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(targetY, dur) { BeginTime = begin, EasingFunction = moveEase });
            v.Text.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, dur) { BeginTime = begin, EasingFunction = ease });
            var s = new DoubleAnimation(scale, dur) { BeginTime = begin, EasingFunction = animate && !_browsing ? SpringEase.Lively : ease };
            v.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
            v.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, s);

            if (d == 0 && !_browsing) v.Text.Effect = v.Glow;
            else if (dist <= EffectRange + (depth ? 2 : 0))
            {
                v.Text.Effect = v.Blur;
                v.Blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(blur, dur) { BeginTime = begin, EasingFunction = ease });
            }
            else v.Text.Effect = null;
        }
    }

    // ======================= ручная прокрутка =======================

    private bool _browsing;
    private double _browse;
    private readonly DispatcherTimer _browseEnd = new() { Interval = TimeSpan.FromSeconds(3) };

    /// <summary>Колесо листает текст: строки проясняются, любую можно нажать. Через 3 с текст сам вернётся к звучащей строке.</summary>
    private void LyricsPane_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_lines.Count == 0) return;
        double focusY = _current >= 0 && _current < _lines.Count ? _lines[_current].BaseY : _lines[0].BaseY;
        double max = focusY - _lines[0].BaseY, min = -(_lines[^1].BaseY - focusY);
        _browse = Math.Clamp(_browse + e.Delta * 0.7, min, max);
        _browsing = true;
        _browseEnd.Stop();
        _browseEnd.Start();
        ShowChrome();
        Highlight(_current, animate: true);
    }

    private void EndBrowse()
    {
        _browseEnd.Stop();
        if (!_browsing) return;
        _browsing = false;
        _browse = 0;
        Highlight(_current, animate: true);
    }

    // ======================= «Кинетика» =======================

    /// <summary>Слова строки: по пробелам, а для языков без пробелов (японский, китайский) — по знакам.</summary>
    private static List<string> Words(string text)
    {
        var parts = new List<string>();
        if (text.Contains(' '))
        {
            parts.AddRange(text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return parts;
        }
        var en = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (en.MoveNext()) parts.Add(en.GetTextElement());
        return parts;
    }

    /// <summary>
    /// Строка собирается по словам: каждое всплывает снизу из размытия, в темпе строки (за ~60 % её длительности).
    /// Прошлая строка улетает вверх и тает; под текущей — следующая, тускло.
    /// </summary>
    private void ShowKinetic(int idx)
    {
        foreach (var old in KineticHost.Children.OfType<FrameworkElement>().ToList())
        {
            var t = old.RenderTransform as TranslateTransform ?? new TranslateTransform();
            old.RenderTransform = t;
            var gone = new DoubleAnimation(0, TimeSpan.FromMilliseconds(380));
            gone.Completed += (_, _) => KineticHost.Children.Remove(old);
            old.BeginAnimation(OpacityProperty, gone);
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(t.Y - 70, TimeSpan.FromMilliseconds(420)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
        }
        if (idx < 0 || idx >= _lyricLines.Count) return;
        string text = _lyricLines[idx].Text.Trim();
        if (text.Length == 0) text = "♪";

        double size = Math.Clamp(ActualHeight * 0.078, 30, 96);
        var words = Words(text);
        if (words.Count > 9) size *= 0.82;
        var wrap = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        double lineSec = idx + 1 < _lyricLines.Count ? (_lyricLines[idx + 1].Time - _lyricLines[idx].Time).TotalSeconds : 3;
        double stepMs = Math.Clamp(lineSec * 600 / Math.Max(1, words.Count), 35, 260);
        bool spaced = text.Contains(' ');
        for (int i = 0; i < words.Count; i++)
        {
            var tb = new TextBlock
            {
                Text = words[i], FontSize = size, FontWeight = FontWeights.Black, Foreground = Brushes.White,
                Margin = new Thickness(0, 0, spaced ? size * 0.28 : 0, 0), Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 1),
            };
            var tt = new TranslateTransform(0, size * 0.55);
            var sc = new ScaleTransform(0.8, 0.8);
            tb.RenderTransform = new TransformGroup { Children = { sc, tt } };
            var blur = new BlurEffect { Radius = 14, RenderingBias = RenderingBias.Performance };
            tb.Effect = blur;
            var begin = TimeSpan.FromMilliseconds(i * stepMs);
            tb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = begin });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(size * 0.55, 0, TimeSpan.FromMilliseconds(620)) { BeginTime = begin, EasingFunction = SpringEase.Lively });
            var grow = new DoubleAnimation(0.8, 1, TimeSpan.FromMilliseconds(620)) { BeginTime = begin, EasingFunction = SpringEase.Bouncy };
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            var unblur = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = begin };
            unblur.Completed += (_, _) => tb.Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 34, ShadowDepth = 0, Opacity = 0.3, RenderingBias = RenderingBias.Performance };
            blur.BeginAnimation(BlurEffect.RadiusProperty, unblur);
            wrap.Children.Add(tb);
        }

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, RenderTransform = new TranslateTransform() };
        stack.Children.Add(wrap);
        string next = idx + 1 < _lyricLines.Count ? _lyricLines[idx + 1].Text.Trim() : "";
        if (next.Length > 0)
        {
            var nextTb = new TextBlock
            {
                Text = next, FontSize = size * 0.42, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, Opacity = 0,
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, size * 0.5, 0, 0),
            };
            nextTb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.32, TimeSpan.FromMilliseconds(600)) { BeginTime = TimeSpan.FromMilliseconds(300) });
            stack.Children.Add(nextTb);
        }
        KineticHost.Children.Add(stack);
    }

    private string _caption = "";

    /// <summary>Субтитр «В центре»: новая строка всплывает снизу, старая уходит вверх и гаснет.</summary>
    private void SetCaption(string text)
    {
        if (text == "♪") text = "";
        if (text == _caption) return;
        _caption = text;
        var (incoming, outgoing) = _captionA ? (CaptionB, CaptionA) : (CaptionA, CaptionB);
        _captionA = !_captionA;
        incoming.Text = text;
        var dur = TimeSpan.FromMilliseconds(520);
        incoming.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(360)));
        ((TranslateTransform)incoming.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(18, 0, dur) { EasingFunction = SpringEase.Lively });
        outgoing.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)));
        ((TranslateTransform)outgoing.RenderTransform).BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-14, TimeSpan.FromMilliseconds(320)));
    }

    // ======================= цвета =======================

    private static Color Brighten(Color c)
    {
        var (h, s, v) = ToHsv(c);
        return FromHsv(h, Math.Min(1, s * 0.9 + 0.1), Math.Max(0.75, v));
    }

    /// <summary>Цвет пятна фона: тон обложки, сдвинутый по кругу, насыщенный и светлый.</summary>
    private static Color Hue(Color c, double shift)
    {
        var (h, s, _) = ToHsv(c);
        return FromHsv((h + shift + 360) % 360, Math.Clamp(s * 1.1, 0.45, 0.9), 0.9);
    }

    private static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

#if DEBUG
    /// <summary>Для скриншотов: подсветить строку (как будто песня дошла до неё).</summary>
    public void DebugHighlight(int idx) => Highlight(idx, animate: true);

    /// <summary>Для скриншотов: прокрутить текст колесом.</summary>
    public void DebugBrowse(double delta)
    {
        _browsing = true;
        _browse = delta;
        Highlight(_current, animate: false);
    }

    /// <summary>Для скриншотов: сразу включить стиль без анимации перехода.</summary>
    public void DebugSetStyle(FullscreenStyle style)
    {
        _style = style;
        ApplyStyleVisuals(false);
        _aurora = AuroraTarget; _beam = BeamTarget; _particles = ParticlesTarget;
        Relayout();
        if (style == FullscreenStyle.Cinematic) BarTop.BeginAnimation(HeightProperty, null);
    }

    /// <summary>Для скриншотов: песня без текста (видна подсказка внизу), управление показано.</summary>
    public void DebugNoLyrics()
    {
        _loadVersion++;
        _demo = false;
        ShowLyrics(LyricsResult.None);
        ShowChrome();
        _idle.Stop();
    }
#endif
}
