using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SnowyNotch.Interop;
using SnowyNotch.Services;

namespace SnowyNotch;

public partial class MainWindow
{
    private enum GhostMode { Normal, Ghost, Hidden }

    private const double GhostOpacity = 0.16;

    private GhostMode _ghost = GhostMode.Normal;
    private MiddleClickHook? _middleHook;
    private bool _clickThrough;
    private readonly ScaleTransform _pulse = new();

    private void InitGhost()
    {
        Island.RenderTransformOrigin = new Point(0.5, 0);
        Island.RenderTransform = _pulse;

        _middleHook = new MiddleClickHook(IsOverIsland) { Enabled = _settings.MiddleClickGhost };
        _middleHook.Click += () => SetGhost(_ghost == GhostMode.Normal ? GhostMode.Ghost : GhostMode.Normal);
        _middleHook.LongPress += () => SetGhost(_ghost == GhostMode.Hidden ? GhostMode.Normal : GhostMode.Hidden);
        _middleHook.ButtonDown += OnAnyButtonDown;
        Closed += (_, _) => _middleHook?.Dispose();
    }

    private void OnAnyButtonDown(int x, int y)
    {
        if (!_sticky || IsOverIsland(x, y)) return;
        Dispatcher.BeginInvoke(() => CloseSticky());
    }

    private void CloseSticky()
    {
        if (!_sticky) return;
        _sticky = false;
        _hoverActive = false;
        _mouseOver = Island.IsMouseOver || NavHitArea.IsMouseOver;
        UpdateState();
    }

    public void ShowIsland() => SetGhost(GhostMode.Normal);

    private bool IsGhosted => _ghost != GhostMode.Normal;

    private void SetGhost(GhostMode mode)
    {
        if (mode == _ghost) return;
        _ghost = mode;

        if (mode != GhostMode.Normal)
        {
            if (_customizing) ExitCustomize(save: false);
            if (_pinned) ClosePinned();
            _hoverDelay.Stop(); _leaveDelay.Stop(); _peekTimer.Stop();
            _hoverActive = _peeking = _dragOpen = _mouseOver = false;
            UpdateState();
        }

        double opacity = mode switch { GhostMode.Ghost => GhostOpacity, GhostMode.Hidden => 0, _ => 1 };
        Cluster.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(mode == GhostMode.Normal ? 240 : 200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        SetClickThrough(mode != GhostMode.Normal);
        Pulse(mode == GhostMode.Normal ? 0.9 : 1.06);
        ApplyIdleVisibility();
    }

    private void Pulse(double from)
    {
        var anim = new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(Ms(360)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 },
        };
        _pulse.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        _pulse.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private bool IsOverIsland(int x, int y)
    {
        if (_hwnd == IntPtr.Zero || !IsVisible || _suppressed) return false;
        var pt = PointFromScreen(new Point(x, y));
        if (BoundsOf(Island, 4).Contains(pt)) return true;
        return _state == IslandState.Expanded && NavBar.IsHitTestVisible && BoundsOf(NavHitArea, 0).Contains(pt);
    }

    private void SetClickThrough(bool through)
    {
        if (through == _clickThrough || _hwnd == IntPtr.Zero) return;
        _clickThrough = through;
        NativeMethods.SetClickThrough(_hwnd, through);
    }

    private Rect BoundsOf(FrameworkElement el, double inflate)
    {
        if (el.ActualWidth <= 0 || !el.IsVisible) return Rect.Empty;
        try
        {
            var r = el.TransformToAncestor(this).TransformBounds(new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            r.Inflate(inflate, inflate);
            return r;
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
    }
}
