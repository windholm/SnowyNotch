using System;
using System.Windows;
using System.Windows.Media;
using SnowyNotch.Services;

namespace SnowyNotch.Animation;

public readonly record struct ShapeSize(double W, double H, double TopR, double BottomR, double Ear);

public sealed record IslandProfile(bool Attached, ShapeSize Idle, ShapeSize Compact, ShapeSize Expanded)
{
    public static IslandProfile For(IslandPreset preset) => preset switch
    {
        IslandPreset.MacBookNotch => new(
            Attached: true,
            Idle: new(190, 32, 0, 10, 6),
            Compact: new(290, 34, 0, 13, 7),
            Expanded: new(0, 0, 0, 26, 18)),

        // Режим «Остров»: компактная капсула поменьше, пузыри по бокам — чуть крупнее её высоты.
        _ => new(
            Attached: false,
            Idle: new(120, 30, 15, 15, 0),
            Compact: new(200, 34, 17, 17, 0),
            Expanded: new(0, 0, 30, 30, 0)),
    };

    /// <summary>Диаметр пузырей инструментов рядом с островом.</summary>
    public double BubbleSize => Attached ? Compact.H : Compact.H + 6;
}

public static class IslandGeometry
{
    public static Geometry Build(double w, double h, double topR, double bottomR, double ear, bool includeEars)
    {
        Normalize(ref w, ref h, ref topR, ref bottomR, ref ear);
        double x0 = includeEars ? ear : 0, x1 = x0 + w;

        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x0 + topR, 0), true, true);
            c.LineTo(new Point(x1 - topR, 0), true, false);
            if (topR > 0) c.ArcTo(new Point(x1, topR), new Size(topR, topR), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(x1, h - bottomR), true, false);
            if (bottomR > 0) c.ArcTo(new Point(x1 - bottomR, h), new Size(bottomR, bottomR), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(x0 + bottomR, h), true, false);
            if (bottomR > 0) c.ArcTo(new Point(x0, h - bottomR), new Size(bottomR, bottomR), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(x0, topR), true, false);
            if (topR > 0) c.ArcTo(new Point(x0 + topR, 0), new Size(topR, topR), 0, false, SweepDirection.Clockwise, true, false);

            if (includeEars && ear > 0.5)
            {
                c.BeginFigure(new Point(0, 0), true, true);
                c.LineTo(new Point(x0, 0), true, false);
                c.LineTo(new Point(x0, ear), true, false);
                c.ArcTo(new Point(0, 0), new Size(ear, ear), 0, false, SweepDirection.Counterclockwise, true, false);

                c.BeginFigure(new Point(x1 + ear, 0), true, true);
                c.LineTo(new Point(x1, 0), true, false);
                c.LineTo(new Point(x1, ear), true, false);
                c.ArcTo(new Point(x1 + ear, 0), new Size(ear, ear), 0, false, SweepDirection.Clockwise, true, false);
            }
        }
        g.Freeze();
        return g;
    }

    public static Geometry BuildOutline(double w, double h, double topR, double bottomR, double ear, bool attached)
    {
        Normalize(ref w, ref h, ref topR, ref bottomR, ref ear);
        if (!attached) return Build(w, h, topR, bottomR, 0, includeEars: false);

        double x0 = ear, x1 = ear + w;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(0, 0.5), false, false);
            if (ear > 0.5) c.ArcTo(new Point(x0, ear), new Size(ear, ear), 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(x0, h - bottomR), true, false);
            if (bottomR > 0) c.ArcTo(new Point(x0 + bottomR, h), new Size(bottomR, bottomR), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(x1 - bottomR, h), true, false);
            if (bottomR > 0) c.ArcTo(new Point(x1, h - bottomR), new Size(bottomR, bottomR), 0, false, SweepDirection.Counterclockwise, true, false);
            c.LineTo(new Point(x1, ear), true, false);
            if (ear > 0.5) c.ArcTo(new Point(x1 + ear, 0.5), new Size(ear, ear), 0, false, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }

    private static void Normalize(ref double w, ref double h, ref double topR, ref double bottomR, ref double ear)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        double half = Math.Min(w, h) / 2;
        topR = Math.Clamp(topR, 0, half);
        bottomR = Math.Clamp(bottomR, 0, half);
        ear = Math.Clamp(ear, 0, h / 2);
    }
}
