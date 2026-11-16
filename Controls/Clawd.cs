using System;
using System.Windows;
using System.Windows.Media;

namespace SnowyNotch.Controls;

public enum BuddyMood { Idle, Busy, Wait, Done }

/// <summary>
/// Классический пиксельный маскот Claude, живой по состоянию агента:
/// Idle — дышит, моргает и оглядывается, а если задачи долго нет — засыпает (Z z z);
/// Busy — деловито ходит туда-сюда, перебирая лапками;
/// Wait — подпрыгивает и машет лапкой, вокруг пульсирует свечение («нужен ответ»);
/// Done — радостный прыжок с поднятыми лапками и глазами «^ ^».
/// </summary>
public sealed class Clawd : FrameworkElement
{
    // Сетка 11×8: корпус — колонки 1..9 и ряды 0..5, «руки» — колонки 0 и 10 в рядах 2..3, ноги — ряды 6..7.
    private const double Cols = 11, Rows = 8, BodyRows = 6, LegRows = 2;
    private static readonly int[] LegCols = { 1, 3, 7, 9 };
    private static readonly int[] EyeCols = { 3, 7 };
    private const double SleepAfter = 25;

    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(BuddyMood), typeof(Clawd),
        new PropertyMetadata(BuddyMood.Idle, (d, _) => ((Clawd)d).OnMoodChanged()));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Color), typeof(Clawd),
        new FrameworkPropertyMetadata(Color.FromRgb(0xD9, 0x77, 0x57), FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((Clawd)d).RebuildBrushes()));

    public static readonly DependencyProperty EyeColorProperty = DependencyProperty.Register(
        nameof(EyeColor), typeof(Color), typeof(Clawd),
        new FrameworkPropertyMetadata(Color.FromRgb(0x19, 0x1A, 0x1B), FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((Clawd)d).RebuildBrushes()));

    private readonly Random _rng = new();
    private readonly double[] _lift = new double[4];
    private Brush _body = Brushes.Orange, _eye = Brushes.Black, _z = Brushes.White;
    private Pen _happyPen = new(Brushes.Black, 1);

    private TimeSpan _last;
    private bool _running;
    private double _t, _moodT;
    private double _blinkAt = -10, _nextBlink = 1.5;
    private bool _doubleBlink;
    private double _look, _lookTarget, _nextLook = 2;
    private double _glow;

    static Clawd()
    {
        WidthProperty.OverrideMetadata(typeof(Clawd), new FrameworkPropertyMetadata(28.0));
        HeightProperty.OverrideMetadata(typeof(Clawd), new FrameworkPropertyMetadata(28.0));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(Clawd), new UIPropertyMetadata(false));
    }

    public Clawd()
    {
        RebuildBrushes();
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => { if (IsVisible) Start(); else Stop(); };
    }

    public BuddyMood Mood { get => (BuddyMood)GetValue(MoodProperty); set => SetValue(MoodProperty, value); }
    public Color Tint { get => (Color)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public Color EyeColor { get => (Color)GetValue(EyeColorProperty); set => SetValue(EyeColorProperty, value); }

    private bool Sleeping => Mood == BuddyMood.Idle && _moodT > SleepAfter;

    private void OnMoodChanged()
    {
        _moodT = 0;
        if (Mood == BuddyMood.Done) _glow = 1;
        Start();
    }

    private void RebuildBrushes()
    {
        _body = Frozen(new SolidColorBrush(Tint));
        _eye = Frozen(new SolidColorBrush(EyeColor));
        var t = Tint;
        _z = Frozen(new SolidColorBrush(Color.FromRgb(
            (byte)(t.R + (255 - t.R) * 0.55), (byte)(t.G + (255 - t.G) * 0.55), (byte)(t.B + (255 - t.B) * 0.55))));
        _happyPen = new Pen(_eye, 1); // толщина задаётся при отрисовке
    }

    private void Start()
    {
        if (_running || !IsLoaded || !IsVisible) return;
        _running = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : (now - _last).TotalSeconds;
        if (dt <= 0) return;
        _last = now;
        dt = Math.Min(dt, 0.1);
        _t += dt;
        _moodT += dt;

        // Моргание: раз в 2–5 секунд, иногда дважды подряд.
        if (_t >= _nextBlink)
        {
            _blinkAt = _t;
            _doubleBlink = _rng.NextDouble() < 0.25;
            _nextBlink = _t + 2.2 + _rng.NextDouble() * 3.2;
        }

        // Куда смотрим.
        switch (Mood)
        {
            case BuddyMood.Idle when !Sleeping:
                if (_t >= _nextLook)
                {
                    _lookTarget = _rng.Next(4) switch { 0 => -1, 1 => 1, _ => 0 };
                    _nextLook = _t + 1.2 + _rng.NextDouble() * 2.6;
                }
                break;
            case BuddyMood.Busy:
                _lookTarget = Math.Clamp(Math.Cos(_t * 0.9) * 3, -1, 1); // по направлению шага
                break;
            default:
                _lookTarget = 0;
                break;
        }
        _look += (_lookTarget - _look) * Math.Min(1, dt * 12);

        double targetGlow = Mood == BuddyMood.Wait ? 0.35 + 0.35 * Math.Sin(_t * 3.2) : 0;
        _glow += (targetGlow - _glow) * Math.Min(1, dt * (Mood == BuddyMood.Done ? 1.2 : 4));

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double u = Math.Min(w / (Cols + 0.6), h / (Rows + 1.6));
        double ox = (w - Cols * u) / 2;
        double ground = h / 2 + (Rows / 2 + 0.5) * u;

        // ---- поза ----
        double dx = 0, jump = 0, squash = 0, armL = 0, armR = 0, eyeOpen = 1;
        bool happy = false, sleeping = Sleeping;
        Array.Clear(_lift);

        switch (Mood)
        {
            case BuddyMood.Busy:
            {
                double s = Math.Sin(_t * 8);
                _lift[0] = _lift[2] = Math.Max(0, s) * 0.7;
                _lift[1] = _lift[3] = Math.Max(0, -s) * 0.7;
                jump = Math.Abs(s) * 0.22;
                armL = s * 0.35;
                armR = -s * 0.35;
                dx = Math.Sin(_t * 0.9) * 1.0;
                break;
            }
            case BuddyMood.Wait:
            {
                // Два прыжка подряд, потом пауза — и всё время машет правой лапкой.
                double c = _moodT % 2.0;
                foreach (double t0 in new[] { 0.0, 0.5 })
                {
                    double p = c - t0;
                    if (p is >= 0 and < 0.42) jump = Math.Sin(p / 0.42 * Math.PI) * 1.1;
                    else if (p is >= 0.42 and < 0.54) squash = Math.Sin((p - 0.42) / 0.12 * Math.PI) * 0.12;
                }
                if (jump > 0.05) _lift[0] = _lift[1] = _lift[2] = _lift[3] = 0.4;
                armR = -2.3 + Math.Sin(_t * 14) * 0.45;
                armL = jump > 0.05 ? -0.3 : 0;
                break;
            }
            case BuddyMood.Done:
            {
                happy = true;
                if (_moodT < 0.6) jump = Math.Sin(_moodT / 0.6 * Math.PI) * 1.5;
                else if (_moodT < 0.75) squash = Math.Sin((_moodT - 0.6) / 0.15 * Math.PI) * 0.14;
                double up = _moodT < 1.4 ? 1 : Math.Max(0, 1 - (_moodT - 1.4) * 3);
                double b = (Math.Sin(_t * 2.0) + 1) / 2;
                armL = armR = -2.2 * up + b * 0.12 * (1 - up);
                if (_moodT > 0.75) squash = Math.Max(squash, b * 0.05 * (1 - up));
                break;
            }
            default:
            {
                if (sleeping)
                {
                    double b = (Math.Sin(_t * 1.3) + 1) / 2;
                    squash = 0.04 + b * 0.07;
                    armL = armR = 0.2;
                    eyeOpen = Math.Max(0, 1 - (_moodT - SleepAfter) / 0.8);
                }
                else
                {
                    double b = (Math.Sin(_t * 2.0) + 1) / 2;
                    squash = b * 0.05;
                    armL = armR = b * 0.12;
                }
                break;
            }
        }

        if (!sleeping && !happy)
        {
            double bt = _t - _blinkAt;
            double blink = Tri(bt / 0.16);
            if (_doubleBlink) blink = Math.Max(blink, Tri((bt - 0.26) / 0.16));
            eyeOpen = Math.Min(eyeOpen, 1 - blink);
        }

        // ---- свечение ----
        double left = ox + dx * u;
        double bodyBottom = ground - (jump + LegRows) * u;
        double bodyH = BodyRows * u * (1 - squash);
        double bodyTop = bodyBottom - bodyH;
        double row = bodyH / BodyRows;

        if (_glow > 0.02)
        {
            var c = Tint;
            var halo = new RadialGradientBrush();
            halo.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(120 * _glow), c.R, c.G, c.B), 0.45));
            halo.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
            dc.DrawEllipse(halo, null, new Point(left + Cols / 2 * u, bodyTop + bodyH / 2 + u), 7 * u, 7 * u);
        }

        // ---- тело ----
        dc.DrawRectangle(_body, null, new Rect(left + 1 * u, bodyTop, 9 * u, bodyH + 0.02 * u));

        double armTop = bodyTop + 2 * row;
        double armMin = bodyTop - 1.4 * u;
        dc.DrawRectangle(_body, null, new Rect(left, Math.Max(armMin, armTop + armL * u), 1.02 * u, 2 * row));
        dc.DrawRectangle(_body, null, new Rect(left + 9.98 * u, Math.Max(armMin, armTop + armR * u), 1.02 * u, 2 * row));

        for (int i = 0; i < LegCols.Length; i++)
        {
            double legH = Math.Max(0.4 * u, (LegRows - _lift[i]) * u);
            dc.DrawRectangle(_body, null, new Rect(left + LegCols[i] * u, bodyBottom - 0.02 * u, u, legH + 0.02 * u));
        }

        // ---- глаза ----
        double eyeTop = bodyTop + 2 * row;
        foreach (int col in EyeCols)
        {
            double ex = left + (col + _look * 0.3) * u;
            if (happy)
            {
                _happyPen.Thickness = Math.Max(1, 0.34 * u);
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(new Point(ex - 0.1 * u, eyeTop + 0.9 * row), false, false);
                    ctx.LineTo(new Point(ex + 0.5 * u, eyeTop + 0.15 * row), true, false);
                    ctx.LineTo(new Point(ex + 1.1 * u, eyeTop + 0.9 * row), true, false);
                }
                dc.DrawGeometry(null, _happyPen, g);
            }
            else
            {
                double eh = Math.Max(0.2 * row, row * eyeOpen);
                double ey = sleeping ? eyeTop + row - eh : eyeTop + (row - eh) / 2;
                dc.DrawRectangle(_eye, null, new Rect(ex, ey, u, eh));
            }
        }

        // ---- Z z z ----
        if (sleeping)
        {
            double fade = Math.Min(1, (_moodT - SleepAfter) / 1.5);
            for (int i = 0; i < 3; i++)
            {
                double p = (_t + i * 0.9) % 2.7 / 2.7;
                double px = (0.3 + 0.25 * p) * u;
                double zx = left + 9.6 * u + p * 1.5 * u + Math.Sin(p * 6) * 0.2 * u;
                double zy = bodyTop - 0.6 * u - p * 2.6 * u;
                dc.PushOpacity(Math.Sin(p * Math.PI) * fade);
                DrawZ(dc, zx, zy, px);
                dc.Pop();
            }
        }
    }

    private void DrawZ(DrawingContext dc, double x, double y, double px)
    {
        for (int i = 0; i < 3; i++)
        {
            dc.DrawRectangle(_z, null, new Rect(x + i * px, y, px, px));
            dc.DrawRectangle(_z, null, new Rect(x + i * px, y + 2 * px, px, px));
        }
        dc.DrawRectangle(_z, null, new Rect(x + px, y + px, px, px));
    }

    private static double Tri(double x) => x is > 0 and < 1 ? 1 - Math.Abs(2 * x - 1) : 0;

    private static Brush Frozen(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
