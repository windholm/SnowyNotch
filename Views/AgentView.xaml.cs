using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SnowyNotch.Controls;
using SnowyNotch.Services;
using SnowyNotch.Localization;

namespace SnowyNotch.Views;

/// <summary>Что нужно виду Agents: сессии, лимиты и какие интеграции подключены.</summary>
public sealed record AgentsSnapshot(AgentTracker Tracker, AgentUsage Claude, AgentUsage Codex, bool ClaudeConnected, bool CodexConnected);

/// <summary>
/// Сервис Agents. Две страницы:
/// «Лимиты» — кольца Claude и Codex (остаток лимита) и карточка выбранного агента: план, 5-часовой и недельный
/// лимит со временем сброса, что агент делает сейчас;
/// «Прогресс» — шаги, файл и статус текущей задачи (открывается сама, когда агент закончил или ждёт ответа).
/// </summary>
public partial class AgentView : UserControl
{
    private const int VisibleSteps = 4;
    private const double RingOffset = 61; // центр кольца от середины: (112 + 10) / 2

    private static readonly Brush ClaudeBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x78, 0x4F)));
    private static readonly Brush CodexBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x9E, 0xFF)));
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84)));
    private static readonly Brush Yellow = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x5A, 0x4E)));
    private static readonly Brush Dim = Freeze(new SolidColorBrush(Color.FromArgb(0x8E, 0xFF, 0xFF, 0xFF)));
    private static CultureInfo Ru => L.Culture; // язык интерфейса

    private readonly HashSet<string> _shownSteps = new();
    private string _sessionId = "";
    private AgentStatus _lastStatus = (AgentStatus)(-1);
    private bool _shimmering;
    private bool _activity;
    private AgentKind? _selected;
    private AgentsSnapshot? _snap;
    private readonly Dictionary<AgentKind, (double Used, DateTime? At)> _lastFive = new();
    private readonly HashSet<AgentKind> _resetSeen = new();

    private readonly DispatcherTimer _dotsTimer = new() { Interval = TimeSpan.FromMilliseconds(420) };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };
    private string _statusBase = "";
    private bool _statusAnimated;
    private int _dots;

    public AgentView()
    {
        InitializeComponent();
        _dotsTimer.Tick += (_, _) =>
        {
            _dots = (_dots + 1) % 4;
            StatusText.Text = _statusBase + new string('.', _dots);
        };
        _clock.Tick += (_, _) => { if (_snap != null) RenderUsage(_snap); }; // «сброс через…» и «обновлено…» идут сами
        IsVisibleChanged += (_, _) =>
        {
            UpdateDotsTimer();
            if (IsVisible) _clock.Start(); else _clock.Stop();
            // Бесконечные анимации (пульс статуса, мигание шага, блик полосы) держат цикл отрисовки окна
            // активным даже на скрытом экране. Пока Agents не виден — они стоят; при показе — снова идут.
            if (IsVisible)
            {
                if (_snap != null) Render(_snap);
                if (_lastStatus is AgentStatus.Working or AgentStatus.NeedsYou) StartPulse();
            }
            else PauseAnimations();
        };
    }

    public event Action? ConnectionsRequested;
    public event Action<AgentKind>? RefreshRequested;
    /// <summary>Сменилась страница — размер острова надо пересчитать.</summary>
    public event Action? LayoutChanged;

    public bool IsActivityPage => _activity;

    public void ShowActivity() => SetPage(true);
    public void ShowUsage() => SetPage(false);

    public void Render(AgentsSnapshot snap)
    {
        _snap = snap;
        RenderUsage(snap);
        RenderActivity(snap);
    }

    // =================== страницы ===================

    private void SetPage(bool activity)
    {
        if (_activity == activity) return;
        _activity = activity;
        var show = activity ? (FrameworkElement)ActivityPage : UsagePage;
        var hide = activity ? (FrameworkElement)UsagePage : ActivityPage;
        hide.Visibility = Visibility.Collapsed;
        show.Visibility = Visibility.Visible;

        var tt = new TranslateTransform(activity ? 14 : -14, 0);
        show.RenderTransform = tt;
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        show.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));

        HeaderIcon.Data = (Geometry)FindResource(activity ? "IconChecklist" : "IconUsage");
        HeaderText.Text = activity ? L.T("Прогресс") : L.T("Лимиты");
        PageButtonIcon.Data = (Geometry)FindResource(activity ? "IconUsage" : "IconList");
        PageButton.ToolTip = activity ? L.T("Лимиты") : L.T("Прогресс задачи");
        if (_snap != null) Render(_snap);
        LayoutChanged?.Invoke();
    }

    public event Action? BackRequested;

    /// <summary>Назад: со страницы «Прогресс» — к лимитам, с лимитов — к панели сервисов.</summary>
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_activity) SetPage(false);
        else BackRequested?.Invoke();
    }

    private void PageButton_Click(object sender, RoutedEventArgs e) => SetPage(!_activity);
    private void NowRow_Click(object sender, MouseButtonEventArgs e) => SetPage(true);
    private void Connections_Click(object sender, RoutedEventArgs e) => ConnectionsRequested?.Invoke();

    private void ClaudeRing_Click(object sender, RoutedEventArgs e) => Select(AgentKind.Claude);
    private void CodexRing_Click(object sender, RoutedEventArgs e) => Select(AgentKind.Codex);

    private void Select(AgentKind agent)
    {
        bool changed = _selected != agent;
        _selected = agent;
        if (_snap != null) RenderUsage(_snap);
        if (!changed) return;
        DetailCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(220)));
        var tt = new TranslateTransform(0, 6);
        DetailCard.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
        LayoutChanged?.Invoke();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshSpin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        });
        RefreshRequested?.Invoke(_selected ?? AgentKind.Claude);
    }

    // =================== «Лимиты» ===================

    private void RenderUsage(AgentsSnapshot snap)
    {
        bool anyConnected = snap.ClaudeConnected || snap.CodexConnected;
        ConnectionsButton.Background = new SolidColorBrush(anyConnected ? Color.FromArgb(0x2E, 0x30, 0xD1, 0x58) : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        ConnectionsIcon.Foreground = anyConnected ? new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)) : Dim;
        ConnectionsButton.ToolTip = anyConnected ? L.T("Подключения агентов") : L.T("Подключить Claude Code или Codex");

        var claudeSession = Latest(snap.Tracker, AgentKind.Claude);
        var codexSession = Latest(snap.Tracker, AgentKind.Codex);
        RenderRing(ClaudeRing, ClaudeLeftText, ClaudeLogo, snap.Claude, snap.ClaudeConnected, claudeSession);
        RenderRing(CodexRing, CodexLeftText, CodexLogo, snap.Codex, snap.CodexConnected, codexSession);

        var agent = _selected ?? snap.Tracker.Current?.Agent ?? (snap.ClaudeConnected || !snap.CodexConnected ? AgentKind.Claude : AgentKind.Codex);
        bool claude = agent == AgentKind.Claude;
        var usage = claude ? snap.Claude : snap.Codex;
        bool connected = claude ? snap.ClaudeConnected : snap.CodexConnected;
        var session = claude ? claudeSession : codexSession;
        var brush = claude ? ClaudeBrush : CodexBrush;

        CardArrowShift.X = claude ? -RingOffset : RingOffset;
        ClaudeRingButton.Opacity = claude ? 1 : 0.7;
        CodexRingButton.Opacity = claude ? 0.7 : 1;

        DetailLogo.Agent = agent;
        DetailLogo.Busy = session?.Status == AgentStatus.Working;
        DetailName.Text = claude ? "Claude" : "Codex";
        DetailPlan.Text = !connected ? L.T("Не подключён") : usage.Plan.Length > 0 ? usage.Plan : claude ? "Claude Code" : "Codex CLI";

        bool hasLimits = usage.FiveHour != null || usage.Weekly != null;
        LimitsPanel.Visibility = connected && hasLimits ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        ConnectButton.Background = brush;

        string? message = !connected
            ? claude
                ? L.T("Подключите Claude Code: остров покажет шаги и статус, а с вашего согласия — лимиты подписки.")
                : L.T("Подключите Codex: остров будет показывать ход работы и лимиты из журналов Codex на этом компьютере.")
            : hasLimits ? null : usage.Error ?? L.T("Лимиты появятся после первого запроса");
        DetailMessage.Text = message ?? "";
        DetailMessage.Visibility = message != null ? Visibility.Visible : Visibility.Collapsed;

        if (hasLimits)
        {
            Limit(usage.FiveHour, FiveResetText, FiveLeftText, FiveBar, FiveFill, brush);
            Limit(usage.Weekly, WeekResetText, WeekLeftText, WeekBar, WeekFill, brush);
            RenderExtra(usage, brush);
            BreakdownText.Text = usage.Breakdown.Count > 0
                ? L.F("Неделя ушла на: {0}", string.Join(" · ", usage.Breakdown.Select(b => $"{b.Name} {b.Percent:0}%")))
                : "";
            BreakdownText.Visibility = usage.Breakdown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // Что делает сейчас.
        NowCircle.Fill = new SolidColorBrush(Color.FromArgb(0x2E, ((SolidColorBrush)brush).Color.R, ((SolidColorBrush)brush).Color.G, ((SolidColorBrush)brush).Color.B));
        NowIcon.Foreground = brush;
        (NowTitle.Text, NowSub.Text) = session?.Status switch
        {
            AgentStatus.Working => (L.T("Работает над задачей"), session.CurrentStep?.Text ?? session.Task),
            AgentStatus.NeedsYou => (L.T("Ждёт вашего ответа"), session.Note.Length > 0 ? session.Note : session.Task),
            AgentStatus.Done => (L.T("Готово"), session.Note.Length > 0 ? session.Note : session.Task),
            _ => (L.T("Свободен"), connected ? L.T("Нет активных задач") : L.T("Не подключён")),
        };

        // Строка состояния данных.
        UpdatedRow.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        bool reset = DetectReset(agent, usage);
        if (usage.Error != null && !hasLimits)
        {
            UpdatedDot.Fill = Yellow;
            UpdatedGlyph.Data = (Geometry)FindResource("IconInfo");
            UpdatedText.Foreground = Yellow;
            UpdatedText.Text = connected ? L.T("Нет данных о лимитах") : "";
        }
        else
        {
            UpdatedDot.Fill = Green;
            UpdatedGlyph.Data = (Geometry)FindResource("IconCheck");
            UpdatedText.Foreground = Green;
            UpdatedText.Text = reset ? L.T("Лимит сброшен") : usage.UpdatedUtc is { } at ? L.F("Обновлено {0}", Ago(at)) : L.T("Ждём данные");
        }
    }

    private static AgentSession? Latest(AgentTracker t, AgentKind agent) =>
        t.Sessions.Where(s => s.Agent == agent).OrderByDescending(s => s.LastActivity).FirstOrDefault();

    // Последние показанные значения: анимируем только когда цифра правда изменилась,
    // а не на каждое событие агента (иначе кольца и полоски дёргаются).
    private readonly Dictionary<FrameworkElement, double> _shown = new();

    private void RenderRing(RingProgress ring, TextBlock left, AgentLogo logo, AgentUsage usage, bool connected, AgentSession? session)
    {
        // Как на странице сервиса: кольцо и подпись — сколько использовано.
        double? pct = connected ? usage.UsedPercent : null;
        double target = (pct ?? 0) / 100;
        ring.RingBrush = usage.IsHigh ? HighRed : ring == ClaudeRing ? ClaudeBrush : CodexBrush;
        if (!_shown.TryGetValue(ring, out double was) || Math.Abs(was - target) > 0.0005)
        {
            _shown[ring] = target;
            ring.BeginAnimation(RingProgress.ProgressProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(600))
            {
                EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
            });
        }
        left.Text = !connected ? L.T("не подключён") : pct is { } p ? L.F("{0:0}% использовано", p) : session?.Status == AgentStatus.Working ? L.T("работает") : L.T("нет данных");
        left.Foreground = connected && usage.IsHigh ? HighRed : MetaGray;
        logo.Busy = session?.Status == AgentStatus.Working;
        logo.Opacity = connected ? 1 : 0.5;
    }

    // Цвета как на странице лимитов Claude: синяя полоска в норме, красная — когда сервис предупреждает.
    private static readonly Brush HighRed = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)));
    private static readonly Brush NormalBlue = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)));
    private static readonly Brush MetaGray = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));

    private static bool High(LimitWindow w) =>
        w.Severity is "warning" or "critical" or "exceeded" or "blocked" || (w.Severity.Length == 0 && w.UsedPercent >= 80);

    private void Limit(LimitWindow? w, TextBlock reset, TextBlock used, Grid bar, Border fill, Brush brush)
    {
        if (w == null)
        {
            reset.Text = "";
            used.Text = "—";
            SetFill(bar, fill, 0);
            return;
        }
        bool high = High(w);
        reset.Text = ResetText(w.ResetsAtUtc);
        used.Text = L.F("{0:0}% использовано", Math.Clamp(w.UsedPercent, 0, 100));
        used.Foreground = high ? HighRed : MetaGray;
        fill.Background = high ? HighRed : NormalBlue;
        SetFill(bar, fill, Math.Clamp(w.UsedPercent, 0, 100) / 100);
    }

    /// <summary>Заполнение полоски в долях; ширина пересчитывается и при изменении размера (карточка могла быть скрыта).</summary>
    private void SetFill(Grid bar, Border fill, double fraction)
    {
        bool changed = !_shown.TryGetValue(fill, out double was) || Math.Abs(was - fraction) > 0.0005;
        _shown[fill] = fraction;
        if (bar.Tag == null)
        {
            bar.Tag = fill;
            bar.SizeChanged += (_, e) =>
            {
                if (e.WidthChanged && _shown.TryGetValue(fill, out double f))
                {
                    fill.BeginAnimation(WidthProperty, null);
                    fill.Width = e.NewSize.Width * f;
                }
            };
        }
        if (bar.ActualWidth <= 0) return; // посчитается в SizeChanged
        if (!changed && fill.ActualWidth > 0) return;
        fill.BeginAnimation(WidthProperty, new DoubleAnimation(bar.ActualWidth * fraction, TimeSpan.FromMilliseconds(600))
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void RenderExtra(AgentUsage usage, Brush brush)
    {
        // Как на официальной странице: лимиты по отдельным моделям видны, только когда они реально расходуются.
        var extra = usage.Extra.Where(x => x.UsedPercent > 0).ToList();
        usage = new AgentUsage { Agent = usage.Agent, Extra = extra };
        // Строки пересоздаём, только если поменялся их набор.
        string key = string.Join("|", usage.Extra.Select(x => x.Label));
        if (ExtraLimits.Tag as string != key)
        {
            ExtraLimits.Tag = key;
            ExtraLimits.Children.Clear();
            foreach (var x in usage.Extra)
            {
                var title = new TextBlock { Text = x.Label, Style = (Style)FindResource("LimitTitle") };
                var reset = new TextBlock { Style = (Style)FindResource("LimitMeta") };
                var left = new TextBlock { Style = (Style)FindResource("LimitLeft") };
                var head = new Grid { Margin = new Thickness(0, 14, 0, 0) };
                head.Children.Add(title);
                head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { reset, left } });
                var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
                var bar = new Grid { Height = 6, Margin = new Thickness(0, 8, 0, 0) };
                bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)) });
                bar.Children.Add(fill);
                ExtraLimits.Children.Add(new StackPanel { Children = { head, bar }, Tag = (reset, left, bar, fill) });
            }
        }
        for (int i = 0; i < usage.Extra.Count && i < ExtraLimits.Children.Count; i++)
        {
            if (ExtraLimits.Children[i] is StackPanel { Tag: ValueTuple<TextBlock, TextBlock, Grid, Border> parts })
                Limit(usage.Extra[i], parts.Item1, parts.Item2, parts.Item3, parts.Item4, brush);
        }
    }

    private static readonly string[] OnDay =
        { L.T("в воскресенье"), L.T("в понедельник"), L.T("во вторник"), L.T("в среду"), L.T("в четверг"), L.T("в пятницу"), L.T("в субботу") };

    /// <summary>Как на странице сервиса — точное время сброса: «Сброс в 16:50», «Сброс завтра в 09:00», «Сброс в субботу в 11:00».</summary>
    private static string ResetText(DateTime? atUtc)
    {
        if (atUtc is not { } at) return "";
        if (at <= DateTime.UtcNow) return L.T("Сбрасывается");
        var local = at.ToLocalTime();
        string time = local.ToString("HH:mm", Ru);
        var today = DateTime.Now.Date;
        if (local.Date == today) return L.F("Сброс в {0}", time);
        if (local.Date == today.AddDays(1)) return L.F("Сброс завтра в {0}", time);
        if (local.Date < today.AddDays(7)) return L.F("Сброс {0} в {1}", OnDay[(int)local.DayOfWeek], time);
        return L.F("Сброс: {0}, {1}", local.ToString("d MMMM", Ru), time);
    }

    private static string Ago(DateTime atUtc)
    {
        var d = DateTime.UtcNow - atUtc;
        if (d < TimeSpan.FromMinutes(1)) return L.T("только что");
        if (d < TimeSpan.FromHours(1)) return L.F("{0} мин назад", (int)d.TotalMinutes);
        if (d < TimeSpan.FromDays(1)) return L.F("{0} ч назад", (int)d.TotalHours);
        return atUtc.ToLocalTime().ToString("d MMM HH:mm", Ru);
    }

    /// <summary>Расход 5-часового окна резко упал или окно сменилось — значит лимит сбросился.</summary>
    private bool DetectReset(AgentKind agent, AgentUsage usage)
    {
        if (usage.FiveHour is not { } five) return _resetSeen.Contains(agent);
        if (_lastFive.TryGetValue(agent, out var prev))
        {
            bool newWindow = prev.At is { } a && five.ResetsAtUtc is { } b && b - a > TimeSpan.FromMinutes(30);
            if (prev.Used - five.UsedPercent >= 20 || (newWindow && five.UsedPercent < prev.Used)) _resetSeen.Add(agent);
            else if (five.UsedPercent > prev.Used) _resetSeen.Remove(agent);
        }
        _lastFive[agent] = (five.UsedPercent, five.ResetsAtUtc);
        return _resetSeen.Contains(agent);
    }

    // =================== «Прогресс» ===================

    private void RenderActivity(AgentsSnapshot snap)
    {
        var s = snap.Tracker.Current;
        int active = snap.Tracker.ActiveCount;
        bool connected = snap.ClaudeConnected || snap.CodexConnected;

        FolderPill.Visibility = _activity && s is { Folder.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        FolderText.Text = s?.Folder ?? "";
        SessionsPill.Visibility = _activity && active > 1 ? Visibility.Visible : Visibility.Collapsed;
        SessionsText.Text = L.F("Сессий: {0}", active);

        if (s == null)
        {
            WorkArea.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = Visibility.Visible;
            AgentNameText.Text = L.T("Агенты");
            ShowBuddy(AgentKind.Claude);
            EmptyTitle.Text = connected ? L.T("Агенты пока ничем не заняты") : L.T("Агенты не подключены");
            EmptyHint.Text = connected
                ? L.T("Дайте задачу в Claude Code или Codex — здесь появятся шаги, файлы и статус.")
                : L.T("Подключите Claude Code или Codex в Настройки → Agents, и остров будет показывать, как идёт работа.");
            SetStatus(connected ? L.T("Ждёт задачу") : L.T("Не подключены"), connected);
            StatusText.Foreground = Dim;
            StatusDot.Fill = Dim;
            StopPulse();
            Buddy.Mood = BuddyMood.Idle;
            _lastStatus = (AgentStatus)(-1);
            TaskText.Text = "";
            return;
        }

        WorkArea.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        AgentNameText.Text = s.AgentName;
        ShowBuddy(s.Agent);

        if (s.Id != _sessionId)
        {
            _sessionId = s.Id;
            _shownSteps.Clear();
            _lastStatus = (AgentStatus)(-1);
        }

        var accent = s.Agent == AgentKind.Codex ? CodexBrush : ClaudeBrush;
        ApplyStatus(s, accent);
        RenderSteps(s, accent);
        RenderFile(s, accent);
        TaskText.Text = s.Status is AgentStatus.NeedsYou or AgentStatus.Done && s.Note.Length > 0 ? s.Note : s.Task;
    }

    private void ShowBuddy(AgentKind agent)
    {
        Buddy.Visibility = agent == AgentKind.Claude ? Visibility.Visible : Visibility.Collapsed;
        CodexBuddy.Visibility = agent == AgentKind.Codex ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string text, bool animated)
    {
        if (text != _statusBase) _dots = 0;
        _statusBase = text;
        _statusAnimated = animated;
        StatusText.Text = animated ? text + new string('.', _dots) : text;
        UpdateDotsTimer();
    }

    private void UpdateDotsTimer()
    {
        if (_statusAnimated && IsVisible) _dotsTimer.Start();
        else _dotsTimer.Stop();
    }

    private void ApplyStatus(AgentSession s, Brush accent)
    {
        (string text, Brush brush, BuddyMood mood) = s.Status switch
        {
            AgentStatus.Working => (L.T("Работает над задачей"), accent, BuddyMood.Busy),
            AgentStatus.NeedsYou => (L.T("Ждёт вашего ответа"), Yellow, BuddyMood.Wait),
            AgentStatus.Done => (L.T("Готово"), Green, BuddyMood.Done),
            _ => (L.T("Ждёт задачу"), Dim, BuddyMood.Idle),
        };
        SetStatus(text, s.Status != AgentStatus.Done);
        StatusText.Foreground = brush;
        StatusDot.Fill = brush;
        CodexBuddy.Busy = s.Status == AgentStatus.Working;

        if (s.Status == _lastStatus) return;
        _lastStatus = s.Status;
        Buddy.Mood = mood;
        if (s.Status is AgentStatus.Working or AgentStatus.NeedsYou) StartPulse(); else StopPulse();

        var tt = new TranslateTransform(0, 6);
        StatusText.RenderTransform = tt;
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
        StatusText.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(260)));
    }

    private void StartPulse()
    {
        if (!IsVisible) return; // запустится при показе экрана
        StatusDot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    /// <summary>Остановить все бесконечные анимации экрана (он скрыт).</summary>
    private void PauseAnimations()
    {
        StopPulse();
        StopShimmer();
        foreach (var row in Steps.Children.OfType<StackPanel>())
            if (row.Children.Count > 0) row.Children[0].BeginAnimation(OpacityProperty, null);
    }

    private void StopPulse()
    {
        StatusDot.BeginAnimation(OpacityProperty, null);
        StatusDot.Opacity = 1;
    }

    private void RenderSteps(AgentSession s, Brush accent)
    {
        Steps.Children.Clear();
        foreach (var step in s.Steps.Skip(Math.Max(0, s.Steps.Count - VisibleSteps)))
        {
            bool running = !step.Done && s.Status == AgentStatus.Working;
            var glyph = new IconGlyph
            {
                Data = (Geometry)FindResource(step.Failed ? "IconClose" : step.Done ? "IconCheck" : "GlyphPlay"),
                Filled = !step.Done && !step.Failed,
                Foreground = step.Failed ? Red : accent,
                Width = 18, Height = 18, StrokeWidth = 2.2,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var text = new TextBlock
            {
                Text = step.Text,
                FontSize = 15,
                Foreground = step.Done ? new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)) : Brushes.White,
                Margin = new Thickness(12, 0, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 9), Children = { glyph, text } };
            Steps.Children.Add(row);

            if (running && IsVisible)
            {
                glyph.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(600))
                {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                });
            }

            if (_shownSteps.Add(step.ToolUseId + step.Text))
            {
                var tt = new TranslateTransform(0, 12);
                row.RenderTransform = tt;
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(360)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
                row.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
            }
        }
    }

    private void RenderFile(AgentSession s, Brush accent)
    {
        var fs = s.FileStep;
        if (fs?.File == null)
        {
            FileCard.Visibility = Visibility.Collapsed;
            StopShimmer();
            return;
        }
        FileCard.Visibility = Visibility.Visible;
        FileName.Text = fs.File;
        FileName.Foreground = accent;

        if (s.FileStats.TryGetValue(fs.File, out var st) && (st.Added > 0 || st.Removed > 0))
        {
            AddedText.Text = $"+{st.Added}";
            RemovedText.Text = $"−{st.Removed}";
        }
        else
        {
            AddedText.Text = "";
            RemovedText.Text = "";
        }

        bool busy = !fs.Done && s.Status == AgentStatus.Working;
        FileStatus.Text = busy ? (fs.IsEdit ? L.T("Сохраняю…") : L.T("Читаю…")) : s.Status == AgentStatus.Done ? L.T("Готово") : (fs.IsEdit ? L.T("Изменён") : L.T("Прочитан"));

        if (busy) StartShimmer();
        else
        {
            StopShimmer();
            BarFill.Background = s.Status == AgentStatus.Done ? Green : accent;
            double w = Math.Max(1, Bar.ActualWidth > 0 ? Bar.ActualWidth : 200);
            BarFill.BeginAnimation(WidthProperty, new DoubleAnimation(w, TimeSpan.FromMilliseconds(420)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
        }
    }

    private void StartShimmer()
    {
        BarFill.BeginAnimation(WidthProperty, null);
        BarFill.Width = 0;
        if (_shimmering || !IsVisible) return;
        _shimmering = true;
        BarShimmer.Opacity = 1;
        double w = Bar.ActualWidth > 0 ? Bar.ActualWidth : 200;
        ShimmerShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-70, w, TimeSpan.FromMilliseconds(1100))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void StopShimmer()
    {
        if (!_shimmering) return;
        _shimmering = false;
        ShimmerShift.BeginAnimation(TranslateTransform.XProperty, null);
        BarShimmer.Opacity = 0;
    }

    private static Brush Freeze(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
