using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SnowyNotch.Localization;

namespace SnowyNotch.Widgets;

/// <summary>
/// Часы. На главной — просто время; в режиме сервиса клик по часам открывает календарь на месяц
/// (стрелки или колесо листают месяцы, клик по названию месяца — назад к текущему).
/// </summary>
public partial class ClockWidget : UserControl, IIslandWidget, IBackNavigable
{
    private static CultureInfo Ru => L.Culture; // язык интерфейса
    private static readonly Brush HoverBg = Frozen(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Secondary = Frozen(Color.FromArgb(0x8E, 0xFF, 0xFF, 0xFF));
    private static readonly Brush WeekendHead = Frozen(Color.FromArgb(0xB3, 0xFF, 0x45, 0x3A));

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _calendarEnabled;
    private DateTime _month;      // первое число показанного месяца
    private DateTime _builtToday; // «сегодня», с которым построена сетка (чтобы перерисовать после полуночи)

    public ClockWidget()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => Refresh();
        Refresh();
    }

    public void SetActive(bool active)
    {
        if (active) { Refresh(); _tick.Start(); }
        else _tick.Stop();
    }

    /// <summary>Включает переход в календарь по клику (только в режиме сервиса, не на главной).</summary>
    public void EnableCalendar()
    {
        _calendarEnabled = true;
        ClockPage.Cursor = Cursors.Hand;
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        DayText.Text = now.ToString("ddd", Ru).ToUpper(Ru);
        DayNumText.Text = now.Day.ToString(Ru);
        WeekText.Text = L.T("Н") + ISOWeek.GetWeekOfYear(now);
        TimeText.Text = now.ToString("HH:mm", Ru);
        DateText.Text = now.ToString("d MMMM", Ru);
        if (CalendarPage.Visibility == Visibility.Visible && now.Date != _builtToday) BuildCalendar(0);
    }

    // ======================= переходы =======================

    private void ClockPage_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_calendarEnabled) ClockPage.Background = HoverBg;
    }

    private void ClockPage_MouseLeave(object sender, MouseEventArgs e) => ClockPage.Background = Brushes.Transparent;

    private void ClockPage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_calendarEnabled) return;
        e.Handled = true;
        var today = DateTime.Today;
        _month = new DateTime(today.Year, today.Month, 1);
        BuildCalendar(0);
        Slide(ClockPage, CalendarPage, forward: true);
    }

    public bool TryGoBack()
    {
        if (CalendarPage.Visibility != Visibility.Visible) return false;
        ClockPage.Background = Brushes.Transparent;
        Slide(CalendarPage, ClockPage, forward: false);
        return true;
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

    // ======================= календарь =======================

    private void PrevMonth_Click(object sender, RoutedEventArgs e) => StepMonth(-1);
    private void NextMonth_Click(object sender, RoutedEventArgs e) => StepMonth(1);

    private void CalendarPage_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true; // не листать страницы острова
        StepMonth(e.Delta > 0 ? -1 : 1);
    }

    private void MonthText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var today = DateTime.Today;
        var current = new DateTime(today.Year, today.Month, 1);
        if (_month == current) return;
        int dir = current > _month ? 1 : -1;
        _month = current;
        BuildCalendar(dir);
    }

    private void StepMonth(int dir)
    {
        _month = _month.AddMonths(dir);
        BuildCalendar(dir);
    }

    private void BuildCalendar(int dir)
    {
        var fmt = Ru.DateTimeFormat;
        var today = DateTime.Today;
        _builtToday = today;

        var title = _month.ToString(fmt.YearMonthPattern, Ru);
        MonthText.Text = title.Length > 0 ? char.ToUpper(title[0], Ru) + title[1..] : title;

        var first = fmt.FirstDayOfWeek;
        WeekdayRow.Children.Clear();
        for (int i = 0; i < 7; i++)
        {
            var dow = (DayOfWeek)(((int)first + i) % 7);
            WeekdayRow.Children.Add(new TextBlock
            {
                Text = fmt.ShortestDayNames[(int)dow].ToUpper(Ru),
                FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                Foreground = IsWeekend(dow) ? WeekendHead : Secondary,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        int offset = ((int)_month.DayOfWeek - (int)first + 7) % 7;
        int days = DateTime.DaysInMonth(_month.Year, _month.Month);
        int rows = (offset + days + 6) / 7;
        var start = _month.AddDays(-offset);

        DaysGrid.Children.Clear();
        DaysGrid.Rows = rows;
        var red = (Brush)FindResource("DangerRed");
        for (int i = 0; i < rows * 7; i++)
        {
            var d = start.AddDays(i);
            bool inMonth = d.Month == _month.Month;
            bool isToday = d == today;
            DaysGrid.Children.Add(new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 1, 0, 1),
                Background = isToday ? red : Brushes.Transparent,
                Opacity = inMonth ? 1 : 0.28,
                Child = new TextBlock
                {
                    Text = d.Day.ToString(Ru), FontSize = 12.5,
                    FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = !isToday && IsWeekend(d.DayOfWeek) ? Secondary : Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }

        if (dir != 0)
        {
            var tt = new TranslateTransform(dir * 18, 0);
            DaysGrid.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
            DaysGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }
    }

    private static bool IsWeekend(DayOfWeek d) => d is DayOfWeek.Saturday or DayOfWeek.Sunday;

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
