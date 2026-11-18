using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SnowyNotch.Controls;

/// <summary>
/// Двухтонные глифы сервисов (сетка 24×24): Main — сплошная часть, Tone — полупрозрачная подложка.
/// Рисуются «пером»: линии и дуги с круглыми концами превращаются в заливаемые контуры, поэтому у всех
/// иконок одинаковая толщина штриха и скругления, как у единого набора.
/// </summary>
public static class ServiceGlyphs
{
    public static readonly Geometry MusicMain = Union(
        Head(7.0, 17.3), Head(16.6, 15.0),
        Stroke("M8.9 6.6 V17 M18.5 4.3 V14.8", 1.7),
        Soft("M8.05 6.3 L19.35 3.7 V6.9 L8.05 9.5 Z", 0.5));

    public static readonly Geometry TimerMain = Union(
        Stroke("M7.2 3.8 H16.8 M7.2 20.2 H16.8", 2.0),
        Stroke("M8.6 4 V6.9 C8.6 9.4 11.3 10.6 11.3 12 C11.3 13.4 8.6 14.6 8.6 17.1 V20 " +
               "M15.4 4 V6.9 C15.4 9.4 12.7 10.6 12.7 12 C12.7 13.4 15.4 14.6 15.4 17.1 V20", 1.6),
        Fill("M9.9 19.4 C9.9 17.5 11.2 16.6 12 15.7 C12.8 16.6 14.1 17.5 14.1 19.4 Z"));
    public static readonly Geometry TimerTone = Fill(
        "M8.6 4 H15.4 V6.9 C15.4 9.4 12.7 10.6 12.7 12 C12.7 13.4 15.4 14.6 15.4 17.1 V20 H8.6 V17.1 " +
        "C8.6 14.6 11.3 13.4 11.3 12 C11.3 10.6 8.6 9.4 8.6 6.9 Z");

    public static readonly Geometry NotesMain = Union(
        Stroke("M8.5 11.3 H15.5 M8.5 14.4 H15.5 M8.5 17.5 H12.5", 1.5),
        Fill("M14.2 3.4 L18.8 8 H15.5 A1.3 1.3 0 0 1 14.2 6.7 Z"));
    public static readonly Geometry NotesTone = Soft(
        "M7.4 3.6 H14.2 L18.6 8 V18.4 A2 2 0 0 1 16.6 20.4 H7.4 A2 2 0 0 1 5.4 18.4 V5.6 A2 2 0 0 1 7.4 3.6 Z", 0.8);

    public static readonly Geometry SystemMain = Union(
        Stroke(Arc(12, 14, 8.2, 160, 292), 2.5),
        Stroke("M12 14 L14.4 8.2", 2.0),
        Circle(12, 14, 2.1));
    public static readonly Geometry SystemTone = Stroke(Arc(12, 14, 8.2, 160, 380), 2.5);

    public static readonly Geometry StopwatchMain = Union(
        Stroke(Ring(12, 13.7, 7.1), 1.9),
        Stroke("M10 2.9 H14 M12 3 V6 M17.9 6.9 L19 5.8", 1.9),
        Stroke("M12 13.7 V9.7", 1.9));
    public static readonly Geometry StopwatchTone = Circle(12, 13.7, 7.1);

    public static readonly Geometry SoundMain = Union(
        Soft("M4.2 9.8 H7.2 L11.4 6.3 V17.7 L7.2 14.2 H4.2 Z", 1.3),
        Stroke(Arc(11.2, 12, 4.3, -48, 48), 1.9));
    public static readonly Geometry SoundTone = Stroke(Arc(11.2, 12, 8.0, -48, 48), 1.9);

    public static readonly Geometry ClockMain = Union(
        Stroke(Ring(12, 12, 8.3), 1.9),
        Stroke("M12 7.4 V12 L15.2 13.9", 1.9));
    public static readonly Geometry ClockTone = Circle(12, 12, 8.3);

    public static readonly Geometry AgentsMain = Union(Sparkle(10.2, 11.4, 7.6), Sparkle(18.3, 17.8, 3.3));
    public static readonly Geometry AgentsTone = Sparkle(18.1, 5.7, 2.5);

    // ---------- перо ----------

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static Geometry Fill(string data)
    {
        var g = Geometry.Parse(data).Clone();
        g.Freeze();
        return g;
    }

    /// <summary>Линия заданной толщины с круглыми концами и стыками → заливаемый контур.</summary>
    private static Geometry Stroke(string data, double width)
    {
        var pen = new Pen(Brushes.Black, width)
        {
            StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
        };
        var g = Geometry.Parse(data).GetWidenedPathGeometry(pen, 0.01, ToleranceType.Absolute)
            .GetOutlinedPathGeometry(0.01, ToleranceType.Absolute);
        g.Freeze();
        return g;
    }

    /// <summary>Залитая фигура со скруглёнными углами (заливка + обводка той же фигуры).</summary>
    private static Geometry Soft(string data, double round) => Union(Fill(data), Stroke(data, round));

    private static Geometry Union(params Geometry[] parts)
    {
        Geometry acc = parts[0];
        for (int i = 1; i < parts.Length; i++)
            acc = Geometry.Combine(acc, parts[i], GeometryCombineMode.Union, null, 0.01, ToleranceType.Absolute);
        acc.Freeze();
        return acc;
    }

    private static Geometry Circle(double cx, double cy, double r)
    {
        var g = new EllipseGeometry(new Point(cx, cy), r, r).GetFlattenedPathGeometry(0.01, ToleranceType.Absolute);
        g.Freeze();
        return g;
    }

    private static string Ring(double cx, double cy, double r) =>
        $"M{N(cx)} {N(cy - r)} A{N(r)} {N(r)} 0 1 1 {N(cx - 0.01)} {N(cy - r)} Z";

    /// <summary>Дуга по часовой стрелке; углы в градусах от оси X (экранные координаты, Y вниз).</summary>
    private static string Arc(double cx, double cy, double r, double fromDeg, double toDeg)
    {
        double a0 = fromDeg * Math.PI / 180, a1 = toDeg * Math.PI / 180;
        var p0 = new Point(cx + r * Math.Cos(a0), cy + r * Math.Sin(a0));
        var p1 = new Point(cx + r * Math.Cos(a1), cy + r * Math.Sin(a1));
        int large = toDeg - fromDeg > 180 ? 1 : 0;
        return $"M{N(p0.X)} {N(p0.Y)} A{N(r)} {N(r)} 0 {large} 1 {N(p1.X)} {N(p1.Y)}";
    }

    /// <summary>Нотная головка: наклонённый эллипс.</summary>
    private static Geometry Head(double cx, double cy)
    {
        var e = new EllipseGeometry(new Point(cx, cy), 2.8, 2.15) { Transform = new RotateTransform(-20, cx, cy) };
        var g = e.GetFlattenedPathGeometry(0.01, ToleranceType.Absolute);
        g.Freeze();
        return g;
    }

    /// <summary>Четырёхлучевая искра с вогнутыми сторонами.</summary>
    private static Geometry Sparkle(double cx, double cy, double r)
    {
        double p = r * 0.17;
        string d = $"M{N(cx)} {N(cy - r)} Q{N(cx + p)} {N(cy - p)} {N(cx + r)} {N(cy)} " +
                   $"Q{N(cx + p)} {N(cy + p)} {N(cx)} {N(cy + r)} " +
                   $"Q{N(cx - p)} {N(cy + p)} {N(cx - r)} {N(cy)} " +
                   $"Q{N(cx - p)} {N(cy - p)} {N(cx)} {N(cy - r)} Z";
        return Soft(d, r * 0.16);
    }
}
