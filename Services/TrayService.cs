using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using SnowyNotch.Localization;

namespace SnowyNotch.Services;

public enum TrayKind { Image, Folder, Code, Text, Document }

public sealed class TrayItem : Observable
{
    private bool _isSelected, _selecting;

    public TrayItem(string path, TrayKind kind, ImageSource? thumb, double width, double height, double radius, bool isIcon)
    {
        Path = path;
        Kind = kind;
        Thumb = thumb;
        ThumbWidth = width;
        ThumbHeight = height;
        ThumbRadius = new CornerRadius(radius);
        IsIcon = isIcon;
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
    public TrayKind Kind { get; }

    /// <summary>Превью: миниатюра, иконка Проводника или нарисованная «страница» кода/текста.</summary>
    public ImageSource? Thumb { get; }
    public double ThumbWidth { get; }
    public double ThumbHeight { get; }
    public CornerRadius ThumbRadius { get; }
    /// <summary>Это иконка со своей формой (папка, программа) — без тени и обрезки углов.</summary>
    public bool IsIcon { get; }
    public double ShadowOpacity => IsIcon ? 0 : 0.45;

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; Raise(); }
    }

    /// <summary>Трей в режиме выбора — на плитке видна отметка.</summary>
    public bool Selecting
    {
        get => _selecting;
        set { if (_selecting == value) return; _selecting = value; Raise(); }
    }
}

