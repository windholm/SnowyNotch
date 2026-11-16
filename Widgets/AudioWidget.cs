using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SnowyNotch.Controls;
using SnowyNotch.Services;
using SnowyNotch.Localization;

namespace SnowyNotch.Widgets;

/// <summary>
/// Сервис «Звук»: громкость каждой программы отдельно, как в микшере Windows, но в острове.
/// Строка — полоска-капсула: тянете — меняется громкость, клик по иконке — без звука.
/// Сверху общая громкость устройства. Пока программа играет, внутри полоски дышит уровень сигнала.
/// </summary>
public sealed class AudioWidget : UserControl, IIslandWidget
{
    private const double RowHeight = 36;

    private static readonly Brush Track = Frozen(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
    private static readonly Brush FillBrush = Frozen(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF));
    private static readonly Brush FillMuted = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Orange = Frozen(Color.FromRgb(0xFF, 0x9F, 0x0A));
    private static readonly Brush Secondary = Frozen(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));

    private readonly AudioMixerService _mixer;
    private readonly VolumeService _volume;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly StackPanel _rows = new();
    private readonly TextBlock _empty;
    private readonly Dictionary<string, Row> _byKey = new();
    private readonly Row _master;
    private Row? _dragging;

    public AudioWidget(AudioMixerService mixer, VolumeService volume)
    {
        _mixer = mixer;
        _volume = volume;
        Width = 380;
        Foreground = Brushes.White;

        var header = new Grid { Margin = new Thickness(2, 0, 0, 10) };
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new IconGlyph { Data = (Geometry)Application.Current.FindResource("IconSpeakerHigh"), Foreground = Orange, Width = 16, Height = 16, StrokeWidth = 1.9 },
                new TextBlock { Text = L.T("Звук"), FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center },
            },
        });

        _master = new Row(this, L.T("Громкость"), null, master: true);
        _empty = new TextBlock
        {
            Text = L.T("Сейчас ни одна программа не выводит звук"), FontSize = 12, Foreground = Secondary,
            Margin = new Thickness(4, 4, 0, 2), Visibility = Visibility.Collapsed,
        };

        Content = new StackPanel { Children = { header, _master.Root, _rows, _empty } };
        _poll.Tick += (_, _) => Refresh();
        Refresh();
    }

    public void SetActive(bool active)
    {
        if (active) { Refresh(); _poll.Start(); }
        else _poll.Stop();
    }

    private void Refresh()
    {
        if (_dragging == null)
        {
            _master.Set(_volume.Muted ? 0 : _volume.Volume, _volume.Muted, 0, animate: true);
        }

        var apps = _mixer.Snapshot();
        _empty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var seen = new HashSet<string>();
        int index = 0;
        foreach (var app in apps.Take(7))
        {
            seen.Add(app.Key);
            if (!_byKey.TryGetValue(app.Key, out var row))
            {
                row = new Row(this, app.Name, app.Icon, master: false);
                _byKey[app.Key] = row;
                _rows.Children.Insert(Math.Min(index, _rows.Children.Count), row.Root);
                row.Appear();
            }
            else if (_rows.Children.IndexOf(row.Root) != index)
            {
                _rows.Children.Remove(row.Root);
                _rows.Children.Insert(Math.Min(index, _rows.Children.Count), row.Root);
            }
            row.App = app;
            if (row != _dragging) row.Set(app.Muted ? 0 : app.Volume, app.Muted, app.Active ? app.Peak : 0, animate: true);
            row.Root.Opacity = app.Active ? 1 : 0.6;
            index++;
        }
        foreach (var key in _byKey.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _rows.Children.Remove(_byKey[key].Root);
            _byKey.Remove(key);
        }
    }

    // ---------- строка ----------

    private sealed class Row
    {
        private readonly AudioWidget _owner;
        private readonly bool _master;
        private readonly Border _fill, _peak;
        private readonly TextBlock _percent;
        private readonly FrameworkElement _icon;
        private readonly Grid _bar;
        private double _level;

        public MixerApp? App;
        public Border Root { get; }

        public Row(AudioWidget owner, string name, ImageSource? icon, bool master)
        {
            _owner = owner;
            _master = master;
            _bar = new Grid { Height = RowHeight, Background = Brushes.Transparent, Cursor = Cursors.SizeWE, ClipToBounds = true };
            var track = new Border { CornerRadius = new CornerRadius(RowHeight / 2), Background = Track };
            _fill = new Border { CornerRadius = new CornerRadius(RowHeight / 2), Background = FillBrush, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
            _peak = new Border
            {
                CornerRadius = new CornerRadius(RowHeight / 2), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Opacity = 0,
                Background = new LinearGradientBrush(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 0),
                IsHitTestVisible = false,
            };

            _icon = icon != null
                ? new Image { Source = icon, Width = 20, Height = 20 }
                : new IconGlyph
                {
                    Data = (Geometry)Application.Current.FindResource(master ? "IconSpeakerHigh" : "IconSettings"),
                    Width = 18, Height = 18, Foreground = Brushes.White, StrokeWidth = 1.8,
                };
            var iconHost = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = Brushes.Transparent,
                Margin = new Thickness(5, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand, ToolTip = L.T("Без звука"), Child = _icon,
            };
            iconHost.MouseLeftButtonDown += (_, e) => { e.Handled = true; ToggleMute(); };

            var label = new TextBlock
            {
                Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(40, 0, 60, 0),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false,
            };
            _percent = new TextBlock
            {
                FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Secondary, Margin = new Thickness(0, 0, 14, 0),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
            };

            _bar.Children.Add(track);
            _bar.Children.Add(_fill);
            _bar.Children.Add(_peak);
            _bar.Children.Add(label);
            _bar.Children.Add(_percent);
            _bar.Children.Add(iconHost);

            _bar.MouseLeftButtonDown += (_, e) => { _owner._dragging = this; _bar.CaptureMouse(); Apply(e.GetPosition(_bar).X); e.Handled = true; };
            _bar.MouseMove += (_, e) => { if (_owner._dragging == this) Apply(e.GetPosition(_bar).X); };
            _bar.MouseLeftButtonUp += (_, _) => { if (_owner._dragging != this) return; _owner._dragging = null; _bar.ReleaseMouseCapture(); };
            _bar.MouseWheel += (_, e) => { Apply(null, _level + (e.Delta > 0 ? 0.05 : -0.05)); e.Handled = true; };
            _bar.SizeChanged += (_, _) => Layout(false);

            Root = new Border { Child = _bar, Margin = new Thickness(0, 0, 0, 8), RenderTransform = new TranslateTransform() };
            if (master) Root.Margin = new Thickness(0, 0, 0, 12);
        }

        public void Appear()
        {
            Root.Opacity = 0;
            ((TranslateTransform)Root.RenderTransform).BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
            Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { FillBehavior = FillBehavior.Stop });
        }

        public void Set(double level, bool muted, double peak, bool animate)
        {
            bool changed = Math.Abs(level - _level) > 0.004;
            _level = level;
            _fill.Background = muted ? FillMuted : FillBrush;
            _icon.Opacity = muted ? 0.45 : 1;
            _percent.Text = muted ? L.T("без звука") : $"{level * 100:0}%";
            // Живой уровень: подсветка внутри заливки дышит вместе со звуком.
            _peak.Opacity = Math.Clamp(peak * 1.6, 0, 1);
            if (changed || _fill.Width == 0) Layout(animate && changed);
        }

        private void Layout(bool animate)
        {
            double w = _bar.ActualWidth;
            if (w <= 0) return;
            double target = Math.Max(RowHeight, w * _level);
            if (_level <= 0.001) target = 0;
            _peak.Width = target;
            if (animate)
                _fill.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            else
            {
                _fill.BeginAnimation(FrameworkElement.WidthProperty, null);
                _fill.Width = target;
            }
        }

        private void Apply(double? x, double? direct = null)
        {
            double w = Math.Max(1, _bar.ActualWidth);
            double level = Math.Clamp(direct ?? (x!.Value / w), 0, 1);
            if (_master) _owner._volume.SetVolume((float)level);
            else if (App != null) _owner._mixer.SetVolume(App, (float)level);
            Set(level, false, 0, animate: false);
        }

        private void ToggleMute()
        {
            if (_master) _owner._volume.ToggleMute();
            else if (App != null)
            {
                _owner._mixer.ToggleMute(App);
                Set(App.Muted ? 0 : App.Volume, App.Muted, 0, animate: true);
            }
        }
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
