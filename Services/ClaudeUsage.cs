using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using SnowyNotch.Localization;

namespace SnowyNotch.Services;

/// <summary>
/// Лимиты подписки Claude (5 часов / неделя) — те же, что Claude Code показывает в /usage.
/// Берём вход Claude Code этого пользователя (~/.claude/.credentials.json) и спрашиваем только api.anthropic.com.
/// Токен никуда больше не уходит и не сохраняется; истёкший токен сами не обновляем — это делает Claude Code.
/// Включается отдельно в Настройки → Agents.
/// </summary>
public sealed class ClaudeUsage
{
    private const string Endpoint = "https://api.anthropic.com/api/oauth/usage";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(5) };
    private bool _enabled, _busy;

    public ClaudeUsage()
    {
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public static string ClaudeHome =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

    private static string CredentialsPath => Path.Combine(ClaudeHome, ".credentials.json");

    public AgentUsage Usage { get; } = new() { Agent = AgentKind.Claude, Error = L.T("Лимиты Claude выключены") };

    public event Action? Changed;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (value)
            {
                _timer.Start();
                _ = RefreshAsync();
            }
            else
            {
                _timer.Stop();
                Usage.FiveHour = Usage.Weekly = null;
                Usage.Error = L.T("Лимиты Claude выключены");
                Changed?.Invoke();
            }
        }
    }

    public async Task RefreshAsync()
    {
        if (!_enabled || _busy) return;
        _busy = true;
        try
        {
            var cred = ReadCredentials();
            if (cred == null)
            {
                Fail(L.T("Войдите в Claude Code (claude → /login)"));
                return;
            }
            Usage.Plan = PlanName(cred.Value.Subscription, cred.Value.Tier);
            if (cred.Value.ExpiresAt is { } exp && exp < DateTimeOffset.UtcNow)
            {
                Fail(L.T("Вход Claude Code истёк — откройте Claude Code, он обновит его сам"));
                return;
            }

            using var req = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cred.Value.Token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            req.Headers.UserAgent.ParseAdd("SnowyNotch");
            using var resp = await Http.SendAsync(req);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Fail(L.T("Claude не пустил — откройте Claude Code, чтобы обновить вход"));
                return;
            }
            if (!resp.IsSuccessStatusCode)
            {
                Fail(L.F("Не удалось получить лимиты ({0})", (int)resp.StatusCode));
                return;
            }

            string body = await resp.Content.ReadAsStringAsync();
            SaveLastResponse(body);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            ParseLimits(root);
            Usage.FiveHour ??= Window(root, "five_hour", 300);
            Usage.Weekly ??= Window(root, "seven_day", 10080);
            ParseBreakdown(root);
            Usage.UpdatedUtc = DateTime.UtcNow;
            Usage.Error = Usage.FiveHour == null && Usage.Weekly == null ? L.T("Для этого плана лимиты не отдаются") : null;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[claude-usage] {ex.Message}");
            Fail(L.T("Нет связи с Claude"));
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Основной источник — массив limits: [{kind:"session"|"weekly_all"|"weekly_scoped", percent, severity, resets_at,
    /// scope:{model:{display_name}}}]. Старые поля five_hour/seven_day — запасной вариант.
    /// </summary>
    private void ParseLimits(JsonElement root)
    {
        Usage.FiveHour = null;
        Usage.Weekly = null;
        Usage.Extra = new();
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array) return;

        foreach (var l in limits.EnumerateArray())
        {
            string kind = AgentTracker.Str(l, "kind");
            double used = l.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
            DateTime? resets = ParseTime(AgentTracker.Str(l, "resets_at"));
            string severity = AgentTracker.Str(l, "severity");
            string group = AgentTracker.Str(l, "group");

            switch (kind)
            {
                case "session":
                    Usage.FiveHour = new LimitWindow(used, 300, resets, severity);
                    break;
                case "weekly_all":
                    Usage.Weekly = new LimitWindow(used, 10080, resets, severity);
                    break;
                default:
                    string scope = ScopeName(l);
                    string label = (group == "weekly" ? L.T("Эта неделя") : group == "session" ? L.T("Текущая сессия") : L.T("Лимит"))
                                   + (scope.Length > 0 ? $" · {scope}" : "");
                    Usage.Extra.Add(new LimitWindow(used, group == "session" ? 300 : 10080, resets, severity, label));
                    break;
            }
        }
    }

    private static string ScopeName(JsonElement l)
    {
        if (!l.TryGetProperty("scope", out var s) || s.ValueKind != JsonValueKind.Object) return "";
        foreach (var key in new[] { "model", "surface" })
            if (s.TryGetProperty(key, out var m) && m.ValueKind == JsonValueKind.Object && AgentTracker.Str(m, "display_name") is { Length: > 0 } n)
                return n;
        return "";
    }

    /// <summary>seven_day_breakdown.rows: [{display_name, percent}] — доли недельного расхода.</summary>
    private void ParseBreakdown(JsonElement root)
    {
        Usage.Breakdown = new();
        if (!root.TryGetProperty("seven_day_breakdown", out var b) || b.ValueKind != JsonValueKind.Object) return;
        if (!b.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return;
        foreach (var r in rows.EnumerateArray())
        {
            string name = AgentTracker.Str(r, "display_name");
            double pct = r.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
            if (name.Length > 0 && pct > 0) Usage.Breakdown.Add((name, pct));
        }
    }

    private static DateTime? ParseTime(string s) =>
        DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;

    /// <summary>Последний ответ (только цифры лимитов, без токена) — для диагностики в %APPDATA%\SnowyNotch.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private static void SaveLastResponse(string body)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnowyNotch");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "claude-usage.json"), body);
        }
        catch { }
    }

    private void Fail(string message)
    {
        Usage.Error = message;
        Changed?.Invoke();
    }

    /// <summary>{"five_hour":{"utilization":41.0,"resets_at":"…"},"seven_day":{…}}.</summary>
    private static LimitWindow? Window(JsonElement root, string key, double minutes)
    {
        if (!root.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        double used = w.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetDouble() : 0;
        DateTime? resets = DateTime.TryParse(AgentTracker.Str(w, "resets_at"), null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var r)
            ? r
            : null;
        return new LimitWindow(used, minutes, resets);
    }

    private static (string Token, DateTimeOffset? ExpiresAt, string Subscription, string Tier)? ReadCredentials()
    {
        try
        {
            if (!File.Exists(CredentialsPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(CredentialsPath));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var o)) return null;
            string token = AgentTracker.Str(o, "accessToken");
            if (token.Length == 0) return null;
            DateTimeOffset? exp = o.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(e.GetInt64())
                : null;
            return (token, exp, AgentTracker.Str(o, "subscriptionType"), AgentTracker.Str(o, "rateLimitTier"));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[claude-usage] credentials: {ex.Message}");
            return null;
        }
    }

    /// <summary>«pro» → «Pro», «max» + «…_20x» → «Max 20x».</summary>
    private static string PlanName(string subscription, string tier)
    {
        if (subscription.Length == 0) return "";
        string name = char.ToUpperInvariant(subscription[0]) + subscription[1..];
        var m = System.Text.RegularExpressions.Regex.Match(tier, @"(\d+)x");
        return m.Success ? $"{name} {m.Groups[1].Value}x" : name;
    }
}
