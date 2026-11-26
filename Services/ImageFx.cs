using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnowyNotch.Services;

/// <summary>
/// Обработка картинок вне потока интерфейса: декодирование обложек и «запечённые» размытие и тени.
/// Зачем: WPF-эффекты (BlurEffect, DropShadowEffect) пересчитываются на каждом кадре, если рядом хоть что-то движется.
/// В полноэкранном плеере фон плывёт постоянно, и размытие радиусом 160 на весь экран съедало кадры.
/// Здесь то же самое считается один раз, а на экран идёт обычная картинка.
/// Ядро совпадает с WPF (замерено): Гаусс с σ = радиус / 3, обрезанный на радиусе.
/// </summary>
public static class ImageFx
{
    /// <summary>Декодировать картинку в её родном размере (большие — уменьшить до <paramref name="maxSide"/>). Потокобезопасно.</summary>
    public static BitmapSource? Decode(byte[] data, int maxSide = 1200)
    {
        try
        {
            int w = 0, h = 0;
            using (var probe = new MemoryStream(data, writable: false))
            {
                var frame = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
                w = frame.PixelWidth;
                h = frame.PixelHeight;
            }
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            if (w > maxSide || h > maxSide)
            {
                if (w >= h) bmp.DecodePixelWidth = maxSide;
                else bmp.DecodePixelHeight = maxSide;
            }
            bmp.StreamSource = new MemoryStream(data, writable: false);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Только размеры картинки — без декодирования пикселей.</summary>
    public static (int W, int H)? Measure(byte[] data)
    {
        try
        {
            using var ms = new MemoryStream(data, writable: false);
            var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return null;
        }
    }

    // ======================= размытый фон =======================

    /// <summary>
    /// Картинка, которая выглядит как <paramref name="src"/> с BlurEffect радиуса <paramref name="radius"/>, растянутая
    /// (UniformToFill) на элемент размера <paramref name="target"/>. Считается в малом разрешении — после размытия
    /// мелких деталей нет, и растяжение на экран незаметно.
    /// </summary>
    public static BitmapSource BlurredFill(BitmapSource src, double radius, Size target)
    {
        double sw = src.PixelWidth, sh = src.PixelHeight;
        double fill = Math.Max(target.Width / sw, target.Height / sh); // экранных точек на пиксель исходника
        double shownW = sw * fill, shownH = sh * fill;

        // Рабочее разрешение: σ ≈ 3.5 рабочих пикселя (плавно при растяжении), но не больше 640 по стороне.
        double perWork = Math.Max(1, radius / 3 / 3.5);
        double k = Math.Min(1, 640 / Math.Max(shownW / perWork, shownH / perWork));
        int ww = Math.Max(8, (int)Math.Round(shownW / perWork * k));
        int wh = Math.Max(8, (int)Math.Round(shownH / perWork * k));
        double sigma = radius / 3 * (ww / shownW);

        var scaled = new TransformedBitmap(src, new ScaleTransform(ww / sw, wh / sh));
        var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Pbgra32, null, 0);
        ww = bgra.PixelWidth;
        wh = bgra.PixelHeight;
        var px = new byte[ww * wh * 4];
        bgra.CopyPixels(px, ww * 4, 0);

        var f = new float[px.Length];
        for (int i = 0; i < px.Length; i++) f[i] = px[i];
        Gaussian(f, ww, wh, 4, sigma, radius * (ww / shownW));
        for (int i = 0; i < px.Length; i++) px[i] = (byte)Math.Clamp((int)(f[i] + 0.5f), 0, 255);

        var result = BitmapSource.Create(ww, wh, 96, 96, PixelFormats.Pbgra32, null, px, ww * 4);
        result.Freeze();
        return result;
    }

    public static Task<BitmapSource> BlurredFillAsync(BitmapSource src, double radius, Size target) =>
        Task.Run(() => BlurredFill(src, radius, target));

    // ======================= тени =======================

    /// <summary>
    /// Тень как у DropShadowEffect (чёрная, без смещения — его задаёт тот, кто ставит картинку) под скруглённым
    /// прямоугольником или кругом размера <paramref name="w"/>×<paramref name="h"/>. Картинка шире фигуры на
    /// <see cref="ShadowPad"/> с каждой стороны. <paramref name="scale"/> — разрешение (тень мягкая, 0.5 хватает).
    /// </summary>
    public static BitmapSource Shadow(double w, double h, double cornerRadius, bool circle, double blurRadius, double opacity, double scale = 0.5)
    {
        double pad = ShadowPad(blurRadius);
        int pw = Math.Max(1, (int)Math.Ceiling((w + pad * 2) * scale));
        int ph = Math.Max(1, (int)Math.Ceiling((h + pad * 2) * scale));
        var a = new float[pw * ph];

        // Фигура: сглаженная маска по расстоянию до контура (в пикселях картинки).
        double cx = pw / 2.0, cy = ph / 2.0;
        double hx = w * scale / 2, hy = h * scale / 2;
        double r = circle ? Math.Min(hx, hy) : Math.Min(cornerRadius * scale, Math.Min(hx, hy));
        for (int y = 0; y < ph; y++)
            for (int x = 0; x < pw; x++)
            {
                double dx = Math.Abs(x + 0.5 - cx), dy = Math.Abs(y + 0.5 - cy);
                double d;
                if (circle) d = Math.Sqrt(dx * dx + dy * dy) - r;
                else
                {
                    double qx = dx - (hx - r), qy = dy - (hy - r);
                    d = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - r;
                }
                a[y * pw + x] = (float)Math.Clamp(0.5 - d, 0, 1);
            }

        Gaussian(a, pw, ph, 1, blurRadius / 3 * scale, blurRadius * scale);

        var px = new byte[pw * ph * 4];
        for (int i = 0; i < a.Length; i++)
            px[i * 4 + 3] = (byte)Math.Clamp((int)(a[i] * opacity * 255 + 0.5), 0, 255); // чёрный: цвет в Pbgra = 0
        var bmp = BitmapSource.Create(pw, ph, 96, 96, PixelFormats.Pbgra32, null, px, pw * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>На сколько тень выходит за фигуру (ядро WPF обрезано на радиусе).</summary>
    public static double ShadowPad(double blurRadius) => Math.Ceiling(blurRadius) + 2;

    public static Task<BitmapSource> ShadowAsync(double w, double h, double cornerRadius, bool circle, double blurRadius, double opacity) =>
        Task.Run(() => Shadow(w, h, cornerRadius, circle, blurRadius, opacity));

    // ======================= Гаусс =======================

    /// <summary>Раздельный Гаусс с обрезкой ядра на <paramref name="extent"/>; края продолжаются крайним пикселем.</summary>
    private static void Gaussian(float[] data, int w, int h, int channels, double sigma, double extent)
    {
        if (sigma < 0.3) return;
        int r = Math.Max(1, (int)Math.Ceiling(Math.Min(extent, sigma * 3.2)));
        var kernel = new float[r * 2 + 1];
        double sum = 0;
        for (int i = -r; i <= r; i++) sum += kernel[i + r] = (float)Math.Exp(-i * i / (2 * sigma * sigma));
        for (int i = 0; i < kernel.Length; i++) kernel[i] /= (float)sum;

        var tmp = new float[data.Length];
        // По строкам.
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
                for (int c = 0; c < channels; c++)
                {
                    float acc = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        int xx = Math.Clamp(x + k, 0, w - 1);
                        acc += data[(row + xx) * channels + c] * kernel[k + r];
                    }
                    tmp[(row + x) * channels + c] = acc;
                }
        });
        // По столбцам.
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
                for (int c = 0; c < channels; c++)
                {
                    float acc = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        int yy = Math.Clamp(y + k, 0, h - 1);
                        acc += tmp[(yy * w + x) * channels + c] * kernel[k + r];
                    }
                    data[(y * w + x) * channels + c] = acc;
                }
        });
    }
}
