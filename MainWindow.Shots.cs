#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SnowyNotch.Services;
using SnowyNotch.Widgets;

namespace SnowyNotch;

/// <summary>
/// Скриншоты для README (Debug, «SnowyNotch.exe --shots=папка»). Остров по очереди открывает экраны с демо-данными
/// (вымышленный трек и обложка, свой текст песни, демо-файлы), каждый кадр рендерится в PNG с прозрачным фоном
/// (raw\*.png), а потом кладётся на «обои» (*.png). Приложение закрывается само.
/// </summary>
public partial class MainWindow
{
    private const double ShotScale = 2;

    private static readonly string[] DemoLyrics =
    {
        "Streetlights hum beneath the snow",
        "Every window softly glows",
        "We leave our footprints in the quiet",
        "Till the morning lets them go",
        "Hold the moment, hold it slow",
        "Winter sings and then we know",
    };

    public async void RunShots(string dir, TrayService files, ToolsService tools)
    {
        string raw = Path.Combine(dir, "raw");
        try
        {
            Directory.CreateDirectory(raw);
            var cover = Cover(Color.FromRgb(0x1B, 0x2F, 0x7A), Color.FromRgb(0x6A, 0x3F, 0xC8), Color.FromRgb(0x5F, 0xD4, 0xFF), 7);
            OnMediaChanged(new MediaSnapshot("Snowfall Over Harbor", "Lumen Drift", cover, Color.FromRgb(0x7F, 0xD8, 0xFF),
                IsPlaying: true, Position: TimeSpan.FromSeconds(78), PositionAt: DateTimeOffset.Now, Duration: TimeSpan.FromSeconds(222),
                SourceAppId: "", CanShuffle: false, IsShuffle: false, CanRepeat: false,
                Repeat: Windows.Media.MediaPlaybackAutoRepeatMode.None));
            await Task.Delay(600);

            // «--shot-only=a,b» — снять только названные кадры (быстрее при доводке дизайна).
            string? onlyArg = Array.Find(Environment.GetCommandLineArgs(), a => a.StartsWith("--shot-only="));
            var only = onlyArg?["--shot-only=".Length..].Split(',');
            bool Wanted(string name) => only == null || Array.IndexOf(only, name) >= 0;

            var shots = new List<(string Name, BitmapSource Image)>();
            async Task Take(string name, Action show, int waitMs = 2400, Action? beforeRender = null)
            {
                if (!Wanted(name)) return;
                show();
                await Task.Delay(waitMs);
                beforeRender?.Invoke();
                if (beforeRender != null) await Task.Delay(120);
                var img = RenderIsland();
                Save(img, Path.Combine(raw, name + ".png"));
                shots.Add((name, img));
            }

            if (Wanted("icons")) Save(IconSheet(), Path.Combine(dir, "icons.png"));

            await Take("home", () => DebugPreview("home"));
            await Take("widgets-hover", () => DebugPreview("widgets"), beforeRender: () => Dock.DebugSettle(210));
            await Take("widgets-end", () => DebugPreview("widgets"), beforeRender: () => Dock.DebugSettle(null, 9999));
            // Кадры посреди анимаций: появление дока и раскрытие сервиса из иконки.
            await Take("anim-dock", () => { DebugPreview("home"); }, 900, () => DebugPreview("widgets"));
            await Take("anim-zoom", () => DebugPreview("widgets"), 1500, () => { _zoomOriginX = 0.2; OpenFocus("timer"); _peeking = true; UpdateState(); });
            await Take("artwork", () => DebugPreview("artwork"));
            await Take("lyrics", () =>
            {
                var lv = EnsureLyricsView();
                lv.Update(_snap);              // шапка: обложка, название, исполнитель
                lv.ShowDemo(_snap, DemoLyrics); // свой текст вместо поиска в сети
                DebugPreview("lyrics-shot");
            });
            await Take("widgets", () => DebugPreview("widgets"));
            await Take("calendar", () => { DebugPreview("focus:clock"); (_focusWidget as ClockWidget)?.OpenCalendar(); });

            tools.Timer.SetDuration(TimeSpan.FromMinutes(25));
            tools.Timer.Start();
            await Take("timer", () => DebugPreview("focus:timer"));

            files.Add(DemoFiles(Path.Combine(dir, "_files"), cover));
            await Take("tray", () => DebugPreview("tray"));
            await Take("settings", () => DebugPreview("look"));
            await Take("settings-general", () => { EnsureSettingsView().ShowSection("general"); DebugPreview("settings"); });
            await Take("settings-media", () => { EnsureSettingsView().ShowSection("media"); DebugPreview("settings"); });

            // Плеер во весь экран: каждый стиль, плюс классический на паузе (видно кнопку «play»).
            foreach (var (name, style, paused) in new[]
            {
                ("fullscreen", FullscreenStyle.Classic, false),
                ("fullscreen-cinema", FullscreenStyle.Cinematic, false),
                ("fullscreen-spotlight", FullscreenStyle.Spotlight, false),
                ("fullscreen-vinyl", FullscreenStyle.Vinyl, false),
                ("fullscreen-paused", FullscreenStyle.Classic, true),
                ("fullscreen-depth", FullscreenStyle.Depth, false),
                ("fullscreen-kinetic", FullscreenStyle.Kinetic, false),
                ("fullscreen-float", FullscreenStyle.Float, false),
                ("fullscreen-browse", FullscreenStyle.Classic, false),
                ("fullscreen-nolyrics", FullscreenStyle.Kinetic, false),
                ("fullscreen-jump", FullscreenStyle.Classic, false),
            })
            {
                if (!Wanted(name)) continue;
                _settings.FullscreenStyle = style;
                var player = DebugOpenFullscreen(DemoLyrics, 2);
                if (player == null) continue;
                player.DebugSetStyle(style);
                if (paused && _snap != null) player.Update(_snap with { IsPlaying = false });
                if (style == FullscreenStyle.Float)
                {
                    // Несколько строк подряд — чтобы было видно, как они расходятся по экрану.
                    for (int i = 0; i < 3; i++) { await Task.Delay(1300); player.DebugHighlight(i); }
                }
                if (name == "fullscreen-browse") { await Task.Delay(1500); player.DebugBrowse(-220); }
                // Прокрутили к концу текста и нажали последнюю строку — она должна встать на место звучащей.
                if (name == "fullscreen-jump") { await Task.Delay(1500); player.DebugBrowse(-240); await Task.Delay(700); player.DebugJump(5); }
                if (name == "fullscreen-nolyrics") { await Task.Delay(800); player.DebugNoLyrics(); player.SetOverlayActive(true); }
                if (name == "fullscreen-nolyrics")
                {
                    // Меню стилей с подсказкой «нет текста» — отдельным кадром (это всплывающее окно).
                    var menu = player.DebugOpenStyleMenu();
                    await Task.Delay(600);
                    var mb = new RenderTargetBitmap((int)(menu.ActualWidth * ShotScale), (int)(menu.ActualHeight * ShotScale),
                        96 * ShotScale, 96 * ShotScale, PixelFormats.Pbgra32);
                    mb.Render(menu);
                    Save(mb, Path.Combine(dir, "fullscreen-stylemenu.png"));
                }
                await Task.Delay(3400);
                var root = (FrameworkElement)player.Content;
                var rtb = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(root);
                Save(rtb, Path.Combine(dir, name + ".png"));
                player.Close();
                await Task.Delay(500);
            }

            // «--shot-only=perf»: замер полноэкранного плеера — кадры в секунду и процессор за 6 секунд на стиль.
            if (only != null && Array.IndexOf(only, "perf") >= 0)
            {
                var report = new System.Text.StringBuilder();
                foreach (var style in (Environment.GetEnvironmentVariable("SN_PERF_STYLES") ?? "Classic,Cinematic,Vinyl,Depth").Split(',').Select(Enum.Parse<FullscreenStyle>))
                {
                    _settings.FullscreenStyle = style;
                    var player = DebugOpenFullscreen(DemoLyrics, 2);
                    if (player == null) continue;
                    player.DebugSetStyle(style);
                    await Task.Delay(2000);
                    player.DebugDisable(Environment.GetEnvironmentVariable("SN_PERF_OFF") ?? "");
                    await Task.Delay(300);
                    int frames = 0;
                    EventHandler count = (_, _) => frames++;
                    var proc = System.Diagnostics.Process.GetCurrentProcess();
                    var cpu0 = proc.TotalProcessorTime;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    System.Windows.Media.CompositionTarget.Rendering += count;
                    await Task.Delay(6000);
                    System.Windows.Media.CompositionTarget.Rendering -= count;
                    proc.Refresh();
                    double sec = sw.Elapsed.TotalSeconds;
                    double cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds / sec / Environment.ProcessorCount * 100;
                    report.AppendLine($"{style,-10} {frames / sec,5:0.0} fps   CPU {cpu,5:0.0} %");
                    player.Close();
                    await Task.Delay(600);
                }
                File.WriteAllText(Path.Combine(dir, "perf.txt"), report.ToString());
            }

            // «Текст поверх экрана» — поверх «обоев», как над рабочим столом.
            if (Wanted("overlay"))
            {
                var ov = new Views.LyricsOverlay(_lyricsService, _settings);
                ov.Show();
                await Task.Delay(600);
                for (int i = 0; i < 3; i++) { ov.DebugShowLine(DemoLyrics[i]); await Task.Delay(1500); }
                await Task.Delay(1200);
                var fl = (FrameworkElement)ov.Content;
                int ow = (int)fl.ActualWidth, oh = (int)fl.ActualHeight;
                var layer = new RenderTargetBitmap(ow, oh, 96, 96, PixelFormats.Pbgra32);
                layer.Render(fl);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    Wallpaper(dc, ow, oh, 5, _ => false);
                    dc.DrawImage(layer, new Rect(0, 0, ow, oh));
                }
                var outImg = new RenderTargetBitmap(ow, oh, 96, 96, PixelFormats.Pbgra32);
                outImg.Render(dv);
                Save(outImg, Path.Combine(dir, "overlay.png"));
                ov.Close();
            }

            foreach (var (name, img) in shots) Save(Compose(img), Path.Combine(dir, name + ".png"));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
        }
        finally
        {
            try { Directory.Delete(Path.Combine(dir, "_files"), true); } catch { }
            Application.Current.Shutdown();
        }
    }

    /// <summary>Все иконки сервисов крупно и в размере дока — на тёмном фоне, как в острове.</summary>
    private static BitmapSource IconSheet()
    {
        var big = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(24, 28, 24, 20) };
        var small = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(24, 8, 24, 32) };
        foreach (var info in WidgetCatalog.All)
        {
            var a = info.Icon.Create(128);
            a.Margin = new Thickness(14);
            big.Children.Add(a);
            var b = info.Icon.Create(50);
            b.Margin = new Thickness(53, 14, 53, 14);
            small.Children.Add(b);
        }
        var root = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0D)),
            Child = new System.Windows.Controls.StackPanel { Children = { big, small } },
        };
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        var rtb = new RenderTargetBitmap((int)root.DesiredSize.Width, (int)root.DesiredSize.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Окно целиком в 2× и обрезка по непрозрачным пикселям.</summary>
    private BitmapSource RenderIsland()
    {
        var root = (FrameworkElement)Content;
        var rtb = new RenderTargetBitmap((int)(root.ActualWidth * ShotScale), (int)(root.ActualHeight * ShotScale),
            96 * ShotScale, 96 * ShotScale, PixelFormats.Pbgra32);
        rtb.Render(root);

        int w = rtb.PixelWidth, h = rtb.PixelHeight;
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4 + 3] > 6)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        if (maxX < 0) return rtb;
        var crop = new CroppedBitmap(rtb, new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1));
        var frozen = new WriteableBitmap(crop);
        frozen.Freeze();
        return frozen;
    }

    /// <summary>Кадр на «обоях» в стиле иконки: остров прижат к верхнему краю, как на экране.</summary>
    private static BitmapSource Compose(BitmapSource shot)
    {
        const int W = 2000;
        int sw = shot.PixelWidth, sh = shot.PixelHeight;
        int H = Math.Max(1000, sh + 420);
        double left = (W - sw) / 2.0;

        // Остров стеклянный: снежинки за ним просвечивают и выглядят как пыль, поэтому там их не рисуем.
        var alpha = new byte[sw * sh * 4];
        shot.CopyPixels(alpha, sw * 4, 0);
        bool Covered(Point p)
        {
            const int pad = 18;
            for (int dy = -pad; dy <= pad; dy += 6)
                for (int dx = -pad; dx <= pad; dx += 6)
                {
                    int x = (int)(p.X - left) + dx, y = (int)p.Y + dy;
                    if (x >= 0 && y >= 0 && x < sw && y < sh && alpha[(y * sw + x) * 4 + 3] > 8) return true;
                }
            return false;
        }

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, W, H), 36, 36));
            Wallpaper(dc, W, H, 11, Covered);
            dc.DrawImage(shot, new Rect(left, 0, sw, sh));
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static void Wallpaper(DrawingContext dc, double w, double h, int seed, Func<Point, bool> covered)
    {
        var bg = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x5C, 0x86, 0xD6), 0),
                new GradientStop(Color.FromRgb(0x2A, 0x4F, 0xA8), 0.38),
                new GradientStop(Color.FromRgb(0x24, 0x5E, 0x96), 0.7),
                new GradientStop(Color.FromRgb(0x2E, 0x7F, 0xA8), 1),
            },
        };
        dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));

        // фиолетовое свечение в правом верхнем углу, как на иконке
        dc.DrawEllipse(Glow(Color.FromRgb(0x7A, 0x52, 0xD0), 0.85), null, new Point(w * 0.92, h * 0.02), w * 0.45, h * 0.6);
        dc.DrawEllipse(Glow(Color.FromRgb(0x1E, 0x2E, 0x6E), 0.55), null, new Point(w * 0.1, h * 0.75), w * 0.4, h * 0.45);

        var rnd = new Random(seed);
        for (int i = 0; i < 70; i++)
        {
            double r = 2 + rnd.NextDouble() * 5;
            byte a = (byte)(70 + rnd.Next(140));
            var p = new Point(rnd.NextDouble() * w, rnd.NextDouble() * h * 0.82);
            if (covered(p)) continue;
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(a, 255, 255, 255)), null, p, r, r);
        }

        // сугробы
        var snowA = new RadialGradientBrush(Color.FromRgb(0xF6, 0xF8, 0xFC), Color.FromRgb(0xD5, 0xDF, 0xEE)) { GradientOrigin = new Point(0.4, 0.2) };
        var snowB = new RadialGradientBrush(Color.FromRgb(0xF2, 0xF6, 0xFF), Color.FromRgb(0xC9, 0xD8, 0xEE)) { GradientOrigin = new Point(0.5, 0.2) };
        dc.DrawEllipse(snowA, null, new Point(w * 0.22, h + h * 0.06), w * 0.48, h * 0.2);
        dc.DrawEllipse(snowB, new Pen(new SolidColorBrush(Color.FromArgb(0x60, 255, 255, 255)), 2), new Point(w * 0.74, h + h * 0.09), w * 0.5, h * 0.22);
    }

    private static Brush Glow(Color c, double opacity) =>
        new RadialGradientBrush(Color.FromArgb((byte)(255 * opacity), c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));

    /// <summary>Вымышленная обложка: ночное небо, свечение, снег и сугробы.</summary>
    private static BitmapSource Cover(Color top, Color bottom, Color glow, int seed)
    {
        const int S = 600;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(top, bottom, 70), null, new Rect(0, 0, S, S));
            dc.DrawEllipse(Glow(glow, 0.75), null, new Point(S * 0.7, S * 0.3), S * 0.55, S * 0.45);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(235, 0xFF, 0xF4, 0xD8)), null, new Point(S * 0.7, S * 0.3), S * 0.09, S * 0.09);
            var rnd = new Random(seed);
            for (int i = 0; i < 45; i++)
            {
                double r = 1.5 + rnd.NextDouble() * 4;
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(90 + rnd.Next(150)), 255, 255, 255)), null,
                    new Point(rnd.NextDouble() * S, rnd.NextDouble() * S * 0.75), r, r);
            }
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xE4, 0xEC, 0xFA)), null, new Point(S * 0.2, S * 1.08), S * 0.6, S * 0.32);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xF4, 0xF7, 0xFF)), null, new Point(S * 0.85, S * 1.12), S * 0.6, S * 0.34);
        }
        var rtb = new RenderTargetBitmap(S, S, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static string[] DemoFiles(string dir, BitmapSource cover)
    {
        Directory.CreateDirectory(dir);
        string art = Path.Combine(dir, "Cover art.png");
        Save(cover, art);
        string alt = Path.Combine(dir, "Moodboard.png");
        Save(Cover(Color.FromRgb(0x0E, 0x4D, 0x6E), Color.FromRgb(0x2D, 0x9C, 0xC9), Color.FromRgb(0xFF, 0xC8, 0x7A), 3), alt);
        string notes = Path.Combine(dir, "Release notes.md");
        File.WriteAllText(notes, "# SnowyNotch 0.3\n\n- Month calendar in the clock\n- New app icon\n- Faster island animations\n");
        string code = Path.Combine(dir, "Island.cs");
        File.WriteAllText(code, "public sealed class Island\n{\n    public void Expand() => Spring.To(Expanded);\n    public void Collapse() => Spring.To(Idle);\n}\n");
        string folder = Path.Combine(dir, "Screenshots");
        Directory.CreateDirectory(folder);
        return new[] { art, alt, notes, code, folder };
    }

    private static void Save(BitmapSource img, string path)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(img));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
#endif
