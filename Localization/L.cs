using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Markup;

namespace SnowyNotch.Localization;

/// <summary>
/// Перевод интерфейса. Ключ — исходная русская строка (как в gettext): в коде <c>L.T("Ничего не играет")</c>,
/// в XAML <c>{l:T 'Ничего не играет'}</c>, с подстановками <c>L.F("Помидор {0} из {1}", a, b)</c>.
/// Встроенные файлы: keys.txt — «номер|русская строка», {код}.txt — «номер|перевод».
/// Нет перевода — английский, нет и его — исходная строка. Язык выбирается при запуске (смена = перезапуск).
/// </summary>
public static class L
{
    public sealed record Lang(string Code, string Native);

    /// <summary>Языки интерфейса; по алфавиту самоназваний (латиница, затем кириллица, затем японский и китайский).</summary>
    public static readonly Lang[] Languages =
    {
        new("cs", "Čeština"),
        new("da", "Dansk"),
        new("de", "Deutsch"),
        new("en", "English"),
        new("es", "Español"),
        new("fr", "Français"),
        new("it", "Italiano"),
        new("nl", "Nederlands"),
        new("nb", "Norsk"),
        new("pl", "Polski"),
        new("pt", "Português"),
        new("fi", "Suomi"),
        new("sv", "Svenska"),
        new("tr", "Türkçe"),
        new("ru", "Русский"),
        new("uk", "Українська"),
        new("ja", "日本語"),
        new("zh", "简体中文"),
    };

    private static Dictionary<string, string> _map = new();
    private static Dictionary<string, string> _fallback = new();

    /// <summary>Текущий язык (код).</summary>
    public static string Code { get; private set; } = "ru";

    /// <summary>Культура для дат, месяцев и дней недели на текущем языке.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Выбрать язык: код из настроек или "" — язык системы (если он не поддерживается — английский).</summary>
    public static void Init(string? setting)
    {
        Code = Resolve(setting);
        Culture = CultureInfo.GetCultureInfo(Code switch
        {
            "en" => "en-US", "ru" => "ru-RU", "uk" => "uk-UA", "sv" => "sv-SE", "de" => "de-DE",
            "fr" => "fr-FR", "es" => "es-ES", "pt" => "pt-PT", "pl" => "pl-PL", "it" => "it-IT",
            "nl" => "nl-NL", "cs" => "cs-CZ", "tr" => "tr-TR", "da" => "da-DK", "fi" => "fi-FI", "nb" => "nb-NO",
            "ja" => "ja-JP", "zh" => "zh-CN", _ => "en-US",
        });
        if (Code == "ru") { _map = new(); _fallback = new(); return; }

        var keys = ReadTable("keys");
        _map = Join(keys, ReadTable(Code));
        _fallback = Code == "en" ? new() : Join(keys, ReadTable("en"));
    }

    public static string Resolve(string? setting)
    {
        if (!string.IsNullOrWhiteSpace(setting) && Languages.Any(l => l.Code == setting)) return setting!;
        string sys = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (sys == "nn" || sys == "no") sys = "nb"; // нюношк и общий «норвежский» — на букмол
        return Languages.Any(l => l.Code == sys) ? sys : "en";
    }

    /// <summary>Перевод строки.</summary>
    public static string T(string ru) =>
        _map.TryGetValue(ru, out var s) ? s : _fallback.TryGetValue(ru, out var f) ? f : ru;

    /// <summary>«16 ноября», «November 16», «11月16日» — день и месяц в порядке, принятом в языке.</summary>
    public static string MonthDay(DateTime d, bool shortMonth = false)
    {
        string pattern = Culture.DateTimeFormat.MonthDayPattern;
        if (shortMonth) pattern = pattern.Replace("MMMM", "MMM");
        return d.ToString(pattern, Culture);
    }

    /// <summary>Перевод шаблона с подстановками {0}, {1}…</summary>
    public static string F(string ru, params object?[] args)
    {
        try { return string.Format(Culture, T(ru), args); }
        catch (FormatException) { return string.Format(Culture, ru, args); }
    }

    private static Dictionary<string, string> Join(Dictionary<string, string> keys, Dictionary<string, string> lang)
    {
        var map = new Dictionary<string, string>();
        foreach (var (id, ru) in keys)
            if (lang.TryGetValue(id, out var t) && t.Length > 0) map[ru] = t;
        return map;
    }

    private static Dictionary<string, string> ReadTable(string name)
    {
        var table = new Dictionary<string, string>();
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"SnowyNotch.Localization.{name}.txt");
        if (stream == null) return table;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            int bar = line.IndexOf('|');
            if (bar <= 0) continue;
            table[line[..bar].Trim()] = line[(bar + 1)..].Replace("\\n", "\n");
        }
        return table;
    }
}

/// <summary>XAML: <c>Text="{l:T 'Ничего не играет'}"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
