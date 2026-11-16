using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SnowyNotch.Services;
using SnowyNotch.Widgets;
using SnowyNotch.Localization;

namespace SnowyNotch.Views;

public partial class SettingsView : UserControl
{
    private readonly AppSettings _settings;

    public SettingsView(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        DataContext = settings;

        var v = typeof(App).Assembly.GetName().Version;
        VersionText.Text = v != null ? L.F("Версия {0}.{1}.{2}", v.Major, v.Minor, v.Build) : "";

        SectionSubtitle.Text = L.T("Язык, раскрытие, скрытие и запуск острова");
        BuildLanguageChips();
        BuildSwatches();
        RefreshWidgets();
        RefreshNavStyle();
        settings.PropertyChanged += OnSettingChanged;
    }

    public event Action? CloseRequested;
    public event Action? CustomizeRequested;

    public void ShowSection(string name)
    {
        var nav = name switch
        {
            "look" => NavLook,
            "widgets" => NavWidgets,
            "media" => NavMedia,
            "claude" or "agents" => NavClaude,
            "devices" or "audio" => NavDevices,
            "navigation" => NavNavigation,
            "about" => NavAbout,
            _ => NavGeneral,
        };
        nav.IsChecked = true;
    }

    /// <summary>Чипы языков: «Как в системе» и каждый язык своим названием (English, Svenska…).</summary>
    private void BuildLanguageChips()
    {
        LanguageChips.Children.Clear();
        var options = new System.Collections.Generic.List<(string Code, string Title)> { ("", L.T("Как в системе")) };
        foreach (var lang in L.Languages) options.Add((lang.Code, lang.Native));

        foreach (var (code, title) in options)
        {
            var chip = new RadioButton
            {
                Style = (Style)FindResource("ChipToggle"),
                Content = title,
                GroupName = "Language",
                Margin = new Thickness(0, 0, 6, 6),
                IsChecked = _settings.Language == code,
                Tag = code,
            };
            chip.Checked += (_, _) =>
            {
                string chosen = (string)chip.Tag;
                if (_settings.Language == chosen) return;
                string before = L.Resolve(_settings.Language);
                _settings.Language = chosen;
                // Язык не изменился по факту (например, «Как в системе» = текущий) — перезапуск не нужен.
                if (L.Resolve(chosen) != before) App.Relaunch("general");
            };
            LanguageChips.Children.Add(chip);
        }
    }

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.HomeWidgets)) RefreshWidgets();
        if (e.PropertyName == nameof(AppSettings.Navigation)) RefreshNavStyle();
        if (e.PropertyName == nameof(AppSettings.AccentColor)) SyncSwatches();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (Sections == null || sender is not RadioButton rb) return;
        (StackPanel target, string subtitle) =
            rb == NavLook ? (SecLook, L.T("Форма, отделка, цвет и размер острова")) :
            rb == NavWidgets ? (SecWidgets, L.T("Что стоит на главной странице острова")) :
            rb == NavMedia ? (SecMedia, L.T("Плеер, тексты песен и какие источники считать музыкой")) :
            rb == NavDevices ? (SecDevices, L.T("Громкость программ, HUD, наушники и батарея")) :
            rb == NavClaude ? (SecClaude, L.T("Claude Code и Codex: ход работы и лимиты")) :
            rb == NavNavigation ? (SecNavigation, L.T("Как листать страницы и все сочетания клавиш")) :
            rb == NavAbout ? (SecAbout, L.T("Версия и сброс настроек")) :
            (SecGeneral, L.T("Язык, раскрытие, скрытие и запуск острова"));

        foreach (UIElement child in Sections.Children)
            child.Visibility = child == target ? Visibility.Visible : Visibility.Collapsed;

        SectionTitle.Text = rb.Content as string ?? "";
        SectionSubtitle.Text = subtitle;
        Scroller.ScrollToTop();
        if (target == SecClaude) RefreshClaude();
        if (target == SecDevices) RefreshDevices();
        if (target == SecNavigation) RefreshShortcuts();

        target.Opacity = 0;
        var tt = new TranslateTransform(0, 8);
        target.RenderTransform = tt;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    private void BuildSwatches()
    {
        foreach (var hex in AppSettings.AccentPresets)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                GroupName = "Accent",
                Background = new SolidColorBrush(color),
                Tag = hex,
                ToolTip = hex,
            };
            rb.Checked += (_, _) => _settings.AccentColor = hex;
            Swatches.Children.Add(rb);
        }
        SyncSwatches();
    }

    private void SyncSwatches()
    {
        foreach (RadioButton rb in Swatches.Children)
            rb.IsChecked = string.Equals(rb.Tag as string, _settings.AccentColor, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshWidgets()
    {
        CurrentWidgets.Children.Clear();
        foreach (var id in _settings.HomeWidgets)
        {
            var info = WidgetCatalog.Find(id);
            if (info == null) continue;
            var chip = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x33, info.Tint.R, info.Tint.G, info.Tint.B)),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 4, 10, 4),
                Margin = new Thickness(0, 0, 6, 6),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new SnowyNotch.Controls.IconGlyph
                        {
                            Data = (Geometry)FindResource(info.IconKey),
                            Foreground = new SolidColorBrush(info.Tint),
                            Width = 16, Height = 16,
                        },
                        new TextBlock { Text = info.Title, FontSize = 12, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            CurrentWidgets.Children.Add(chip);
        }
    }

    private void RefreshNavStyle() => NavStyleRow.IsEnabled = _settings.ButtonsEnabled;

    private void RefreshClaude(string? error = null)
    {
        bool on = ClaudeHooksInstaller.IsInstalled();
        ClaudeConnectButton.Content = on ? L.T("Отключить") : L.T("Подключить");
        ClaudeConnectButton.Background = on
            ? new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromRgb(0xE0, 0x78, 0x4F));
        ClaudeStatusText.Text = error ?? (on
            ? L.T("Подключено — остров получает события Claude Code")
            : L.T("Не подключено"));
        RefreshCodex();
    }

    private void RefreshCodex(string? message = null)
    {
        bool on = _settings.CodexEnabled;
        CodexConnectButton.Content = on ? L.T("Отключить") : L.T("Подключить");
        CodexConnectButton.Background = on
            ? new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xFF));
        CodexStatusText.Text = message ?? (!on ? L.T("Не подключено")
            : CodexIntegration.NotifyInstalled() ? L.T("Подключено — ход работы, конец хода и лимиты")
            : L.T("Подключено — ход работы и лимиты из журналов (notify занят другим, конец хода по паузе)"));
    }

    private void CodexConnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_settings.CodexEnabled)
            {
                CodexIntegration.UninstallNotify();
                _settings.CodexEnabled = false;
                RefreshCodex();
                return;
            }
            bool notify = CodexIntegration.InstallNotify();
            _settings.CodexEnabled = true;
            RefreshCodex(notify ? null
                : L.T("Подключено. В ~/.codex/config.toml уже есть свой notify — его не трогаем, конец хода определяется по паузе."));
        }
        catch (Exception ex)
        {
            // Журналы читать можно и без notify.
            _settings.CodexEnabled = true;
            RefreshCodex(L.F("Подключено без notify: не получилось изменить ~/.codex/config.toml ({0})", ex.Message));
        }
    }

    private void ClaudeConnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ClaudeHooksInstaller.IsInstalled()) ClaudeHooksInstaller.Uninstall();
            else ClaudeHooksInstaller.Install();
            RefreshClaude();
        }
        catch (Exception ex)
        {
            RefreshClaude(L.F("Не получилось изменить ~/.claude/settings.json: {0}", ex.Message));
        }
    }

    public event Action? AudioRequested;

    private void OpenAudio_Click(object sender, RoutedEventArgs e) => AudioRequested?.Invoke();

    private void RefreshDevices()
    {
        AudioHotkeyText.Text = App.AudioHotkeyLabel is { } hk
            ? L.F("Громкость каждой программы отдельно. Открыть из любого места: {0}", hk)
            : L.T("Громкость каждой программы отдельно. Горячая клавиша занята другой программой — откройте через «Виджеты»");
        bool battery = _settings.HasBattery;
        BatterySwitch.IsEnabled = battery;
        BatteryRow.Opacity = battery ? 1 : 0.55;
        BatteryHint.Text = battery
            ? L.T("Показывает подключение зарядки и предупреждает при 20 % и 10 %")
            : L.T("Батарея не найдена — это не ноутбук, настройка не нужна");
    }

    private void RefreshShortcuts()
    {
        ShortcutList.Children.Clear();
        var items = new (string Keys, string What)[]
        {
            ("Win + /", L.T("Открыть или закрыть настройки")),
            (App.AudioHotkeyLabel ?? "—", L.T("Звук: громкость программ")),
            (L.T("Правый клик по острову"), L.T("Настройки")),
            (L.T("Клик колёсиком по острову"), L.T("Сделать прозрачным / вернуть")),
            (L.T("Колёсико над островом"), L.T("Листать Главную → Трей → Виджеты")),
            ("Ctrl + 1 / 2 / 3", L.T("Заметки: заголовок")),
            ("Ctrl + Shift + L", L.T("Заметки: чек-лист")),
            ("Ctrl + B / I / U", L.T("Заметки: жирный, курсив, подчёркнутый")),
            ("Esc", L.T("Закрыть открытую панель")),
        };
        for (int i = 0; i < items.Length; i++)
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            row.Children.Add(new TextBlock { Text = items[i].What, Style = (Style)FindResource("Label"), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right, CornerRadius = new CornerRadius(7), Padding = new Thickness(8, 3, 8, 3),
                Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1, 1, 1, 2),
                Child = new TextBlock { Text = items[i].Keys, FontSize = 12, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI") },
            });
            ShortcutList.Children.Add(row);
            if (i < items.Length - 1) ShortcutList.Children.Add(new Border { Style = (Style)FindResource("Divider") });
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void Customize_Click(object sender, RoutedEventArgs e) => CustomizeRequested?.Invoke();
    private void Reset_Click(object sender, RoutedEventArgs e) => _settings.ResetToDefaults();
    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
