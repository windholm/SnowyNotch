using System;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SnowyNotch.Controls;

public sealed class IconGlyph : FrameworkElement
{
    private const double Grid = 24;

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(IconGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(IconGlyph),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth), typeof(double), typeof(IconGlyph),
        new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FilledProperty = DependencyProperty.Register(
        nameof(Filled), typeof(bool), typeof(IconGlyph),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Метка на самой геометрии: такая иконка всегда рисуется заливкой (например, пиксельный маскот Claude),
    /// даже если подставлена через Tag в шаблон, который о заливке не знает.</summary>
    public static readonly DependencyProperty SolidProperty = DependencyProperty.RegisterAttached(
        "Solid", typeof(bool), typeof(IconGlyph), new PropertyMetadata(false));

    public static bool GetSolid(DependencyObject o) => (bool)o.GetValue(SolidProperty);
    public static void SetSolid(DependencyObject o, bool value) => o.SetValue(SolidProperty, value);

    static IconGlyph()
    {
        WidthProperty.OverrideMetadata(typeof(IconGlyph), new FrameworkPropertyMetadata(18.0));
        HeightProperty.OverrideMetadata(typeof(IconGlyph), new FrameworkPropertyMetadata(18.0));
        IsHitTestVisibleProperty.OverrideMetadata(typeof(IconGlyph), new UIPropertyMetadata(false));
    }

    public Geometry? Data { get => (Geometry?)GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }
    public bool Filled { get => (bool)GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var data = Data;
        if (data == null) return;
        double side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;

        double s = side / Grid;
        dc.PushTransform(new TranslateTransform((ActualWidth - side) / 2, (ActualHeight - side) / 2));
        dc.PushTransform(new ScaleTransform(s, s));

        if (Filled || GetSolid(data))
        {
            dc.DrawGeometry(Foreground, null, data);
        }
        else
        {
            var pen = new Pen(Foreground, StrokeWidth / s)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            dc.DrawGeometry(null, pen, data);
        }

        dc.Pop();
        dc.Pop();
    }
}
