using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using SnowyNotch.Localization;

namespace SnowyNotch.Services;

/// <summary>
/// Codex CLI пишет каждую сессию в ~/.codex/sessions/ГГГГ/ММ/ДД/rollout-*.jsonl (запрос, команды, apply_patch,
/// ответы, лимиты). Читаем хвосты свежих журналов и переводим в шаги/статусы <see cref="AgentTracker"/>,
/// а из событий token_count берём лимиты (5 часов / неделя) — без сети и без токенов.
/// Конец хода точно приходит через notify (<see cref="HandleNotify"/>); без него — по паузе после ответа.
/// </summary>
public sealed class CodexWatcher
{
    private sealed class FileState
    {
        public long Offset;
        public string Partial = "";
        public string SessionId = "";
        public string Cwd = "";
        public DateTime LastEvent = DateTime.Now;
        public bool AnswerPending;
        public string LastAnswer = "";
    }

    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan DoneAfterAnswer = TimeSpan.FromSeconds(4);

    private readonly AgentTracker _tracker;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);
    private bool _enabled;

    public CodexWatcher(AgentTracker tracker)
    {
        _tracker = tracker;
        _poll.Tick += (_, _) => Poll();
    }

    public static string Home =>
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h
            ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    private static string SessionsDir => Path.Combine(Home, "sessions");

    /// <summary>Последние известные лимиты Codex.</summary>
    public AgentUsage Usage { get; } = new() { Agent = AgentKind.Codex };

    public event Action? UsageChanged;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (value)
            {
                LoadLastUsage();
                _poll.Start();
            }
            else
            {
                _poll.Stop();
                foreach (var st in _files.Values) if (st.SessionId.Length > 0) _tracker.End(st.SessionId);
                _files.Clear();
            }
        }
    }

    // ---------- notify: конец хода ----------

    /// <summary>Payload от notify: {"type":"agent-turn-complete","thread-id"?,"turn-id","cwd"?,"input-messages":[…],"last-assistant-message":…}.</summary>
    public void HandleNotify(JsonElement e)
    {
        if (!_enabled || AgentTracker.Str(e, "type") != "agent-turn-complete") return;
        string thread = AgentTracker.Str(e, "thread-id");
        string cwd = AgentTracker.Str(e, "cwd");
        var s = (thread.Length > 0 ? _tracker.Find("codex:" + thread) : null)
                ?? _tracker.Sessions.Where(x => x.Agent == AgentKind.Codex).OrderByDescending(x => x.LastActivity).FirstOrDefault()
                ?? _tracker.Touch(AgentKind.Codex, "codex:" + (thread.Length > 0 ? thread : "notify"), cwd);
        foreach (var st in _files.Values.Where(f => f.SessionId == s.Id)) st.AnswerPending = false;
        _tracker.Finish(s, AgentTracker.Str(e, "last-assistant-message"));
    }

    // ---------- журналы сессий ----------

    private void Poll()
    {
        try
        {
            var now = DateTime.Now;
            foreach (var path in RecentRollouts())
            {
                var info = new FileInfo(path);
                if (now - info.LastWriteTime > Fresh && !_files.ContainsKey(path)) continue;
                if (!_files.TryGetValue(path, out var st))
                {
                    st = new FileState();
                    ReadMeta(path, st);
                    // Только что начатую сессию читаем с начала, давнюю — только новое.
                    st.Offset = now - info.CreationTime < TimeSpan.FromSeconds(30) ? 0 : info.Length;
                    _files[path] = st;
                }
                if (info.Length > st.Offset) ReadNew(path, st, info.Length);
                else if (info.Length < st.Offset) st.Offset = info.Length;
            }

            foreach (var (path, st) in _files.ToList())
            {
                var s = st.SessionId.Length > 0 ? _tracker.Find(st.SessionId) : null;
                // Ответ дан и дальше тишина — ход закончен (если notify не подключён или не дошёл).
                if (st.AnswerPending && now - st.LastEvent > DoneAfterAnswer && s is { Status: AgentStatus.Working })
                {
                    st.AnswerPending = false;
                    _tracker.Finish(s, st.LastAnswer);
                }
                // Давно молчащие сессии убираем.
                if (now - st.LastEvent > Fresh && (s == null || s.Status is AgentStatus.Done or AgentStatus.Idle))
                {
                    _files.Remove(path);
                    if (s != null) _tracker.End(s.Id);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[codex] poll failed: {ex.Message}");
        }
    }

    private static IEnumerable<string> RecentRollouts()
    {
        if (!Directory.Exists(SessionsDir)) yield break;
        var days = new HashSet<DateTime> { DateTime.Now.Date, DateTime.Now.Date.AddDays(-1), DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(-1) };
        foreach (var d in days)
        {
            string dir = Path.Combine(SessionsDir, d.ToString("yyyy"), d.ToString("MM"), d.ToString("dd"));
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "rollout-*.jsonl")) yield return f;
        }
    }

    private static void ReadMeta(string path, FileState st)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            string? first = reader.ReadLine();
            if (first == null) return;
            using var doc = JsonDocument.Parse(first);
            var root = doc.RootElement;
            if (AgentTracker.Str(root, "type") != "session_meta" || !root.TryGetProperty("payload", out var p)) return;
            string id = AgentTracker.Str(p, "id");
            st.SessionId = "codex:" + (id.Length > 0 ? id : Path.GetFileNameWithoutExtension(path));
            st.Cwd = AgentTracker.Str(p, "cwd");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[codex] meta failed: {ex.Message}");
        }
        if (st.SessionId.Length == 0) st.SessionId = "codex:" + Path.GetFileNameWithoutExtension(path);
    }

    private void ReadNew(string path, FileState st, long length)
    {
        string chunk;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.Seek(st.Offset, SeekOrigin.Begin);
            var buf = new byte[Math.Min(length - st.Offset, 4 * 1024 * 1024)];
            int n = fs.Read(buf, 0, buf.Length);
            st.Offset += n;
            chunk = Encoding.UTF8.GetString(buf, 0, n);
        }

        var text = st.Partial + chunk;
        int last = text.LastIndexOf('\n');
        if (last < 0) { st.Partial = text; return; }
        st.Partial = text[(last + 1)..];
        foreach (var line in text[..last].Split('\n'))
        {
            if (line.Length < 2) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                Process(st, doc.RootElement);
            }
            catch (JsonException) { }
        }
    }

    private void Process(FileState st, JsonElement root)
    {
        string type = AgentTracker.Str(root, "type");
        if (!root.TryGetProperty("payload", out var p)) return;
        st.LastEvent = DateTime.Now;

        switch (type)
        {
            case "session_meta":
            {
                string id = AgentTracker.Str(p, "id");
                if (id.Length > 0) st.SessionId = "codex:" + id;
                if (AgentTracker.Str(p, "cwd") is { Length: > 0 } c) st.Cwd = c;
                return;
            }
            case "turn_context":
                if (AgentTracker.Str(p, "cwd") is { Length: > 0 } cwd) st.Cwd = cwd;
                return;
            case "event_msg":
                OnEvent(st, p, root);
                return;
            case "response_item":
                OnItem(st, p);
                return;
        }
    }

    private AgentSession Session(FileState st) => _tracker.Touch(AgentKind.Codex, st.SessionId, st.Cwd);

    private void OnEvent(FileState st, JsonElement p, JsonElement root)
    {
        switch (AgentTracker.Str(p, "type"))
        {
            case "user_message":
            {
                string msg = AgentTracker.Str(p, "message");
                if (msg.Length == 0 || msg.StartsWith('<')) return;
                st.AnswerPending = false;
                var s = Session(st);
                if (s.Status != AgentStatus.Working || s.Task != AgentTracker.OneLine(msg, 90)) _tracker.BeginTask(s, msg);
                return;
            }
            case "task_started":
                st.AnswerPending = false;
                _tracker.Working(Session(st));
                return;
            case "agent_reasoning":
                st.AnswerPending = false;
                return;
            case "agent_message":
            {
                string msg = AgentTracker.Str(p, "message");
                if (msg.Length == 0) return;
                st.LastAnswer = msg;
                st.AnswerPending = true;
                _tracker.SetNote(Session(st), msg);
                return;
            }
            case "task_complete":
                st.AnswerPending = false;
                _tracker.Finish(Session(st), AgentTracker.Str(p, "last_agent_message") is { Length: > 0 } m ? m : st.LastAnswer);
                return;
            case "turn_aborted":
                st.AnswerPending = false;
                _tracker.Finish(Session(st), L.T("Остановлено"));
                return;
            case "exec_approval_request":
            case "apply_patch_approval_request":
                st.AnswerPending = false;
                _tracker.NeedsYou(Session(st), L.T("Нужно ваше разрешение"));
                return;
            case "token_count":
                if (p.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object)
                    ApplyRateLimits(rl, Timestamp(root));
                return;
        }
    }

    private void OnItem(FileState st, JsonElement p)
    {
        string type = AgentTracker.Str(p, "type");
        switch (type)
        {
            case "function_call":
            case "custom_tool_call":
            case "local_shell_call":
            {
                st.AnswerPending = false;
                string callId = AgentTracker.Str(p, "call_id") is { Length: > 0 } c ? c : AgentTracker.Str(p, "id");
                string name = AgentTracker.Str(p, "name");
                var (step, stats) = DescribeCall(type, name, p, callId);
                _tracker.AddStep(Session(st), step, stats);
                return;
            }
            case "function_call_output":
            case "custom_tool_call_output":
            case "local_shell_call_output":
            {
                string output = p.TryGetProperty("output", out var o)
                    ? o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : o.GetRawText()
                    : "";
                _tracker.FinishStep(Session(st), AgentTracker.Str(p, "call_id"), Failed(output));
                return;
            }
            case "web_search_call":
            {
                st.AnswerPending = false;
                string q = p.TryGetProperty("action", out var a) ? AgentTracker.Str(a, "query") : "";
                var s = Session(st);
                string id = AgentTracker.Str(p, "id") is { Length: > 0 } wid ? wid : Guid.NewGuid().ToString("N");
                _tracker.AddStep(s, new AgentStep { ToolUseId = id, Text = q.Length > 0 ? L.F("Ищет в сети «{0}»", AgentTracker.OneLine(q, 30)) : L.T("Ищет в сети") });
                _tracker.FinishStep(s, id, false);
                return;
            }
            case "message":
            {
                string role = AgentTracker.Str(p, "role");
                string text = MessageText(p);
                if (text.Length == 0) return;
                var s = Session(st);
                if (role == "user" && !text.StartsWith('<'))
                {
                    st.AnswerPending = false;
                    if (s.Status != AgentStatus.Working || s.Task != AgentTracker.OneLine(text, 90)) _tracker.BeginTask(s, text);
                }
                else if (role == "assistant")
                {
                    st.LastAnswer = text;
                    st.AnswerPending = true;
                    _tracker.SetNote(s, text);
                }
                return;
            }
        }
    }

    // ---------- разбор вызовов ----------

    private static (AgentStep Step, List<(string, int, int)>? Stats) DescribeCall(string type, string name, JsonElement p, string callId)
    {
        // apply_patch: как custom_tool_call (input — текст патча) или function_call (arguments.input).
        string patch = "";
        if (name == "apply_patch")
        {
            patch = AgentTracker.Str(p, "input");
            if (patch.Length == 0 && ParseArgs(p) is { } args) patch = AgentTracker.Str(args, "input");
        }
        if (patch.Length > 0) return DescribePatch(patch, callId);

        string command = "";
        if (type == "local_shell_call" && p.TryGetProperty("action", out var action))
            command = CommandText(action);
        else if (name is "shell" or "container.exec" or "exec_command" or "shell_command" or "local_shell" && ParseArgs(p) is { } a)
            command = CommandText(a);

        if (command.Length > 0)
        {
            var (text, file) = DescribeCommand(command);
            return (new AgentStep { ToolUseId = callId, Text = text, File = file }, null);
        }

        string label = name switch
        {
            "update_plan" => L.T("Обновляет план"),
            "view_image" => L.T("Смотрит картинку"),
            "" => L.T("Работает"),
            _ => L.F("Инструмент {0}", name.Split("__").Last()),
        };
        return (new AgentStep { ToolUseId = callId, Text = label }, null);
    }

    private static JsonElement? ParseArgs(JsonElement p)
    {
        if (!p.TryGetProperty("arguments", out var a)) return null;
        if (a.ValueKind == JsonValueKind.Object) return a.Clone();
        if (a.ValueKind != JsonValueKind.String) return null;
        try
        {
            using var doc = JsonDocument.Parse(a.GetString() ?? "");
            return doc.RootElement.Clone();
        }
        catch (JsonException) { return null; }
    }

    private static string CommandText(JsonElement args)
    {
        foreach (var key in new[] { "command", "cmd" })
        {
            if (!args.TryGetProperty(key, out var c)) continue;
            if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
            if (c.ValueKind == JsonValueKind.Array)
            {
                var parts = c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList();
                // ["bash","-lc","…"] / ["powershell","-Command","…"] → сама команда.
                if (parts.Count >= 3 && parts[^2] is "-lc" or "-c" or "-Command" or "/c") return parts[^1];
                return string.Join(' ', parts);
            }
        }
        return "";
    }

    private static readonly Regex Words = new(@"""[^""]*""|'[^']*'|\S+", RegexOptions.Compiled);

    private static (string Text, string? File) DescribeCommand(string command)
    {
        var tokens = Words.Matches(command.Trim()).Select(m => m.Value.Trim('"', '\'')).ToList();
        if (tokens.Count == 0) return (L.T("Команда"), null);
        string head = Path.GetFileNameWithoutExtension(tokens[0]).ToLowerInvariant();
        string? LastFile() => tokens.Skip(1).LastOrDefault(t => !t.StartsWith('-') && t.Contains('.') && !t.Contains('|')) is { } f
            ? Path.GetFileName(f.TrimEnd('\\', '/'))
            : null;

        switch (head)
        {
            case "cat" or "type" or "get-content" or "gc" or "head" or "tail" or "less" or "bat" or "nl":
            case "sed" when tokens.Contains("-n"):
                return LastFile() is { } rf ? (L.F("Читает {0}", rf), rf) : (L.T("Читает файл"), null);
            case "rg" or "grep" or "findstr" or "select-string" or "ag":
            {
                string pat = tokens.Skip(1).FirstOrDefault(t => !t.StartsWith('-')) ?? "";
                return (L.F("Ищет «{0}»", AgentTracker.OneLine(pat, 30)), null);
            }
            case "ls" or "dir" or "get-childitem" or "gci" or "find" or "tree" or "fd":
                return (L.T("Смотрит файлы"), null);
            case "git":
                return ($"Git: {string.Join(' ', tokens.Skip(1).Take(2))}", null);
            case "dotnet" or "npm" or "pnpm" or "yarn" or "cargo" or "go" or "make" or "pytest" or "python" or "python3"
                or "node" or "msbuild" or "gradle" or "mvn" or "uv" or "bun" or "deno":
                return (L.F("Запускает {0}", $"{head} {tokens.Skip(1).FirstOrDefault(t => !t.StartsWith('-')) ?? ""}".TrimEnd()), null);
            default:
                return (L.F("Команда: {0}", AgentTracker.OneLine(command, 44)), null);
        }
    }

    private static (AgentStep, List<(string, int, int)>) DescribePatch(string patch, string callId)
    {
        var stats = new List<(string File, int Added, int Removed)>();
        string? current = null;
        bool created = false;
        int add = 0, rem = 0;
        void Flush()
        {
            if (current != null) stats.Add((current, add, rem));
            add = rem = 0;
        }

        foreach (var raw in patch.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string? file = line.StartsWith("*** Update File: ") ? line[17..]
                : line.StartsWith("*** Add File: ") ? line[14..]
                : line.StartsWith("*** Delete File: ") ? line[17..]
                : null;
            if (file != null)
            {
                Flush();
                if (stats.Count == 0) created = line.StartsWith("*** Add File: ");
                current = Path.GetFileName(file.Trim().TrimEnd('\\', '/'));
                continue;
            }
            if (line.StartsWith("*** ")) continue;
            if (line.StartsWith('+')) add++;
            else if (line.StartsWith('-')) rem++;
        }
        Flush();

        string first = stats.FirstOrDefault().File ?? L.T("файлы");
        string verb = created && stats.Count == 1 ? L.T("Создаёт") : L.T("Правит");
        string text = stats.Count > 1 ? L.F("{0} {1} и ещё {2}", verb, first, stats.Count - 1) : $"{verb} {first}";
        return (new AgentStep { ToolUseId = callId, Text = text, File = stats.Count > 0 ? first : null, IsEdit = true }, stats);
    }

    private static readonly Regex ExitCode = new(@"""exit_code""\s*:\s*(-?\d+)|^Exit code:\s*(-?\d+)", RegexOptions.Compiled | RegexOptions.Multiline);

    private static bool Failed(string output)
    {
        var m = ExitCode.Match(output);
        if (!m.Success) return false;
        string v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return int.TryParse(v, out int code) && code != 0;
    }

    private static string MessageText(JsonElement p)
    {
        if (!p.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            string t = AgentTracker.Str(part, "text");
            if (t.Length > 0) sb.Append(t).Append(' ');
        }
        return sb.ToString().Trim();
    }

    // ---------- лимиты ----------

    private static DateTime Timestamp(JsonElement root) =>
        DateTime.TryParse(AgentTracker.Str(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : DateTime.UtcNow;

    /// <summary>rate_limits: {primary:{used_percent, window_minutes, resets_at | resets_in_seconds}, secondary:{…}, plan_type?}.</summary>
    private void ApplyRateLimits(JsonElement rl, DateTime atUtc)
    {
        LimitWindow? Window(string key)
        {
            if (!rl.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return null;
            double used = Num(w, "used_percent") ?? 0;
            double minutes = Num(w, "window_minutes") ?? 0;
            DateTime? resets = Num(w, "resets_at") is { } at ? DateTimeOffset.FromUnixTimeSeconds((long)at).UtcDateTime
                : Num(w, "resets_in_seconds") is { } inSec ? atUtc.AddSeconds(inSec)
                : null;
            return new LimitWindow(used, minutes, resets);
        }

        var a = Window("primary");
        var b = Window("secondary");
        var windows = new[] { a, b }.Where(x => x != null).Select(x => x!).ToList();
        var five = windows.FirstOrDefault(x => x.Minutes is > 0 and <= 360) ?? a;
        var week = windows.FirstOrDefault(x => x.Minutes >= 1440) ?? (b != five ? b : null);

        Usage.FiveHour = five;
        Usage.Weekly = week;
        if (AgentTracker.Str(rl, "plan_type") is { Length: > 0 } plan) Usage.Plan = Capitalize(plan);
        Usage.UpdatedUtc = atUtc;
        Usage.Error = null;
        UsageChanged?.Invoke();
    }

    /// <summary>Перечитать последние лимиты из журналов (кнопка «Обновить»).</summary>
    public void RefreshUsage()
    {
        if (_enabled) LoadLastUsage();
    }

    /// <summary>При включении ищем последние лимиты в самом свежем журнале, чтобы кольцо не пустовало до первой сессии.</summary>
    private void LoadLastUsage()
    {
        try
        {
            if (!Directory.Exists(SessionsDir)) { Usage.Error = L.T("Codex ещё не запускался"); UsageChanged?.Invoke(); return; }
            var newest = Directory.EnumerateFiles(SessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).Take(5);
            foreach (var f in newest)
            {
                using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long start = Math.Max(0, fs.Length - 512 * 1024);
                fs.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                string? line, lastLimits = null;
                while ((line = reader.ReadLine()) != null)
                    if (line.Contains("\"rate_limits\"", StringComparison.Ordinal)) lastLimits = line;
                if (lastLimits == null) continue;
                using var doc = JsonDocument.Parse(lastLimits);
                var root = doc.RootElement;
                if (root.TryGetProperty("payload", out var p) && p.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object)
                {
                    ApplyRateLimits(rl, Timestamp(root));
                    return;
                }
            }
            Usage.Error = L.T("Лимиты появятся после первого запроса в Codex");
            UsageChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[codex] usage scan failed: {ex.Message}");
        }
    }

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : null;

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>Окно лимита: сколько процентов израсходовано, длина окна, когда сбросится,
/// и насколько это серьёзно по мнению самого сервиса (normal / warning / critical), если он это сообщает.</summary>
public sealed record LimitWindow(double UsedPercent, double Minutes, DateTime? ResetsAtUtc, string Severity = "", string Label = "")
{
    public double LeftPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

public sealed class AgentUsage
{
    public AgentKind Agent { get; init; }
    public string Plan { get; set; } = "";
    public LimitWindow? FiveHour { get; set; }
    public LimitWindow? Weekly { get; set; }
    /// <summary>Дополнительные лимиты (например, недельный на отдельную модель).</summary>
    public List<LimitWindow> Extra { get; set; } = new();
    /// <summary>На что ушёл недельный лимит: «Claude Code 81%», «Cowork 19%».</summary>
    public List<(string Name, double Percent)> Breakdown { get; set; } = new();
    public DateTime? UpdatedUtc { get; set; }
    /// <summary>Почему данных нет (не подключено, ещё не запускался…).</summary>
    public string? Error { get; set; }

    /// <summary>Остаток самого «тесного» окна.</summary>
    public double? LeftPercent => UsedPercent is { } u ? 100 - u : null;

    /// <summary>Что показывает кольцо: расход самого «тесного» окна — как «% used» на странице сервиса.</summary>
    public double? UsedPercent =>
        FiveHour == null && Weekly == null ? null
        : Math.Clamp(Math.Max(FiveHour?.UsedPercent ?? 0, Weekly?.UsedPercent ?? 0), 0, 100);

    /// <summary>Самая тревожная оценка среди окон (warning / critical), если сервис её сообщает.</summary>
    public bool IsHigh =>
        new[] { FiveHour, Weekly }.Any(w => w != null && (w.Severity is "warning" or "critical" or "exceeded" or "blocked" || w.UsedPercent >= 80));
}
