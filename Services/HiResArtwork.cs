using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SnowyNotch.Services;

/// <summary>
/// Обложка в высоком разрешении. Windows отдаёт плееру маленькую картинку (Spotify — 300×300, браузеры — ещё меньше),
/// и на большой обложке и во весь экран она мылится. Ищем тот же трек в каталоге iTunes и берём обложку 1000×1000.
/// Чтобы не подменить картинку чужой (другое издание, тёзка), найденную обложку сравниваем с исходной попиксельно
/// в малом размере — берём, только если это та же самая картинка.
/// Всё — в фоне; результат кешируется. Нет сети / не нашлось — остаётся исходная обложка.
/// </summary>
public static class HiResArtwork
{
    private const int Target = 1000;
    private static readonly HttpClient Http = CreateHttp();
    private static readonly Dictionary<string, BitmapSource?> Cache = new();
    private static readonly List<string> Recent = new(); // найденные обложки, от старых к новым
    private static readonly object Lock = new();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SnowyNotch/1.0");
        return http;
    }

    /// <summary>Стоит ли искать замену: картинка квадратная (обложка альбома, а не кадр видео) и мелкая.</summary>
    public static bool Worth(BitmapSource? local)
    {
        if (local == null || local.PixelWidth <= 0 || local.PixelHeight <= 0) return false;
        double aspect = local.PixelWidth / (double)local.PixelHeight;
        return aspect is > 0.92 and < 1.08 && Math.Max(local.PixelWidth, local.PixelHeight) < 800;
    }

    public static async Task<BitmapSource?> FindAsync(string title, string artist, BitmapSource local)
    {
        string key = $"{Norm(artist)}\u0001{Norm(title)}";
        lock (Lock) if (Cache.TryGetValue(key, out var known)) return known;

        BitmapSource? found;
        try { found = await Task.Run(() => SearchAsync(title, artist, local)); }
        catch { return null; } // нет сети или каталог недоступен — исходная обложка; не запоминаем, попробуем в следующий раз

        lock (Lock)
        {
            // Картинка 1000×1000 — около 4 МБ в памяти: храним несколько последних. «Не нашлось» почти ничего не весит.
            if (found != null)
            {
                Recent.Remove(key);
                Recent.Add(key);
                while (Recent.Count > 8)
                {
                    Cache.Remove(Recent[0]);
                    Recent.RemoveAt(0);
                }
            }
            else if (Cache.Count > 300) { Cache.Clear(); Recent.Clear(); }
            Cache[key] = found;
        }
        return found;
    }

    private static async Task<BitmapSource?> SearchAsync(string title, string artist, BitmapSource local)
    {
        var mine = Signature(local);
        if (mine == null) return null;

        // Сначала общий каталог, потом — каталог страны пользователя (местные исполнители бывают только там).
        var countries = new List<string> { "US" };
        try
        {
            string region = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            if (region.Length == 2 && region != "US") countries.Add(region);
        }
        catch { }

        var tried = new HashSet<string>();
        foreach (var country in countries)
        {
            foreach (var url in await CandidatesAsync(title, artist, country))
            {
                if (!tried.Add(url)) continue;
                // Сверяем по маленькой версии (100×100) — быстро; большую качаем, только если совпало.
                var small = ImageFx.Decode(await Http.GetByteArrayAsync(url));
                if (small == null || !Same(mine, Signature(small))) continue;
                string big = Regex.Replace(url, @"/\d+x\d+bb\.", $"/{Target}x{Target}bb.");
                var hi = ImageFx.Decode(await Http.GetByteArrayAsync(big), Target);
                if (hi != null && hi.PixelWidth > local.PixelWidth) return hi;
                return null;
            }
        }
        return null;
    }

    /// <summary>Ссылки на обложки (100×100) треков, похожих по названию и исполнителю; лучшие совпадения — первыми.</summary>
    private static async Task<List<string>> CandidatesAsync(string title, string artist, string country)
    {
        string term = Uri.EscapeDataString($"{artist} {Clean(title)}".Trim());
        string json = await Http.GetStringAsync($"https://itunes.apple.com/search?term={term}&media=music&entity=song&limit=10&country={country}");
        using var doc = JsonDocument.Parse(json);
        var list = new List<(string Url, int Score)>();
        string na = Norm(artist), nt = Norm(Clean(title));
        foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            string url = r.TryGetProperty("artworkUrl100", out var u) ? u.GetString() ?? "" : "";
            if (url.Length == 0) continue;
            string ra = Norm(r.TryGetProperty("artistName", out var a) ? a.GetString() ?? "" : "");
            string rt = Norm(Clean(r.TryGetProperty("trackName", out var t) ? t.GetString() ?? "" : ""));
            int score = (ra == na ? 2 : ra.Contains(na) || na.Contains(ra) ? 1 : 0) + (rt == nt ? 2 : rt.Contains(nt) || nt.Contains(rt) ? 1 : 0);
            if (score >= 2) list.Add((url, score));
        }
        return list.OrderByDescending(x => x.Score).Select(x => x.Url).Take(5).ToList();
    }

    // ---------- сравнение картинок ----------

    private const int Sig = 12;

    /// <summary>Картинка, ужатая до 12×12 (RGB), — «отпечаток» для сравнения.</summary>
    private static float[]? Signature(BitmapSource src)
    {
        try
        {
            var scaled = new TransformedBitmap(src, new ScaleTransform(Sig / (double)src.PixelWidth, Sig / (double)src.PixelHeight));
            var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var px = new byte[w * h * 4];
            bgra.CopyPixels(px, w * 4, 0);
            var sig = new float[w * h * 3];
            for (int i = 0, j = 0; i < px.Length; i += 4, j += 3)
            {
                sig[j] = px[i]; sig[j + 1] = px[i + 1]; sig[j + 2] = px[i + 2];
            }
            return sig;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Та же картинка: средняя разница по пикселям мала (сжатие и масштаб дают единицы, другая обложка — десятки).</summary>
    private static bool Same(float[]? a, float[]? b)
    {
        if (a == null || b == null || a.Length != b.Length) return false;
        double diff = 0;
        for (int i = 0; i < a.Length; i++) diff += Math.Abs(a[i] - b[i]);
        return diff / a.Length < 14;
    }

    // ---------- названия ----------

    /// <summary>Без пометок в скобках: «(feat. …)», «[Remastered]», «- Radio Edit».</summary>
    private static string Clean(string s)
    {
        s = Regex.Replace(s, @"\s*[\(\[][^\)\]]*[\)\]]", "");
        s = Regex.Replace(s, @"\s+-\s+.*(remaster|edit|version|mix|live|mono|stereo).*$", "", RegexOptions.IgnoreCase);
        return s.Trim();
    }

    private static string Norm(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
