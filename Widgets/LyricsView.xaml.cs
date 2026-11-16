using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SnowyNotch.Services;
using SnowyNotch.Localization;

namespace SnowyNotch.Widgets;

public partial class LyricsView : UserControl, IMediaView, IIslandWidget
{
    // Строка подсвечивается чуть раньше таймкода: глаз должен успеть перескочить к ней к началу фразы.
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(120);
    private const double LineGap = 14;
    private const double Anchor = 0.30;
    private const int EffectRange = 5;

    private readonly LyricsService _service;
    private readonly AppSettings _settings;
    private bool _active;
    private readonly DispatcherTimer _notFound = new() { Interval = TimeSpan.FromMilliseconds(1400) };

    private MediaSnapshot? _snap;
    private string _trackKey = "";
    private IReadOnlyList<LyricLine> _lines = Array.Empty<LyricLine>();
    private readonly List<LineVisual> _visuals = new();
    private int _current = -2;
    private int _loadVersion;
    private bool _demo;

    private sealed class LineVisual
    {
        public required TextBlock Text;
        public required TranslateTransform Shift;
        public required ScaleTransform Scale;
        public required BlurEffect Blur;
        public double BaseY;
    }

    public LyricsView(LyricsService service, AppSettings settings)
    {
        InitializeComponent();
        _service = service;
        _settings = settings;
        _notFound.Tick += (_, _) => { _notFound.Stop(); NotFound?.Invoke(); };
        // Перекладываем строки только при смене ширины (меняются переносы).
        Viewport.SizeChanged += (_, e) =>
        {
            if (!e.WidthChanged || Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5) return;
            Relayout();
            if (_current >= -1) Highlight(_current, animate: false);
        };
    }

    public event Action? BackRequested;

    public event Action? NotFound;

    public void SetActive(bool active)
    {
        if (active == _active) { if (active) Tick(); return; }
        _active = active;
        // Каждый кадр, а не DispatcherTimer: у таймера фоновый приоритет, и пока остров анимируется,
        // его тики опаздывают на сотни миллисекунд — строки отставали от музыки.
        if (active) { CompositionTarget.Rendering += OnFrame; Tick(); }
        else { CompositionTarget.Rendering -= OnFrame; _notFound.Stop(); }
    }

    private void OnFrame(object? sender, EventArgs e) => Tick();

    public void ApplySettings() { }

    public void Update(MediaSnapshot? snap)
    {
        _snap = snap;
        if (snap == null)
        {
            ShowStatus(L.T("Ничего не играет"));
            _trackKey = "";
            return;
        }
        TitleText.Text = snap.Title;
        ArtistText.Text = snap.Artist;
        Art.Background = MediaVisuals.ArtBrush(snap);

        string key = $"{snap.Artist}\u0001{snap.Title}";
        if (key != _trackKey)
        {
            _trackKey = key;
            _demo = false;
            _ = LoadAsync(snap);
        }
    }

    private async System.Threading.Tasks.Task LoadAsync(MediaSnapshot snap)
    {
        int version = ++_loadVersion;
        _notFound.Stop();
        if (!_settings.LyricsEnabled)
        {
            ShowStatus(L.T("Поиск текстов выключен"));
            _notFound.Start();
            return;
        }
        ShowStatus(L.T("Ищу текст…"));
        var result = await _service.GetAsync(snap.Title, snap.Artist, snap.Duration);
        if (version != _loadVersion) return;
        Show(result);
    }

    public void ShowDemo(MediaSnapshot? snap)
    {
        _loadVersion++;
        _snap = snap;
        var lines = new List<LyricLine>();
        string[] demo = { L.T("Первая строка для проверки"), L.T("Вторая строка чуть длиннее первой, чтобы перенестись"),
                          L.T("Третья — сейчас звучит"), L.T("Четвёртая строка"), L.T("Пятая строка"), L.T("Шестая строка") };
        for (int i = 0; i < demo.Length; i++) lines.Add(new LyricLine(TimeSpan.FromSeconds(i * 3), demo[i]));
        Show(new LyricsResult(lines, null));
        _demo = true;
        Dispatcher.InvokeAsync(() =>
        {
            Relayout();
            Highlight(2, animate: false);
        }, DispatcherPriority.Loaded);
    }

