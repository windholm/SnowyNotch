using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using SnowyNotch.Localization;

namespace SnowyNotch.Services;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected void Raise(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

public enum TimerMode { Simple, Pomodoro }

public enum PomodoroPhase { Focus, ShortBreak, LongBreak }

/// <summary>
/// Два режима. «Таймер» — обычный обратный отсчёт на любое время.
/// «Помидоро» — классическая техника: 25 мин фокуса → 5 мин перерыва, после каждого 4-го помидора — 15 мин.
/// Закончился фокус — перерыв стартует сам; закончился перерыв — следующий фокус ждёт нажатия «Старт».
/// </summary>
public sealed class TimerTool : Observable
{
    public static readonly TimeSpan FocusLength = TimeSpan.FromMinutes(25);
    public static readonly TimeSpan ShortBreakLength = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LongBreakLength = TimeSpan.FromMinutes(15);
    public const int PomodorosPerCycle = 4;

    private TimeSpan _simpleDuration = TimeSpan.FromMinutes(10);
    private TimeSpan _duration = TimeSpan.FromMinutes(10);
    private TimeSpan _remaining = TimeSpan.FromMinutes(10);
    private DateTime _endsAtUtc;
    private bool _isRunning;
    private TimerMode _mode = TimerMode.Simple;
    private PomodoroPhase _phase = PomodoroPhase.Focus;
    private int _completed; // помидоров, завершённых в текущем цикле из четырёх

    public TimeSpan Duration => _duration;
    public TimeSpan Remaining => _remaining;
    public bool IsRunning => _isRunning;

    public bool IsActive => _isRunning || _remaining < _duration;

    public TimerMode Mode => _mode;
    public PomodoroPhase Phase => _phase;
    public int CompletedInCycle => _completed;

    /// <summary>Идёт перерыв помидоро («кофе», пузырь слева от острова).</summary>
    public bool IsBreak => _mode == TimerMode.Pomodoro && _phase != PomodoroPhase.Focus;

    public string PhaseTitle => _phase switch
    {
        PomodoroPhase.ShortBreak => L.T("Перерыв"),
        PomodoroPhase.LongBreak => L.T("Длинный перерыв"),
        _ => L.T("Фокус"),
    };

    /// <summary>Сменить режим. Пока таймер идёт — нельзя, чтобы случайно не потерять отсчёт.</summary>
    public void SetMode(TimerMode mode)
    {
        if (_isRunning || mode == _mode) return;
        _mode = mode;
        _duration = _remaining = mode == TimerMode.Simple ? _simpleDuration : PhaseLength(_phase);
        RaisePomodoro();
        RaiseAll();
    }

    /// <summary>Помидоро: к следующему этапу, как будто текущий закончился (фокус засчитывается).</summary>
    public void Skip()
    {
        if (_mode != TimerMode.Pomodoro) return;
        _isRunning = false;
        Advance();
        RaiseAll();
    }

    private static TimeSpan PhaseLength(PomodoroPhase p) => p switch
    {
        PomodoroPhase.ShortBreak => ShortBreakLength,
        PomodoroPhase.LongBreak => LongBreakLength,
        _ => FocusLength,
    };

    private void Advance()
    {
        if (_phase == PomodoroPhase.Focus)
        {
            _completed = Math.Min(PomodorosPerCycle, _completed + 1);
            SetPhase(_completed >= PomodorosPerCycle ? PomodoroPhase.LongBreak : PomodoroPhase.ShortBreak);
            // Перерыв начинается сам — так в классической технике: прозвенело, отдыхаешь.
            _endsAtUtc = DateTime.UtcNow + _remaining;
            _isRunning = true;
        }
        else
        {
            if (_phase == PomodoroPhase.LongBreak) _completed = 0; // новый цикл из четырёх
            SetPhase(PomodoroPhase.Focus);
        }
    }

    private void SetPhase(PomodoroPhase phase)
    {
        _phase = phase;
        _duration = _remaining = PhaseLength(phase);
        RaisePomodoro();
    }

    private void RaisePomodoro() =>
        Raise(nameof(Mode), nameof(Phase), nameof(PhaseTitle), nameof(CompletedInCycle), nameof(IsBreak));

    public double Progress => _duration.TotalSeconds > 0 ? _remaining.TotalSeconds / _duration.TotalSeconds : 0;

    /// <summary>Кольцо пузыря: и в фокусе, и в перерыве показывает, сколько осталось, — полоска уходит.</summary>
    public double RingProgress => Progress;

    public string Display => Format(_remaining);

    public event Action? Finished;

    public void Toggle()
    {
        if (_isRunning) Pause(); else Start();
    }

    public void Start()
    {
        if (_remaining <= TimeSpan.Zero) _remaining = _duration;
        _endsAtUtc = DateTime.UtcNow + _remaining;
        _isRunning = true;
        RaiseAll();
    }

    public void Pause()
    {
        if (!_isRunning) return;
        _remaining = Clamp(_endsAtUtc - DateTime.UtcNow);
        _isRunning = false;
        RaiseAll();
    }

    /// <summary>Сброс. В помидоро повторный сброс (время уже полное) начинает цикл из четырёх заново.</summary>
    public void Reset()
    {
        bool alreadyReset = !_isRunning && _remaining >= _duration;
        _isRunning = false;
        if (_mode == TimerMode.Pomodoro && (alreadyReset || _phase != PomodoroPhase.Focus))
        {
            _completed = alreadyReset ? 0 : _completed;
            SetPhase(PomodoroPhase.Focus);
        }
        _remaining = _duration;
        RaiseAll();
    }

    /// <summary>Длительность обычного таймера (в помидоро длительности классические и не меняются).</summary>
    public void SetDuration(TimeSpan duration)
    {
        if (_mode != TimerMode.Simple) return;
        _simpleDuration = duration;
        _duration = duration;
        _remaining = duration;
        if (_isRunning) _endsAtUtc = DateTime.UtcNow + duration;
        RaiseAll();
    }

    public void Tick()
    {
        if (!_isRunning) return;
        _remaining = Clamp(_endsAtUtc - DateTime.UtcNow);
        if (_remaining <= TimeSpan.Zero)
        {
            _isRunning = false;
            if (_mode == TimerMode.Pomodoro) Advance();
            RaiseAll();
            Finished?.Invoke();
            return;
        }
        Raise(nameof(Remaining), nameof(Progress), nameof(RingProgress), nameof(Display));
    }

    private void RaiseAll() =>
        Raise(nameof(Duration), nameof(Remaining), nameof(IsRunning), nameof(IsActive), nameof(Progress), nameof(RingProgress), nameof(Display));

    private static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    private static string Format(TimeSpan t)
    {
        var s = (int)Math.Ceiling(t.TotalSeconds);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }
}

public sealed class StopwatchTool : Observable
{
    private readonly System.Diagnostics.Stopwatch _sw = new();

    public TimeSpan Elapsed => _sw.Elapsed;
    public bool IsRunning => _sw.IsRunning;
    public bool IsActive => _sw.IsRunning || _sw.Elapsed > TimeSpan.Zero;

    public double Progress => _sw.Elapsed.TotalSeconds % 60 / 60;

    public string Display
    {
        get
        {
            var t = _sw.Elapsed;
            return t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
                : $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
        }
    }

    public void Toggle()
    {
        if (_sw.IsRunning) _sw.Stop(); else _sw.Start();
        RaiseAll();
    }

    public void Reset()
    {
        _sw.Reset();
        RaiseAll();
    }

    public void Tick()
    {
        if (_sw.IsRunning) Raise(nameof(Elapsed), nameof(Progress), nameof(Display));
    }

    private void RaiseAll() => Raise(nameof(Elapsed), nameof(IsRunning), nameof(IsActive), nameof(Progress), nameof(Display));
}

public sealed class ToolsService
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public ToolsService()
    {
        _tick.Tick += (_, _) =>
        {
            Timer.Tick();
            Stopwatch.Tick();
            if (!Timer.IsRunning && !Stopwatch.IsRunning) _tick.Stop();
        };
        Timer.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TimerTool.IsRunning)) Sync(); };
        Stopwatch.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(StopwatchTool.IsRunning)) Sync(); };
    }

    public TimerTool Timer { get; } = new();
    public StopwatchTool Stopwatch { get; } = new();

    private void Sync()
    {
        if (Timer.IsRunning || Stopwatch.IsRunning) _tick.Start();
    }
}
