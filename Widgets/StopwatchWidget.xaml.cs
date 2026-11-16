using System.Windows;
using System.Windows.Controls;
using SnowyNotch.Services;

namespace SnowyNotch.Widgets;

public partial class StopwatchWidget : UserControl
{
    private readonly StopwatchTool _stopwatch;

    public StopwatchWidget(StopwatchTool stopwatch)
    {
        InitializeComponent();
        _stopwatch = stopwatch;
        DataContext = stopwatch;
    }

    private void Toggle_Click(object sender, RoutedEventArgs e) => _stopwatch.Toggle();
    private void Reset_Click(object sender, RoutedEventArgs e) => _stopwatch.Reset();
}
