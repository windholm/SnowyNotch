using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SnowyNotch.Controls;
using SnowyNotch.Interop;
using SnowyNotch.Services;
using SnowyNotch.Widgets;

namespace SnowyNotch.Views;

/// <summary>
/// «Текст поверх экрана»: прозрачное окно на весь монитор, сквозь которое проходят клики. Строки песни
/// возникают в разных местах экрана с разными анимациями, пока вы работаете за компьютером.
/// Окно не берёт фокус и не мешает; включается и выключается из острова, трея или сочетанием клавиш.
/// </summary>
public sealed class LyricsOverlay : Window, IMediaView
{
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(120);

    private readonly LyricsService _lyrics;
    private readonly AppSettings _settings;
    private readonly FloatingLyrics _float = new() { Shadowed = true, SafeArea = new Thickness(0.05, 0.13, 0.05, 0.1) };
    private MediaSnapshot? _snap;
    private string _trackKey = "";
    private int _loadVersion;
    private IReadOnlyList<LyricLine> _lines = Array.Empty<LyricLine>();
    private int _current = -2;
    private bool _closing;

    public LyricsOverlay(LyricsService lyrics, AppSettings settings)
    {
        _lyrics = lyrics;
        _settings = settings;
        Title = "SnowyNotch lyrics";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        Content = _float;
        Opacity = 0;

        var area = SystemParameters.WorkArea;
        Left = area.Left; Top = area.Top; Width = area.Width; Height = area.Height;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.MakeOverlay(hwnd);       // без активации и без кнопки на панели задач
            NativeMethods.SetClickThrough(hwnd, true); // клики проходят насквозь
        };
        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(400)));
            CompositionTarget.Rendering += OnFrame;
        };
        Closed += (_, _) => CompositionTarget.Rendering -= OnFrame;
    }

    public void CloseAnimated()
    {
        if (_closing) return;
        _closing = true;
        _float.Clear();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(450));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    public void ApplySettings() { }

    public void Update(MediaSnapshot? snap)
    {
        _snap = snap;
        if (snap == null) return;
        string key = $"{snap.Artist}\u0001{snap.Title}";
        if (key == _trackKey) return;
        _trackKey = key;
        _ = LoadAsync(snap);
    }

    private async System.Threading.Tasks.Task LoadAsync(MediaSnapshot snap)
    {
        int version = ++_loadVersion;
        _lines = Array.Empty<LyricLine>();
        _current = -2;
        _float.Clear();
        // Новый трек — на мгновение название и исполнитель.
        _float.ShowLine(snap.Artist.Length > 0 ? $"♫  {snap.Title} — {snap.Artist}" : $"♫  {snap.Title}");
        if (!_settings.LyricsEnabled) return;
        LyricsResult result;
        try { result = await _lyrics.GetAsync(snap.Title, snap.Artist, snap.Duration); }
        catch { result = LyricsResult.None; }
        if (version != _loadVersion || !result.IsSynced) return;
        _lines = result.Lines;
        Tick();
    }

    private void OnFrame(object? sender, EventArgs e) => Tick();

#if DEBUG
    /// <summary>Для скриншотов: показать строку без загрузки текста.</summary>
    public void DebugShowLine(string text) => _float.ShowLine(text);
#endif

    public void Tick()
    {
        if (_snap == null || _lines.Count == 0) return;
        var p = _snap.CurrentPosition + Lead;
        int idx = -1;
        for (int i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].Time <= p) idx = i;
            else break;
        }
        if (idx == _current) return;
        _current = idx;
        if (idx >= 0) _float.ShowLine(_lines[idx].Text);
    }
}
