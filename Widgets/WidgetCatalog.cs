using System.Linq;
using System.Windows.Media;
using SnowyNotch.Localization;

namespace SnowyNotch.Widgets;

/// <summary>Как выглядит иконка сервиса на панели: глиф, градиент плитки и градиент глифа.</summary>
public sealed record AppIconStyle(string Glyph, Color Top, Color Bottom, Color GlyphTop, Color GlyphBottom)
{
    private static readonly Color White = Colors.White;
    private static readonly Color Pearl = Color.FromRgb(0xEE, 0xEE, 0xF4);
    private static readonly Color DarkTop = Color.FromRgb(0x3C, 0x3C, 0x41);
    private static readonly Color DarkBottom = Color.FromRgb(0x1A, 0x1A, 0x1D);

    /// <summary>Цветная плитка, белый глиф.</summary>
    public static AppIconStyle Bright(string glyph, Color top, Color bottom) => new(glyph, top, bottom, White, Pearl);

    /// <summary>Тёмная плитка, цветной глиф.</summary>
    public static AppIconStyle Dark(string glyph, Color glyphTop, Color glyphBottom) => new(glyph, DarkTop, DarkBottom, glyphTop, glyphBottom);
}

public sealed record WidgetInfo(string Id, string Title, string IconKey, Color Tint, AppIconStyle Icon);

public static class WidgetCatalog
{
    private static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static readonly WidgetInfo[] All =
    {
        new("media", L.T("Музыка"), "IconMusic", C(0xFF375F), AppIconStyle.Bright("AppGlyphMusic", C(0xFF7092), C(0xE2134A))),
        new("audio", L.T("Звук"), "IconSpeakerHigh", C(0x0A84FF), AppIconStyle.Bright("AppGlyphSpeaker", C(0x4FB2FF), C(0x0057D9))),
        new("clock", L.T("Время"), "IconClock", C(0x5E5CE6), AppIconStyle.Bright("AppGlyphClock", C(0x8C8AFF), C(0x3936C9))),
        new("timer", L.T("Таймер"), "IconTimer", C(0xFF9F0A), AppIconStyle.Bright("AppGlyphTimer", C(0xFFC14D), C(0xF57300))),
        new("stopwatch", L.T("Секундомер"), "IconStopwatch", C(0x32ADE6), AppIconStyle.Bright("AppGlyphStopwatch", C(0x5EE3F5), C(0x0B91C9))),
        new("system", L.T("Система"), "IconCpu", C(0x30D158), AppIconStyle.Dark("AppGlyphGauge", C(0x8AF5A6), C(0x24B84C))),
        new("notes", L.T("Заметки"), "IconNote", C(0xF5B700), AppIconStyle.Dark("AppGlyphNotes", C(0xFFE57A), C(0xFFB300))),
        new("agents", "Agents", "IconAgents", C(0x8B7CF6), AppIconStyle.Dark("AppGlyphAgents", C(0xD2C6FF), C(0x7B5BFF))),
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
