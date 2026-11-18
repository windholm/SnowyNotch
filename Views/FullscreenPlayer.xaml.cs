using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SnowyNotch.Animation;
using SnowyNotch.Localization;
using SnowyNotch.Services;
using SnowyNotch.Widgets;

namespace SnowyNotch.Views;

/// <summary>
/// Плеер во весь экран: крупная обложка (подлетает оттуда, где её отпустили), размытый «живой» фон из обложки
/// с цветными пятнами в её тон, синхронный текст песни со свечением текущей строки, управление и прогресс.
/// Выход — Esc или стрелка вверху слева. Пробел — пауза, ← / → — перемотка на 5 секунд.
/// </summary>
public partial class FullscreenPlayer : Window, IMediaView
{
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(120);
    private const double LyricAnchor = 0.38;
    private const int EffectRange = 5;

    private readonly MediaService _media;
    private readonly LyricsService _lyrics;
    private readonly AppSettings _settings;
    private readonly Point _screenPx;
    private readonly Rect? _fromPx;

    private MediaSnapshot? _snap;
    private string _trackKey = "";
    private BitmapSourceKey _artKey;
    private int _loadVersion;
    private bool _demo;
    private bool _closing;
    private bool _entered;
    private bool _backB;

    private readonly List<LineVisual> _lines = new();
    private IReadOnlyList<LyricLine> _lyricLines = Array.Empty<LyricLine>();
    private int _current = -2;
    private double _lyricFont = 40;

    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(2.6) };
    private Point _lastMouse;

    private sealed class LineVisual
    {
        public required TextBlock Text;
        public required TranslateTransform Shift;
        public required ScaleTransform Scale;
        public required BlurEffect Blur;
        public required DropShadowEffect Glow;
        public double BaseY;
    }

    private readonly record struct BitmapSourceKey(object? Art, string Title);

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

        BackArtB.RenderTransform = BackArtA.RenderTransform; // оба слоя фона дрейфуют вместе
        SourceInitialized += (_, _) => CoverMonitor();
        SizeChanged += (_, _) => Relayout();
        ContentRendered += (_, _) => PlayEnter();
        PreviewKeyDown += OnKey;
        MouseMove += OnMouseMove;
        _idle.Tick += (_, _) => { _idle.Stop(); HideChrome(); };
        Closed += (_, _) => { CompositionTarget.Rendering -= OnFrame; _idle.Stop(); };
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
        const uint SWP_SHOWWINDOW = 0x0040;
        SetWindowPos(hwnd, new IntPtr(-1), b.X, b.Y, b.Width, b.Height, SWP_SHOWWINDOW);
    }

    /// <summary>Размеры от экрана: обложка ~44 % высоты, шрифты — от неё.</summary>
    private void Relayout()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double s = Math.Round(Math.Min(h * 0.44, w * 0.27));
        CoverHost.Width = CoverHost.Height = s;
        LeftCol.Width = s;
        TitleText.FontSize = Math.Clamp(s * 0.072, 22, 40);
        ArtistText.FontSize = Math.Clamp(s * 0.046, 15, 24);
        _lyricFont = Math.Clamp(h * 0.04, 24, 48);
        PlainText.FontSize = _lyricFont * 0.66;
        PlainText.LineHeight = _lyricFont;
        LyricsPane.Margin = new Thickness(0, 0, w * 0.06, 0);

        // Цветные пятна — по углам и центру экрана.
        Canvas.SetLeft(BlobA, -w * 0.12); Canvas.SetTop(BlobA, -h * 0.2);
        Canvas.SetLeft(BlobB, w * 0.55); Canvas.SetTop(BlobB, h * 0.35);
        Canvas.SetLeft(BlobC, w * 0.3); Canvas.SetTop(BlobC, -h * 0.1);

        foreach (var v in _lines) v.Text.FontSize = _lyricFont;
        RelayoutLines();
        if (_current >= -1) Highlight(_current, animate: false);
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

        // Обложка подлетает с места, где её отпустили, и пружинисто встаёт в центр своей колонки.
        CoverHost.Opacity = 1;
        var final = CoverHost.TransformToAncestor(Root).TransformBounds(new Rect(0, 0, CoverHost.ActualWidth, CoverHost.ActualHeight));
        double fromScale = 0.6;
        Vector fromShift = new(0, 40);
        if (_fromPx is Rect r && final.Width > 0)
        {
            var a = Root.PointFromScreen(r.TopLeft);
            var b = Root.PointFromScreen(r.BottomRight);
            var from = new Rect(a, b);
            fromScale = Math.Clamp(from.Width / final.Width, 0.05, 1);
            fromShift = new Vector(from.X + from.Width / 2 - (final.X + final.Width / 2), from.Y + from.Height / 2 - (final.Y + final.Height / 2));
        }
        var fly = TimeSpan.FromMilliseconds(820);
        var grow = new DoubleAnimation(fromScale, 1, fly) { EasingFunction = SpringEase.Lively };
        FlyScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        FlyScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        FlyShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromShift.X, 0, fly) { EasingFunction = SpringEase.Lively });
        FlyShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromShift.Y, 0, fly) { EasingFunction = SpringEase.Lively });
        CoverGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.85, TimeSpan.FromMilliseconds(1200)) { BeginTime = TimeSpan.FromMilliseconds(300) });

        // Остальное поднимается каскадом.
        int i = 0;
        foreach (FrameworkElement el in new FrameworkElement[] { TitleRow, ProgressRow, ControlsRow })
            Rise(el, 240 + i++ * 70);
        LyricsPane.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(700)) { BeginTime = TimeSpan.FromMilliseconds(360) });
        LyricsShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(70, 0, TimeSpan.FromMilliseconds(900)) { BeginTime = TimeSpan.FromMilliseconds(360), EasingFunction = SpringEase.Soft });
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
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease };
        fade.Completed += (_, _) => Close();
        Backdrop.BeginAnimation(OpacityProperty, fade);
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
                await _media.TogglePlayPauseAsync();
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
        TopBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
        _idle.Stop();
        _idle.Start();
    }

    /// <summary>Мышь не двигается — прячем кнопку выхода и курсор, остаётся только музыка.</summary>
    private void HideChrome()
    {
        if (TopBar.IsMouseOver) { _idle.Start(); return; }
        Root.Cursor = Cursors.None;
        TopBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(600)));
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseAnimated();
    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _media.TogglePlayPauseAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void Prev_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

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
        Bars.Visibility = _settings.ShowVisualizer ? Visibility.Visible : Visibility.Collapsed;
        Wave.IsWavy = _settings.WavyProgress;
    }

    public void Update(MediaSnapshot? snap)
    {
        _snap = snap;
        if (snap == null)
        {
            Bars.IsPlaying = false;
            Wave.IsPlaying = false;
            SetPaused(true);
            return;
        }

        TitleText.Text = snap.Title;
        ArtistText.Text = snap.Artist;
        Bars.IsPlaying = snap.IsPlaying;
        Bars.BarColor = Brighten(snap.Accent);
        Wave.IsPlaying = snap.IsPlaying;
        PlayGlyph.Visibility = snap.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseGlyph.Visibility = snap.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        SetPaused(!snap.IsPlaying);

        var key = new BitmapSourceKey(snap.Artwork, snap.Title);
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
        }
        else Cover.Background = brush;

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

    private bool _pausedShown;

    /// <summary>На паузе обложка отходит назад, как в Apple Music; при игре — возвращается с пружинкой.</summary>
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
        CoverGlow.BeginAnimation(OpacityProperty, new DoubleAnimation(paused ? 0.35 : 0.85, TimeSpan.FromMilliseconds(500)));
    }

    private void OnFrame(object? sender, EventArgs e) => Tick();

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
        if (!_settings.LyricsEnabled) { ShowLyricsColumn(false); return; }
        ShowStatus(L.T("Ищу текст…"));
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
        if (result.IsSynced)
        {
            var lines = new List<LyricLine>(result.Lines);
            if (lines[0].Time > TimeSpan.FromSeconds(2.5) && !string.IsNullOrWhiteSpace(lines[0].Text))
                lines.Insert(0, new LyricLine(TimeSpan.Zero, ""));
            _lyricLines = lines;
            BuildLines();
            LyricsStatus.Visibility = Visibility.Collapsed;
            ShowLyricsColumn(true);
            Tick();
        }
        else if (!string.IsNullOrWhiteSpace(result.Plain))
        {
            PlainText.Text = result.Plain;
            PlainScroll.ScrollToTop();
            PlainScroll.Visibility = Visibility.Visible;
            LyricsStatus.Visibility = Visibility.Collapsed;
            ShowLyricsColumn(true);
        }
        else
        {
            // Текста нет — колонка уходит, обложка с управлением встаёт по центру экрана.
            ShowLyricsColumn(false);
        }
    }

    private bool _lyricsColumn = true;

    private void ShowLyricsColumn(bool show)
    {
        if (show == _lyricsColumn) return;
        _lyricsColumn = show;
        RightColDef.Width = show ? new GridLength(1.15, GridUnitType.Star) : new GridLength(0);
        LyricsPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_entered) Rise(LeftCol, 0);
    }

    private void ShowStatus(string text)
    {
        ClearLines();
        LyricsStatus.Text = text;
        LyricsStatus.Visibility = Visibility.Visible;
        ShowLyricsColumn(true);
    }

    private void ClearLines()
    {
        _lyricLines = Array.Empty<LyricLine>();
        _lines.Clear();
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
                Foreground = Brushes.White, Opacity = 0.2,
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = new TransformGroup { Children = { scale, shift } },
                Effect = blur,
            };
            LinesCanvas.Children.Add(tb);
            _lines.Add(new LineVisual { Text = tb, Shift = shift, Scale = scale, Blur = blur, Glow = glow });
        }
        RelayoutLines();
    }

    private void RelayoutLines()
    {
        double width = Math.Max(200, LyricsPane.ActualWidth - 10);
        double y = 0, gap = _lyricFont * 0.55;
        foreach (var v in _lines)
        {
            v.Text.MaxWidth = width;
            v.Text.Measure(new Size(width, double.PositiveInfinity));
            v.BaseY = y;
            y += v.Text.DesiredSize.Height + gap;
        }
    }

    /// <summary>Текущая строка — яркая, крупная, со свечением; соседние — тусклее и размыты тем сильнее, чем дальше.</summary>
    private void Highlight(int idx, bool animate)
    {
        _current = idx;
        if (_lines.Count == 0) return;
        double anchorY = LyricsPane.ActualHeight * LyricAnchor;
        double focusY = idx >= 0 && idx < _lines.Count ? _lines[idx].BaseY : _lines[0].BaseY - _lyricFont;
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };

        for (int i = 0; i < _lines.Count; i++)
        {
            var v = _lines[i];
            int d = idx < 0 ? i + 1 : i - idx;
            int dist = Math.Abs(d);
            double opacity = d == 0 ? 1 : dist == 1 ? 0.4 : dist == 2 ? 0.26 : 0.15;
            if (d < 0) opacity *= 0.75;
            double blur = d == 0 ? 0 : Math.Min(7, 1.4 + (dist - 1) * 1.8);
            double scale = d == 0 ? 1 : 0.94;
            double targetY = v.BaseY - focusY + anchorY;

            var begin = TimeSpan.FromMilliseconds(animate && d > 0 ? Math.Min(6, d) * 35 : 0);
            var dur = TimeSpan.FromMilliseconds(animate ? 700 : 0);
            v.Shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(targetY, dur) { BeginTime = begin, EasingFunction = animate ? SpringEase.Soft : ease });
            v.Text.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, dur) { BeginTime = begin, EasingFunction = ease });
            var s = new DoubleAnimation(scale, dur) { BeginTime = begin, EasingFunction = animate ? SpringEase.Lively : ease };
            v.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
            v.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, s);

            if (d == 0) v.Text.Effect = v.Glow;
            else if (dist <= EffectRange)
            {
                v.Text.Effect = v.Blur;
                v.Blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(blur, dur) { BeginTime = begin, EasingFunction = ease });
            }
            else v.Text.Effect = null;
        }
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
}
