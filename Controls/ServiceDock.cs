using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SnowyNotch.Controls;

/// <summary>
/// Док сервисов: одна строка иконок с подписями.
/// • Прокрутка: колесо, перетаскивание мышью, горизонтальный жест тачпада; у краёв — мягкое затухание.
/// • Увеличение под курсором, как в доке macOS: иконка растёт от нижнего края, соседи раздвигаются.
/// • Появление каскадом с лёгким «перелётом», нажатие — пружинящее сжатие.
/// • Точка под иконкой — сервис сейчас работает (играет музыка, идёт таймер…).
/// Всё движение считается покадрово и только пока что-то меняется.
/// </summary>
public sealed class ServiceDock : Canvas
{
    public const double IconSize = 50;
    private const double Pitch = 80, PadX = 12, TopPad = 16;
    private const double MaxScale = 1.24, Reach = 2.1, Spread = 0.85;
    private const double FadeWidth = 30;

    private sealed class Item
    {
        public string Id = "";
        public Grid Root = null!;
        public TextBlock Label = null!;
        public Border Dot = null!;
        public readonly ScaleTransform Scale = new(1, 1);
        public readonly TranslateTransform Shift = new();
        public double S = 1, Press = 1, PressTarget = 1;
    }

    private readonly List<Item> _items = new();
    private readonly GradientStop _maskL0 = new(Colors.Black, 0), _maskL1 = new(Colors.Black, 0),
                                  _maskR1 = new(Colors.Black, 1), _maskR0 = new(Colors.Black, 1);

    private double _scroll, _scrollTarget, _scrollVel;
    private double? _mouseX;
    private double _anchor;
    private bool _running;
    private TimeSpan _last;

    private int _pressIndex = -1;
    private bool _pressed, _dragging;
    private double _pressX, _dragStartScroll, _dragVel, _lastDragX;
    private TimeSpan _lastDragTime;

    private double _enterClock = double.PositiveInfinity; // секунды с начала появления; ∞ — появление закончено

    public ServiceDock()
    {
        Height = TopPad + IconSize + 7 + 16 + 5 + 4 + 6;
        Background = Brushes.Transparent;
        ClipToBounds = true;
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        mask.GradientStops.Add(_maskL0);
        mask.GradientStops.Add(_maskL1);
        mask.GradientStops.Add(_maskR1);
        mask.GradientStops.Add(_maskR0);
        OpacityMask = mask;
        IsVisibleChanged += (_, _) => { if (IsVisible) Wake(); };
        SizeChanged += (_, _) => { ClampScroll(); Wake(); };
    }

    /// <summary>Нажали на сервис: id и центр иконки по горизонтали (в координатах дока).</summary>
    public event Action<string, double>? Invoked;

    public bool HasItems => _items.Count > 0;

    public void Add(string id, string title, AppIcon icon, Color dotColor)
    {
        icon.Width = icon.Height = IconSize;
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, TopPad, 0, 0);
        icon.RenderTransformOrigin = new Point(0.5, 1);