    private void Show(LyricsResult result)
    {
        if (result.IsSynced)
        {
            var lines = new List<LyricLine>(result.Lines);
            // Длинное вступление: до первой строки горит «♪», а не весь текст тусклый, будто всё сломалось.
            if (lines[0].Time > TimeSpan.FromSeconds(2.5) && !string.IsNullOrWhiteSpace(lines[0].Text))
                lines.Insert(0, new LyricLine(TimeSpan.Zero, ""));
            _lines = lines;
            BuildLines();
            Status.Visibility = Visibility.Collapsed;
            PlainScroll.Visibility = Visibility.Collapsed;
            LinesCanvas.Visibility = Visibility.Visible;
            _current = -2;
            Tick();
        }
        else if (!string.IsNullOrWhiteSpace(result.Plain))
        {
            ClearLines();
            PlainText.Text = result.Plain;
            PlainScroll.ScrollToTop();
            PlainScroll.Visibility = Visibility.Visible;
            Status.Visibility = Visibility.Collapsed;
        }
        else
        {
            ShowStatus(L.T("Текст не найден"));
            _notFound.Start();
        }
    }

    private void ShowStatus(string text)
    {
        ClearLines();
        StatusText.Text = text;
        Status.Visibility = Visibility.Visible;
        Status.Opacity = 0;
        Status.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
    }

    private void ClearLines()
    {
        _lines = Array.Empty<LyricLine>();
        _visuals.Clear();
        LinesCanvas.Children.Clear();
        PlainScroll.Visibility = Visibility.Collapsed;
        _current = -2;
    }

    private void BuildLines()
    {
        _visuals.Clear();
        LinesCanvas.Children.Clear();
        foreach (var line in _lines)
        {
            var scale = new ScaleTransform(0.96, 0.96);
            var shift = new TranslateTransform();
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(shift);
            var blur = new BlurEffect { Radius = 3, RenderingBias = RenderingBias.Performance };

            var tb = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text,
                FontSize = 23,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                Opacity = 0.2,
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = group,
                Effect = blur,
            };
            LinesCanvas.Children.Add(tb);
            _visuals.Add(new LineVisual { Text = tb, Shift = shift, Scale = scale, Blur = blur });
        }
        Relayout();
    }

    private void Relayout()
    {
        double width = Math.Max(100, Viewport.ActualWidth - 8);
        double y = 0;
        foreach (var v in _visuals)
        {
            v.Text.MaxWidth = width;
            v.Text.Measure(new Size(width, double.PositiveInfinity));
            v.BaseY = y;
            y += v.Text.DesiredSize.Height + LineGap;
        }
    }

    public void Tick()
    {
        if (_demo || _snap == null || _lines.Count == 0) return;
        var pos = _snap.CurrentPosition + Lead;
        int idx = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].Time <= pos) idx = i;
            else break;
        }
        if (idx != _current) Highlight(idx, animate: _current != -2);
    }

    /// <summary>
    /// Звучащая строка целиком белая, как в Apple Music для построчных текстов. Заливки по словам нет
    /// намеренно: в LRC известно только начало строки, и «ползущая» заливка шла мимо того, что поётся.
    /// </summary>
    private void Highlight(int idx, bool animate)
    {
        _current = idx;
        double anchorY = Viewport.ActualHeight * Anchor;
        double focusY = idx >= 0 && idx < _visuals.Count ? _visuals[idx].BaseY : (_visuals.Count > 0 ? _visuals[0].BaseY - 40 : 0);
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };

        for (int i = 0; i < _visuals.Count; i++)
        {
            var v = _visuals[i];
            int d = idx < 0 ? i + 1 : i - idx;
            int dist = Math.Abs(d);

            double opacity = d == 0 ? 1 : dist == 1 ? 0.42 : dist == 2 ? 0.28 : 0.17;
            if (d < 0) opacity *= 0.8;
            double blur = d == 0 ? 0 : Math.Min(5, 1.1 + (dist - 1) * 1.4);
            double scale = d == 0 ? 1 : 0.96;
            double targetY = v.BaseY - focusY + anchorY;

            var begin = TimeSpan.FromMilliseconds(animate && d > 0 ? Math.Min(6, d) * 30 : 0);
            var dur = TimeSpan.FromMilliseconds(animate ? 480 : 0);

            v.Shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(targetY, dur) { BeginTime = begin, EasingFunction = ease });
            v.Text.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, dur) { BeginTime = begin, EasingFunction = ease });
            var s = new DoubleAnimation(scale, dur) { BeginTime = begin, EasingFunction = ease };
            v.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, s);
            v.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, s);

            if (dist <= EffectRange)
            {
                if (v.Text.Effect == null) v.Text.Effect = v.Blur;
                v.Blur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(blur, dur) { BeginTime = begin, EasingFunction = ease });
            }
            else
            {
                v.Text.Effect = null;
            }
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
}
