using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnowyNotch.Services;

/// <summary>
/// Превью для плиток трея: настоящие миниатюры Windows (страница PDF, кадр видео, иконка папки в высоком
/// разрешении) и собственные мини-превью для кода и текста — «страница», на которой видно сами строки.
/// </summary>
public static class TrayThumbnails
{
    // ---------- Windows Shell: IShellItemImageFactory ----------

    [Flags]
    private enum Siigbf
    {
        ResizeToFit = 0x0,
        BiggerSizeOk = 0x1,
        IconOnly = 0x4,
        ThumbnailOnly = 0x8,
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, Siigbf flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr h, int c, out DIBSECTION ds);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr h);

    /// <summary>Миниатюра (thumbnailOnly) или иконка (iconOnly) от Проводника; null, если не вышло.</summary>
    public static BitmapSource? Shell(string path, int size, bool thumbnailOnly = false, bool iconOnly = false)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            var flags = Siigbf.BiggerSizeOk | (thumbnailOnly ? Siigbf.ThumbnailOnly : 0) | (iconOnly ? Siigbf.IconOnly : 0);
            int hr = factory.GetImage(new NativeSize { cx = size, cy = size }, flags, out hbm);
            Marshal.ReleaseComObject(factory);
            return hr == 0 && hbm != IntPtr.Zero ? FromHBitmap(hbm) : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[tray] shell image failed for {path}: {ex.Message}");
            return null;
        }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
        }
    }

    /// <summary>HBITMAP → BitmapSource с сохранением прозрачности (Imaging.CreateBitmapSourceFromHBitmap её теряет).</summary>
    private static BitmapSource? FromHBitmap(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), out var ds) == 0) return null;
        var bm = ds.dsBm;
        if (bm.bmBits == IntPtr.Zero || bm.bmBitsPixel != 32 || bm.bmWidth <= 0 || bm.bmHeight <= 0) return null;

        int w = bm.bmWidth, h = bm.bmHeight, stride = bm.bmWidthBytes;
        var src = new byte[stride * h];
        Marshal.Copy(bm.bmBits, src, 0, src.Length);

        // DIB по умолчанию хранится снизу вверх.
        bool bottomUp = ds.dsBmih.biHeight > 0;
        var buf = new byte[w * 4 * h];
        bool anyAlpha = false;
        for (int y = 0; y < h; y++)
        {
            int sy = bottomUp ? h - 1 - y : y;
            Buffer.BlockCopy(src, sy * stride, buf, y * w * 4, w * 4);
        }
        for (int i = 3; i < buf.Length; i += 4) if (buf[i] != 0) { anyAlpha = true; break; }

        var bmp = BitmapSource.Create(w, h, 96, 96, anyAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, buf, w * 4);
        bmp.Freeze();
        return bmp;
    }

    // ---------- Картинки ----------

    public static BitmapSource? Image(string path, int decodeWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[tray] image thumbnail failed: {ex.Message}");
            return null;
        }
    }

    // ---------- Мини-превью кода и текста ----------

    public const double PageW = 58, PageH = 74;
    private const double Scale = 3;

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "using", "namespace", "class", "struct", "record", "interface", "enum", "public", "private", "protected", "internal",
        "static", "readonly", "const", "void", "var", "let", "new", "return", "if", "else", "for", "foreach", "while", "do",
        "switch", "case", "break", "continue", "true", "false", "null", "this", "base", "async", "await", "import", "from",
        "export", "default", "function", "def", "self", "None", "True", "False", "and", "or", "not", "in", "is", "fn", "pub",
        "mut", "impl", "use", "package", "func", "type", "int", "string", "bool", "double", "float", "long", "char", "object",
        "try", "catch", "finally", "throw", "override", "virtual", "abstract", "sealed", "partial", "get", "set", "with",
        "lambda", "yield", "elif", "pass", "raise", "extends", "implements", "echo", "then", "fi", "param", "select", "where",
    };

    private static readonly Regex Token = new(
        @"(?<c>//.*|/\*.*|--\s.*)|(?<h>#.*)|(?<s>""(?:\\.|[^""\\])*""?|'(?:\\.|[^'\\])*'?|`[^`]*`?)|(?<n>\b\d+(?:\.\d+)?\b)|(?<w>[A-Za-z_][\w]*)|(?<o>.)",
        RegexOptions.Compiled);

    private static readonly HashSet<string> HashComments = new(StringComparer.OrdinalIgnoreCase)
    { ".py", ".sh", ".ps1", ".psm1", ".rb", ".yaml", ".yml", ".toml", ".r", ".pl", ".conf", ".ini", ".gitignore", ".dockerfile" };

    /// <summary>«Страница» с первыми строками файла: тёмная как редактор для кода, светлая для текста.</summary>
    public static ImageSource? TextPage(string path, bool code, Color badge)
    {
        try
        {
            var lines = ReadHead(path, 17, 44);
            string ext = Path.GetExtension(path);
            bool hashComments = HashComments.Contains(ext);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(Scale, Scale));
                var page = new Rect(0, 0, PageW, PageH);
                dc.DrawRoundedRectangle(
                    new SolidColorBrush(code ? Color.FromRgb(0x1E, 0x1F, 0x24) : Color.FromRgb(0xFA, 0xFA, 0xF7)),
                    new Pen(new SolidColorBrush(code ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x24, 0, 0, 0)), 0.4),
                    page, 5, 5);

                var face = new Typeface(new FontFamily("Cascadia Mono, Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                const double size = 3.3, lineH = 3.95, left = 4, top = 4.5;
                double maxW = PageW - left - 3;

                dc.PushClip(new RectangleGeometry(new Rect(2, 2, PageW - 4, PageH - 4), 4, 4));
                for (int i = 0; i < lines.Count; i++)
                {
                    double y = top + i * lineH;
                    if (y > PageH - 6) break;
                    if (code) DrawCodeLine(dc, lines[i], left, y, size, face, hashComments, maxW);
                    else
                    {
                        var ft = Text(lines[i], i == 0 ? bold : face, i == 0 ? size * 1.15 : size,
                            i == 0 ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0x55, 0x55, 0x5A));
                        ft.MaxTextWidth = maxW;
                        ft.MaxLineCount = 1;
                        ft.Trimming = TextTrimming.None;
                        dc.DrawText(ft, new Point(left, y));
                    }
                }
                dc.Pop();

                // Метка расширения внизу страницы.
                string label = ext.TrimStart('.').ToUpperInvariant();
                if (label.Length is > 0 and <= 6)
                {
                    var lt = Text(label, bold, 5.2, Colors.White);
                    double pw = lt.Width + 6, ph = 8;
                    var pill = new Rect((PageW - pw) / 2, PageH - ph - 4, pw, ph);
                    dc.DrawRoundedRectangle(new SolidColorBrush(badge), null, pill, 3, 3);
                    dc.DrawText(lt, new Point(pill.X + 3, pill.Y + (ph - lt.Height) / 2));
                }
                dc.Pop();
            }

            var rtb = new RenderTargetBitmap((int)(PageW * Scale), (int)(PageH * Scale), 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[tray] text preview failed: {ex.Message}");
            return null;
        }
    }

    private static void DrawCodeLine(DrawingContext dc, string line, double x, double y, double size, Typeface face, bool hashComments, double maxW)
    {
        double start = x;
        foreach (Match m in Token.Matches(line))
        {
            if (x - start > maxW) break;
            Color c;
            if (m.Groups["c"].Success) c = Color.FromRgb(0x6A, 0x99, 0x55);
            else if (m.Groups["h"].Success) c = hashComments ? Color.FromRgb(0x6A, 0x99, 0x55) : Color.FromRgb(0xC5, 0x86, 0xC0);
            else if (m.Groups["s"].Success) c = Color.FromRgb(0xCE, 0x91, 0x78);
            else if (m.Groups["n"].Success) c = Color.FromRgb(0xB5, 0xCE, 0xA8);
            else if (m.Groups["w"].Success)
                c = Keywords.Contains(m.Value) ? Color.FromRgb(0x56, 0x9C, 0xD6)
                    : char.IsUpper(m.Value[0]) ? Color.FromRgb(0x4E, 0xC9, 0xB0)
                    : Color.FromRgb(0xD4, 0xD4, 0xD4);
            else c = Color.FromRgb(0xB0, 0xB0, 0xB0);

            if (m.Groups["c"].Success || (m.Groups["h"].Success && hashComments))
            {
                var rest = Text(line[m.Index..], face, size, c);
                rest.MaxTextWidth = Math.Max(1, maxW - (x - start));
                rest.MaxLineCount = 1;
                rest.Trimming = TextTrimming.None;
                dc.DrawText(rest, new Point(x, y));
                return;
            }
            var ft = Text(m.Value, face, size, c);
            dc.DrawText(ft, new Point(x, y));
            x += ft.WidthIncludingTrailingWhitespace;
        }
    }

    private static FormattedText Text(string s, Typeface face, double size, Color color) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, new SolidColorBrush(color), 1.0);

    private static List<string> ReadHead(string path, int maxLines, int maxChars)
    {
        var result = new List<string>();
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var buf = new char[8192];
        int n = reader.ReadBlock(buf, 0, buf.Length);
        foreach (var raw in new string(buf, 0, n).Split('\n'))
        {
            if (result.Count >= maxLines) break;
            var l = raw.TrimEnd('\r').Replace("\t", "    ");
            result.Add(l.Length > maxChars ? l[..maxChars] : l);
        }
        return result;
    }
}
