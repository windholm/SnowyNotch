using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SnowyNotch.Services;
using SnowyNotch.Localization;

namespace SnowyNotch;

public partial class MainWindow
{
    private readonly DispatcherTimer _dragLeaveDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private Point _tileDownPoint;
    private TrayItem? _tileDownItem;
    private bool _draggingOut;

    private void InitTray()
    {
        TrayList.ItemsSource = _tray.Items;
        _tray.Items.CollectionChanged += (_, _) => RefreshTray();
        _dragLeaveDelay.Tick += (_, _) =>
        {
            _dragLeaveDelay.Stop();
            _dragOpen = false;
            SetDropGlow(false);
            UpdateState();
        };
        RefreshTray();
    }

    private void RefreshTray()
    {
        int n = _tray.Items.Count;
        if (n == 0 && _tray.Selecting) SetTraySelecting(false);
        TrayEmpty.Visibility = n == 0 ? Visibility.Visible : Visibility.Collapsed;
        TrayScroll.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
        TrayHeader.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshTrayCount();
        NavTrayBadge.Text = n.ToString();
        NavTrayBadge.Visibility = n == 0 ? Visibility.Collapsed : Visibility.Visible;
        Dispatcher.InvokeAsync(() => MoveNavIndicator(), DispatcherPriority.Loaded);
        ApplyIdleVisibility();
        if (_view == View.Tray) SetTargetsAfterLayout();
    }

    private void RefreshTrayCount()
    {
        int n = _tray.Items.Count, sel = _tray.Selected.Count;
        TrayCountText.Text = n == 0 ? "" : _tray.Selecting && sel > 0 ? L.F("{0} из {1}", sel, n) : n.ToString();
        TrayClearButton.ToolTip = _tray.Selecting && sel > 0 ? L.T("Убрать выбранные") : L.T("Очистить трей");
    }

    private void SetTraySelecting(bool on)
    {
        _tray.Selecting = on;
        TraySelectButton.Background = on ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                                         : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
        TraySelectButton.ToolTip = on ? L.T("Готово") : L.T("Выбрать файлы");
        RefreshTrayCount();
    }

    /// <summary>С чем работают корзинка и перетаскивание: выбранные файлы, а если ничего не выбрано — все.</summary>
    private string[] TrayTargetPaths()
    {
        var sel = _tray.Selecting ? _tray.Selected : Array.Empty<TrayItem>();
        return (sel.Count > 0 ? sel : _tray.Items).Select(i => i.Path).ToArray();
    }

    private void TraySelect_Click(object sender, RoutedEventArgs e) => SetTraySelecting(!_tray.Selecting);

    private Point _basketDown;
    private bool _basketPressed, _basketDragged;

