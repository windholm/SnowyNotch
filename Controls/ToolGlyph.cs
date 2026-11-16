using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SnowyNotch.Controls;

public enum ToolGlyphKind { Timer, Coffee, Stopwatch }

/// <summary>
/// Живые иконки для пузырей инструментов (в стиле SF Symbols):
/// Timer — круг с прорезью и стрелкой, которая «тикает» раз в секунду, пока таймер идёт;
/// Coffee — чашка на блюдце, над ней поднимается пар (перерыв Pomodoro);
/// Stopwatch — секундомер с кнопкой сверху, стрелка плавно идёт по секундам.
/// При старте/паузе кнопка «нажимается», при смене вида иконка мягко выпрыгивает.
/// Рисуется в сетке 24×24, как <see cref="IconGlyph"/>.
/// </summary>
public sealed class ToolGlyph : FrameworkElement
{
    private const double Grid = 24;

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(ToolGlyphKind), typeof(ToolGlyph),
        new FrameworkPropertyMetadata(ToolGlyphKind.Timer, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, e) => ((ToolGlyph)d).OnKindChanged()));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(ToolGlyph),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ToolGlyph),
        new FrameworkPropertyMetadata(0.0, (d, _) => ((ToolGlyph)d).Kick()));

    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(
        nameof(IsRunning), typeof(bool), typeof(ToolGlyph),
        new FrameworkPropertyMetadata(false, (d, _) => ((ToolGlyph)d).OnRunningChanged()));

    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth), typeof(double), typeof(ToolGlyph),
        new FrameworkPropertyMetadata(1.8, FrameworkPropertyMetadataOptions.AffectsRender));

    public ToolGlyphKind Kind { get => (ToolGlyphKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>Для секундомера — доля текущей минуты (0..1): по ней идёт стрелка.</summary>
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    private TimeSpan _last;
    private bool _looping;
    private double _t;

    private double _hand = -40, _handVel, _tickTarget = -40, _nextTick;
    private double _press;      // 1 → 0: кнопка нажата и отпускается
    private double _pop = 1, _popVel;
    private double _steam;      // 0..1: насколько виден пар

    static ToolGlyph()
    {
        WidthProperty.OverrideMetadata(typeof(ToolGlyph), new FrameworkPropertyMetadata(18.0));
        HeightProperty.OverrideMetadata(typeof(ToolGlyph), new FrameworkPropertyMetadata(18.0));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(ToolGlyph), new UIPropertyMetadata(false));
    }

    public ToolGlyph()
    {
        Loaded += (_, _) => Kick();
        Unloaded += (_, _) => StopLoop();
        IsVisibleChanged += (_, _) => { if (IsVisible) { _pop = 0.55; _popVel = 0; Kick(); } else StopLoop(); };
    }

    private void OnKindChanged()
    {
        _pop = 0.55;
        _popVel = 0;
        _steam = 0;
        Kick();
    }

    private void OnRunningChanged()
    {
        _press = 1;
        _nextTick = _t + 1;
        Kick();
    }

    private void Kick()
    {
        if (_looping || !IsLoaded || !IsVisible) { InvalidateVisual(); return; }
        _looping = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopLoop()
    {
        if (!_looping) return;
        _looping = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : (now - _last).TotalSeconds;
        if (dt <= 0) return;
        _last = now;
        dt = Math.Min(dt, 0.05);
        _t += dt;

        // Стрелка: у таймера — тик раз в секунду с лёгким перелётом, у секундомера — плавно по секундам.
        double target;
        if (Kind == ToolGlyphKind.Stopwatch)
        {
            target = Progress * 360;
            if (target < _hand - 180) _hand -= 360; // переход через 12 часов без обратного прокрута
        }
        else
        {
            if (IsRunning && _t >= _nextTick)
            {
                _nextTick = _t + 1;
                _tickTarget += 30;
            }
            target = _tickTarget;
        }
        Spring(ref _hand, ref _handVel, target, 260, 20, dt);
        Spring(ref _pop, ref _popVel, 1, 320, 16, dt);
        _press = Math.Max(0, _press - dt * 4);
        _steam += ((Kind == ToolGlyphKind.Coffee && IsRunning ? 1 : 0.0) - _steam) * Math.Min(1, dt * 3);

        InvalidateVisual();

        bool settled = Math.Abs(_hand - target) < 0.05 && Math.Abs(_handVel) < 0.05
                       && Math.Abs(_pop - 1) < 0.002 && Math.Abs(_popVel) < 0.01 && _press <= 0
                       && (_steam < 0.01 || _steam > 0.99);
        bool continuous = IsRunning; // тиканье, пар и стрелка секундомера живут, пока инструмент идёт
        if (settled && !continuous)
        {
            _hand = target;
            _pop = 1;
            StopLoop();
        }
    }

    private static void Spring(ref double x, ref double v, double target, double k, double d, double dt)
    {
        v += ((target - x) * k - v * d) * dt;
        x += v * dt;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;
        double s = side / Grid;

        dc.PushTransform(new TranslateTransform((ActualWidth - side) / 2, (ActualHeight - side) / 2));
        dc.PushTransform(new ScaleTransform(s, s));
        // «Выпрыгивание» — вокруг центра сетки (в её же координатах), иначе иконка уезжает вбок.
        dc.PushTransform(new ScaleTransform(_pop, _pop, Grid / 2, Grid / 2));

        var brush = Foreground;
        var pen = new Pen(brush, StrokeWidth / s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

        switch (Kind)
        {
            case ToolGlyphKind.Timer: DrawTimer(dc, brush, pen); break;
            case ToolGlyphKind.Coffee: DrawCoffee(dc, brush, pen, s); break;
            case ToolGlyphKind.Stopwatch: DrawStopwatch(dc, brush, pen); break;
        }

        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    private void DrawTimer(DrawingContext dc, Brush brush, Pen pen)
    {
        var c = new Point(12, 12.5);
        const double r = 8.2;
        // Круг с прорезью слева от «12 часов», от верхней точки внутрь — короткая риска.
        var arc = new StreamGeometry();
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(Polar(c, r, 0), false, false);
            ctx.ArcTo(Polar(c, r, 318), new Size(r, r), 0, true, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, pen, arc);
        double dip = _press * 0.9;
        dc.DrawLine(pen, new Point(12, c.Y - r + dip), new Point(12, c.Y - r + 2.6 + dip));

        dc.DrawLine(pen, c, Polar(c, 5.2, _hand));
        dc.DrawEllipse(brush, null, c, 1.25, 1.25);
    }

    private void DrawStopwatch(DrawingContext dc, Brush brush, Pen pen)
    {
        var c = new Point(12, 13.4);
        const double r = 7.6;
        dc.DrawEllipse(null, pen, c, r, r);

        // Заводная головка сверху «нажимается» при старте/паузе.
        double dip = _press * 1.3;
        dc.DrawLine(pen, new Point(10.1, 2.9 + dip), new Point(13.9, 2.9 + dip));
        dc.DrawLine(pen, new Point(12, 2.9 + dip), new Point(12, c.Y - r));
        // Боковая кнопка.
        dc.DrawLine(pen, Polar(c, r + 0.6, 45), Polar(c, r + 2.2, 45));

        dc.DrawLine(pen, c, Polar(c, 5.4, _hand));
        dc.DrawEllipse(brush, null, c, 1.2, 1.2);
    }

    private void DrawCoffee(DrawingContext dc, Brush brush, Pen pen, double s)
    {
        // Чашка.
        var cup = new StreamGeometry();
        using (var ctx = cup.Open())
        {
            ctx.BeginFigure(new Point(5, 11), true, true);
            ctx.LineTo(new Point(16.6, 11), true, false);
            ctx.LineTo(new Point(16.6, 13.2), true, false);
            ctx.ArcTo(new Point(12.3, 17.6), new Size(4.6, 4.4), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(9.3, 17.6), true, false);
            ctx.ArcTo(new Point(5, 13.2), new Size(4.6, 4.4), 0, false, SweepDirection.Clockwise, true, false);
        }
        cup.Freeze();
        dc.DrawGeometry(brush, null, cup);

        // Ручка.
        var handle = new StreamGeometry();
        using (var ctx = handle.Open())
        {
            ctx.BeginFigure(new Point(16.4, 12), false, false);
            ctx.LineTo(new Point(17.4, 12), true, false);
            ctx.ArcTo(new Point(17.4, 16.2), new Size(2.1, 2.1), 0, false, SweepDirection.Clockwise, true, false);
            ctx.LineTo(new Point(15.4, 16.2), true, false);
        }
        handle.Freeze();
        dc.DrawGeometry(null, pen, handle);

        // Блюдце.
        dc.DrawEllipse(brush, null, new Point(10.8, 19.3), 7.6, 1.45);

        // Пар: три струйки поднимаются, извиваются и тают.
        if (_steam < 0.01) return;
        var steamPen = new Pen(brush, Math.Max(StrokeWidth * 0.8, 1) / s) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        double[] xs = { 8.3, 10.8, 13.3 };
        for (int i = 0; i < xs.Length; i++)
        {
            double p = (_t * 0.6 + i * 0.37) % 1;
            double bottom = 9.6 - p * 2.6, len = 3.4;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                const int n = 8;
                for (int k = 0; k <= n; k++)
                {
                    double y = bottom - len * k / n;
                    double x = xs[i] + Math.Sin(y * 1.4 + _t * 3 + i) * 0.75;
                    if (k == 0) ctx.BeginFigure(new Point(x, y), false, false);
                    else ctx.LineTo(new Point(x, y), true, true);
                }
            }
            g.Freeze();
            dc.PushOpacity(Math.Sin(p * Math.PI) * _steam);
            dc.DrawGeometry(null, steamPen, g);
            dc.Pop();
        }
    }

    private static Point Polar(Point c, double r, double deg)
    {
        double a = deg * Math.PI / 180;
        return new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
    }
}
