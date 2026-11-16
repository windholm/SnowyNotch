using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using SnowyNotch.Localization;

namespace SnowyNotch.Services;

public enum AgentStatus { Idle, Working, NeedsYou, Done }

/// <summary>Какой агент ведёт сессию. Все они сводятся к одним и тем же шагам и статусам.</summary>
public enum AgentKind { Claude, Codex }

public sealed class AgentStep
{
    public required string ToolUseId { get; init; }
    public required string Text { get; init; }
    public string? File { get; init; }
    public bool IsEdit { get; init; }
    public bool Done { get; set; }
    public bool Failed { get; set; }
}

public sealed class AgentSession
{
    public required string Id { get; init; }
    public AgentKind Agent { get; init; }
    public string Folder { get; set; } = "";
    public string Task { get; set; } = "";
    public string Note { get; set; } = "";
    public AgentStatus Status { get; set; } = AgentStatus.Idle;
    public List<AgentStep> Steps { get; } = new();
    public Dictionary<string, (int Added, int Removed)> FileStats { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime LastActivity { get; set; } = DateTime.Now;

    public string AgentName => Agent == AgentKind.Codex ? "Codex" : "Claude";

    public AgentStep? CurrentStep => Steps.LastOrDefault();

    public AgentStep? FileStep => Steps.LastOrDefault(s => s.File != null);
}

/// <summary>
/// Сессии всех агентов (Claude Code через HTTP-хуки, Codex через журналы сессий и notify).
/// Источники вызывают общие методы — BeginTask, AddStep, FinishStep, NeedsYou, Finish, End.
/// </summary>
public sealed class AgentTracker
{
    private const int MaxSessions = 32;
    private readonly Dictionary<string, AgentSession> _sessions = new();

    public AgentSession? Current { get; private set; }

    public event Action? Changed;

    public event Action<AgentSession, AgentStatus>? StatusChanged;

    public int ActiveCount => _sessions.Values.Count(s => s.Status is AgentStatus.Working or AgentStatus.NeedsYou);

    public IEnumerable<AgentSession> Sessions => _sessions.Values;

    // ---------- общий API для источников ----------

    public AgentSession Touch(AgentKind agent, string id, string? cwd)
    {
        if (!_sessions.TryGetValue(id, out var s))
        {
            s = new AgentSession { Id = id, Agent = agent };
            _sessions[id] = s;
            // Сессии, чей агент упал и не прислал SessionEnd, не должны копиться бесконечно.
            while (_sessions.Count > MaxSessions)
            {
                var oldest = _sessions.Values.Where(x => x != s && x.Status is not (AgentStatus.Working or AgentStatus.NeedsYou))
                                             .OrderBy(x => x.LastActivity).FirstOrDefault();
                if (oldest == null) break;
                _sessions.Remove(oldest.Id);
            }
        }
        if (!string.IsNullOrEmpty(cwd)) s.Folder = Path.GetFileName(cwd.TrimEnd('\\', '/')) is { Length: > 0 } f ? f : cwd;
        s.LastActivity = DateTime.Now;
        Current = s;
        return s;
    }

    public AgentSession? Find(string id) => _sessions.TryGetValue(id, out var s) ? s : null;

    public void BeginTask(AgentSession s, string prompt) => Mutate(s, forceStatusEvent: true, x =>
    {
        x.Task = OneLine(prompt, 90);
        x.Steps.Clear();
        x.FileStats.Clear();
        x.Note = "";
        x.Status = AgentStatus.Working;
    });

    public void AddStep(AgentSession s, AgentStep step, IEnumerable<(string File, int Added, int Removed)>? stats = null) => Mutate(s, false, x =>
    {
        x.Steps.Add(step);
        if (x.Steps.Count > 40) x.Steps.RemoveAt(0);
        foreach (var (file, add, rem) in stats ?? Enumerable.Empty<(string, int, int)>())
        {
            var cur = x.FileStats.TryGetValue(file, out var v) ? v : (0, 0);
            x.FileStats[file] = (cur.Item1 + add, cur.Item2 + rem);
        }
        x.Status = AgentStatus.Working;
        x.Note = "";
    });

    public void FinishStep(AgentSession s, string callId, bool failed) => Mutate(s, false, x =>
    {
        var step = x.Steps.LastOrDefault(st => st.ToolUseId == callId && !st.Done) ?? x.Steps.LastOrDefault(st => !st.Done);
        if (step != null)
        {
            step.Done = true;
            step.Failed = failed;
        }
        if (x.Status == AgentStatus.NeedsYou) x.Status = AgentStatus.Working;
    });

    public void Working(AgentSession s) => Mutate(s, false, x => { if (x.Status != AgentStatus.NeedsYou) x.Status = AgentStatus.Working; });

    public void SetNote(AgentSession s, string note) => Mutate(s, false, x => x.Note = OneLine(note, 90));

    public void NeedsYou(AgentSession s, string note) => Mutate(s, false, x =>
    {
        x.Status = AgentStatus.NeedsYou;
        x.Note = OneLine(note, 80) is { Length: > 0 } m ? m : L.T("Ждёт вас");
    });