    private void TrayBasket_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _basketDown = e.GetPosition(this);
        _basketPressed = true;
        _basketDragged = false;
    }

    private void TrayBasket_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_basketPressed || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _basketDown.X) < 6 && Math.Abs(p.Y - _basketDown.Y) < 6) return;
        _basketPressed = false;
        _basketDragged = true;
        var paths = TrayTargetPaths();
        if (paths.Length == 0) return;
        _draggingOut = true;
        try
        {
            DragDrop.DoDragDrop(TrayBasketButton, new DataObject(DataFormats.FileDrop, paths),
                DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            _draggingOut = false;
        }
    }

    private async void TrayBasket_Click(object sender, RoutedEventArgs e)
    {
        _basketPressed = false;
        if (_basketDragged) { _basketDragged = false; return; }
        var paths = TrayTargetPaths();
        if (paths.Length == 0) return;
        try
        {
            var list = new System.Collections.Specialized.StringCollection();
            list.AddRange(paths);
            Clipboard.SetFileDropList(list);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[tray] clipboard failed: {ex.Message}");
            return;
        }
        // Подтверждение: корзинка на секунду превращается в галочку.
        TrayBasketIcon.Data = (System.Windows.Media.Geometry)FindResource("IconCheck");
        TrayBasketButton.ToolTip = paths.Length == 1 ? L.T("Файл скопирован") : L.F("Скопировано: {0}", paths.Length);
        await System.Threading.Tasks.Task.Delay(1200);
        TrayBasketIcon.Data = (System.Windows.Media.Geometry)FindResource("IconBasket");
        TrayBasketButton.ToolTip = L.T("Нажмите — скопировать файлы, потяните — перенести все сразу");
    }

    private void TrayTile_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        var st = new System.Windows.Media.ScaleTransform(0.55, 0.55);
        el.RenderTransform = st;
        var ease = new System.Windows.Media.Animation.BackEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut, Amplitude = 0.45 };
        var grow = new System.Windows.Media.Animation.DoubleAnimation(0.55, 1, TimeSpan.FromMilliseconds(Ms(420))) { EasingFunction = ease };
        st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow);
        st.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow);
        el.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Ms(240))));
    }

    private void SetDropGlow(bool on)
    {
        Fade(DropGlow, on ? 1 : 0, Ms(on ? 160 : 200));
        Fade(TrayEmpty, on ? 0 : 1, Ms(on ? 120 : 220));
        Fade(TrayScroll, on ? 0.15 : 1, Ms(on ? 120 : 220));
        Fade(TrayHeader, on ? 0 : 1, Ms(on ? 120 : 220));
        if (on)
        {
            DropDashes.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, -18, TimeSpan.FromMilliseconds(700))
                { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
            DropIconShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, -6, TimeSpan.FromMilliseconds(520))
                {
                    AutoReverse = true,
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
                    EasingFunction = new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
                });
        }
        else
        {
            DropDashes.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
            DropIconShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        }
    }

    private static bool HasFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text);

    private void Island_DragEnter(object sender, DragEventArgs e)
    {
        if (_draggingOut || !HasFiles(e)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        _dragLeaveDelay.Stop();
        if (!_dragOpen)
        {
            _dragOpen = true;
            _welcome = false;
            if (_customizing) ExitCustomize(save: false);
            if (_view != View.Tray) ShowView(View.Tray);
            UpdateState();
        }
        SetDropGlow(true);
    }

    private void Island_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_draggingOut && HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        _dragLeaveDelay.Stop();
    }

    private void Island_DragLeave(object sender, DragEventArgs e) => _dragLeaveDelay.Start();

    private void Island_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _dragLeaveDelay.Stop();
        _dragOpen = false;
        SetDropGlow(false);
        if (_draggingOut || !HasFiles(e)) { UpdateState(); return; }

        bool added = false;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            _tray.Add(paths);
            added = true;
        }
        else if ((e.Data.GetData(DataFormats.UnicodeText) ?? e.Data.GetData(DataFormats.Text)) is string text && !string.IsNullOrWhiteSpace(text))
        {
            added = _tray.AddText(text);
        }

        if (added)
        {
            if (_view != View.Tray) ShowView(View.Tray);
            Peek(2.5);
        }
        UpdateState();
    }

    private void TrayTile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TrayItem item })
        {
            _tileDownItem = item;
            _tileDownPoint = e.GetPosition(this);
        }
    }

    private void TrayTile_MouseMove(object sender, MouseEventArgs e)
    {
        if (_tileDownItem == null || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _tileDownPoint.X) < 6 && Math.Abs(p.Y - _tileDownPoint.Y) < 6) return;

        var item = _tileDownItem;
        _tileDownItem = null;
        _draggingOut = true;
        try
        {
            // В режиме выбора тянем всю выбранную пачку, если плитка в ней.
            var paths = _tray.Selecting && item.IsSelected ? TrayTargetPaths() : new[] { item.Path };
            var data = new DataObject(DataFormats.FileDrop, paths);
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            _draggingOut = false;
        }
    }

    private void TrayTile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_tileDownItem is { } item && sender is FrameworkElement { DataContext: TrayItem clicked } && clicked == item)
        {
            if (_tray.Selecting)
            {
                item.IsSelected = !item.IsSelected;
                RefreshTrayCount();
                _tileDownItem = null;
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[tray] open failed: {ex.Message}");
            }
        }
        _tileDownItem = null;
    }

    private void TrayRemove_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _tileDownItem = null;
        if (sender is FrameworkElement { DataContext: TrayItem item }) _tray.Remove(item);
    }

    private void TrayClear_Click(object sender, RoutedEventArgs e)
    {
        if (_tray.Selecting && _tray.Selected.Count > 0) _tray.RemoveSelected();
        else _tray.Clear();
    }
}
