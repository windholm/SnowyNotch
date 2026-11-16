using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SnowyNotch.Controls;

public enum MdBlock { Text, H1, H2, H3, Bullet, Number, Todo, Quote }

/// <summary>
/// Редактор заметок «как в Apple Notes / Obsidian live preview»: текст сразу оформлен, а хранится и копируется
/// как Markdown. Блоки: текст, заголовки H1–H3, маркированный и нумерованный список, чек-лист (выполненное зачёркнуто),
/// цитата. Внутри строки: **жирный**, *курсив*, &lt;u&gt;подчёркнутый&lt;/u&gt;, ~~зачёркнутый~~, ==маркер==, цвет.
/// Сокращения при наборе: «# », «## », «### », «- », «1. », «[] », «> ».
/// </summary>
public sealed class MarkdownEditor : RichTextBox
{
    private sealed class BlockInfo
    {
        public MdBlock Type;
        public bool Done;
        public BlockInfo(MdBlock type, bool done = false) { Type = type; Done = done; }
    }

    private const string MarkerTag = "marker";
    public static readonly Color DefaultColor = Colors.White;
    public static readonly Color[] Palette =
    {
        Colors.White, Color.FromRgb(0xFF, 0xD6, 0x0A), Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xFF, 0x45, 0x3A),
        Color.FromRgb(0x30, 0xD1, 0x58), Color.FromRgb(0x64, 0xD2, 0xFF), Color.FromRgb(0xBF, 0x5A, 0xF2),
    };
    private static readonly Brush Highlight = Frozen(Color.FromArgb(0x70, 0xFF, 0xD6, 0x0A));
    private static readonly Brush MarkerBrush = Frozen(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
    private static readonly Brush DoneBrush = Frozen(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
    private static readonly Brush QuoteBrush = Frozen(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF));
    private static readonly Brush QuoteBar = Frozen(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

    private bool _normalizing;

    public MarkdownEditor()
    {
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Foreground = Brushes.White;
        CaretBrush = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 14;
        Padding = new Thickness(0);
        AcceptsTab = false;
        IsDocumentEnabled = true; // чек-боксы внутри текста кликабельны
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        SpellCheck.SetIsEnabled(this, false);
        Document = new FlowDocument { PagePadding = new Thickness(0), LineHeight = double.NaN };
        SetResourceReference(SelectionBrushProperty, "AccentBrush");

        TextChanged += (_, _) =>
        {
            if (_normalizing) return;
            Normalize();
            MarkdownChanged?.Invoke();
        };
        SelectionChanged += (_, _) => KeepCaretAfterMarker();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewTextInput += OnPreviewTextInput;
        CommandManager.AddPreviewExecutedHandler(this, OnPreviewCommand);
    }

    /// <summary>Текст поменял пользователь (не при загрузке).</summary>
    public event Action? MarkdownChanged;

    public bool IsEmpty => Paragraphs.All(p => PlainText(p).Trim().Length == 0);

    public string Markdown => Serialize(null, null);

    public void Load(string markdown)
    {
        _normalizing = true;
        Document.Blocks.Clear();
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
            Document.Blocks.Add(ParseLine(line));
        if (Document.Blocks.Count == 0) Document.Blocks.Add(new Paragraph { Tag = new BlockInfo(MdBlock.Text) });
        Normalize();
        _normalizing = false;
        CaretPosition = Document.ContentEnd;
    }

    private IEnumerable<Paragraph> Paragraphs => Document.Blocks.OfType<Paragraph>();

    private static BlockInfo Info(Paragraph p)
    {
        if (p.Tag is not BlockInfo i) p.Tag = i = new BlockInfo(MdBlock.Text);
        return i;
    }

    private static bool IsList(MdBlock t) => t is MdBlock.Bullet or MdBlock.Number or MdBlock.Todo;

    // ======================= оформление блоков =======================

    /// <summary>Приводит каждую строку в соответствие её типу: размер, отступы, маркер, нумерация.
    /// Идемпотентно: меняет только то, что отличается.</summary>
    private void Normalize()
    {
        _normalizing = true;
        BeginChange();
        try
        {
            var seen = new HashSet<BlockInfo>();
            int number = 0;
            foreach (var p in Paragraphs.ToList())
            {
                var info = Info(p);
                // При разбиении абзаца WPF может скопировать ссылку на тот же BlockInfo — разделяем.
                if (!seen.Add(info)) { info = new BlockInfo(info.Type, info.Done); p.Tag = info; seen.Add(info); }
                number = info.Type == MdBlock.Number ? number + 1 : 0;
                StyleBlock(p, info);
                SyncMarker(p, info, number);
            }
        }
        finally
        {
            EndChange();
            _normalizing = false;
        }
    }

    private static void Set<T>(DependencyObject o, DependencyProperty dp, T value)
    {
        if (!Equals(o.GetValue(dp), value)) o.SetValue(dp, value);
    }

    private static void StyleBlock(Paragraph p, BlockInfo info)
    {
        var (size, weight, margin) = info.Type switch
        {
            MdBlock.H1 => (22.0, FontWeights.Bold, new Thickness(0, 4, 0, 6)),
            MdBlock.H2 => (18.0, FontWeights.Bold, new Thickness(0, 4, 0, 4)),
            MdBlock.H3 => (15.5, FontWeights.SemiBold, new Thickness(0, 3, 0, 3)),
            _ => (14.0, FontWeights.Normal, new Thickness(0, 2, 0, 2)),
        };
        Set(p, TextElement.FontSizeProperty, size);
        Set(p, TextElement.FontWeightProperty, weight);
        Set(p, Block.MarginProperty, margin);

        bool quote = info.Type == MdBlock.Quote;
        Set(p, Block.BorderThicknessProperty, quote ? new Thickness(2.5, 0, 0, 0) : new Thickness(0));
        Set(p, Block.PaddingProperty, quote ? new Thickness(10, 0, 0, 0) : new Thickness(0));
        if (quote) Set(p, Block.BorderBrushProperty, QuoteBar);

        bool done = info.Type == MdBlock.Todo && info.Done;
        if (done) Set(p, Paragraph.TextDecorationsProperty, TextDecorations.Strikethrough);
        else if (p.TextDecorations is { Count: > 0 }) p.TextDecorations = null;

        var fg = done ? DoneBrush : quote ? QuoteBrush : null;
        if (fg != null) Set(p, TextElement.ForegroundProperty, fg);
        else if (p.ReadLocalValue(TextElement.ForegroundProperty) != DependencyProperty.UnsetValue) p.ClearValue(TextElement.ForegroundProperty);
    }

    private void SyncMarker(Paragraph p, BlockInfo info, int number)
    {
        var markers = p.Inlines.OfType<InlineUIContainer>().Where(c => Equals(c.Tag, MarkerTag)).ToList();
        bool need = IsList(info.Type);
        var first = p.Inlines.FirstInline as InlineUIContainer;
        bool firstOk = first != null && Equals(first.Tag, MarkerTag) && first.Child is FrameworkElement fe && Equals(fe.Tag, info.Type);

        foreach (var m in markers.Where(m => !(need && firstOk && m == first))) p.Inlines.Remove(m);
        if (need && !firstOk)
        {
            var marker = MakeMarker(p, info, number);
            if (p.Inlines.FirstInline != null) p.Inlines.InsertBefore(p.Inlines.FirstInline, marker);
            else p.Inlines.Add(marker);
        }
        else if (need && first?.Child is TextBlock tb && info.Type == MdBlock.Number) tb.Text = $"{number}.";
        else if (need && first?.Child is TodoBox box) box.SetChecked(info.Done, animate: false);
    }

    private InlineUIContainer MakeMarker(Paragraph p, BlockInfo info, int number)
    {
        FrameworkElement child = info.Type switch
        {
            MdBlock.Bullet => new TextBlock { Text = "•", FontSize = 17, Foreground = MarkerBrush, Margin = new Thickness(2, -2, 8, 0) },
            MdBlock.Number => new TextBlock { Text = $"{number}.", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = MarkerBrush, Margin = new Thickness(0, 0, 7, 0) },
            _ => new TodoBox(info.Done, () =>
            {
                var current = Info(p); // актуальное состояние строки, а не захваченное при создании
                current.Done = !current.Done;
                Normalize();
                MarkdownChanged?.Invoke();
            }) { Margin = new Thickness(0, 0, 8, 0) },
        };
        child.Tag = info.Type;
        return new InlineUIContainer(child) { Tag = MarkerTag, BaselineAlignment = BaselineAlignment.Center };
    }

    /// <summary>Курсор не должен стоять перед маркером списка.</summary>
    private void KeepCaretAfterMarker()
    {
        if (!Selection.IsEmpty || CaretPosition.Paragraph is not { } p) return;
        if (p.Inlines.FirstInline is InlineUIContainer m && Equals(m.Tag, MarkerTag) && CaretPosition.CompareTo(m.ElementEnd) < 0)
            CaretPosition = m.ElementEnd;
    }

    private static string PlainText(Paragraph p) => new TextRange(p.ContentStart, p.ContentEnd).Text;

    private static string TextBeforeCaret(Paragraph p, TextPointer caret) => new TextRange(p.ContentStart, caret).Text;

    // ======================= команды панели =======================

    public void ApplyBlock(MdBlock type)
    {
        var ps = SelectedParagraphs().ToList();
        if (ps.Count == 0) return;
        bool all = ps.All(p => Info(p).Type == type);
        foreach (var p in ps)
        {
            var i = Info(p);
            i.Type = all ? MdBlock.Text : type;
            i.Done = false;
        }
        Normalize();
        MarkdownChanged?.Invoke();
        Focus();
    }

    private IEnumerable<Paragraph> SelectedParagraphs()
    {
        var start = Selection.Start.Paragraph;
        var end = Selection.End.Paragraph;
        bool inside = false;
        foreach (var p in Paragraphs)
        {
            if (p == start) inside = true;
            if (inside) yield return p;
            if (p == end) break;
        }
    }

    public void ToggleBold() { EditingCommands.ToggleBold.Execute(null, this); Focus(); }
    public void ToggleItalic() { EditingCommands.ToggleItalic.Execute(null, this); Focus(); }
    public void ToggleUnderline() => ToggleDecoration(TextDecorations.Underline);
    public void ToggleStrike() => ToggleDecoration(TextDecorations.Strikethrough);

    private void ToggleDecoration(TextDecorationCollection deco)
    {
        var loc = deco[0].Location;
        var current = Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        bool has = current != null && current.Any(d => d.Location == loc);
        var result = new TextDecorationCollection();
        if (current != null) foreach (var d in current.Where(d => d.Location != loc)) result.Add(d);
        if (!has) result.Add(deco[0]);
        Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, result);
        Focus();
    }

    public void ToggleHighlight()
    {
        bool has = Selection.GetPropertyValue(TextElement.BackgroundProperty) is SolidColorBrush;
        Selection.ApplyPropertyValue(TextElement.BackgroundProperty, has ? null : Highlight);
        Focus();
    }

    public void SetColor(Color c)
    {
        Selection.ApplyPropertyValue(TextElement.ForegroundProperty, Frozen(c));
        Focus();
    }

    // ======================= клавиши =======================

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var p = CaretPosition.Paragraph;

        if (ctrl && !shift && e.Key is Key.D1 or Key.D2 or Key.D3)
        {
            ApplyBlock(e.Key == Key.D1 ? MdBlock.H1 : e.Key == Key.D2 ? MdBlock.H2 : MdBlock.H3);
            e.Handled = true;
            return;
        }
        if (ctrl && shift && e.Key == Key.L) { ApplyBlock(MdBlock.Todo); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.X) { ToggleStrike(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.H) { ToggleHighlight(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.U) { ToggleUnderline(); e.Handled = true; return; }
        if (p == null) return;
        var info = Info(p);

        if (e.Key == Key.Enter && !shift)
        {
            // Пустой пункт списка/цитаты — выход из списка.
            if (info.Type is not MdBlock.Text && PlainText(p).Trim().Length == 0)
            {
                info.Type = MdBlock.Text;
                info.Done = false;
                Normalize();
                MarkdownChanged?.Invoke();
                e.Handled = true;
                return;
            }
            if (info.Type == MdBlock.Text) return;
            e.Handled = true;
            EditingCommands.EnterParagraphBreak.Execute(null, this);
            if (CaretPosition.Paragraph is { } np && np != p)
                np.Tag = new BlockInfo(info.Type is MdBlock.H1 or MdBlock.H2 or MdBlock.H3 ? MdBlock.Text : info.Type);
            Normalize();
            KeepCaretAfterMarker();
            MarkdownChanged?.Invoke();
            return;
        }

        if (e.Key == Key.Back && Selection.IsEmpty && info.Type != MdBlock.Text && TextBeforeCaret(p, CaretPosition).Length == 0)
        {
            info.Type = MdBlock.Text;
            info.Done = false;
            Normalize();
            MarkdownChanged?.Invoke();
            e.Handled = true;
        }
    }

    private static readonly Regex NumberPrefix = new(@"^\d+\.$", RegexOptions.Compiled);

    /// <summary>Markdown-сокращения в начале строки: «# » → заголовок, «- » → список, «[] » → чек-лист и т. п.</summary>
    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (e.Text != " " || !Selection.IsEmpty || CaretPosition.Paragraph is not { } p) return;
        var info = Info(p);
        string before = TextBeforeCaret(p, CaretPosition);
        MdBlock? to = null;
        bool done = false;
        if (info.Type == MdBlock.Text)
        {
            to = before switch
            {
                "#" => MdBlock.H1,
                "##" => MdBlock.H2,
                "###" => MdBlock.H3,
                "-" or "*" or "•" => MdBlock.Bullet,
                ">" => MdBlock.Quote,
                "[]" or "[ ]" or "-[]" or "- [ ]" => MdBlock.Todo,
                "[x]" or "[X]" or "- [x]" => MdBlock.Todo,
                _ => NumberPrefix.IsMatch(before) ? MdBlock.Number : null,
            };
            done = before.Contains('x', StringComparison.OrdinalIgnoreCase);
        }
        else if (info.Type == MdBlock.Bullet && before is "[]" or "[ ]" or "[x]" or "[X]")
        {
            to = MdBlock.Todo;
            done = before.Contains('x', StringComparison.OrdinalIgnoreCase);
        }
        if (to == null) return;

        e.Handled = true;
        BeginChange();
        var start = p.Inlines.FirstInline is InlineUIContainer m && Equals(m.Tag, MarkerTag) ? m.ElementEnd : p.ContentStart;
        new TextRange(start, CaretPosition).Text = "";
        info.Type = to.Value;
        info.Done = done;
        EndChange();
        Normalize();
        KeepCaretAfterMarker();
        MarkdownChanged?.Invoke();
    }

    // ======================= буфер обмена =======================

    private void OnPreviewCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.Cut)
        {
            if (Selection.IsEmpty) return;
            e.Handled = true;
            var data = new DataObject();
            data.SetText(Serialize(Selection.Start, Selection.End));
            try
            {
                using var ms = new MemoryStream();
                new TextRange(Selection.Start, Selection.End).Save(ms, DataFormats.Rtf);
                data.SetData(DataFormats.Rtf, Encoding.UTF8.GetString(ms.ToArray()));
            }
            catch { }
            try { Clipboard.SetDataObject(data, true); } catch { }
            if (e.Command == ApplicationCommands.Cut) Selection.Text = "";
        }
        else if (e.Command == ApplicationCommands.Paste)
        {
            e.Handled = true; // картинки и прочее в заметку не вставляем
            string text;
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { return; }
            if (text.Length > 0) InsertMarkdown(text.Replace("\r\n", "\n"));
        }
    }

    /// <summary>Вставка: Markdown превращается в оформленный текст.</summary>
    private void InsertMarkdown(string md)
    {
        BeginChange();
        Selection.Text = "";
        var lines = md.Split('\n');
        var p = CaretPosition.Paragraph;
        if (lines.Length == 1 && p != null && PlainText(p).Trim().Length > 0)
        {
            // Одна строка в непустой абзац — вставляем как оформленный фрагмент.
            var temp = new Paragraph();
            AddInlines(temp, Regex.Replace(lines[0], @"^(#{1,3}\s|[-*]\s\[[ xX]\]\s|[-*]\s|\d+\.\s|>\s)", ""));
            var pos = CaretPosition;
            foreach (var run in temp.Inlines.OfType<Run>().ToList())
            {
                var r = new Run(run.Text, pos);
                CopyFormat(run, r);
                pos = r.ElementEnd;
            }
            CaretPosition = pos;
        }
        else if (p != null)
        {
            var after = CaretPosition.InsertParagraphBreak();
            var tail = after.Paragraph;
            Paragraph anchor = p;
            Paragraph? last = null;
            foreach (var line in lines)
            {
                var np = ParseLine(line);
                Document.Blocks.InsertAfter(anchor, np);
                anchor = np;
                last = np;
            }
            if (PlainText(p).Trim().Length == 0 && Info(p).Type == MdBlock.Text) Document.Blocks.Remove(p);
            if (tail != null && PlainText(tail).Trim().Length == 0 && Info(tail).Type == MdBlock.Text) Document.Blocks.Remove(tail);
            if (last != null) CaretPosition = last.ContentEnd;
        }
        EndChange();
        Normalize();
        MarkdownChanged?.Invoke();
    }

    private static void CopyFormat(Run from, Run to)
    {
        to.FontWeight = from.FontWeight;
        to.FontStyle = from.FontStyle;
        if (from.TextDecorations is { Count: > 0 }) to.TextDecorations = from.TextDecorations;
        if (from.Background != null) to.Background = from.Background;
        if (from.ReadLocalValue(TextElement.ForegroundProperty) != DependencyProperty.UnsetValue) to.Foreground = from.Foreground;
    }

    // ======================= Markdown → документ =======================

    private static readonly Regex BlockPrefix = new(@"^(?<todo>[-*]\s\[(?<x>[ xX])\]\s)|^(?<h>#{1,3})\s|^(?<bullet>[-*])\s|^(?<num>\d+)\.\s|^(?<quote>>)\s?", RegexOptions.Compiled);
    private static readonly Regex SpanOpen = new(@"^<span style=""color:\s*#(?<c>[0-9A-Fa-f]{6})"">", RegexOptions.Compiled);

    private Paragraph ParseLine(string line)
    {
        var info = new BlockInfo(MdBlock.Text);
        string rest = line;
        if (line.StartsWith('\\') && line.Length > 1 && BlockPrefix.IsMatch(line[1..])) rest = line[1..];
        else if (BlockPrefix.Match(line) is { Success: true } m)
        {
            rest = line[m.Length..];
            if (m.Groups["todo"].Success) { info.Type = MdBlock.Todo; info.Done = m.Groups["x"].Value.Trim().Length > 0; }
            else if (m.Groups["h"].Success) info.Type = m.Groups["h"].Value.Length switch { 1 => MdBlock.H1, 2 => MdBlock.H2, _ => MdBlock.H3 };
            else if (m.Groups["bullet"].Success) info.Type = MdBlock.Bullet;
            else if (m.Groups["num"].Success) info.Type = MdBlock.Number;
            else if (m.Groups["quote"].Success) info.Type = MdBlock.Quote;
        }
        var p = new Paragraph { Tag = info };
        AddInlines(p, rest);
        return p;
    }

    private static void AddInlines(Paragraph p, string s)
    {
        bool bold = false, italic = false, underline = false, strike = false, mark = false;
        var colors = new Stack<Color>();
        var buf = new StringBuilder();

        void Flush()
        {
            if (buf.Length == 0) return;
            var r = new Run(buf.ToString());
            if (bold) r.FontWeight = FontWeights.Bold;
            if (italic) r.FontStyle = FontStyles.Italic;
            if (underline || strike)
            {
                var d = new TextDecorationCollection();
                if (underline) d.Add(TextDecorations.Underline[0]);
                if (strike) d.Add(TextDecorations.Strikethrough[0]);
                r.TextDecorations = d;
            }
            if (mark) r.Background = Highlight;
            if (colors.Count > 0) r.Foreground = Frozen(colors.Peek());
            p.Inlines.Add(r);
            buf.Clear();
        }

        for (int i = 0; i < s.Length; i++)
        {
            string at = s[i..];
            if (s[i] == '\\' && i + 1 < s.Length) { buf.Append(s[++i]); continue; }
            if (at.StartsWith("**")) { Flush(); bold = !bold; i++; continue; }
            if (at.StartsWith("~~")) { Flush(); strike = !strike; i++; continue; }
            if (at.StartsWith("==")) { Flush(); mark = !mark; i++; continue; }
            if (at.StartsWith("<u>")) { Flush(); underline = true; i += 2; continue; }
            if (at.StartsWith("</u>")) { Flush(); underline = false; i += 3; continue; }
            if (at.StartsWith("<br>")) { Flush(); p.Inlines.Add(new LineBreak()); i += 3; continue; }
            if (at.StartsWith("</span>")) { Flush(); if (colors.Count > 0) colors.Pop(); i += 6; continue; }
            if (SpanOpen.Match(at) is { Success: true } sm)
            {
                Flush();
                colors.Push((Color)ColorConverter.ConvertFromString("#" + sm.Groups["c"].Value));
                i += sm.Length - 1;
                continue;
            }
            if (s[i] == '*') { Flush(); italic = !italic; continue; }
            buf.Append(s[i]);
        }
        Flush();
    }

    // ======================= документ → Markdown =======================

    private readonly record struct Fmt(bool Bold, bool Italic, bool Underline, bool Strike, bool Mark, Color? Color);

    /// <summary>Весь документ (from = null) или выделенный фрагмент — в Markdown.</summary>
    private string Serialize(TextPointer? from, TextPointer? to)
    {
        var lines = new List<string>();
        int number = 0;
        foreach (var p in Paragraphs)
        {
            var info = Info(p);
            number = info.Type == MdBlock.Number ? number + 1 : 0;
            if (from != null && (p.ContentEnd.CompareTo(from) < 0 || p.ContentStart.CompareTo(to) > 0)) continue;

            var contentStart = p.Inlines.FirstInline is InlineUIContainer m && Equals(m.Tag, MarkerTag) ? m.ElementEnd : p.ContentStart;
            bool fromStart = from == null || from.CompareTo(contentStart) <= 0;
            string prefix = !fromStart ? "" : info.Type switch
            {
                MdBlock.H1 => "# ",
                MdBlock.H2 => "## ",
                MdBlock.H3 => "### ",
                MdBlock.Bullet => "- ",
                MdBlock.Number => $"{number}. ",
                MdBlock.Todo => info.Done ? "- [x] " : "- [ ] ",
                MdBlock.Quote => "> ",
                _ => "",
            };
            string body = InlineMarkdown(p, info, from, to);
            if (prefix.Length == 0 && fromStart && BlockPrefix.IsMatch(body)) body = "\\" + body;
            lines.Add(prefix + body);
        }
        return string.Join("\n", lines);
    }

    private static string InlineMarkdown(Paragraph p, BlockInfo info, TextPointer? from, TextPointer? to)
    {
        bool heading = info.Type is MdBlock.H1 or MdBlock.H2 or MdBlock.H3;
        var parts = new List<(string Text, Fmt Fmt)>();
        foreach (var inline in Flatten(p.Inlines))
        {
            if (inline is LineBreak lb)
            {
                if (from == null || (lb.ContentStart.CompareTo(from) >= 0 && lb.ContentStart.CompareTo(to) <= 0)) parts.Add(("<br>", default));
                continue;
            }
            if (inline is not Run run) continue;
            string text;
            if (from == null) text = run.Text;
            else
            {
                var a = run.ContentStart.CompareTo(from) < 0 ? from : run.ContentStart;
                var b = run.ContentEnd.CompareTo(to) > 0 ? to! : run.ContentEnd;
                if (a.CompareTo(b) >= 0) continue;
                text = new TextRange(a, b).Text;
            }
            if (text.Length == 0) continue;
            parts.Add((Escape(text), Format(run, p, heading)));
        }

        var sb = new StringBuilder();
        Fmt cur = default;
        foreach (var (text, fmt) in parts)
        {
            if (text == "<br>") { sb.Append(Close(cur)); cur = default; sb.Append(text); continue; }
            if (fmt != cur) { sb.Append(Close(cur)); sb.Append(Open(fmt)); cur = fmt; }
            sb.Append(text);
        }
        sb.Append(Close(cur));
        return sb.ToString();
    }

    private static IEnumerable<Inline> Flatten(InlineCollection inlines)
    {
        foreach (var i in inlines)
        {
            if (i is Span span) foreach (var c in Flatten(span.Inlines)) yield return c;
            else yield return i;
        }
    }

    private static Fmt Format(Run run, Paragraph p, bool heading)
    {
        bool underline = false, strike = false, mark = false;
        Color? color = null;
        for (DependencyObject? o = run; o is Inline inl; o = inl.Parent as DependencyObject)
        {
            if (inl.TextDecorations != null)
                foreach (var d in inl.TextDecorations)
                {
                    if (d.Location == TextDecorationLocation.Underline) underline = true;
                    if (d.Location == TextDecorationLocation.Strikethrough) strike = true;
                }
            if (inl.Background is SolidColorBrush bg && bg.Color.A > 0) mark = true;
            if (color == null && inl.ReadLocalValue(TextElement.ForegroundProperty) != DependencyProperty.UnsetValue
                && inl.Foreground is SolidColorBrush fg && fg.Color != DefaultColor)
                color = fg.Color;
        }
        bool bold = !heading && run.FontWeight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight();
        return new Fmt(bold, run.FontStyle == FontStyles.Italic, underline, strike, mark, color);
    }

    private static string Open(Fmt f) =>
        (f.Color is { } c ? $"<span style=\"color:#{c.R:X2}{c.G:X2}{c.B:X2}\">" : "")
        + (f.Mark ? "==" : "") + (f.Bold ? "**" : "") + (f.Italic ? "*" : "") + (f.Underline ? "<u>" : "") + (f.Strike ? "~~" : "");

    private static string Close(Fmt f) =>
        (f.Strike ? "~~" : "") + (f.Underline ? "</u>" : "") + (f.Italic ? "*" : "") + (f.Bold ? "**" : "") + (f.Mark ? "==" : "")
        + (f.Color != null ? "</span>" : "");

    private static readonly Regex TagLike = new(@"<(?=/?(u|span|br)\b)", RegexOptions.Compiled);

    private static string Escape(string s)
    {
        s = s.Replace("\\", "\\\\").Replace("*", "\\*").Replace("~~", "\\~\\~").Replace("==", "\\=\\=");
        return TagLike.Replace(s, "\\<");
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ======================= чек-бокс =======================

    /// <summary>Круглый чек-бокс в духе Apple Notes; щелчок отмечает пункт с лёгким «пружинным» отскоком.</summary>
    private sealed class TodoBox : Border
    {
        private readonly IconGlyph _check;
        private readonly ScaleTransform _scale = new();
        private bool _checked;

        public TodoBox(bool isChecked, Action toggle)
        {
            Width = Height = 17;
            CornerRadius = new CornerRadius(5);
            BorderThickness = new Thickness(1.6);
            Cursor = Cursors.Arrow;
            RenderTransformOrigin = new Point(0.5, 0.5);
            RenderTransform = _scale;
            _check = new IconGlyph { Data = (Geometry)Application.Current.FindResource("IconCheck"), Width = 12, Height = 12, StrokeWidth = 2.6, Foreground = Brushes.Black };
            Child = _check;
            SetChecked(isChecked, animate: false);
            MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                SetChecked(!_checked, animate: true);
                toggle();
            };
        }

        public void SetChecked(bool value, bool animate)
        {
            _checked = value;
            Background = value ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)) : Brushes.Transparent;
            BorderBrush = value ? Background : new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
            _check.Opacity = value ? 1 : 0;
            if (!animate) return;
            var pop = new DoubleAnimation(0.7, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.7 } };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        }
    }
}
