using System;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SnowyNotch.Services;

namespace SnowyNotch;

public partial class MainWindow
{
    private const double BubbleGap = 7;

    private bool _stopwatchBubbleShown, _timerBubbleShown;
    private bool _timerOnLeft; // перерыв Pomodoro — пузырь слева от острова

    private bool AnyToolActive => _tools.Timer.IsActive || _tools.Stopwatch.IsActive;

    private void InitTools()
    {
        _tools.Timer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TimerTool.IsActive)) { UpdateBubbles(); ApplyIdleVisibility(); }
            if (e.PropertyName == nameof(TimerTool.IsBreak)) OnTimerSideChanged();
        };
        _tools.Stopwatch.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(StopwatchTool.IsActive)) { UpdateBubbles(); ApplyIdleVisibility(); }
        };
        _tools.Timer.Finished += () =>
        {
            SystemSounds.Exclamation.Play();
            if (!_pinned && !_hoverActive && !IsGhosted)
            {
                OpenFocus("timer");
                _sticky = false;
                Peek(5);
            }
        };
    }

    private void UpdateBubbles()
    {
        bool collapsed = _state != IslandState.Expanded;
        SetBubble(StopwatchBubble, StopwatchBubbleScale, ref _stopwatchBubbleShown, collapsed && _tools.Stopwatch.IsActive);
        SetBubble(TimerBubble, TimerBubbleScale, ref _timerBubbleShown, collapsed && _tools.Timer.IsActive);
        SetBubble(HeadsetBubble, HeadsetBubbleScale, ref _headsetBubbleShown, collapsed && _headsetActive && _settings.ShowBluetooth);
        // Системные пузыри важнее сервисных: если слева уже стоит перерыв Pomodoro или секундомер,
        // Claude уступает место и вернётся, когда оно освободится.
        bool leftTakenBySystem = _tools.Stopwatch.IsActive || (_tools.Timer.IsActive && _tools.Timer.IsBreak);
        bool claudeWasShown = _claudeBubbleShown;
        SetBubble(ClaudeBubble, ClaudeBubbleScale, ref _claudeBubbleShown,
            collapsed && _claudeBubbleWanted && _settings.ShowAgents && !leftTakenBySystem);
        if (claudeWasShown != _claudeBubbleShown) UpdateClaudeBubble(); // включить/остановить анимацию кольца
        PositionBubbles(Island.Width > 0 ? Island.Width : _w.Value, Math.Max(0, _top.Value));
    }

    private void SetBubble(Border bubble, ScaleTransform scale, ref bool shown, bool show)
    {
        if (show == shown) return;
        shown = show;
        bubble.IsHitTestVisible = show;
        FadeVisibility(bubble, show ? 1 : 0, Ms(show ? 220 : 110), delayMs: show ? Ms(160) : 0);
        Scale(scale, show ? 1 : 0.4, Ms(show ? 360 : 130), delayMs: show ? Ms(160) : 0);
    }

    private void PositionBubbles(double islandWidth, double top)
    {
        double size = _profile.BubbleSize;
        // В режиме «Остров» пузырь крупнее капсулы — центрируем его по высоте компактного острова.
        double bubbleTop = _profile.Attached ? 2 : Math.Max(0, top - (size - _profile.Compact.H) / 2);
        foreach (var b in new[] { StopwatchBubble, TimerBubble, HeadsetBubble, ClaudeBubble })
        {
            b.Width = b.Height = size;
            b.CornerRadius = new CornerRadius(size / 2);
            b.Margin = new Thickness(0, bubbleTop, 0, 0);
        }
        // Слева от острова: перерыв Pomodoro (кофе), секундомер, Claude; справа: таймер фокуса, наушники.
        // Ближе к острову — тот, кто раньше в списке; скрытый пузырь стоит там, где появится.
        double shift = islandWidth / 2 + BubbleGap + size / 2;
        double step = size + BubbleGap;

        double x = shift;
        if (_timerOnLeft) Place(TimerBubbleShift, _timerBubbleShown, -1);
        Place(StopwatchBubbleShift, _stopwatchBubbleShown, -1);
        Place(ClaudeBubbleShift, _claudeBubbleShown, -1);

        x = shift;
        if (!_timerOnLeft) Place(TimerBubbleShift, _timerBubbleShown, 1);
        Place(HeadsetBubbleShift, _headsetBubbleShown, 1);

        void Place(TranslateTransform t, bool shown, int side)
        {
            t.X = side * x;
            if (shown) x += step;
        }
    }

    /// <summary>Таймер сменил режим фокус ↔ перерыв: пузырь переезжает на свою сторону —
    /// гаснет, переставляется и появляется заново, а не пролетает через остров.</summary>
    private async void OnTimerSideChanged()
    {
        bool left = _tools.Timer.IsBreak;
        if (left == _timerOnLeft) return;
        if (_timerBubbleShown)
        {
            SetBubble(TimerBubble, TimerBubbleScale, ref _timerBubbleShown, false);
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromMilliseconds(Ms(140)));
            if (_tools.Timer.IsBreak != left) return; // пока ждали, режим успели переключить обратно
        }
        _timerOnLeft = left;
        UpdateBubbles();
    }

    private void Bubble_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: string id })
        {
            OpenFocus(id);
            _hoverActive = true;
            Peek(4);
        }
    }
}
