using System;
using System.Windows;
using System.Windows.Media;

namespace SnowyNotch.Controls;

public sealed class AudioBars : FrameworkElement
{
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(AudioBars),
        new PropertyMetadata(false, (d, _) => ((AudioBars)d).EnsureRendering()));

    public static readonly DependencyProperty BarColorProperty = DependencyProperty.Register(
        nameof(BarColor), typeof(Color), typeof(AudioBars),
        new FrameworkPropertyMetadata(Colors.White, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, e) => ((AudioBars)d).UpdateBrush((Color)e.NewValue)));

    public static readonly DependencyProperty BarCountProperty = DependencyProperty.Register(
        nameof(BarCount), typeof(int), typeof(AudioBars),
        new FrameworkPropertyMetadata(6, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((AudioBars)d).ResetBars()));

    private const double IdleLevel = 0.0;

    private readonly Random _rng = new();
    private double[] _levels = Array.Empty<double>();
    private double[] _targets = Array.Empty<double>();
    private double[] _retargetIn = Array.Empty<double>();
    private Brush _brush = Brushes.White;
    private bool _subscribed;
    private TimeSpan _lastFrame;

    public AudioBars()
    {
        ResetBars();
        UpdateBrush(BarColor);
        Loaded += (_, _) => EnsureRendering();
        Unloaded += (_, _) => Unsubscribe();
        IsVisibleChanged += (_, _) => { if (IsVisible) EnsureRendering(); else Unsubscribe(); };
    }

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    public Color BarColor
    {
        get => (Color)GetValue(BarColorProperty);
        set => SetValue(BarColorProperty, value);
    }

    public int BarCount
    {
        get => (int)GetValue(BarCountProperty);
        set => SetValue(BarCountProperty, value);
    }

    private void ResetBars()
    {
        int n = Math.Max(1, BarCount);
        _levels = new double[n];
        _targets = new double[n];
        _retargetIn = new double[n];
        InvalidateVisual();
    }

    private void UpdateBrush(Color c)
    {
        var lighter = Color.FromRgb(
            (byte)Math.Min(255, c.R + 60),
            (byte)Math.Min(255, c.G + 60),
            (byte)Math.Min(255, c.B + 60));
        var brush = new LinearGradientBrush(lighter, c, 90);
        brush.Freeze();
        _brush = brush;
    }

    private void EnsureRendering()
    {
        if (!IsLoaded || !IsVisible || _subscribed) return;
        CompositionTarget.Rendering += OnFrame;
        _subscribed = true;
        _lastFrame = TimeSpan.Zero;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        CompositionTarget.Rendering -= OnFrame;
        _subscribed = false;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _lastFrame == TimeSpan.Zero ? 1 / 60.0 : (now - _lastFrame).TotalSeconds;
        if (dt <= 0) return;
        _lastFrame = now;
        dt = Math.Min(dt, 0.1);

        int n = _levels.Length;
        bool playing = IsPlaying;
        bool settled = true;

        for (int i = 0; i < n; i++)
        {
            if (playing)
            {
                _retargetIn[i] -= dt;
                if (_retargetIn[i] <= 0)
                {
                    double x = n == 1 ? 0 : (i / (double)(n - 1)) * 2 - 1;
                    double envelope = 1.0 - 0.45 * x * x;
                    _targets[i] = (0.25 + 0.75 * _rng.NextDouble()) * envelope;
                    _retargetIn[i] = 0.08 + _rng.NextDouble() * 0.12;
                }
            }
            else
            {
                _targets[i] = IdleLevel;
            }

            double speed = playing ? 16 : 10;
            _levels[i] += (_targets[i] - _levels[i]) * Math.Min(1, dt * speed);
            if (Math.Abs(_levels[i] - _targets[i]) > 0.005) settled = false;
        }

        InvalidateVisual();

        if (!playing && settled) Unsubscribe();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        int n = _levels.Length;
        if (w <= 0 || h <= 0 || n == 0) return;

        const double gapRatio = 0.75;
        double barW = w / (n + (n - 1) * gapRatio);
        double gap = barW * gapRatio;

        for (int i = 0; i < n; i++)
        {
            double bh = Math.Max(barW, _levels[i] * h);
            double x = i * (barW + gap);
            double y = (h - bh) / 2;
            dc.DrawRoundedRectangle(_brush, null, new Rect(x, y, barW, bh), barW / 2, barW / 2);
        }
    }
}
