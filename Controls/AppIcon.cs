using System;
using System.Windows;
using System.Windows.Media;

namespace SnowyNotch.Controls;

/// <summary>
/// Иконка сервиса: «стеклянный» суперэллипс с диагональным градиентом, светом из левого верхнего угла,
/// тонкой светлой кромкой и цветным свечением под плиткой. Глиф двухтонный: Tone — полупрозрачная подложка,
/// Glyph — сплошная часть (см. <see cref="ServiceGlyphs"/>). Глифы — заливаемая геометрия в сетке 24×24.
/// </summary>
public sealed class AppIcon : FrameworkElement
{
    public static readonly DependencyProperty GlyphProperty = Reg<Geometry?>(nameof(Glyph), null);
    public static readonly DependencyProperty ToneProperty = Reg<Geometry?>(nameof(Tone), null);
    public static readonly DependencyProperty TopProperty = Reg(nameof(Top), Color.FromRgb(0x6E, 0x6E, 0x73));
    public static readonly DependencyProperty BottomProperty = Reg(nameof(Bottom), Color.FromRgb(0x2C, 0x2C, 0x2E));
    public static readonly DependencyProperty GlyphTopProperty = Reg(nameof(GlyphTop), Colors.White);
    public static readonly DependencyProperty GlyphBottomProperty = Reg(nameof(GlyphBottom), Color.FromRgb(0xF1, 0xF1, 0xF7));
    public static readonly DependencyProperty ToneColorProperty = Reg(nameof(ToneColor), Colors.White);
    public static readonly DependencyProperty ToneOpacityProperty = Reg(nameof(ToneOpacity), 0.42);
    /// <summary>Цвет свечения под иконкой (прозрачный — без него). Рисуется слоями прямо здесь,
    /// без DropShadowEffect: эффект на каждой иконке тормозил анимации панели.</summary>
    public static readonly DependencyProperty ShadowColorProperty = Reg(nameof(ShadowColor), Colors.Transparent);

    public Geometry? Glyph { get => (Geometry?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public Geometry? Tone { get => (Geometry?)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public Color Top { get => (Color)GetValue(TopProperty); set => SetValue(TopProperty, value); }
    public Color Bottom { get => (Color)GetValue(BottomProperty); set => SetValue(BottomProperty, value); }
    public Color GlyphTop { get => (Color)GetValue(GlyphTopProperty); set => SetValue(GlyphTopProperty, value); }
    public Color GlyphBottom { get => (Color)GetValue(GlyphBottomProperty); set => SetValue(GlyphBottomProperty, value); }
    public Color ToneColor { get => (Color)GetValue(ToneColorProperty); set => SetValue(ToneColorProperty, value); }
    public double ToneOpacity { get => (double)GetValue(ToneOpacityProperty); set => SetValue(ToneOpacityProperty, value); }
    public Color ShadowColor { get => (Color)GetValue(ShadowColorProperty); set => SetValue(ShadowColorProperty, value); }

    static AppIcon()
    {
        WidthProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(76.0));
        HeightProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(76.0));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(AppIcon), new UIPropertyMetadata(false));
    }

    private static DependencyProperty Reg<T>(string name, T def) =>
        DependencyProperty.Register(name, typeof(T), typeof(AppIcon), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

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

        // Цветное свечение под плиткой: расширяющиеся полупрозрачные силуэты, смещённые вниз.
        var sc = ShadowColor;
        if (sc.A > 0)
        {
            const int layers = 6;
            for (int i = layers; i >= 1; i--)
            {
                double grow = s * 0.026 * i;
                double alpha = sc.A / 255.0 * 0.13 * (1 - (i - 1) / (double)layers);
                var r = new Rect(box.X - grow + s * 0.06, box.Y - grow + s * 0.13 + i * s * 0.008, s + grow * 2 - s * 0.12, s + grow * 2 - s * 0.12);
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), sc.R, sc.G, sc.B)), null, Squircle(r, 4.2));
            }
        }

        var shape = Squircle(box);

        // Основа: диагональный градиент от светлого угла к насыщенному.
        dc.DrawGeometry(new LinearGradientBrush(Top, Bottom, new Point(0.12, 0), new Point(0.88, 1)), null, shape);

        dc.PushClip(shape);
        // Свет из левого верхнего угла — «стекло».
        var light = new RadialGradientBrush
        {
            Center = new Point(0.26, 0.02), GradientOrigin = new Point(0.26, 0.02), RadiusX = 0.95, RadiusY = 0.85,
        };
        light.GradientStops.Add(new GradientStop(Color.FromArgb(0x5C, 0xFF, 0xFF, 0xFF), 0));
        light.GradientStops.Add(new GradientStop(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF), 0.5));
        light.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 1));
        dc.DrawRectangle(light, null, box);
        // Затемнение к нижнему правому углу — объём.
        var shade = new LinearGradientBrush { StartPoint = new Point(0.3, 0.45), EndPoint = new Point(0.8, 1) };
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0));
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x26, 0, 0, 0), 1));
        dc.DrawRectangle(shade, null, box);
        dc.Pop();

        // Кромка: яркая сверху, почти исчезает книзу.
        var edge = new LinearGradientBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF), 90);
        double ew = Math.Max(1, s * 0.016);
        dc.DrawGeometry(null, new Pen(edge, ew), Squircle(new Rect(box.X + ew / 2, box.Y + ew / 2, s - ew, s - ew)));

        var glyph = Glyph;
        var tone = Tone;
        if (glyph == null && tone == null) return;
        double gs = s * 0.62 / 24;
        double ox = box.X + (s - 24 * gs) / 2, oy = box.Y + (s - 24 * gs) / 2;

        if (tone != null)
        {
            dc.PushTransform(new TranslateTransform(ox, oy));
            dc.PushTransform(new ScaleTransform(gs, gs));
            dc.PushOpacity(ToneOpacity);
            dc.DrawGeometry(new SolidColorBrush(ToneColor), null, tone);
            dc.Pop();
            dc.Pop();
            dc.Pop();
        }

        if (glyph == null) return;

        // Мягкая тень глифа.
        dc.PushTransform(new TranslateTransform(ox, oy + s * 0.02));
        dc.PushTransform(new ScaleTransform(gs, gs));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)), null, glyph);
        dc.Pop();
        dc.Pop();

        dc.PushTransform(new TranslateTransform(ox, oy));
        dc.PushTransform(new ScaleTransform(gs, gs));
        dc.DrawGeometry(new LinearGradientBrush(GlyphTop, GlyphBottom, 90), null, glyph);
        dc.Pop();
        dc.Pop();
    }
}