    public void Finish(AgentSession s, string note) => Mutate(s, false, x =>
    {
        foreach (var st in x.Steps) st.Done = true;
        x.Status = AgentStatus.Done;
        if (note.Length > 0) x.Note = OneLine(note, 90);
    });

    public void End(string id)
    {
        if (!_sessions.Remove(id)) return;
        if (Current?.Id == id) Current = _sessions.Values.OrderByDescending(s => s.LastActivity).FirstOrDefault();
        Changed?.Invoke();
    }

    private void Mutate(AgentSession s, bool forceStatusEvent, Action<AgentSession> change)
    {
        var before = s.Status;
        s.LastActivity = DateTime.Now;
        Current = s;
        change(s);
        Changed?.Invoke();
        if (s.Status != before || forceStatusEvent) StatusChanged?.Invoke(s, s.Status);
    }

    // ---------- Claude Code: HTTP-хуки ----------

    public void Handle(JsonElement e)
    {
        string evt = Str(e, "hook_event_name");
        string id = Str(e, "session_id");
        if (id.Length == 0) return;
        id = "claude:" + id;

        if (evt == "SessionEnd") { End(id); return; }

        var s = Touch(AgentKind.Claude, id, Str(e, "cwd"));
        switch (evt)
        {
            case "UserPromptSubmit":
                BeginTask(s, Str(e, "prompt"));
                break;

            case "PreToolUse":
            {
                string tool = Str(e, "tool_name");
                var input = e.TryGetProperty("tool_input", out var ti) ? ti : default;
                var (text, file, isEdit) = Describe(tool, input);
                var stats = isEdit && file != null ? new[] { (file, DiffStats(tool, input).Added, DiffStats(tool, input).Removed) } : null;
                AddStep(s, new AgentStep { ToolUseId = Str(e, "tool_use_id"), Text = text, File = file, IsEdit = isEdit }, stats);
                break;
            }

            case "PostToolUse":
            case "PostToolUseFailure":
                FinishStep(s, Str(e, "tool_use_id"), evt == "PostToolUseFailure");
                break;

            case "PermissionRequest":
                NeedsYou(s, L.T("Нужно ваше разрешение"));
                break;

            case "Notification":
                NeedsYou(s, Str(e, "message"));
                break;

            case "Stop":
                Finish(s, Str(e, "last_assistant_message"));
                break;
        }
    }

    private static (string Text, string? File, bool IsEdit) Describe(string tool, JsonElement input)
    {
        string file = FileName(input);
        switch (tool)
        {
            case "Read": return (L.F("Читает {0}", file), file.Length > 0 ? file : null, false);
            case "Edit":
            case "MultiEdit": return (L.F("Правит {0}", file), file.Length > 0 ? file : null, true);
            case "Write": return (L.F("Создаёт {0}", file), file.Length > 0 ? file : null, true);
            case "NotebookEdit": return (L.F("Правит {0}", file), file.Length > 0 ? file : null, true);
            case "Bash":
            case "PowerShell":
            {
                string d = Str(input, "description");
                return (d.Length > 0 ? d : L.F("Команда: {0}", OneLine(Str(input, "command"), 48)), null, false);
            }
            case "Grep": return (L.F("Ищет «{0}»", OneLine(Str(input, "pattern"), 30)), null, false);
            case "Glob": return (L.F("Ищет файлы {0}", OneLine(Str(input, "pattern"), 30)), null, false);
            case "WebFetch": return (L.F("Открывает {0}", Host(Str(input, "url"))), null, false);
            case "WebSearch": return (L.F("Ищет в сети «{0}»", OneLine(Str(input, "query"), 30)), null, false);
            case "Task":
            case "Agent": return (L.T("Запускает помощника"), null, false);
            case "TodoWrite": return (L.T("Обновляет план"), null, false);
            default:
                return (tool.StartsWith("mcp__") ? L.F("Инструмент {0}", tool.Split("__").Last()) : tool, null, false);
        }
    }

    private static (int Added, int Removed) DiffStats(string tool, JsonElement input)
    {
        static int Lines(string s) => s.Length == 0 ? 0 : s.Split('\n').Length;
        switch (tool)
        {
            case "Edit":
                return (Lines(Str(input, "new_string")), Lines(Str(input, "old_string")));
            case "MultiEdit":
                int a = 0, r = 0;
                if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array)
                    foreach (var ed in edits.EnumerateArray()) { a += Lines(Str(ed, "new_string")); r += Lines(Str(ed, "old_string")); }
                return (a, r);
            case "Write":
                return (Lines(Str(input, "content")), 0);
            default:
                return (0, 0);
        }
    }

    private static string FileName(JsonElement input)
    {
        foreach (var key in new[] { "file_path", "notebook_path", "path" })
        {
            string p = Str(input, key);
            if (p.Length > 0) return Path.GetFileName(p.TrimEnd('\\', '/'));
        }
        return "";
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : OneLine(url, 30);

    internal static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static string OneLine(string s, int max)
    {
        s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
