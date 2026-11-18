using System;
using System.Windows;
using System.Windows.Media;

namespace SnowyNotch.Controls;

/// <summary>
/// Полоса прогресса трека. Обычный вид — прямая полоса. Волнистый (как в Android / Material You):
/// пройденная часть — бегущая синусоида, пока музыка играет; на паузе волна плавно разглаживается в прямую.
/// Остаток — тонкая прямая дорожка, на стыке — вертикальная «ручка». Кадры рисуются только пока волна движется.
/// </summary>
public sealed class WaveProgress : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = Reg(nameof(Progress), 0.0);
    public static readonly DependencyProperty IsWavyProperty = DependencyProperty.Register(nameof(IsWavy), typeof(bool), typeof(WaveProgress),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((WaveProgress)d).Wake()));
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(WaveProgress),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((WaveProgress)d).Wake()));
    public static readonly DependencyProperty ThicknessProperty = Reg(nameof(Thickness), 4.0);
    public static readonly DependencyProperty ActiveColorProperty = DependencyProperty.Register(nameof(ActiveColor), typeof(Color), typeof(WaveProgress),
        new FrameworkPropertyMetadata(Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TrackColorProperty = DependencyProperty.Register(nameof(TrackColor), typeof(Color), typeof(WaveProgress),
        new FrameworkPropertyMetadata(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF), FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public bool IsWavy { get => (bool)GetValue(IsWavyProperty); set => SetValue(IsWavyProperty, value); }
    public bool IsPlaying { get => (bool)GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Color ActiveColor { get => (Color)GetValue(ActiveColorProperty); set => SetValue(ActiveColorProperty, value); }
    public Color TrackColor { get => (Color)GetValue(TrackColorProperty); set => SetValue(TrackColorProperty, value); }

    private static DependencyProperty Reg(string name, double def) =>
        DependencyProperty.Register(name, typeof(double), typeof(WaveProgress), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Длина волны и высота гребня относительно толщины полосы.</summary>
    private double WaveLength => Thickness * 9;
    private double MaxAmplitude => Thickness * 0.95;

    private double _phase, _amp;
    private bool _running;
    private TimeSpan _last;

    public WaveProgress()
    {
        IsVisibleChanged += (_, _) => Wake();
        SnapsToDevicePixels = false;
    }

    private double TargetAmplitude => IsWavy && IsPlaying ? MaxAmplitude : 0;

    private void Wake()
    {
        if (_running || !IsVisible) return;
        if (Math.Abs(_amp - TargetAmplitude) < 0.01 && !(IsWavy && IsPlaying)) { InvalidateVisual(); return; }
        _running = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : Math.Min(0.05, (now - _last).TotalSeconds);
        _last = now;
        if (dt <= 0) return;

        double target = TargetAmplitude;
        _amp += (target - _amp) * (1 - Math.Exp(-dt * 7));
        if (Math.Abs(_amp - target) < 0.01) _amp = target;
        // Скорость бега волны — примерно одна длина волны в секунду.
        if (_amp > 0.01) _phase = (_phase + dt * Math.PI * 2 * 1.1) % (Math.PI * 2);
        InvalidateVisual();

        bool moving = _amp > 0.01 || Math.Abs(_amp - target) > 0.01;
        if (!moving || !IsVisible)
        {
            CompositionTarget.Rendering -= OnFrame;
            _running = false;
        }
    }

    protected override Size MeasureOverride(Size available) =>
        new(double.IsInfinity(available.Width) ? 100 : available.Width, Math.Max(Thickness, MaxAmplitude * 2 + Thickness) + 2);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double t = Thickness, cy = h / 2;
        double x0 = t / 2, x1 = w - t / 2;
        double split = x0 + Math.Clamp(Progress, 0, 1) * (x1 - x0);
        bool wavy = IsWavy;
        double gap = wavy ? t * 1.1 : 0; // зазор вокруг ручки — только в волнистом виде

        var active = new Pen(new SolidColorBrush(ActiveColor), t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        var track = new Pen(new SolidColorBrush(TrackColor), wavy ? t * 0.75 : t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };

        // Остаток пути.
        if (x1 - (split + gap) > 0.5)
            dc.DrawLine(track, new Point(split + gap, cy), new Point(x1, cy));

        // Пройденная часть: волна (или прямая, когда амплитуда сошла на нет).
        double end = split - gap;
        if (end - x0 > 0.3)
        {
            if (_amp < 0.05)
            {
                dc.DrawLine(active, new Point(x0, cy), new Point(end, cy));
            }
            else
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    double k = Math.PI * 2 / WaveLength;
                    // Амплитуда мягко нарастает от левого края, чтобы волна не начиналась со ступеньки.
                    double Y(double x) => cy + _amp * Math.Min(1, (x - x0) / (WaveLength * 0.5)) * Math.Sin(k * x - _phase);
                    c.BeginFigure(new Point(x0, Y(x0)), false, false);
                    for (double x = x0 + 1.5; x < end; x += 1.5) c.LineTo(new Point(x, Y(x)), true, true);
                    c.LineTo(new Point(end, Y(end)), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, active, g);
            }
        }

        // Ручка: в волнистом виде — вертикальная палочка; у прямой полосы её нет (как было раньше).
        if (wavy)
        {
            double knobH = Math.Min(h, t * 3.4);
            var knob = new Pen(new SolidColorBrush(ActiveColor), t * 0.95) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(knob, new Point(split, cy - knobH / 2), new Point(split, cy + knobH / 2));
        }
    }
}
