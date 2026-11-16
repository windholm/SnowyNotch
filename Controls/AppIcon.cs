using System;
using System.Windows;
using System.Windows.Media;

namespace SnowyNotch.Controls;

/// <summary>
/// Иконка сервиса в стиле Apple: суперэллипс («сквиркл»), насыщенный вертикальный градиент, глянцевый блик сверху,
/// тонкая светлая кромка; глиф по центру — белый (на цветных плитках) или цветной градиентный (на тёмных),
/// с мягкой тенью. Глиф — заливаемая геометрия в сетке 24×24.
/// </summary>
public sealed class AppIcon : FrameworkElement
{
    public static readonly DependencyProperty GlyphProperty = Reg<Geometry?>(nameof(Glyph), null);
    public static readonly DependencyProperty TopProperty = Reg(nameof(Top), Color.FromRgb(0x6E, 0x6E, 0x73));
    public static readonly DependencyProperty BottomProperty = Reg(nameof(Bottom), Color.FromRgb(0x2C, 0x2C, 0x2E));
    public static readonly DependencyProperty GlyphTopProperty = Reg(nameof(GlyphTop), Colors.White);
    public static readonly DependencyProperty GlyphBottomProperty = Reg(nameof(GlyphBottom), Color.FromRgb(0xEC, 0xEC, 0xF2));
    /// <summary>Цвет мягкой тени под иконкой (прозрачный — без тени). Рисуется слоями прямо здесь,
    /// без DropShadowEffect: эффект на каждой из восьми иконок тормозил анимации панели.</summary>
    public static readonly DependencyProperty ShadowColorProperty = Reg(nameof(ShadowColor), Colors.Transparent);
    public Color ShadowColor { get => (Color)GetValue(ShadowColorProperty); set => SetValue(ShadowColorProperty, value); }

    public Geometry? Glyph { get => (Geometry?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public Color Top { get => (Color)GetValue(TopProperty); set => SetValue(TopProperty, value); }
    public Color Bottom { get => (Color)GetValue(BottomProperty); set => SetValue(BottomProperty, value); }
    public Color GlyphTop { get => (Color)GetValue(GlyphTopProperty); set => SetValue(GlyphTopProperty, value); }
    public Color GlyphBottom { get => (Color)GetValue(GlyphBottomProperty); set => SetValue(GlyphBottomProperty, value); }

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

        // Мягкая тень: несколько расширяющихся полупрозрачных силуэтов чуть ниже иконки — дёшево и плавно.
        var sc = ShadowColor;
        if (sc.A > 0)
        {
            const int layers = 7;
            for (int i = layers; i >= 1; i--)
            {
                double grow = s * 0.022 * i;
                double alpha = sc.A / 255.0 * 0.11 * (1 - (i - 1) / (double)layers);
                var r = new Rect(box.X - grow + s * 0.03, box.Y - grow + s * 0.075 + i * s * 0.006, s + grow * 2 - s * 0.06, s + grow * 2 - s * 0.06);
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), sc.R, sc.G, sc.B)), null, Squircle(r, 4.2));
            }
        }

        var shape = Squircle(box);

        // Основа: вертикальный градиент.
        dc.DrawGeometry(new LinearGradientBrush(Top, Bottom, 90), null, shape);

        // Глянец: светлый блик в верхней половине.
        dc.PushClip(shape);
        var gloss = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        gloss.GradientStops.Add(new GradientStop(Color.FromArgb(0x42, 0xFF, 0xFF, 0xFF), 0));
        gloss.GradientStops.Add(new GradientStop(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF), 0.45));
        gloss.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.55));
        dc.DrawRectangle(gloss, null, box);
        // Нижняя тень внутри плитки — объём.
        var shade = new LinearGradientBrush { StartPoint = new Point(0.5, 0.6), EndPoint = new Point(0.5, 1) };
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0));
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, 0, 0, 0), 1));
        dc.DrawRectangle(shade, null, box);
        dc.Pop();

        // Кромка: светлая сверху, исчезает книзу.
        var edge = new LinearGradientBrush(Color.FromArgb(0x5A, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF), 90);
        dc.DrawGeometry(null, new Pen(edge, Math.Max(1, s * 0.012)), Squircle(new Rect(box.X + 0.5, box.Y + 0.5, s - 1, s - 1)));

        var glyph = Glyph;
        if (glyph == null) return;
        double gs = s * 0.58 / 24;
        double ox = box.X + (s - 24 * gs) / 2, oy = box.Y + (s - 24 * gs) / 2;

        // Тень глифа.
        dc.PushTransform(new TranslateTransform(ox, oy + s * 0.018));
        dc.PushTransform(new ScaleTransform(gs, gs));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(0x4A, 0, 0, 0)), null, glyph);
        dc.Pop();
        dc.Pop();

        dc.PushTransform(new TranslateTransform(ox, oy));
        dc.PushTransform(new ScaleTransform(gs, gs));
        dc.DrawGeometry(new LinearGradientBrush(GlyphTop, GlyphBottom, 90), null, glyph);
        dc.Pop();
        dc.Pop();
    }
}
