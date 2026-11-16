using System.Linq;
using SnowyNotch.Localization;

namespace SnowyNotch.Services;

/// <summary>
/// Что из системных медиасессий Windows считать музыкой.
/// Музыкальные приложения — всегда; видеоплееры — никогда; браузеры — только если играет похоже на музыку
/// (у музыкальных сайтов — SoundCloud, YouTube Music, Spotify Web, Яндекс Музыка, VK, Bandcamp… — квадратная обложка
/// или указан альбом; у YouTube и прочего видео превью 16:9 и альбома нет). Адрес вкладки Windows не сообщает,
/// поэтому судим по метаданным, которые сайт отдаёт в Media Session.
/// </summary>
public static class MusicSources
{
    public enum Kind { Music, Browser, Video, Other }

    // Ключ ищется в SourceAppUserModelId (без учёта регистра).
    private static readonly (string Key, string Name)[] MusicApps =
    {
        ("spotify", "Spotify"), ("applemusic", "Apple Music"), ("itunes", "iTunes"), ("zunemusic", L.T("Медиаплеер")),
        ("yandex.music", "Яндекс Музыка"), ("yandexmusic", "Яндекс Музыка"), ("youtube-music", "YouTube Music"),
        ("youtubemusic", "YouTube Music"), ("deezer", "Deezer"), ("tidal", "TIDAL"), ("amazonmusic", "Amazon Music"),
        ("amazon music", "Amazon Music"), ("soundcloud", "SoundCloud"), ("vkmusic", "VK Музыка"), ("boom", "BOOM"),
        ("foobar2000", "foobar2000"), ("musicbee", "MusicBee"), ("aimp", "AIMP"), ("winamp", "Winamp"),
        ("mediamonkey", "MediaMonkey"), ("dopamine", "Dopamine"), ("cider", "Cider"), ("plexamp", "Plexamp"),
        ("qobuz", "Qobuz"), ("audacious", "Audacious"), ("clementine", "Clementine"), ("strawberry", "Strawberry"),
        ("audirvana", "Audirvana"), ("roon", "Roon"), ("napster", "Napster"), ("pandora", "Pandora"),
        ("nuclear", "Nuclear"), ("feishin", "Feishin"), ("sonixd", "Sonixd"), ("tauon", "Tauon"), ("quodlibet", "Quod Libet"),
        ("musicolet", "Musicolet"), ("zvuk", "Звук"), ("anghami", "Anghami"),
    };

    private static readonly string[] VideoApps =
    {
        "vlc", "potplayer", "mpc-hc", "mpc-be", "mpv", "netflix", "zunevideo", "kmplayer", "stremio", "twitch",
        "primevideo", "disney", "hbomax", "kinopoisk", "smplayer", "gomplayer", "kodi",
        // Мессенджеры и созвоны: голосовые и видео — не музыка (а квадратная аватарка похожа на обложку).
        "telegram", "ayugram", "kotatogram", "64gram", "unigram", "discord", "whatsapp", "viber", "signal",
        "slack", "teams", "zoom", "skype", "element", "vesktop", "legcord",
    };

    private static readonly string[] Browsers =
    {
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "yandex", "thebrowsercompany", "arc.exe",
        "chromium", "thorium", "librewolf", "waterfox", "floorp", "zen.exe", "mullvad", "iexplore",
    };

    public static Kind Classify(string appId)
    {
        string id = appId.ToLowerInvariant();
        if (VideoApps.Any(id.Contains)) return Kind.Video;
        if (MusicApps.Any(m => id.Contains(m.Key))) return Kind.Music;
        if (Browsers.Any(id.Contains)) return Kind.Browser;
        // Firefox (и его сборки) представляется хэшем пути установки, например 308046B0AF4A39CB.
        if (System.Text.RegularExpressions.Regex.IsMatch(appId, "^[0-9A-Fa-f]{16}$")) return Kind.Browser;
        return Kind.Other;
    }

    /// <summary>Человеческое имя музыкального приложения (для браузеров — null).</summary>
    public static string? AppName(string appId)
    {
        string id = appId.ToLowerInvariant();
        return MusicApps.FirstOrDefault(m => id.Contains(m.Key)).Name;
    }

    /// <summary>Решение по метаданным трека. <paramref name="artAspect"/> — ширина/высота обложки (null — обложки нет).</summary>
    public static bool IsMusic(Kind kind, bool isVideoType, bool isMusicType, string artist, string album, double? artAspect) => kind switch
    {
        Kind.Video => false,
        _ when isVideoType => false,
        Kind.Music => true,
        // Браузер и неизвестные приложения: альбом или квадратная обложка — это музыка; превью 16:9 или пусто — видео.
        // Пометке «Music» не верим: браузеры ставят её на любой звук, включая YouTube.
        _ => artist.Length > 0 && (album.Length > 0 || artAspect is >= 0.85 and <= 1.18),
    };
}
