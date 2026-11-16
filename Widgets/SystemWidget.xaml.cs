using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SnowyNotch.Services;

namespace SnowyNotch.Widgets;

public partial class SystemWidget : UserControl, IIslandWidget
{
    private const int HistoryLength = 40;

    private readonly SystemStats _stats = new();
    private readonly Queue<double> _history = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _primed;

    public SystemWidget()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => Sample();
        SizeChanged += (_, _) => DrawGraph();
    }

    public void SetActive(bool active)
    {
        if (active)
        {
            if (!_primed)
            {
                double first = _stats.SampleCpu();
                for (int i = 0; i < HistoryLength; i++) _history.Enqueue(first);
                _primed = true;
            }
            Sample();
            _tick.Start();
        }
        else _tick.Stop();
    }

    private void Sample()
    {
        double cpu = _stats.SampleCpu();
        double ram = SystemStats.SampleMemory();

        CpuRing.Progress = cpu;
        RamRing.Progress = ram;
        CpuText.Text = $"{cpu * 100:0}%";
        RamText.Text = $"{ram * 100:0}%";

        _history.Enqueue(cpu);
        while (_history.Count > HistoryLength) _history.Dequeue();
        DrawGraph();
    }

    private void DrawGraph()
    {
        double w = GraphHost.ActualWidth, h = GraphHost.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var line = new PointCollection();
        var area = new PointCollection { new Point(0, h) };
        int i = 0;
        foreach (var v in _history)
        {
            var p = new Point(i * w / (HistoryLength - 1), h - Math.Max(0.04, v) * h);
            line.Add(p);
            area.Add(p);
            i++;
        }
        area.Add(new Point(w, h));
        CpuLine.Points = line;
        CpuArea.Points = area;
    }
}
