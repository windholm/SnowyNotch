using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SnowyNotch.Services;

public static class ClaudeHooksInstaller
{
    private static readonly string[] ToolEvents = { "PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest" };
    private static readonly string[] PlainEvents = { "UserPromptSubmit", "Notification", "Stop", "SessionEnd" };

    public static string SettingsPath => Path.Combine(ClaudeUsage.ClaudeHome, "settings.json");

    public static string BackupPath => SettingsPath + ".snowynotch-backup";

    public static bool IsInstalled()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            return File.ReadAllText(SettingsPath).Contains(AgentBridge.Path, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    public static void Install()
    {
        var root = Load();
        if (File.Exists(SettingsPath) && !File.Exists(BackupPath)) File.Copy(SettingsPath, BackupPath);

        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;

        foreach (var evt in ToolEvents.Concat(PlainEvents))
        {
            var groups = hooks[evt] as JsonArray ?? new JsonArray();
            hooks[evt] = groups;
            if (groups.Any(IsOurs)) continue;

            var group = new JsonObject();
            if (ToolEvents.Contains(evt)) group["matcher"] = "*";
            group["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "http",
                    ["url"] = AgentBridge.Url,
                    ["timeout"] = 2,
                },
            };
            groups.Add(group);
        }
        Save(root);
    }

    public static void Uninstall()
    {
        if (!File.Exists(SettingsPath)) return;
        var root = Load();
        if (root["hooks"] is not JsonObject hooks) return;

        foreach (var evt in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[evt] is not JsonArray groups) continue;
            foreach (var g in groups.Where(IsOurs).ToList()) groups.Remove(g);
            if (groups.Count == 0) hooks.Remove(evt);
        }
        if (hooks.Count == 0) root.Remove("hooks");
        Save(root);
    }

    private static bool IsOurs(JsonNode? group) =>
        group?["hooks"] is JsonArray handlers &&
        handlers.Any(h => (h?["url"]?.GetValue<string>() ?? "").Contains(AgentBridge.Path, StringComparison.Ordinal));

    private static JsonObject Load()
    {
        if (!File.Exists(SettingsPath)) return new JsonObject();
        string text = File.ReadAllText(SettingsPath);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
               ?? throw new InvalidOperationException("~/.claude/settings.json is not a JSON object");
    }

    private static void Save(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        string tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, SettingsPath, overwrite: true);
    }
}