public sealed class TrayService : Observable
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".heic" };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".txt", ".md", ".log", ".csv", ".rtf", ".markdown", ".text" };

    /// <summary>Код → цвет метки расширения на мини-превью.</summary>
    private static readonly Dictionary<string, Color> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = Color.FromRgb(0x9B, 0x4F, 0xD6), [".csx"] = Color.FromRgb(0x9B, 0x4F, 0xD6),
        [".xaml"] = Color.FromRgb(0x51, 0x2B, 0xD4), [".xml"] = Color.FromRgb(0xE3, 0x7B, 0x33), [".csproj"] = Color.FromRgb(0x51, 0x2B, 0xD4),
        [".sln"] = Color.FromRgb(0x51, 0x2B, 0xD4), [".json"] = Color.FromRgb(0xC9, 0xA2, 0x1C),
        [".js"] = Color.FromRgb(0xD4, 0xB8, 0x30), [".mjs"] = Color.FromRgb(0xD4, 0xB8, 0x30), [".jsx"] = Color.FromRgb(0x3B, 0xA9, 0xD9),
        [".ts"] = Color.FromRgb(0x31, 0x78, 0xC6), [".tsx"] = Color.FromRgb(0x31, 0x78, 0xC6),
        [".py"] = Color.FromRgb(0x35, 0x72, 0xA5), [".java"] = Color.FromRgb(0xB0, 0x72, 0x19), [".kt"] = Color.FromRgb(0xA9, 0x7B, 0xFF),
        [".c"] = Color.FromRgb(0x55, 0x5D, 0xA8), [".h"] = Color.FromRgb(0x55, 0x5D, 0xA8), [".cpp"] = Color.FromRgb(0xF3, 0x4B, 0x7D),
        [".hpp"] = Color.FromRgb(0xF3, 0x4B, 0x7D), [".go"] = Color.FromRgb(0x00, 0xAD, 0xD8), [".rs"] = Color.FromRgb(0xDE, 0xA5, 0x84),
        [".rb"] = Color.FromRgb(0xCC, 0x34, 0x2D), [".php"] = Color.FromRgb(0x77, 0x7B, 0xB3), [".swift"] = Color.FromRgb(0xF0, 0x51, 0x38),
        [".html"] = Color.FromRgb(0xE3, 0x4C, 0x26), [".htm"] = Color.FromRgb(0xE3, 0x4C, 0x26), [".css"] = Color.FromRgb(0x56, 0x3D, 0x7C),
        [".scss"] = Color.FromRgb(0xC6, 0x53, 0x8C), [".sql"] = Color.FromRgb(0xE3, 0x8C, 0x00), [".sh"] = Color.FromRgb(0x4E, 0xAA, 0x25),
        [".ps1"] = Color.FromRgb(0x01, 0x24, 0x56), [".psm1"] = Color.FromRgb(0x01, 0x24, 0x56), [".bat"] = Color.FromRgb(0x4E, 0xAA, 0x25),
        [".cmd"] = Color.FromRgb(0x4E, 0xAA, 0x25), [".yaml"] = Color.FromRgb(0xCB, 0x17, 0x1E), [".yml"] = Color.FromRgb(0xCB, 0x17, 0x1E),
        [".toml"] = Color.FromRgb(0x9C, 0x42, 0x21), [".ini"] = Color.FromRgb(0x6D, 0x80, 0x86), [".lua"] = Color.FromRgb(0x00, 0x00, 0x80),
        [".dart"] = Color.FromRgb(0x00, 0xB4, 0xAB), [".vue"] = Color.FromRgb(0x41, 0xB8, 0x83), [".svelte"] = Color.FromRgb(0xFF, 0x3E, 0x00),
        [".gitignore"] = Color.FromRgb(0xF0, 0x50, 0x33), [".editorconfig"] = Color.FromRgb(0x6D, 0x80, 0x86),
    };

    // Рамка, в которую вписывается превью на плитке.
    private const double BoxW = 88, BoxH = 70, IconSize = 66;

    private bool _selecting;

    public ObservableCollection<TrayItem> Items { get; } = new();

    public bool Selecting
    {
        get => _selecting;
        set
        {
            if (_selecting == value) return;
            _selecting = value;
            foreach (var i in Items)
            {
                i.Selecting = value;
                if (!value) i.IsSelected = false;
            }
            Raise();
        }
    }

    public IReadOnlyList<TrayItem> Selected => Items.Where(i => i.IsSelected).ToList();

    public void Add(string[] paths)
    {
        foreach (var p in paths)
        {
            if (Items.Any(i => string.Equals(i.Path, p, StringComparison.OrdinalIgnoreCase))) continue;
            if (!File.Exists(p) && !Directory.Exists(p)) continue;
            var item = Create(p);
            item.Selecting = _selecting;
            Items.Add(item);
        }
    }

    private static TrayItem Create(string path)
    {
        string ext = Path.GetExtension(path);

        if (Directory.Exists(path))
            return Icon(path, TrayKind.Folder, TrayThumbnails.Shell(path, 256, iconOnly: true));

        if (ImageExtensions.Contains(ext) && TrayThumbnails.Image(path, 264) is { } img)
            return Fitted(path, TrayKind.Image, img, img.Width, img.Height);

        if (CodeExtensions.TryGetValue(ext, out var badge) || IsDotfile(path))
        {
            if (TrayThumbnails.TextPage(path, code: true, CodeExtensions.TryGetValue(ext, out var b) ? b : Color.FromRgb(0x6D, 0x80, 0x86)) is { } page)
                return Page(path, TrayKind.Code, page);
        }
        else if (TextExtensions.Contains(ext) && TrayThumbnails.TextPage(path, code: false, Color.FromRgb(0x8E, 0x8E, 0x93)) is { } page)
            return Page(path, TrayKind.Text, page);

        // Документы, видео и всё остальное: настоящая миниатюра от Проводника (страница PDF, кадр видео),
        // а если её нет — иконка программы в высоком разрешении.
        if (TrayThumbnails.Shell(path, 256, thumbnailOnly: true) is { } thumb)
            return Fitted(path, TrayKind.Document, thumb, thumb.PixelWidth, thumb.PixelHeight);
        return Icon(path, TrayKind.Document, TrayThumbnails.Shell(path, 256, iconOnly: true));
    }

    private static bool IsDotfile(string path) => Path.GetFileName(path) is { Length: > 1 } n && n[0] == '.' && !n[1..].Contains('.');

    private static TrayItem Fitted(string path, TrayKind kind, ImageSource src, double w, double h)
    {
        double k = Math.Min(BoxW / Math.Max(1, w), BoxH / Math.Max(1, h));
        double tw = Math.Round(w * k), th = Math.Round(h * k);
        bool portrait = th > tw;
        return new TrayItem(path, kind, src, tw, th, portrait ? 5 : 10, isIcon: false);
    }

    private static TrayItem Page(string path, TrayKind kind, ImageSource page) =>
        new(path, kind, page, TrayThumbnails.PageW, TrayThumbnails.PageH, 5, isIcon: false);

    private static TrayItem Icon(string path, TrayKind kind, ImageSource? icon) =>
        new(path, kind, icon, IconSize, IconSize, 0, isIcon: true);

    private static readonly string TextFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnowyNotch", "Tray");

    public bool AddText(string text)
    {
        try
        {
            Directory.CreateDirectory(TextFolder);
            string first = new string(text.Trim().Split('\n')[0].Take(32).ToArray()).Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) first = first.Replace(c, ' ');
            if (first.Length == 0) first = L.T("Текст");
            string path = Path.Combine(TextFolder, $"{first}.txt");
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(TextFolder, $"{first} ({i}).txt");
            File.WriteAllText(path, text);
            Add(new[] { path });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[tray] text drop failed: {ex.Message}");
            return false;
        }
    }

    public void Remove(TrayItem item) => Items.Remove(item);

    public void RemoveSelected()
    {
        foreach (var i in Selected) Items.Remove(i);
    }

    public void Clear() => Items.Clear();
}
