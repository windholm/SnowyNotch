using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SnowyNotch.Controls;

/// <summary>
/// Значок батареи в духе iOS: скруглённый корпус с «носиком», заливка по уровню (зелёная / жёлтая / красная),
/// при зарядке — молния, а заливка мягко «дышит».
/// </summary>
public sealed class BatteryGlyph : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ChargingProperty = DependencyProperty.Register(
        nameof(Charging), typeof(bool), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((BatteryGlyph)d).SyncPulse()));

    private static readonly DependencyProperty PulseProperty = DependencyProperty.Register(
        "Pulse", typeof(double), typeof(BatteryGlyph),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Уровень 0..1.</summary>
    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool Charging { get => (bool)GetValue(ChargingProperty); set => SetValue(ChargingProperty, value); }

    static BatteryGlyph()
    {
        WidthProperty.OverrideMetadata(typeof(BatteryGlyph), new FrameworkPropertyMetadata(28.0));
        HeightProperty.OverrideMetadata(typeof(BatteryGlyph), new FrameworkPropertyMetadata(14.0));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(BatteryGlyph), new UIPropertyMetadata(false));
    }

    public BatteryGlyph()
    {
        Loaded += (_, _) => SyncPulse();
        IsVisibleChanged += (_, _) => SyncPulse();
    }

    private void SyncPulse()
    {
        // Пульс только пока значок виден: HUD гаснет через пару секунд, а зарядка может идти часами.
        if (Charging && IsVisible)
            BeginAnimation(PulseProperty, new DoubleAnimation(1, 0.55, TimeSpan.FromMilliseconds(900))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            });
        else
            BeginAnimation(PulseProperty, null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double nub = Math.Max(2, w * 0.08);
        var body = new Rect(0.75, 0.75, w - nub - 2, h - 1.5);
        double r = h * 0.3;

        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)), 1.3), body, r, r);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)), null,
            new Rect(body.Right + 1, h * 0.32, nub, h * 0.36), nub * 0.5, nub * 0.5);

        double level = Math.Clamp(Level, 0, 1);
        var color = Charging ? Color.FromRgb(0x30, 0xD1, 0x58)
            : level <= 0.1 ? Color.FromRgb(0xFF, 0x45, 0x3A)
            : level <= 0.2 ? Color.FromRgb(0xFF, 0xD6, 0x0A)
            : Colors.White;
        var inner = new Rect(body.X + 2, body.Y + 2, Math.Max(0, (body.Width - 4) * Math.Max(0.06, level)), body.Height - 4);
        var fill = new SolidColorBrush(color) { Opacity = Charging ? (double)GetValue(PulseProperty) : 1 };
        dc.DrawRoundedRectangle(fill, null, inner, r * 0.6, r * 0.6);

        if (!Charging) return;
        // Молния по центру корпуса, с тёмной обводкой, чтобы читалась на заливке.
        double cx = body.X + body.Width / 2, cy = h / 2, s = h * 0.42;
        var bolt = new StreamGeometry();
        using (var ctx = bolt.Open())
        {
            ctx.BeginFigure(new Point(cx + s * 0.15, cy - s), true, true);
            ctx.LineTo(new Point(cx - s * 0.55, cy + s * 0.12), true, true);
            ctx.LineTo(new Point(cx - s * 0.02, cy + s * 0.12), true, true);
            ctx.LineTo(new Point(cx - s * 0.18, cy + s), true, true);
            ctx.LineTo(new Point(cx + s * 0.55, cy - s * 0.15), true, true);
            ctx.LineTo(new Point(cx + s * 0.02, cy - s * 0.15), true, true);
        }
        bolt.Freeze();
        dc.DrawGeometry(Brushes.White, new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)), 1), bolt);
    }
}
