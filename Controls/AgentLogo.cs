using System;
using System.Windows;
using System.Windows.Media;
using SnowyNotch.Services;

namespace SnowyNotch.Controls;

/// <summary>
/// Плитка агента для колец лимитов: тёмный скруглённый квадрат с отметкой агента.
/// Claude — оранжевая «звёздочка» из лучей, Codex — приглашение терминала «>_».
/// Пока агент работает (<see cref="Busy"/>), лучи мягко пульсируют по кругу, а курсор Codex мигает и «печатает».
/// </summary>
public sealed class AgentLogo : FrameworkElement
{
    public static readonly DependencyProperty AgentProperty = DependencyProperty.Register(
        nameof(Agent), typeof(AgentKind), typeof(AgentLogo),
        new FrameworkPropertyMetadata(AgentKind.Claude, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BusyProperty = DependencyProperty.Register(
        nameof(Busy), typeof(bool), typeof(AgentLogo),
        new FrameworkPropertyMetadata(false, (d, _) => ((AgentLogo)d).Sync()));

    public AgentKind Agent { get => (AgentKind)GetValue(AgentProperty); set => SetValue(AgentProperty, value); }
    public bool Busy { get => (bool)GetValue(BusyProperty); set => SetValue(BusyProperty, value); }

    private static readonly Brush Tile = Frozen(new LinearGradientBrush(Color.FromRgb(0x30, 0x30, 0x33), Color.FromRgb(0x1C, 0x1C, 0x1E), 90));
    private static readonly Brush TileEdge = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ClaudeOrange = Frozen(new SolidColorBrush(Color.FromRgb(0xE3, 0x7A, 0x52)));

    // Длины лучей «звёздочки» (доля радиуса) — неровные, как нарисованные от руки.
    private static readonly double[] Rays = { 1.0, 0.78, 0.92, 0.7, 0.96, 0.82, 0.88, 0.74, 1.0, 0.8, 0.9, 0.72 };

    private bool _running;
    private TimeSpan _last;
    private double _t;

    static AgentLogo()
    {
        WidthProperty.OverrideMetadata(typeof(AgentLogo), new FrameworkPropertyMetadata(40.0));
        HeightProperty.OverrideMetadata(typeof(AgentLogo), new FrameworkPropertyMetadata(40.0));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(AgentLogo), new UIPropertyMetadata(false));
    }

    public AgentLogo()
    {
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => Sync();
    }

    private void Sync()
    {
        bool want = IsLoaded && IsVisible && (Busy || Agent == AgentKind.Codex); // курсор Codex мигает всегда
        if (want == _running) { InvalidateVisual(); return; }
        if (want)
        {
            _running = true;
            _last = TimeSpan.Zero;
            CompositionTarget.Rendering += OnFrame;
        }
        else Stop();
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
        InvalidateVisual();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.1, (now - _last).TotalSeconds);
        if (dt <= 0) return;
        _last = now;
        _t += dt;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;
        var box = new Rect((ActualWidth - side) / 2, (ActualHeight - side) / 2, side, side);
        dc.DrawRoundedRectangle(Tile, new Pen(TileEdge, 1), box, side * 0.24, side * 0.24);
        var c = new Point(box.X + side / 2, box.Y + side / 2);

        if (Agent == AgentKind.Claude) DrawSpark(dc, c, side * 0.3);
        else DrawPrompt(dc, box);
    }

    private void DrawSpark(DrawingContext dc, Point c, double r)
    {
        double thick = Math.Max(1.4, r * 0.2);
        for (int i = 0; i < Rays.Length; i++)
        {
            double a = i * Math.PI * 2 / Rays.Length + 0.1;
            // Пока работает — по лучам бежит волна.
            double k = Busy ? 0.82 + 0.18 * Math.Sin(_t * 6 - i * Math.PI * 2 / Rays.Length) : 1;
            double len = r * Rays[i] * k;
            var pen = new Pen(ClaudeOrange, thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Triangle };
            dc.DrawLine(pen, new Point(c.X + Math.Cos(a) * r * 0.12, c.Y + Math.Sin(a) * r * 0.12),
                new Point(c.X + Math.Cos(a) * len, c.Y + Math.Sin(a) * len));
        }
    }

    private void DrawPrompt(DrawingContext dc, Rect box)
    {
        double s = box.Width;
        var pen = new Pen(Brushes.White, Math.Max(1.6, s * 0.075)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        double x = box.X + s * 0.27, y = box.Y + s * 0.5;
        // «>»
        var chevron = new StreamGeometry();
        using (var ctx = chevron.Open())
        {
            ctx.BeginFigure(new Point(x, y - s * 0.14), false, false);
            ctx.LineTo(new Point(x + s * 0.15, y), true, true);
            ctx.LineTo(new Point(x, y + s * 0.14), true, true);
        }
        chevron.Freeze();
        dc.DrawGeometry(null, pen, chevron);

        // «_»: в работе «печатает» (растёт и прыгает вперёд), в покое мигает.
        double cx = box.X + s * 0.5, cy = y + s * 0.14;
        double w = s * 0.2;
        double opacity = 1;
        if (Busy)
        {
            double p = _t * 2.2 % 1;
            cx += p * s * 0.06;
            w *= 0.6 + 0.4 * p;
        }
        else opacity = _t % 1.1 < 0.6 ? 1 : 0.15;
        dc.PushOpacity(opacity);
        dc.DrawLine(pen, new Point(cx, cy), new Point(cx + w, cy));
        dc.Pop();
    }

    private static Brush Frozen(Brush b)
    {
        b.Freeze();
        return b;
    }
}
