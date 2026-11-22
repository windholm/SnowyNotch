using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using SnowyNotch.Animation;

namespace SnowyNotch.Controls;

/// <summary>
/// «Парящий» текст песни: каждая новая строка возникает в другом месте экрана (зоны по кругу, без повторов подряд),
/// своим размером, с лёгким наклоном и одной из анимаций появления. Прошлая строка тускнеет, дрейфует и тает.
/// Используется в полноэкранном плеере (стиль «Парящий») и в режиме «Текст поверх экрана».
/// </summary>
public sealed class FloatingLyrics : Canvas
{
    private enum Entrance { BlurIn, Slide, Pop, Rise, Wipe, Letters }

    private sealed class Item
    {
        public required FrameworkElement El;
        public required TranslateTransform Shift;
        public required ScaleTransform Scale;
        public required RotateTransform Tilt;
    }

    private readonly Random _rnd = new();
    private readonly List<Item> _items = new();
    private readonly List<int> _recentZones = new();
    private Item? _current;
    private string _shown = "";
    private string? _pending;

    /// <summary>Поля, куда текст не заходит (доли ширины/высоты): например, верх — под островом, низ — панель задач.</summary>
    public Thickness SafeArea { get; set; } = new(0.06, 0.12, 0.06, 0.12);

    /// <summary>Тёмная тень вокруг букв — чтобы текст читался поверх любых окон (режим поверх экрана).</summary>
    public bool Shadowed { get; set; }

    /// <summary>Множитель размера шрифта.</summary>
    public double FontScale { get; set; } = 1;

    public FloatingLyrics()
    {
        ClipToBounds = false;
        IsHitTestVisible = false;
        SizeChanged += (_, _) => { if (_pending != null) ShowLine(_pending); };
    }

    /// <summary>Показать строку. Пустая строка (проигрыш) — прошлая просто гаснет.</summary>
    public void ShowLine(string text)
    {
        text = text.Trim();
        if (text == "♪") text = "";
        if (ActualWidth < 50 || ActualHeight < 50) { _pending = text; return; } // покажем, когда появится размер
        _pending = null;
        if (text == _shown) return;
        _shown = text;
        RetireCurrent();
        if (text.Length == 0) return;
        _current = Spawn(text);
    }

    /// <summary>Убрать всё (смена трека, выход).</summary>
    public void Clear()
    {
        _shown = "";
        RetireCurrent();
        foreach (var it in _items.ToArray()) Retire(it, fast: true);
    }

    private void RetireCurrent()
    {
        if (_current == null) return;
        Retire(_current, fast: false);
        _current = null;
    }

    // ---------- появление ----------

