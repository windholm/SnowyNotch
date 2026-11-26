using System;
using System.Windows;
using System.Windows.Media;

namespace SnowyNotch.Controls;

/// <summary>
/// Иконка сервиса в «графитовом» стиле: почти чёрный суперэллипс с мягким светом сверху и тонкой кромкой,
/// на нём — объёмный глиф из «полированного металла» (серебристый градиент, светлая грань сверху, тень снизу).
/// Глиф двухтонный: Glyph — светлый металл, Tone — тёмный металл под ним (см. <see cref="ServiceGlyphs"/>).
/// Глифы — заливаемая геометрия в сетке 24×24.
/// </summary>
public sealed class AppIcon : FrameworkElement
{
    public static readonly DependencyProperty GlyphProperty = Reg<Geometry?>(nameof(Glyph), null);
    public static readonly DependencyProperty ToneProperty = Reg<Geometry?>(nameof(Tone), null);
    public static readonly DependencyProperty ToneOpacityProperty = Reg(nameof(ToneOpacity), 1.0);
    /// <summary>Мягкая тень под плиткой. Рисуется слоями прямо здесь, без DropShadowEffect:
    /// эффект на каждой иконке тормозил анимации панели.</summary>
    public static readonly DependencyProperty GlowProperty = Reg(nameof(Glow), true);

    public Geometry? Glyph { get => (Geometry?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public Geometry? Tone { get => (Geometry?)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public double ToneOpacity { get => (double)GetValue(ToneOpacityProperty); set => SetValue(ToneOpacityProperty, value); }
    public bool Glow { get => (bool)GetValue(GlowProperty); set => SetValue(GlowProperty, value); }

    static AppIcon()
    {
        WidthProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(76.0));
        HeightProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(76.0));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(AppIcon), new UIPropertyMetadata(false));
    }

