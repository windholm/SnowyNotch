using System.Linq;
using System.Windows.Media;
using SnowyNotch.Controls;
using SnowyNotch.Localization;

namespace SnowyNotch.Widgets;

/// <summary>Как выглядит иконка сервиса: двухтонный глиф и диагональный градиент плитки.</summary>
public sealed record AppIconStyle(Geometry Main, Geometry? Tone, Color Top, Color Bottom)
{
    /// <summary>Цвет сплошной части глифа (по умолчанию белый).</summary>
    public Color Glyph { get; init; } = Colors.White;

    /// <summary>Цвет и непрозрачность подложки глифа.</summary>
    public Color ToneColor { get; init; } = Colors.White;
    public double ToneOpacity { get; init; } = 0.42;

    /// <summary>Готовая иконка нужного размера.</summary>
    public AppIcon Create(double size, bool glow = true) => new()
    {
        Width = size, Height = size,
        Glyph = Main, Tone = Tone, Top = Top, Bottom = Bottom,
        GlyphTop = Glyph, GlyphBottom = Glyph, ToneColor = ToneColor, ToneOpacity = ToneOpacity,
        ShadowColor = glow ? Bottom : Colors.Transparent,
    };
}

public sealed record WidgetInfo(string Id, string Title, string IconKey, Color Tint, AppIconStyle Icon);

public static class WidgetCatalog
{
    private static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // Порядок — по цветовому кругу (розовый → оранжевый → жёлтый → зелёный → бирюзовый → синий → индиго → фиолетовый):
    // в одной строке сервисы читаются как плавная радуга, а самые частые (музыка, таймер, заметки) стоят первыми.
    public static readonly WidgetInfo[] All =
    {
        new("media", L.T("Музыка"), "IconMusic", C(0xFF4B84),
            new(ServiceGlyphs.MusicMain, null, C(0xFF86AB), C(0xFF2468))),
        new("timer", L.T("Таймер"), "IconTimer", C(0xFF9B21),
            new(ServiceGlyphs.TimerMain, ServiceGlyphs.TimerTone, C(0xFFC062), C(0xFF7A0A))),
        new("notes", L.T("Заметки"), "IconNote", C(0xF7BC0E),
            new(ServiceGlyphs.NotesMain, ServiceGlyphs.NotesTone, C(0xFFE873), C(0xFFB703)) { Glyph = C(0xE68A00), ToneOpacity = 0.97 }),
        new("system", L.T("Система"), "IconCpu", C(0x34C766),
            new(ServiceGlyphs.SystemMain, ServiceGlyphs.SystemTone, C(0x7EF2A2), C(0x14AE4D))),
        new("stopwatch", L.T("Секундомер"), "IconStopwatch", C(0x22BFC9),
            new(ServiceGlyphs.StopwatchMain, ServiceGlyphs.StopwatchTone, C(0x74EBE2), C(0x0CA3B5))),
        new("audio", L.T("Звук"), "IconSpeakerHigh", C(0x2A8BFF),
            new(ServiceGlyphs.SoundMain, ServiceGlyphs.SoundTone, C(0x74BEFF), C(0x0A66F5))),
        new("clock", L.T("Время"), "IconClock", C(0x6C63F2),
            new(ServiceGlyphs.ClockMain, ServiceGlyphs.ClockTone, C(0xA3A0FF), C(0x4A3CE6))),
        new("agents", "Agents", "IconAgents", C(0xA862F7),
            new(ServiceGlyphs.AgentsMain, ServiceGlyphs.AgentsTone, C(0xD6A2FF), C(0x8638F2))),
    };

    public static WidgetInfo? Find(string id) => All.FirstOrDefault(w => w.Id == id);
}

public interface IIslandWidget
{
    void SetActive(bool active);
}

/// <summary>Сервис со своими внутренними страницами: «назад» сначала листает их, потом выходит к панели сервисов.</summary>
public interface IBackNavigable
{
    /// <returns>true — шаг назад выполнен внутри сервиса.</returns>
    bool TryGoBack();
}

public interface IMediaView
{
    void Update(Services.MediaSnapshot? snap);
    void Tick();
    void ApplySettings();
}
