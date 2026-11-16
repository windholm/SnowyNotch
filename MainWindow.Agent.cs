using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SnowyNotch.Controls;
using SnowyNotch.Services;
using SnowyNotch.Views;

namespace SnowyNotch;

/// <summary>Сервис Agents: Claude Code (HTTP-хуки) и Codex (журналы сессий + notify), лимиты обоих.</summary>
public partial class MainWindow
{
    private AgentBridge _agentBridge = null!;
    private readonly AgentTracker _agents = new();
    private CodexWatcher _codex = null!;
    private readonly ClaudeUsage _claudeUsage = new();
    private AgentView? _agentView;
    private bool _claudeBubbleShown, _claudeBubbleWanted;
    private readonly DispatcherTimer _claudeDoneHide = new() { Interval = TimeSpan.FromSeconds(8) };

    private static readonly Color ClaudeColor = Color.FromRgb(0xE0, 0x78, 0x4F);
    private static readonly Color CodexColor = Color.FromRgb(0x4A, 0x9E, 0xFF);

    private void InitAgents()
    {
        _codex = new CodexWatcher(_agents);
        _agentBridge = new AgentBridge(Dispatcher);
        _agentBridge.EventReceived += (kind, e) =>
        {
            if (!_settings.ShowAgents) return;
            if (kind == AgentKind.Claude) _agents.Handle(e);
            else _codex.HandleNotify(e);
        };
        _agents.Changed += RefreshAgents;
        _agents.StatusChanged += OnAgentStatus;
        _codex.UsageChanged += RefreshAgents;
        _claudeUsage.Changed += RefreshAgents;
        _claudeDoneHide.Tick += (_, _) =>
        {
            _claudeDoneHide.Stop();
            _claudeBubbleWanted = false;
            UpdateBubbles();
        };
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.CodexEnabled) or nameof(AppSettings.ShowAgents) or nameof(AppSettings.ClaudeUsageEnabled))
                SyncAgentSources();
        };
        SyncAgentSources();
        Closed += (_, _) => _agentBridge.Dispose();
    }

    private void SyncAgentSources()
    {
        _codex.Enabled = _settings.CodexEnabled && _settings.ShowAgents;
        _claudeUsage.Enabled = _settings.ClaudeUsageEnabled;
        RefreshAgents();
    }

    private AgentView EnsureAgentView()
    {
        if (_agentView == null)
        {
            _agentView = new AgentView();
            _agentView.ConnectionsRequested += () => OpenSettings("agents");
            _agentView.BackRequested += () => ShowView(View.Widgets, -1);
            _agentView.RefreshRequested += kind =>
            {
                if (kind == AgentKind.Claude) _ = _claudeUsage.RefreshAsync();
                else _codex.RefreshUsage();
            };
            _agentView.LayoutChanged += () => { if (_view == View.Agent) SetTargetsAfterLayout(); };
            AgentHost.Content = _agentView;
        }
        return _agentView;
    }

    private AgentsSnapshot Snapshot() => new(
        _agents, _claudeUsage.Usage, _codex.Usage,
        ClaudeHooksInstaller.IsInstalled(), _settings.CodexEnabled);

    /// <summary>Открыть сервис Agents (с карточки виджета — страница лимитов).</summary>
    public void OpenAgents()
    {
        EnsureAgentView().ShowUsage();
        RefreshAgents();
        ShowView(View.Agent, 1);
    }

    private void RefreshAgents()
    {
        _agentView?.Render(Snapshot());
        if (_view == View.Agent) SetTargetsAfterLayout();
        UpdateClaudeBubble();
    }

    private void OnAgentStatus(AgentSession s, AgentStatus status)
    {
        _claudeDoneHide.Stop();
        _claudeBubbleWanted = status != AgentStatus.Idle;
        if (status == AgentStatus.Done) _claudeDoneHide.Start();
        UpdateBubbles();

        if (status is not (AgentStatus.Done or AgentStatus.NeedsYou)) return;
        if (IsGhosted || !_settings.ShowAgents) return;
        // Пользователь сейчас сам что-то смотрит в острове (в том числе лимиты) — ничего не переключаем под курсором.
        if (_state == IslandState.Expanded && (_hoverActive || _pinned || _customizing || (_sticky && _view != View.Agent))) return;

        // Агент закончил или ждёт ответа — показываем прогресс задачи.
        EnsureAgentView().ShowActivity();
        RefreshAgents();
        if (_view != View.Agent) ShowView(View.Agent);
        _sticky = false;
        Peek(status == AgentStatus.NeedsYou ? 5 : 3.5);
    }

    private void UpdateClaudeBubble()
    {
        var s = _agents.Current;
        if (s == null) return;
        bool codex = s.Agent == AgentKind.Codex;
        ClaudeBuddy.Visibility = codex ? Visibility.Collapsed : Visibility.Visible;
        CodexBubbleLogo.Visibility = codex ? Visibility.Visible : Visibility.Collapsed;
        CodexBubbleLogo.Busy = s.Status == AgentStatus.Working;
        ClaudeBuddy.Mood = s.Status switch
        {
            AgentStatus.Working => BuddyMood.Busy,
            AgentStatus.NeedsYou => BuddyMood.Wait,
            AgentStatus.Done => BuddyMood.Done,
            _ => BuddyMood.Idle,
        };

        ClaudeRingSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        ClaudeRing.BeginAnimation(OpacityProperty, null);
        ClaudeRing.Opacity = 1;
        // Бесконечные анимации кольца — только пока пузырь на экране (иначе цикл отрисовки не засыпает).
        bool animate = _claudeBubbleShown;
        switch (s.Status)
        {
            case AgentStatus.Working:
                ClaudeRing.RingBrush = new SolidColorBrush(codex ? CodexColor : ClaudeColor);
                ClaudeRing.Progress = 0.28;
                if (animate)
                    ClaudeRingSpin.BeginAnimation(RotateTransform.AngleProperty,
                        new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
                break;
            case AgentStatus.NeedsYou:
                ClaudeRing.RingBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A));
                ClaudeRing.Progress = 1;
                // Ждёт ответа — кольцо «дышит», чтобы было заметно и в свёрнутом острове.
                if (animate)
                    ClaudeRing.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(750))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                    });
                break;
            case AgentStatus.Done:
                ClaudeRing.RingBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x84));
                ClaudeRing.Progress = 1;
                break;
        }
    }

    private void ClaudeBubble_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        EnsureAgentView().ShowActivity();
        RefreshAgents();
        ShowView(View.Agent, 1);
        _hoverActive = true;
        UpdateState();
    }
}
