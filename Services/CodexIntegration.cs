using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SnowyNotch.Services;

/// <summary>
/// Подключение Codex CLI: в ~/.codex/config.toml добавляется верхнеуровневый
/// <c>notify = ['…\SnowyNotch.exe', '--codex-notify']</c> — Codex вызывает его в конце каждого хода,
/// а SnowyNotch пересылает событие на 127.0.0.1 в работающий остров. Живой прогресс идёт из журналов сессий
/// (<see cref="CodexWatcher"/>), конфиг для него не нужен.
/// Копия config.toml сохраняется один раз; чужой notify не трогаем — тогда «Готово» определяется по паузе.
/// </summary>
public static class CodexIntegration
{
    public const string NotifyArg = "--codex-notify";

    public static string ConfigPath => Path.Combine(CodexWatcher.Home, "config.toml");
    public static string BackupPath => ConfigPath + ".snowynotch-backup";

    private static readonly Regex NotifyLine = new(@"^\s*notify\s*=", RegexOptions.Compiled);
    private static readonly Regex TableHeader = new(@"^\s*\[", RegexOptions.Compiled);

    public static bool NotifyInstalled()
    {
        try { return File.Exists(ConfigPath) && File.ReadAllText(ConfigPath).Contains(NotifyArg, StringComparison.Ordinal); }
        catch { return false; }
    }

    /// <summary>Ставит notify. false — у пользователя уже свой notify (его не трогаем).</summary>
    public static bool InstallNotify()
    {
        Directory.CreateDirectory(CodexWatcher.Home);
        var lines = File.Exists(ConfigPath) ? File.ReadAllLines(ConfigPath).ToList() : new();
        if (lines.Any(l => l.Contains(NotifyArg, StringComparison.Ordinal))) return true;

        int firstTable = lines.FindIndex(l => TableHeader.IsMatch(l));
        int topEnd = firstTable < 0 ? lines.Count : firstTable;
        if (lines.Take(topEnd).Any(l => NotifyLine.IsMatch(l))) return false;

        if (File.Exists(ConfigPath) && !File.Exists(BackupPath)) File.Copy(ConfigPath, BackupPath);

        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("не удалось определить путь к SnowyNotch.exe");
        // Литеральные строки TOML ('…') — без экранирования обратных слэшей Windows.
        string entry = exe.Contains('\'')
            ? $"notify = [\"{exe.Replace("\\", "\\\\").Replace("\"", "\\\"")}\", \"{NotifyArg}\"]"
            : $"notify = ['{exe}', '{NotifyArg}']";
        lines.Insert(0, "# SnowyNotch: показывать в острове, что Codex закончил ход");
        lines.Insert(1, entry);
        if (lines.Count > 2 && lines[2].Trim().Length > 0) lines.Insert(2, "");
        Save(lines);
        return true;
    }

    public static void UninstallNotify()
    {
        if (!File.Exists(ConfigPath)) return;
        var lines = File.ReadAllLines(ConfigPath).ToList();
        int idx = lines.FindIndex(l => l.Contains(NotifyArg, StringComparison.Ordinal));
        if (idx < 0) return;
        lines.RemoveAt(idx);
        if (idx > 0 && lines[idx - 1].StartsWith("# SnowyNotch", StringComparison.Ordinal)) { lines.RemoveAt(idx - 1); idx--; }
        if (idx < lines.Count && idx == 0 && lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        Save(lines);
    }

    private static void Save(System.Collections.Generic.List<string> lines)
    {
        string tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        File.Move(tmp, ConfigPath, overwrite: true);
    }
}
