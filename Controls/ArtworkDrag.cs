using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using SnowyNotch.Animation;

namespace SnowyNotch.Controls;

/// <summary>
/// Обложку можно «вытащить» из острова: короткий клик — обычное действие (<see cref="Click"/>),
/// а если потянуть — за курсором летит её копия. Отпустили — спрашиваем <see cref="Drop"/>
/// (точка и прямоугольник копии в пикселях экрана); принято — копия исчезает (её место займёт
/// полноэкранный плеер), нет — копия пружинит обратно на место.
/// </summary>
public sealed class ArtworkDrag
{
    private const double Threshold = 7;
    private const double GhostSize = 128;

    private readonly FrameworkElement _source;
    private readonly Func<Brush?> _art;
    private Point _down;
    private bool _pressed, _dragging;
    private Popup? _popup;
    private Border? _ghost;
    private ScaleTransform? _ghostScale;
    private RotateTransform? _ghostTilt;
    private Point _lastDip;

    public ArtworkDrag(FrameworkElement source, Func<Brush?> art)
    {
        _source = source;
        _art = art;
        source.PreviewMouseLeftButtonDown += OnDown;
        source.PreviewMouseMove += OnMove;
        source.PreviewMouseLeftButtonUp += OnUp;
        source.LostMouseCapture += (_, _) => { if (_dragging) Finish(accepted: false); _pressed = false; };
    }

    public event Action? Click;

    /// <summary>Отпустили копию: (точка курсора, прямоугольник копии) в пикселях экрана → принять?</summary>
    public Func<Point, Rect, bool>? Drop { get; set; }

    /// <summary>Тянут прямо сейчас.</summary>
    public bool IsDragging => _dragging;

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _down = e.GetPosition(_source);
        _pressed = true;
        _dragging = false;
        _source.CaptureMouse();
        e.Handled = true;
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!_pressed) return;
        var p = e.GetPosition(_source);
        if (!_dragging && (p - _down).Length > Threshold && _art() != null) Begin();
        if (_dragging) MoveGhost(e);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        bool wasDragging = _dragging;
        e.Handled = true;
        if (wasDragging)
        {
            var px = _source.PointToScreen(e.GetPosition(_source));
            double scale = DeviceScale();
            double size = GhostSize * scale;
            var rect = new Rect(px.X - size / 2, px.Y - size / 2, size, size);
            bool ok = false;
            try { ok = Drop?.Invoke(px, rect) == true; } catch { }
            Finish(ok);
        }
        if (_source.IsMouseCaptured) _source.ReleaseMouseCapture();
        if (!wasDragging) Click?.Invoke();
        _dragging = false;
    }

    private double DeviceScale() =>
        PresentationSource.FromVisual(_source)?.CompositionTarget?.TransformToDevice.M11 ?? 1;

    private Point ToDip(Point screenPx)
    {
        var m = PresentationSource.FromVisual(_source)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return m.Transform(screenPx);
    }

    private void Begin()
    {
        _dragging = true;
        _source.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.3, TimeSpan.FromMilliseconds(160)));

        _ghostScale = new ScaleTransform(0.5, 0.5);
        _ghostTilt = new RotateTransform(0);
        _ghost = new Border
        {
            Width = GhostSize, Height = GhostSize, CornerRadius = new CornerRadius(24),
            Background = _art(), Margin = new Thickness(28),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { _ghostScale, _ghostTilt } },
            Effect = new DropShadowEffect { BlurRadius = 34, ShadowDepth = 12, Opacity = 0.55, Direction = 270, Color = Colors.Black },
        };
        _popup = new Popup
        {
            AllowsTransparency = true, Placement = PlacementMode.AbsolutePoint, StaysOpen = true,
            IsHitTestVisible = false, Child = _ghost,
        };
        var pop = new DoubleAnimation(0.5, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = SpringEase.Bouncy };
        _ghostScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        _ghostScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
    }

    private void MoveGhost(MouseEventArgs e)
    {
        if (_popup == null || _ghostTilt == null) return;
        var dip = ToDip(_source.PointToScreen(e.GetPosition(_source)));
        // Копия чуть наклоняется по направлению движения — как карточка в руке.
        double dx = dip.X - _lastDip.X;
        _lastDip = dip;
        _ghostTilt.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(Math.Clamp(dx * 0.9, -10, 10), TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        double half = GhostSize / 2 + 28;
        _popup.HorizontalOffset = dip.X - half;
        _popup.VerticalOffset = dip.Y - half;
        if (!_popup.IsOpen) _popup.IsOpen = true;
    }

    private void Finish(bool accepted)
    {
        _dragging = false;
        var popup = _popup;
        var ghost = _ghost;
        var scale = _ghostScale;
        _popup = null;
        _source.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(accepted ? 500 : 260)) { BeginTime = TimeSpan.FromMilliseconds(accepted ? 300 : 120) });
        if (popup == null || ghost == null || scale == null) return;

        if (accepted)
        {
            // Полноэкранный плеер уже подхватил обложку с этого места — копию просто убираем.
            popup.IsOpen = false;
            return;
        }

        // Не туда — копия улетает обратно к обложке и тает.
        var home = ToDip(_source.PointToScreen(new Point(_source.ActualWidth / 2, _source.ActualHeight / 2)));
        double half = GhostSize / 2 + 28;
        var dur = TimeSpan.FromMilliseconds(380);
        var ease = SpringEase.Soft;
        var fly = new DoubleAnimation(popup.HorizontalOffset, home.X - half, dur) { EasingFunction = ease };
        var flyY = new DoubleAnimation(popup.VerticalOffset, home.Y - half, dur) { EasingFunction = ease };
        popup.BeginAnimation(Popup.HorizontalOffsetProperty, fly);
        popup.BeginAnimation(Popup.VerticalOffsetProperty, flyY);
        double to = _source.ActualWidth / GhostSize;
        var shrink = new DoubleAnimation(Math.Max(0.2, to), dur) { EasingFunction = ease };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { BeginTime = TimeSpan.FromMilliseconds(220) };
        fade.Completed += (_, _) => popup.IsOpen = false;
        ghost.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
