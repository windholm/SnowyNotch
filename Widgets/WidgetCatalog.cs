using System.Linq;
using System.Windows.Media;
using SnowyNotch.Controls;
using SnowyNotch.Localization;

namespace SnowyNotch.Widgets;

/// <summary>Как выглядит иконка сервиса: двухтонный металлический глиф на графитовой плитке (см. <see cref="AppIcon"/>).</summary>
public sealed record AppIconStyle(Geometry Main, Geometry? Tone)
{
    /// <summary>Готовая иконка нужного размера.</summary>
    public AppIcon Create(double size, bool glow = true) => new()
    {
        Width = size, Height = size, Glyph = Main, Tone = Tone, Glow = glow,
    };
}

public sealed record WidgetInfo(string Id, string Title, string IconKey, Color Tint, AppIconStyle Icon);

public static class WidgetCatalog
{
    private static Color C(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // Самые частые сервисы (музыка, таймер, заметки) стоят первыми. Tint — цвет плиток на главной.
    public static readonly WidgetInfo[] All =
    {
        new("media", L.T("Музыка"), "IconMusic", C(0xFF4B84), new(ServiceGlyphs.MusicMain, null)),
        new("timer", L.T("Таймер"), "IconTimer", C(0xFF9B21), new(ServiceGlyphs.TimerMain, ServiceGlyphs.TimerTone)),
        new("notes", L.T("Заметки"), "IconNote", C(0xF7BC0E), new(ServiceGlyphs.NotesMain, ServiceGlyphs.NotesTone)),
        new("system", L.T("Система"), "IconCpu", C(0x34C766), new(ServiceGlyphs.SystemMain, ServiceGlyphs.SystemTone)),
        new("stopwatch", L.T("Секундомер"), "IconStopwatch", C(0x22BFC9), new(ServiceGlyphs.StopwatchMain, ServiceGlyphs.StopwatchTone)),
        new("audio", L.T("Звук"), "IconSpeakerHigh", C(0x2A8BFF), new(ServiceGlyphs.SoundMain, ServiceGlyphs.SoundTone)),
        new("clock", L.T("Время"), "IconClock", C(0x6C63F2), new(ServiceGlyphs.ClockMain, ServiceGlyphs.ClockTone)),
        new("agents", "Agents", "IconAgents", C(0xA862F7), new(ServiceGlyphs.AgentsMain, ServiceGlyphs.AgentsTone)),
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