        var item = new Item { Id = id };
        icon.RenderTransform = item.Scale;
        item.Label = new TextBlock
        {
            Text = title, FontSize = 11.5, FontWeight = FontWeights.Medium, Foreground = Brushes.White, Opacity = 0.62,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, TopPad + IconSize + 7, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = Pitch - 4,
        };
        item.Dot = new Border
        {
            Width = 4, Height = 4, CornerRadius = new CornerRadius(2), Opacity = 0,
            Background = new SolidColorBrush(dotColor),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, TopPad + IconSize + 7 + 16 + 4, 0, 0),
        };
        item.Root = new Grid
        {
            Width = Pitch, Height = Height, IsHitTestVisible = false, RenderTransform = item.Shift,
            Children = { icon, item.Label, item.Dot },
        };
        SetLeft(item.Root, PadX + _items.Count * Pitch);
        _items.Add(item);
        Children.Add(item.Root);
        Wake();
    }

    /// <summary>Показать или скрыть точку «сервис работает».</summary>
    public void SetActive(string id, bool active)
    {
        var item = _items.Find(i => i.Id == id);
        item?.Dot.BeginAnimation(OpacityProperty, new DoubleAnimation(active ? 1 : 0, TimeSpan.FromMilliseconds(260)));
    }

    /// <summary>Каскадное появление иконок: снизу вверх, с лёгким перелётом.</summary>
    public void PlayEntrance()
    {
        _enterClock = 0;
        foreach (var it in _items) it.Root.Opacity = 0;
        Wake();
    }

    public void ResetScroll()
    {
        _scroll = _scrollTarget = _scrollVel = 0;
        Wake();
    }

    /// <summary>Горизонтальная прокрутка (жест тачпада). У края — упругий отскок.</summary>
    public void ScrollBy(double pixels)
    {
        double next = Math.Clamp(_scrollTarget + pixels, 0, MaxScroll);
        if (Math.Abs(next - _scrollTarget) < 0.5) Bounce(Math.Sign(pixels));
        else _scrollTarget = next;
        Wake();
    }

    private double MaxScroll => Math.Max(0, 2 * PadX + _items.Count * Pitch - ActualWidth);

    private void ClampScroll()
    {
        _scrollTarget = Math.Clamp(_scrollTarget, 0, MaxScroll);
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
    }

    /// <summary>Остановка у целой иконки: слева всегда ровный край (кроме самого конца).</summary>
    private double Snap(double value)
    {
        double max = MaxScroll;
        if (value >= max - Pitch * 0.35) return max;
        return Math.Clamp(Math.Round(value / Pitch) * Pitch, 0, max);
    }

    private int IndexAt(Point p)
    {
        if (p.Y < 0 || p.Y > ActualHeight || p.X < 0 || p.X > ActualWidth) return -1;
        int i = (int)Math.Floor((p.X + _scroll - PadX) / Pitch);
        return i >= 0 && i < _items.Count ? i : -1;
    }

    // ---------- мышь ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        _mouseX = p.X;
        if (_pressed && !_dragging && Math.Abs(p.X - _pressX) > 5)
        {
            _dragging = true;
            _lastDragX = p.X;
            _lastDragTime = TimeSpan.FromMilliseconds(e.Timestamp);
            _dragVel = 0;
            ReleasePress();
        }
        if (_dragging)
        {
            double raw = _dragStartScroll - (p.X - _pressX);
            double max = MaxScroll;
            // За краем — «резинка»: тянется, но всё туже.
            if (raw < 0) raw = -Math.Pow(-raw, 0.72);
            else if (raw > max) raw = max + Math.Pow(raw - max, 0.72);
            _scroll = _scrollTarget = raw;
            _scrollVel = 0;

            var now = TimeSpan.FromMilliseconds(e.Timestamp);
            double dt = (now - _lastDragTime).TotalSeconds;
            if (dt > 0.001)
            {
                _dragVel = 0.6 * _dragVel + 0.4 * (-(p.X - _lastDragX) / dt);
                _lastDragX = p.X;
                _lastDragTime = now;
            }
        }
        Wake();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (!IsMouseCaptured) _mouseX = null;
        Wake();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        _pressed = true;
        _dragging = false;
        _pressX = p.X;
        _dragStartScroll = _scroll;
        _pressIndex = IndexAt(p);
        if (_pressIndex >= 0) _items[_pressIndex].PressTarget = 0.88;
        CaptureMouse();
        e.Handled = true;
        Wake();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        var p = e.GetPosition(this);
        bool wasDragging = _dragging;
        int pressed = _pressIndex;
        EndPress();
        e.Handled = true;

        if (wasDragging)
        {
            // Инерция: докатываемся по скорости броска и встаём у целой иконки.
            _scrollTarget = Snap(_scroll + _dragVel * 0.22);
        }
        else if (pressed >= 0 && IndexAt(p) == pressed)
        {
            var it = _items[pressed];
            double center = PadX + (pressed + 0.5) * Pitch + it.Shift.X;
            Invoked?.Invoke(it.Id, center);
        }
        if (!IsMouseOver) _mouseX = null;
        Wake();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        if (_pressed)
        {
            bool wasDragging = _dragging;
            EndPress(release: false);
            if (wasDragging) _scrollTarget = Snap(_scroll);
        }
        if (!IsMouseOver) _mouseX = null;
        Wake();
    }

    private void ReleasePress()
    {
        if (_pressIndex >= 0) _items[_pressIndex].PressTarget = 1;
        _pressIndex = -1;
    }

    private void EndPress(bool release = true)
    {
        ReleasePress();
        _pressed = _dragging = false;
        if (release && IsMouseCaptured) ReleaseMouseCapture();
    }

    /// <summary>
    /// Колесо над сервисами листает только сервисы — по две иконки за щелчок. У края список упруго
    /// отскакивает, а не передаёт прокрутку острову (раньше это переключало страницу — ложные срабатывания).
    /// </summary>
    public void Wheel(int delta)
    {
        double next = Snap(_scrollTarget - delta / 120.0 * Pitch * 2);
        if (Math.Abs(next - _scrollTarget) < 0.5) Bounce(-Math.Sign(delta));
        else _scrollTarget = next;
        Wake();
    }

    /// <summary>Толчок за край: список чуть уезжает и пружиной возвращается — «дальше некуда».</summary>
    private void Bounce(int dir)
    {
        if (dir == 0 || Math.Abs(_scrollVel) > 400) return;
        _scrollVel += dir * 900;
        Wake();
    }

    // ---------- покадровое движение ----------

    private void Wake()
    {
        if (_running || !IsVisible) return;
        _running = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = _last == TimeSpan.Zero ? 1 / 60.0 : (now - _last).TotalSeconds;
        if (dt <= 0) return;
        _last = now;
        dt = Math.Min(dt, 0.05);

        bool busy = Step(dt);
        if (!busy || !IsVisible)
        {
            CompositionTarget.Rendering -= OnFrame;
            _running = false;
        }
    }

    private bool Step(double dt)
    {
        bool busy = false;

        // Прокрутка: критически задемпфированная пружина к цели.
        if (!_dragging)
        {
            const double k = 210;
            double c = 2 * Math.Sqrt(k);
            for (double left = dt; left > 0; left -= 1 / 240.0)
            {
                double h = Math.Min(1 / 240.0, left);
                _scrollVel += (-k * (_scroll - _scrollTarget) - c * _scrollVel) * h;
                _scroll += _scrollVel * h;
            }
            if (Math.Abs(_scroll - _scrollTarget) < 0.2 && Math.Abs(_scrollVel) < 2) { _scroll = _scrollTarget; _scrollVel = 0; }
            else busy = true;
        }
        else busy = true;

        // Появление.
        bool entering = !double.IsPositiveInfinity(_enterClock);
        if (entering)
        {
            _enterClock += dt;
            busy = true;
            if (_enterClock > 0.05 * _items.Count + 0.6) { _enterClock = double.PositiveInfinity; entering = false; }
        }

        // Цели увеличения.
        bool hover = _mouseX != null && !_dragging && !entering;
        if (hover) _anchor = _mouseX!.Value + _scroll - PadX;
        double kS = 1 - Math.Exp(-dt * 17), kP = 1 - Math.Exp(-dt * 30);
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            double target = 1;
            if (hover)
            {
                double d = Math.Abs(_anchor - (i + 0.5) * Pitch) / (Reach * Pitch);
                if (d < 1) target = 1 + (MaxScale - 1) * 0.5 * (1 + Math.Cos(Math.PI * d));
            }
            it.S += (target - it.S) * kS;
            it.Press += (it.PressTarget - it.Press) * kP;
            if (Math.Abs(target - it.S) < 0.0015) it.S = target; else busy = true;
            if (Math.Abs(it.PressTarget - it.Press) < 0.002) it.Press = it.PressTarget; else busy = true;
        }

        // Раскладка: увеличенные иконки раздвигают соседей, точка под курсором остаётся под курсором.
        double anchorNew = 0, cum = 0;
        int ai = Math.Clamp((int)Math.Floor(_anchor / Pitch), 0, Math.Max(0, _items.Count - 1));
        double af = _anchor / Pitch - ai;
        Span<double> centers = _items.Count <= 32 ? stackalloc double[_items.Count] : new double[_items.Count];
        for (int i = 0; i < _items.Count; i++)
        {
            double w = Pitch * (1 + (_items[i].S - 1) * Spread);
            if (i == ai) anchorNew = cum + af * w;
            centers[i] = cum + w / 2;
            cum += w;
        }
        double shift = _anchor - anchorNew;

        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            double enterScale = 1, enterY = 0, enterOpacity = 1;
            if (entering)
            {
                double t = Math.Clamp((_enterClock - i * 0.045) / 0.46, 0, 1);
                enterScale = 0.5 + 0.5 * OutBack(t);
                enterY = 22 * (1 - OutCubic(t));
                enterOpacity = Math.Clamp(t * 2.4, 0, 1);
            }
            it.Shift.X = centers[i] + shift - (i + 0.5) * Pitch - _scroll;
            it.Shift.Y = enterY;
            it.Scale.ScaleX = it.Scale.ScaleY = it.S * it.Press * enterScale;
            it.Root.Opacity = enterOpacity;
            it.Label.Opacity = 0.62 + 0.38 * Math.Clamp((it.S - 1) / (MaxScale - 1), 0, 1);
        }

        UpdateMask();
        return busy;
    }

    private static double OutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    private static double OutBack(double t)
    {
        const double c1 = 1.9, c3 = c1 + 1;
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }

    /// <summary>Затухание у края появляется только там, где за краем ещё есть иконки.</summary>
    private void UpdateMask()
    {
        double w = Math.Max(1, ActualWidth);
        double left = Math.Clamp(_scroll / 24, 0, 1), right = Math.Clamp((MaxScroll - _scroll) / 24, 0, 1);
        _maskL0.Color = Color.FromArgb((byte)(255 * (1 - left)), 0, 0, 0);
        _maskR0.Color = Color.FromArgb((byte)(255 * (1 - right)), 0, 0, 0);
        _maskL1.Offset = FadeWidth / w;
        _maskR1.Offset = 1 - FadeWidth / w;
    }

#if DEBUG
    /// <summary>Для скриншотов: поставить «курсор» в точку x и сразу довести движение до конца.</summary>
    public void DebugSettle(double? mouseX, double scroll = 0)
    {
        _mouseX = mouseX;
        _scroll = _scrollTarget = Math.Clamp(scroll, 0, MaxScroll);
        _enterClock = double.PositiveInfinity;
        for (int i = 0; i < 240; i++) Step(1 / 60.0);
    }
#endif
}
