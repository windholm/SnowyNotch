using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SnowyNotch.Services;
using SnowyNotch.Localization;

namespace SnowyNotch.Widgets;

/// <summary>Таймер: обычный на любое время (линейка до 3 часов) или классический помидоро 25/5/15.</summary>
public partial class TimerWidget : UserControl
{
    private static readonly Brush Orange = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static readonly Brush DotEmpty = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    private readonly TimerTool _timer = null!;
    private bool _syncing;

    public TimerWidget(TimerTool timer)
    {
        InitializeComponent();
        _timer = timer;
        DataContext = timer;
        timer.PropertyChanged += OnTimerChanged;
        Unloaded += (_, _) => timer.PropertyChanged -= OnTimerChanged;
        Loaded += (_, _) => { timer.PropertyChanged -= OnTimerChanged; timer.PropertyChanged += OnTimerChanged; Sync(); };
        Sync();
    }

    private void OnTimerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TimerTool.IsRunning) or nameof(TimerTool.Duration) or nameof(TimerTool.Mode)
            or nameof(TimerTool.Phase) or nameof(TimerTool.CompletedInCycle) or nameof(TimerTool.IsActive)) Sync();
    }

    private void Sync()
    {
        bool pomodoro = _timer.Mode == TimerMode.Pomodoro;
        _syncing = true;
        (pomodoro ? PomodoroChip : SimpleChip).IsChecked = true;
        _syncing = false;
        // Режим меняется только на остановленном таймере — чтобы случайный клик не сбил отсчёт.
        SimpleChip.IsEnabled = PomodoroChip.IsEnabled = !_timer.IsRunning;
        SimpleChip.Opacity = PomodoroChip.Opacity = _timer.IsRunning ? 0.5 : 1;

        StartText.Text = _timer.IsRunning ? L.T("Пауза") : _timer.IsActive ? L.T("Дальше") : L.T("Старт");

        Ruler.Visibility = pomodoro ? Visibility.Collapsed : Visibility.Visible;
        PomodoroPanel.Visibility = pomodoro ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = pomodoro ? Visibility.Visible : Visibility.Collapsed;

        if (!pomodoro)
        {
            Ruler.IsEnabled = !_timer.IsRunning;
            Ruler.Opacity = _timer.IsRunning ? 0.45 : 1;
            if (!_timer.IsRunning) Ruler.Value = Math.Max(1, Math.Round(_timer.Duration.TotalMinutes));
            ResetButton.ToolTip = L.T("Сбросить");
            return;
        }

        int done = _timer.CompletedInCycle;
        PhaseText.Text = _timer.PhaseTitle;
        PhaseText.Foreground = _timer.Phase == PomodoroPhase.Focus ? Orange : Brushes.White;
        PhaseHint.Text = _timer.Phase switch
        {
            PomodoroPhase.Focus => L.F("Помидор {0} из {1} · 25 мин", Math.Min(done + 1, TimerTool.PomodorosPerCycle), TimerTool.PomodorosPerCycle),
            PomodoroPhase.ShortBreak => L.T("5 мин отдыха, потом фокус"),
            _ => L.T("Цикл пройден · 15 мин отдыха"),
        };
        ResetButton.ToolTip = _timer.IsActive ? L.T("Сбросить этап") : L.T("Начать цикл заново");
        BuildDots(done);
    }

    private void BuildDots(int done)
    {
        Dots.Children.Clear();
        for (int i = 0; i < TimerTool.PomodorosPerCycle; i++)
        {
            bool filled = i < done;
            bool current = i == done && _timer.Phase == PomodoroPhase.Focus;
            Dots.Children.Add(new Ellipse
            {
                Width = 9, Height = 9, Margin = new Thickness(i == 0 ? 0 : 5, 0, 0, 0),
                Fill = filled ? Orange : current ? Frozen(Color.FromArgb(0x55, 0xFF, 0x9F, 0x0A)) : DotEmpty,
                Stroke = current ? Orange : null, StrokeThickness = 1.4,
            });
        }
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || _timer == null) return;
        _timer.SetMode(sender == PomodoroChip ? TimerMode.Pomodoro : TimerMode.Simple);
        Sync();
    }

    private void Ruler_Committed(int minutes)
    {
        if (!_timer.IsRunning) _timer.SetDuration(TimeSpan.FromMinutes(minutes));
    }

    private void Start_Click(object sender, RoutedEventArgs e) => _timer.Toggle();
    private void Reset_Click(object sender, RoutedEventArgs e) => _timer.Reset();
    private void Skip_Click(object sender, RoutedEventArgs e) => _timer.Skip();

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