    private static DependencyProperty Reg<T>(string name, T def) =>
        DependencyProperty.Register(name, typeof(T), typeof(AppIcon), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    // ---------- палитра (общая для всех иконок, кисти заморожены) ----------

    private static readonly Brush TileFill = Gradient(90, (0x2A, 0x2A, 0x2D, 0), (0x19, 0x19, 0x1B, 0.5), (0x0F, 0x0F, 0x11, 1));
    private static readonly Brush TileGloss = Radial();
    private static readonly Brush RimLight = Gradient(90, (0xFF, 0xFF, 0xFF, 0), (0xFF, 0xFF, 0xFF, 0.45), alphaFrom: 0x70, alphaTo: 0x00);
    private static readonly Brush RimDark = Gradient(90, (0, 0, 0, 0.5), (0, 0, 0, 1), alphaFrom: 0x00, alphaTo: 0x8C);

    /// <summary>Светлый металл: белый блик сверху, «горизонт» посередине, тёмный низ.</summary>
    private static readonly Brush MetalLight = Gradient(90,
        (0xFF, 0xFF, 0xFF, 0), (0xEE, 0xEE, 0xF1, 0.32), (0xC4, 0xC4, 0xCA, 0.58), (0xA6, 0xA6, 0xAD, 0.8), (0xCF, 0xCF, 0xD4, 1));
    /// <summary>Тёмный металл для подложки глифа.</summary>
    private static readonly Brush MetalDark = Gradient(90,
        (0x9A, 0x9A, 0xA1, 0), (0x74, 0x74, 0x7B, 0.5), (0x56, 0x56, 0x5C, 1));
    private static readonly Brush EdgeHighlight = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush GlyphShadowNear = Freeze(new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)));
    private static readonly Brush GlyphShadowFar = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0)));

    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    /// <summary>Вертикальный градиент по точкам (r, g, b, offset); прозрачность — равномерно от alphaFrom к alphaTo.</summary>
    private static Brush Gradient(double angle, params (byte R, byte G, byte B, double At)[] stops) =>
        Gradient(angle, 0xFF, 0xFF, stops);

    private static Brush Gradient(double angle, (byte R, byte G, byte B, double At) a, (byte R, byte G, byte B, double At) b,
                                  byte alphaFrom, byte alphaTo) => Gradient(angle, alphaFrom, alphaTo, a, b);

    private static Brush Gradient(double angle, byte alphaFrom, byte alphaTo, params (byte R, byte G, byte B, double At)[] stops)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = angle == 90 ? new Point(0, 1) : new Point(1, 1) };
        for (int i = 0; i < stops.Length; i++)
        {
            var s = stops[i];
            byte alpha = (byte)(alphaFrom + (alphaTo - alphaFrom) * (stops.Length == 1 ? 0 : i / (double)(stops.Length - 1)));
            g.GradientStops.Add(new GradientStop(Color.FromArgb(alpha, s.R, s.G, s.B), s.At));
        }
        g.Freeze();
        return g;
    }

    private static Brush Radial()
    {
        var light = new RadialGradientBrush
        {
            Center = new Point(0.5, -0.05), GradientOrigin = new Point(0.5, -0.05), RadiusX = 0.85, RadiusY = 0.7,
        };
        light.GradientStops.Add(new GradientStop(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), 0));
        light.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
        light.Freeze();
        return light;
    }

    /// <summary>Суперэллипс |x|^n + |y|^n = 1 — форма иконок iOS/macOS.</summary>
    public static Geometry Squircle(Rect r, double n = 5)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            const int steps = 96;
            double a = r.Width / 2, b = r.Height / 2, cx = r.X + a, cy = r.Y + b;
            for (int i = 0; i <= steps; i++)
            {
                double t = i * Math.PI * 2 / steps;
                double cos = Math.Cos(t), sin = Math.Sin(t);
                var p = new Point(cx + a * Math.Sign(cos) * Math.Pow(Math.Abs(cos), 2 / n),
                                  cy + b * Math.Sign(sin) * Math.Pow(Math.Abs(sin), 2 / n));
                if (i == 0) ctx.BeginFigure(p, true, true);
                else ctx.LineTo(p, true, true);
            }
        }
        g.Freeze();
        return g;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double s = Math.Min(ActualWidth, ActualHeight);
        if (s <= 0) return;
        var box = new Rect((ActualWidth - s) / 2, (ActualHeight - s) / 2, s, s);

        // Тень под плиткой: расширяющиеся полупрозрачные силуэты, смещённые вниз.
        if (Glow)
        {
            const int layers = 5;
            for (int i = layers; i >= 1; i--)
            {
                double grow = s * 0.024 * i;
                double alpha = 0.16 * (1 - (i - 1) / (double)layers);
                var r = new Rect(box.X - grow + s * 0.05, box.Y - grow + s * 0.1 + i * s * 0.008, s + grow * 2 - s * 0.1, s + grow * 2 - s * 0.1);
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), 0, 0, 0)), null, Squircle(r, 4.2));
            }
        }

        var shape = Squircle(box);
        dc.DrawGeometry(TileFill, null, shape);
        dc.PushClip(shape);
        dc.DrawRectangle(TileGloss, null, box);
        dc.Pop();

        // Кромка: светлая сверху, тёмная внутренняя тень снизу — плитка выглядит толстой и литой.
        double ew = Math.Max(1, s * 0.018);
        var inner = Squircle(new Rect(box.X + ew / 2, box.Y + ew / 2, s - ew, s - ew));
        dc.DrawGeometry(null, new Pen(RimDark, ew * 1.6), inner);
        dc.DrawGeometry(null, new Pen(RimLight, ew), inner);

        var glyph = Glyph;
        var tone = Tone;
        if (glyph == null && tone == null) return;
        double gs = s * 0.7 / 24;
        double ox = box.X + (s - 24 * gs) / 2, oy = box.Y + (s - 24 * gs) / 2;

        // Тень всего глифа: ближняя плотная и дальняя мягкая.
        var all = tone != null && glyph != null
            ? Geometry.Combine(tone, glyph, GeometryCombineMode.Union, null)
            : (glyph ?? tone)!;
        DrawAt(dc, all, GlyphShadowFar, ox, oy + s * 0.05, gs);
        DrawAt(dc, all, GlyphShadowNear, ox, oy + s * 0.022, gs);

        if (tone != null)
        {
            dc.PushOpacity(ToneOpacity);
            DrawAt(dc, tone, EdgeHighlight, ox, oy - s * 0.008, gs, 0.35);
            DrawAt(dc, tone, MetalDark, ox, oy, gs);
            dc.Pop();
            // Светлый металл отбрасывает короткую тень на тёмный.
            if (glyph != null) DrawAt(dc, glyph, GlyphShadowNear, ox, oy + s * 0.014, gs, 0.7);
        }

        if (glyph == null) return;
        DrawAt(dc, glyph, EdgeHighlight, ox, oy - s * 0.009, gs);
        DrawAt(dc, glyph, MetalLight, ox, oy, gs);
    }

    private static void DrawAt(DrawingContext dc, Geometry g, Brush brush, double x, double y, double scale, double opacity = 1)
    {
        dc.PushTransform(new TranslateTransform(x, y));
        dc.PushTransform(new ScaleTransform(scale, scale));
        if (opacity < 1) dc.PushOpacity(opacity);
        dc.DrawGeometry(brush, null, g);
        if (opacity < 1) dc.Pop();
        dc.Pop();
        dc.Pop();
    }
}
