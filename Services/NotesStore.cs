using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using SnowyNotch.Localization;

namespace SnowyNotch.Services;

/// <summary>Встроенная заметка: текст хранится в Markdown.</summary>
public sealed class Note
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Markdown { get; set; } = "";
    public DateTime Updated { get; set; } = DateTime.Now;

    private static readonly Regex Marks = new(@"^(#{1,3}\s|[-*]\s\[[ xX]\]\s|[-*]\s|\d+\.\s|>\s)|\*\*|~~|==|</?u>|<span[^>]*>|</span>|(?<!\\)\*|\\(?=[\\*~=<])", RegexOptions.Compiled);

    private string[] Lines => Markdown.Split('\n').Select(l => Marks.Replace(l.TrimEnd('\r'), "").Trim()).Where(l => l.Length > 0).ToArray();

    /// <summary>Заголовок — первая непустая строка без разметки.</summary>
    public string Title => Lines.FirstOrDefault() is { } t ? t : L.T("Новая заметка");

    /// <summary>Превью — следующие строки одной строкой.</summary>
    public string Preview => string.Join("  ", Lines.Skip(1).Take(3));

    public bool IsEmpty => Lines.Length == 0;
}

/// <summary>
/// Заметки в %APPDATA%\SnowyNotch\notes.json (сохраняются с небольшой задержкой после правки).
/// Старая одиночная заметка notes.txt переносится как первая.
/// </summary>
public sealed class NotesStore
{
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SnowyNotch");
    private static readonly string FilePath = Path.Combine(Dir, "notes.json");
    private static readonly string LegacyPath = Path.Combine(Dir, "notes.txt");

    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public NotesStore()
    {
        Load();
        _save.Tick += (_, _) => { _save.Stop(); Save(); };
    }

    /// <summary>Свежие сверху.</summary>
    public ObservableCollection<Note> Notes { get; } = new();

    public event Action? Changed;

    /// <summary>Совместимость: заголовок самой свежей заметки (подпись карточки).</summary>
    public string Text => Notes.FirstOrDefault(n => !n.IsEmpty)?.Title ?? "";

    public Note Create()
    {
        var n = new Note { Markdown = "" };
        Notes.Insert(0, n);
        Changed?.Invoke();
        return n;
    }

    public void Update(Note note, string markdown)
    {
        if (note.Markdown == markdown) return;
        note.Markdown = markdown;
        note.Updated = DateTime.Now;
        // Правленая заметка поднимается наверх.
        int i = Notes.IndexOf(note);
        if (i > 0) Notes.Move(i, 0);
        Changed?.Invoke();
        _save.Stop();
        _save.Start();
    }

    public void Delete(Note note)
    {
        Notes.Remove(note);
        Changed?.Invoke();
        _save.Stop();
        Save();
    }

    /// <summary>Пустые заметки (создали и ничего не написали) не храним.</summary>
    public void DropEmpty()
    {
        foreach (var n in Notes.Where(n => n.IsEmpty).ToList()) Notes.Remove(n);
        Changed?.Invoke();
    }

    public void Flush()
    {
        if (!_save.IsEnabled) return;
        _save.Stop();
        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize<Note[]>(File.ReadAllText(FilePath)) ?? Array.Empty<Note>();
                foreach (var n in list.OrderByDescending(n => n.Updated)) Notes.Add(n);
            }
            else if (File.Exists(LegacyPath) && File.ReadAllText(LegacyPath) is { Length: > 0 } legacy && legacy.Trim().Length > 0)
            {
                Notes.Add(new Note { Markdown = legacy.Replace("\r\n", "\n"), Updated = File.GetLastWriteTime(LegacyPath) });
                Save();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[notes] load failed: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var keep = Notes.Where(n => !n.IsEmpty).ToArray();
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(keep, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[notes] save failed: {ex.Message}");
        }
    }
}
