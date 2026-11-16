using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnowyNotch.Services;

public static class ColorExtractor
{
    public static readonly Color Fallback = Color.FromRgb(0xE8, 0xE8, 0xED);

    public static Color GetAccent(BitmapSource source)
    {
        try
        {
            const int size = 24;
            var scaled = new TransformedBitmap(source,
                new ScaleTransform(size / (double)source.PixelWidth, size / (double)source.PixelHeight));
            var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var px = new byte[stride * h];
            bgra.CopyPixels(px, stride, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double pb = px[i] / 255.0, pg = px[i + 1] / 255.0, pr = px[i + 2] / 255.0;
                ToHsv(pr, pg, pb, out _, out double s, out double v);
                double weight = s * s * v + 0.002;
                r += pr * weight; g += pg * weight; b += pb * weight;
                total += weight;
            }
            if (total <= 0) return Fallback;

            ToHsv(r / total, g / total, b / total, out double hue, out double sat, out double val);
            if (sat < 0.12) return Fallback;

            sat = Math.Min(1, sat * 1.25);
            val = Math.Max(val, 0.85);
            FromHsv(hue, sat, val, out double fr, out double fg, out double fb);
            return Color.FromRgb((byte)(fr * 255), (byte)(fg * 255), (byte)(fb * 255));
        }
        catch
        {
            return Fallback;
        }
    }

    private static void ToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) { h = 0; return; }
        if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * (((b - r) / d) + 2);
        else h = 60 * (((r - g) / d) + 4);
        if (h < 0) h += 360;
    }

    private static void FromHsv(double h, double s, double v, out double r, out double g, out double b)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;
        (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        r += m; g += m; b += m;
    }
}
