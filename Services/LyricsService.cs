using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SnowyNotch.Services;

public sealed record LyricLine(TimeSpan Time, string Text);

public sealed record LyricsResult(IReadOnlyList<LyricLine> Lines, string? Plain)
{
    public static readonly LyricsResult None = new(Array.Empty<LyricLine>(), null);
    public bool IsSynced => Lines.Count > 0;
    public bool IsEmpty => Lines.Count == 0 && string.IsNullOrWhiteSpace(Plain);
}

public sealed class LyricsService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Regex Stamp = new(@"\[(\d{1,2}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);
    private static readonly Regex Offset = new(@"\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Noise = new(@"\s*[\(\[][^\)\]]*(official|video|audio|lyrics?|visualizer|mv|clip|клип|премьера|hd|4k|free\s*d(ownload|l)|out now|prod\.?|premiere)[^\)\]]*[\)\]]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const int MaxCached = 64;
    private readonly Dictionary<string, LyricsResult> _cache = new();
    private readonly Queue<string> _cacheOrder = new();

    public async Task<LyricsResult> GetAsync(string title, string artist, TimeSpan duration)
    {
        string key = $"{artist}\u0001{title}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var (cleanTitle, cleanArtist) = Clean(title, artist);
        LyricsResult result = LyricsResult.None;
        try
        {
            result = await Exact(cleanTitle, cleanArtist, duration);

            // SoundCloud и подобные: «Исполнитель - Трек» в названии, а в исполнителе — канал, который залил.
            string t = Noise.Replace(title, "").Trim();
            int dash = t.IndexOf(" - ", StringComparison.Ordinal);
            if (result.IsEmpty && dash > 0)
            {
                string altArtist = t[..dash].Trim(), altTitle = t[(dash + 3)..].Trim();
                if (!string.Equals(altArtist, cleanArtist, StringComparison.OrdinalIgnoreCase) || altTitle != cleanTitle)
                {
                    result = await Exact(altTitle, altArtist, duration);
                    if (!result.IsEmpty) (cleanTitle, cleanArtist) = (altTitle, altArtist);
                }
            }

            if (result.IsEmpty)
            {
                string q = Uri.EscapeDataString($"{cleanArtist} {cleanTitle}".Trim());
                using var resp = await Http.GetAsync($"https://lrclib.net/api/search?q={q}");
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    result = PickBest(doc.RootElement.EnumerateArray().ToList(), duration);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[lyrics] lookup failed: {ex.Message}");
            return LyricsResult.None;
        }

        // Кэш последних треков, а не всех за сессию: старейший уходит первым.
        if (_cache.TryAdd(key, result))
        {
            _cacheOrder.Enqueue(key);
            while (_cacheOrder.Count > MaxCached) _cache.Remove(_cacheOrder.Dequeue());
        }
        return result;
    }

    private static async Task<LyricsResult> Exact(string title, string artist, TimeSpan duration)
    {
        string url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
        if (duration > TimeSpan.Zero) url += $"&duration={(int)Math.Round(duration.TotalSeconds)}";
        using var resp = await Http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return LyricsResult.None;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return Parse(doc.RootElement);
    }

    /// <summary>
    /// Из результатов поиска — синхронный текст той же версии трека: у радио-версии, ремастера или клипа
    /// другое вступление, и все строки сдвинуты. Версию узнаём по длительности.
    /// </summary>
    private static LyricsResult PickBest(List<JsonElement> items, TimeSpan duration)
    {
        static bool Has(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String;
        double Diff(JsonElement e) =>
            duration > TimeSpan.Zero && e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                ? Math.Abs(d.GetDouble() - duration.TotalSeconds)
                : 0;

        var synced = items.Where(e => Has(e, "syncedLyrics")).OrderBy(Diff).ToList();
        if (synced.Count > 0 && Diff(synced[0]) <= 4) return Parse(synced[0]);

        // Синхронный текст есть только от версии с другой длительностью — строки будут мимо бита.
        // Лучше честно показать обычный текст без таймкодов.
        var plain = items.Where(e => Has(e, "plainLyrics")).OrderBy(Diff).FirstOrDefault();
        if (plain.ValueKind != JsonValueKind.Undefined)
            return new LyricsResult(Array.Empty<LyricLine>(), plain.GetProperty("plainLyrics").GetString());
        return synced.Count > 0 ? Parse(synced[0]) : LyricsResult.None;
    }

    private static LyricsResult Parse(JsonElement e)
    {
        string? synced = e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        string? plain = e.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var lines = synced != null ? ParseLrc(synced) : new List<LyricLine>();
        return new LyricsResult(lines, plain);
    }

    public static List<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        // [offset:+500] — сдвиг в мс; положительный значит «строки раньше».
        var off = Offset.Match(lrc);
        var shift = off.Success ? TimeSpan.FromMilliseconds(int.Parse(off.Groups[1].Value, CultureInfo.InvariantCulture)) : TimeSpan.Zero;
        foreach (var raw in lrc.Split('\n'))
        {
            var matches = Stamp.Matches(raw);
            if (matches.Count == 0) continue;
            string text = Stamp.Replace(raw, "").Trim();
            foreach (Match m in matches)
            {
                int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double sec = double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
                var t = TimeSpan.FromSeconds(min * 60 + sec) - shift;
                lines.Add(new LyricLine(t < TimeSpan.Zero ? TimeSpan.Zero : t, text));
            }
        }
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    private static (string Title, string Artist) Clean(string title, string artist)
    {
        string t = Noise.Replace(title, "").Trim();
        string a = artist.Trim();
        int dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            string left = t[..dash].Trim();
            bool channelIsNotArtist = a.Length == 0
                || a.EndsWith("VEVO", StringComparison.OrdinalIgnoreCase)
                || a.Contains("Topic", StringComparison.OrdinalIgnoreCase)
                || string.Equals(left, a, StringComparison.OrdinalIgnoreCase);
            if (channelIsNotArtist)
            {
                a = left;
                t = t[(dash + 3)..].Trim();
            }
        }
        int feat = t.IndexOf(" feat", StringComparison.OrdinalIgnoreCase);
        if (feat > 0) t = t[..feat].Trim();
        return (t, a.Replace(" - Topic", "", StringComparison.OrdinalIgnoreCase));
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SnowyNotch/0.4 (desktop lyrics view)");
        return c;
    }
}