    private Item Spawn(string text)
    {
        double w = ActualWidth, h = ActualHeight;
        double size = Math.Clamp(h * (0.045 + _rnd.NextDouble() * 0.035), 22, 84) * FontScale;
        if (text.Length > 34) size *= 0.82;

        // Зоны 3×3 (центр — реже); не повторяем последние две.
        int zone;
        do zone = _rnd.Next(9);
        while (_recentZones.Contains(zone) || (zone == 4 && _rnd.NextDouble() < 0.6));
        _recentZones.Add(zone);
        if (_recentZones.Count > 2) _recentZones.RemoveAt(0);
        int col = zone % 3, row = zone / 3;

        var align = col == 0 ? TextAlignment.Left : col == 2 ? TextAlignment.Right : TextAlignment.Center;
        double maxW = w * (1 - SafeArea.Left - SafeArea.Right) * 0.46;
        var tb = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = _rnd.NextDouble() < 0.7 ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = align, MaxWidth = maxW,
            Effect = Shadowed
                ? new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, ShadowDepth = 0, Opacity = 0.9, RenderingBias = RenderingBias.Performance }
                : new DropShadowEffect { Color = Colors.White, BlurRadius = 30, ShadowDepth = 0, Opacity = 0.35, RenderingBias = RenderingBias.Performance },
        };
        tb.Measure(new Size(maxW, double.PositiveInfinity));
        var ds = tb.DesiredSize;

        double l = w * SafeArea.Left, t = h * SafeArea.Top;
        double cw = (w * (1 - SafeArea.Left - SafeArea.Right)) / 3, ch = (h * (1 - SafeArea.Top - SafeArea.Bottom)) / 3;
        double x = l + col * cw + (col == 0 ? 0 : col == 2 ? cw - ds.Width : (cw - ds.Width) / 2) + (_rnd.NextDouble() - 0.5) * cw * 0.3;
        double y = t + row * ch + (ch - ds.Height) / 2 + (_rnd.NextDouble() - 0.5) * ch * 0.5;
        x = Math.Clamp(x, l, w - w * SafeArea.Right - ds.Width);
        y = Math.Clamp(y, t, h - h * SafeArea.Bottom - ds.Height);
        SetLeft(tb, x);
        SetTop(tb, y);

        var scale = new ScaleTransform(1, 1);
        var tilt = new RotateTransform((_rnd.NextDouble() - 0.5) * 7);
        var shift = new TranslateTransform();
        tb.RenderTransformOrigin = new Point(align == TextAlignment.Left ? 0 : align == TextAlignment.Right ? 1 : 0.5, 0.5);
        tb.RenderTransform = new TransformGroup { Children = { scale, tilt, shift } };
        Children.Add(tb);
        var item = new Item { El = tb, Shift = shift, Scale = scale, Tilt = tilt };
        _items.Add(item);

        Animate(item, tb, (Entrance)_rnd.Next(6), col);

        // Пока строка звучит — едва заметно плывёт.
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(shift.X, shift.X + (_rnd.NextDouble() - 0.5) * 40, TimeSpan.FromSeconds(9)) { BeginTime = TimeSpan.FromMilliseconds(900) });
        return item;
    }

    private void Animate(Item it, TextBlock tb, Entrance kind, int col)
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420));
        switch (kind)
        {
            case Entrance.BlurIn:
                var blur = new BlurEffect { Radius = 22, RenderingBias = RenderingBias.Performance };
                var keep = tb.Effect;
                tb.Effect = blur;
                var unblur = new DoubleAnimation(22, 0, TimeSpan.FromMilliseconds(700)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                unblur.Completed += (_, _) => { if (tb.Effect == blur) tb.Effect = keep; };
                blur.BeginAnimation(BlurEffect.RadiusProperty, unblur);
                Grow(it, 0.9, SpringEase.Soft, 800);
                tb.BeginAnimation(OpacityProperty, fade);
                break;

            case Entrance.Slide:
                double dx = col == 2 ? 120 : -120;
                it.Shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, TimeSpan.FromMilliseconds(800)) { EasingFunction = SpringEase.Lively });
                tb.BeginAnimation(OpacityProperty, fade);
                break;

            case Entrance.Pop:
                Grow(it, 0.4, SpringEase.Bouncy, 700);
                tb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
                break;

            case Entrance.Rise:
                double angle = it.Tilt.Angle;
                it.Tilt.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle + (_rnd.NextDouble() < 0.5 ? -14 : 14), angle, TimeSpan.FromMilliseconds(900)) { EasingFunction = SpringEase.Lively });
                it.Shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(70, 0, TimeSpan.FromMilliseconds(900)) { EasingFunction = SpringEase.Lively });
                tb.BeginAnimation(OpacityProperty, fade);
                break;

            case Entrance.Wipe:
                // «Стирание»: текст проявляется слева направо мягкой кромкой.
                var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
                var a = new GradientStop(Colors.Black, 0);
                var b = new GradientStop(Colors.Transparent, 0);
                mask.GradientStops.Add(a);
                mask.GradientStops.Add(b);
                tb.OpacityMask = mask;
                var dur = TimeSpan.FromMilliseconds(Math.Clamp(tb.Text.Length * 45, 500, 1600));
                var run = new DoubleAnimation(-0.15, 1, dur);
                var runB = new DoubleAnimation(0, 1.15, dur);
                runB.Completed += (_, _) => tb.OpacityMask = null;
                a.BeginAnimation(GradientStop.OffsetProperty, run);
                b.BeginAnimation(GradientStop.OffsetProperty, runB);
                break;

            case Entrance.Letters:
                // По буквам: каждая всплывает снизу с короткой задержкой.
                string text = tb.Text;
                tb.Text = "";
                tb.Opacity = 1;
                var step = TimeSpan.FromMilliseconds(Math.Clamp(1100.0 / Math.Max(1, text.Length), 18, 70));
                for (int i = 0; i < text.Length; i++)
                {
                    var r = new System.Windows.Documents.Run(text[i].ToString()) { Foreground = new SolidColorBrush(Color.FromArgb(0, 255, 255, 255)) };
                    tb.Inlines.Add(r);
                    ((SolidColorBrush)r.Foreground).BeginAnimation(SolidColorBrush.ColorProperty,
                        new ColorAnimation(Colors.White, TimeSpan.FromMilliseconds(260)) { BeginTime = TimeSpan.FromTicks(step.Ticks * i) });
                }
                it.Shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(900)) { EasingFunction = SpringEase.Soft });
                break;
        }
    }

    private static void Grow(Item it, double from, IEasingFunction ease, double ms)
    {
        var g = new DoubleAnimation(from, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        it.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, g);
        it.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, g);
    }

    // ---------- уход ----------

    /// <summary>Строка отзвучала: тускнеет, уплывает в сторону, чуть уменьшается и исчезает.</summary>
    private void Retire(Item it, bool fast)
    {
        var el = it.El;
        double life = fast ? 0.35 : 4.5;
        var dim = new DoubleAnimationUsingKeyFrames();
        dim.KeyFrames.Add(new EasingDoubleKeyFrame(fast ? 0 : 0.32, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(fast ? life : 0.5))));
        if (!fast) dim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(life))));
        dim.Completed += (_, _) => { Children.Remove(el); _items.Remove(it); };
        el.BeginAnimation(OpacityProperty, dim);
        double dx = (_rnd.NextDouble() - 0.5) * 120, dy = -30 - _rnd.NextDouble() * 50;
        it.Shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(it.Shift.X + dx, TimeSpan.FromSeconds(life)) { EasingFunction = new SineEase() });
        it.Shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(it.Shift.Y + dy, TimeSpan.FromSeconds(life)) { EasingFunction = new SineEase() });
        var shrink = new DoubleAnimation(0.9, TimeSpan.FromSeconds(life));
        it.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        it.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }
}
