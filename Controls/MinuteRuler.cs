using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SnowyNotch.Localization;

namespace SnowyNotch.Controls;

public sealed class MinuteRuler : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(MinuteRuler),
        new FrameworkPropertyMetadata(25.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
        nameof(Tint), typeof(Color), typeof(MinuteRuler),
        new FrameworkPropertyMetadata(Color.FromRgb(0xFF, 0x9F, 0x0A), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(MinuteRuler),
        new FrameworkPropertyMetadata(90.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double PxPerMinute = 9;
    private const double Min = 1;
    private double Max => Maximum;

    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    private static string Label(int m) =>
        m < 60 ? m.ToString(CultureInfo.InvariantCulture) : m % 60 == 0 ? L.F("{0} ч", m / 60) : $"{m / 60}:{m % 60:00}";

    private bool _dragging;
    private double _dragStartX, _dragStartValue;

    public MinuteRuler()
    {
        Cursor = Cursors.SizeWE;
        Height = 40;
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, Math.Clamp(value, Min, Max)); }
    public Color Tint { get => (Color)GetValue(TintProperty); set => SetValue(TintProperty, value); }

    public event Action<int>? Committed;

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        double cx = w / 2, v = Value;
        double tickBottom = h - 9;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        int first = (int)Math.Floor(v - cx / PxPerMinute) - 1;
        int last = (int)Math.Ceiling(v + cx / PxPerMinute) + 1;
        for (int m = Math.Max((int)Min, first); m <= Math.Min((int)Max, last); m++)
        {
            double x = cx + (m - v) * PxPerMinute;
            double edgeFade = Math.Clamp(Math.Min(x, w - x) / 40, 0, 1);
            bool major = m % 5 == 0;
            bool passed = m <= v + 0.01;

            var c = passed ? Tint : Colors.White;
            byte a = (byte)(255 * edgeFade * (passed ? 0.95 : 0.35) * (major ? 1 : 0.7));
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B)), major ? 1.6 : 1.1);
            double len = major ? 12 : 7;
            dc.DrawLine(pen, new Point(x, tickBottom - len), new Point(x, tickBottom));

            if (major)
            {
                bool selected = Math.Abs(m - v) < 0.5;
                var text = new FormattedText(Label(m), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"),
                        FontStyles.Normal, selected ? FontWeights.Bold : FontWeights.SemiBold, FontStretches.Normal),
                    11, new SolidColorBrush(Color.FromArgb((byte)(255 * edgeFade * (selected ? 1 : passed ? 0.75 : 0.45)),
                        selected ? (byte)255 : c.R, selected ? (byte)255 : c.G, selected ? (byte)255 : c.B)), dpi);
                dc.DrawText(text, new Point(x - text.Width / 2, tickBottom - len - text.Height - 1));
            }
        }

        var tri = new StreamGeometry();
        using (var g = tri.Open())
        {
            g.BeginFigure(new Point(cx, h - 7), true, true);
            g.LineTo(new Point(cx - 4, h - 1), true, false);
            g.LineTo(new Point(cx + 4, h - 1), true, false);
        }
        tri.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Tint), null, tri);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled) return;
        _dragging = true;
        _dragStartX = e.GetPosition(this).X;
        _dragStartValue = Value;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        double dx = e.GetPosition(this).X - _dragStartX;
        Value = _dragStartValue - dx / PxPerMinute;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        double dx = e.GetPosition(this).X - _dragStartX;
        if (Math.Abs(dx) < 3)
        {
            Value = _dragStartValue + (e.GetPosition(this).X - ActualWidth / 2) / PxPerMinute;
        }
        Commit();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!IsEnabled) return;
        Value = Math.Round(Value) + (e.Delta > 0 ? 1 : -1);
        Commit();
        e.Handled = true;
    }

    private void Commit()
    {
        Value = Math.Round(Value);
        Committed?.Invoke((int)Value);
    }
}
