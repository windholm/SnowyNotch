using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using SnowyNotch.Controls;
using SnowyNotch.Services;
using SnowyNotch.Localization;

namespace SnowyNotch.Widgets;

/// <summary>
/// Встроенные заметки: список (заголовок, время, превью; копировать/удалить по наведению) и редактор
/// с живым Markdown и панелью форматирования. Переходы между ними — сдвиг с затуханием.
/// </summary>
public partial class NotesWidget : UserControl, IBackNavigable
{
    private static CultureInfo Ru => L.Culture; // язык интерфейса
    private static readonly Brush RowBg = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private static readonly Brush RowHover = Frozen(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Secondary = Frozen(Color.FromArgb(0x8E, 0xFF, 0xFF, 0xFF));

    private readonly NotesStore _store;
    private Note? _note;
    private bool _loading;
    private readonly DispatcherTimer _savedFade = new() { Interval = TimeSpan.FromSeconds(1.2) };

    public NotesWidget(NotesStore store)
    {
        InitializeComponent();
        _store = store;
        Editor.MarkdownChanged += OnEdited;
        _savedFade.Tick += (_, _) => { _savedFade.Stop(); SavedText.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(400))); };

        foreach (var c in MarkdownEditor.Palette)
        {
            var swatch = new Button
            {
                Style = (Style)FindResource("IconButton"), Width = 26, Height = 26, Margin = new Thickness(2, 0, 2, 0),
                Content = new Ellipse { Width = 16, Height = 16, Fill = new SolidColorBrush(c), Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), StrokeThickness = 1 },
            };
            swatch.Click += (_, _) =>
            {
                ColorPopup.IsOpen = false;
                ColorDot.Fill = new SolidColorBrush(c);
                Editor.SetColor(c);
            };
            Swatches.Children.Add(swatch);
        }
        BuildList(animate: false);
    }

    public event Action? KeyboardRequested;

    // ======================= список =======================

    private void BuildList(bool animate)
    {
        Rows.Children.Clear();
        var notes = _store.Notes.Where(n => !n.IsEmpty).ToList();
        CountText.Text = notes.Count > 0 ? notes.Count.ToString() : "";
        EmptyList.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ListScroll.Visibility = notes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        int i = 0;
        foreach (var note in notes)
        {
            var row = MakeRow(note);
            Rows.Children.Add(row);
            if (animate)
            {
                var begin = TimeSpan.FromMilliseconds(30 + i * 35);
                row.Opacity = 0;
                var tt = new TranslateTransform(0, 10);
                row.RenderTransform = tt;
                row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { BeginTime = begin });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(360))
                {
                    BeginTime = begin, EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
                });
            }
            i++;
        }
    }

    private Border MakeRow(Note note)
    {
        var title = new TextBlock { Text = note.Title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var meta = new TextBlock { FontSize = 12.5, Foreground = Secondary, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        meta.Inlines.Add(new System.Windows.Documents.Run(When(note.Updated)) { Foreground = Brushes.White, FontWeight = FontWeights.SemiBold });
        if (note.Preview.Length > 0) meta.Inlines.Add(new System.Windows.Documents.Run("   " + note.Preview));

        var copy = ActionButton("IconCopy", L.T("Скопировать как Markdown"));
        var delete = ActionButton("IconTrash", L.T("Удалить"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Opacity = 0, Children = { copy, delete } };

        var grid = new Grid();
        grid.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 70, 0), Children = { title, meta } });
        grid.Children.Add(actions);

        var row = new Border
        {
            CornerRadius = new CornerRadius(14), Background = RowBg, Padding = new Thickness(14, 10, 8, 10),
            Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand, Child = grid,
        };
        row.MouseEnter += (_, _) => { row.Background = RowHover; actions.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160))); };
        row.MouseLeave += (_, _) => { row.Background = RowBg; actions.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200))); };
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; Open(note); };

        copy.Click += (_, _) => { Copy(note); Flash(copy); };
        delete.Click += (_, _) => Collapse(row, () => { _store.Delete(note); BuildList(false); });
        return row;
    }

    private Button ActionButton(string icon, string tip)
    {
        var b = new Button
        {
            Style = (Style)FindResource("IconButton"), Width = 30, Height = 30, Margin = new Thickness(2, 0, 0, 0),
            Background = Frozen(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), ToolTip = tip,
            Content = new IconGlyph { Data = (Geometry)FindResource(icon), Width = 15, Height = 15 },
        };
        b.PreviewMouseLeftButtonUp += (_, e) => e.Handled = false;
        b.MouseLeftButtonUp += (_, e) => e.Handled = true;
        return b;
    }

    private static string When(DateTime t)
    {
        var d = DateTime.Now.Date;
        if (t.Date == d) return t.ToString("HH:mm", Ru);
        if (t.Date == d.AddDays(-1)) return L.T("Вчера");
        if (t > d.AddDays(-7)) return Ru.TextInfo.ToTitleCase(t.ToString("dddd", Ru));
        return t.ToString("d MMM", Ru);
    }

    private static void Collapse(FrameworkElement row, Action done)
    {
        var anim = new DoubleAnimation(row.ActualHeight, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        anim.Completed += (_, _) => done();
        row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(180)));
        row.BeginAnimation(HeightProperty, anim);
    }

    // ======================= переходы =======================

    private void Open(Note note)
    {
        _note = note;
        _loading = true;
        Editor.Load(note.IsEmpty ? "# " : note.Markdown);
        _loading = false;
        Placeholder.Visibility = Editor.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        SavedText.Text = "";
        Slide(ListPage, EditorPage, forward: true);
    }

    public bool TryGoBack()
    {
        if (EditorPage.Visibility != Visibility.Visible) return false;
        Back_Click(this, new RoutedEventArgs());
        return true;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _note = null;
        _store.DropEmpty();
        BuildList(animate: true);
        Slide(EditorPage, ListPage, forward: false);
    }

    private static void Slide(FrameworkElement from, FrameworkElement to, bool forward)
    {
        from.Visibility = Visibility.Collapsed;
        to.Visibility = Visibility.Visible;
        var tt = new TranslateTransform(forward ? 28 : -28, 0);
        to.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(340)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
        to.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        Open(_store.Create());
        FocusEditor();
    }

    // ======================= редактор =======================

    private void OnEdited()
    {
        Placeholder.Visibility = Editor.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        if (_loading || _note == null) return;
        _store.Update(_note, Editor.Markdown);
        SavedText.Text = L.T("Сохранено");
        SavedText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        _savedFade.Stop();
        _savedFade.Start();
    }

    private void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Editor.IsKeyboardFocusWithin) return;
        FocusEditor();
    }

    private void FocusEditor()
    {
        KeyboardRequested?.Invoke();
        Dispatcher.InvokeAsync(() => { Editor.Focus(); Keyboard.Focus(Editor); }, DispatcherPriority.Input);
    }

    private void CopyNote_Click(object sender, RoutedEventArgs e)
    {
        if (_note == null) return;
        _store.Update(_note, Editor.Markdown);
        Copy(_note);
        Flash(CopyButton);
    }

    private void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (_note != null) _store.Delete(_note);
        _note = null;
        BuildList(animate: true);
        Slide(EditorPage, ListPage, forward: false);
    }

    private static void Copy(Note note)
    {
        try { Clipboard.SetText(note.Markdown); } catch { }
    }

    /// <summary>Подтверждение копирования: иконка кнопки на секунду становится галочкой.</summary>
    private void Flash(Button b)
    {
        if (b.Content is not IconGlyph g) return;
        var was = g.Data;
        g.Data = (Geometry)FindResource("IconCheck");
        g.Foreground = Frozen(Color.FromRgb(0x30, 0xD1, 0x58));
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.1) };
        t.Tick += (_, _) => { t.Stop(); g.Data = was; g.Foreground = Brushes.White; };
        t.Start();
    }

    private void Bold_Click(object sender, RoutedEventArgs e) => Editor.ToggleBold();
    private void Italic_Click(object sender, RoutedEventArgs e) => Editor.ToggleItalic();
    private void Underline_Click(object sender, RoutedEventArgs e) => Editor.ToggleUnderline();
    private void Strike_Click(object sender, RoutedEventArgs e) => Editor.ToggleStrike();
    private void Highlight_Click(object sender, RoutedEventArgs e) => Editor.ToggleHighlight();
    private void Color_Click(object sender, RoutedEventArgs e) => ColorPopup.IsOpen = !ColorPopup.IsOpen;
    private void H1_Click(object sender, RoutedEventArgs e) => Editor.ApplyBlock(MdBlock.H1);
    private void H2_Click(object sender, RoutedEventArgs e) => Editor.ApplyBlock(MdBlock.H2);
    private void H3_Click(object sender, RoutedEventArgs e) => Editor.ApplyBlock(MdBlock.H3);
    private void Bullet_Click(object sender, RoutedEventArgs e) => Editor.ApplyBlock(MdBlock.Bullet);
    private void Number_Click(object sender, RoutedEventArgs e) => Editor.ApplyBlock(MdBlock.Number);
    private void Todo_Click(object sender, RoutedEventArgs e) => Editor.ApplyBlock(MdBlock.Todo);
    private void Quote_Click(object sender, RoutedEventArgs e) => Editor.ApplyBlock(MdBlock.Quote);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
