using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SnowyNotch.Services;
using RepeatMode = Windows.Media.MediaPlaybackAutoRepeatMode;
using SnowyNotch.Localization;

namespace SnowyNotch.Widgets;

public partial class MediaWidget : UserControl, IMediaView
{
    private static readonly Brush ToggleOn = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));
    private static readonly Brush ToggleOff = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));

    private readonly MediaService _media;
    private readonly AppSettings _settings;
    private MediaSnapshot? _snap;

    public MediaWidget(MediaService media, AppSettings settings)
    {
        InitializeComponent();
        _media = media;
        _settings = settings;
        ApplySettings();
        Update(media.Current);
    }

    public event Action? ArtworkClicked;

    public event Action? LyricsClicked;

    public void ApplySettings()
    {
        Bars.Visibility = _settings.ShowVisualizer ? Visibility.Visible : Visibility.Collapsed;
        LyricsButton.Visibility = _settings.LyricsEnabled ? Visibility.Visible : Visibility.Collapsed;
        ShuffleButton.Visibility = _settings.ShowShuffleButton ? Visibility.Visible : Visibility.Collapsed;
        RepeatButton.Visibility = _settings.ShowRepeatButton ? Visibility.Visible : Visibility.Collapsed;
        ApplyAccent();
    }

    public void Update(MediaSnapshot? snap)
    {
        _snap = snap;
        Player.Visibility = snap != null ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = snap != null ? Visibility.Collapsed : Visibility.Visible;
        if (snap == null)
        {
            Bars.IsPlaying = false;
            return;
        }

        TitleText.Text = snap.Title;
        // «Исполнитель · Spotify» — клик выводит приложение вперёд.
        ArtistText.Text = snap.SourceName.Length > 0
            ? (snap.Artist.Length > 0 ? $"{snap.Artist} · {snap.SourceName}" : snap.SourceName)
            : snap.Artist;
        ArtistText.Cursor = snap.SourceName.Length > 0 ? Cursors.Hand : null;
        ArtistText.ToolTip = snap.SourceName.Length > 0 ? L.F("Открыть {0}", snap.SourceName) : null;
        Art.Background = MediaVisuals.ArtBrush(snap);

        Bars.IsPlaying = snap.IsPlaying;
        PlayGlyph.Visibility = snap.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseGlyph.Visibility = snap.IsPlaying ? Visibility.Visible : Visibility.Collapsed;

        ShuffleButton.IsEnabled = snap.CanShuffle;
        ShuffleButton.Foreground = snap.IsShuffle ? ToggleOn : ToggleOff;
        RepeatButton.IsEnabled = snap.CanRepeat;
        RepeatButton.Foreground = snap.Repeat != RepeatMode.None ? ToggleOn : ToggleOff;
        RepeatOneBadge.Visibility = snap.Repeat == RepeatMode.Track ? Visibility.Visible : Visibility.Collapsed;

        ApplyAccent();
        Tick();
    }

    public void Tick()
    {
        if (_snap == null) return;
        var dur = _snap.Duration;
        var pos = _snap.CurrentPosition;
        bool has = dur > TimeSpan.Zero;
        Fill.Width = has ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1) * Track.ActualWidth : 0;
        ElapsedText.Text = has ? MediaVisuals.Format(pos) : "";
        RemainingText.Text = has ? "-" + MediaVisuals.Format(dur - pos) : "";
    }

    private void ApplyAccent() =>
        Bars.BarColor = _settings.AccentFromArtwork && _snap != null ? _snap.Accent : ColorExtractor.Fallback;

    private void Art_MouseEnter(object sender, MouseEventArgs e) => Lift(1.06);
    private void Art_MouseLeave(object sender, MouseEventArgs e) => Lift(1);

    private void Lift(double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        ArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        ArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void Art_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_snap == null) return;
        e.Handled = true;
        ArtworkClicked?.Invoke();
    }

    private void Lyrics_Click(object sender, RoutedEventArgs e) => LyricsClicked?.Invoke();

    private void Source_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_snap is not { SourceName.Length: > 0 }) return;
        e.Handled = true;
        _media.OpenSource();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _media.TogglePlayPauseAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void Prev_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();
    private async void Shuffle_Click(object sender, RoutedEventArgs e) => await _media.ToggleShuffleAsync();
    private async void Repeat_Click(object sender, RoutedEventArgs e) => await _media.CycleRepeatAsync();

    private async void Track_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_snap == null || _snap.Duration <= TimeSpan.Zero || Track.ActualWidth <= 0) return;
        e.Handled = true;
        double f = Math.Clamp(e.GetPosition(Track).X / Track.ActualWidth, 0, 1);
        await _media.SeekAsync(TimeSpan.FromSeconds(_snap.Duration.TotalSeconds * f));
    }
}

internal static class MediaVisuals
{
    private static readonly Brush NoArt = CreateNoArt();
    private static System.Windows.Media.Imaging.BitmapSource? _artFor;
    private static Brush? _art;

    /// <summary>
    /// Кисть обложки. Снимок плеера приходит каждые несколько секунд, а обложка та же — отдаём одну
    /// замороженную кисть на всех (остров, плеер, текст песни), а не новую при каждом обновлении.
    /// </summary>
    public static Brush ArtBrush(MediaSnapshot snap)
    {
        if (snap.Artwork == null) return NoArt;
        if (!ReferenceEquals(snap.Artwork, _artFor) || _art == null)
        {
            var b = new ImageBrush(snap.Artwork) { Stretch = Stretch.UniformToFill };
            b.Freeze();
            _art = b;
            _artFor = snap.Artwork;
        }
        return _art;
    }

    private static Brush CreateNoArt()
    {
        var b = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2E));
        b.Freeze();
        return b;
    }

    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
}
