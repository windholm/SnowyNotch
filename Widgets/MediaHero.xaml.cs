using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SnowyNotch.Services;

namespace SnowyNotch.Widgets;

public partial class MediaHero : UserControl, IMediaView
{
    private readonly MediaService _media;
    private readonly AppSettings _settings;
    private MediaSnapshot? _snap;
    private readonly Controls.ArtworkDrag _artDrag;

    public MediaHero(MediaService media, AppSettings settings)
    {
        InitializeComponent();
        _artDrag = new Controls.ArtworkDrag(BigArt, () => _snap != null ? BigArt.Background : null);
        _artDrag.Click += () => CloseRequested?.Invoke();
        _media = media;
        _settings = settings;
        ApplySettings();
    }

    public event Action? CloseRequested;

    public event Action? LyricsClicked;

    /// <summary>Кнопка «во весь экран»: прямоугольник большой обложки в пикселях экрана — оттуда полетит обложка.</summary>
    public event Action<Rect>? FullscreenRequested;

    public Func<Point, Rect, bool>? ArtworkDropped { get => _artDrag.Drop; set => _artDrag.Drop = value; }

    public void ApplySettings()
    {
        Bars.Visibility = _settings.ShowVisualizer ? Visibility.Visible : Visibility.Collapsed;
        LyricsButton.Visibility = _settings.LyricsEnabled ? Visibility.Visible : Visibility.Collapsed;
        Wave.IsWavy = _settings.WavyProgress;
        ApplyAccent();
    }

    public void Update(MediaSnapshot? snap)
    {
        _snap = snap;
        if (snap == null)
        {
            Bars.IsPlaying = false;
            return;
        }
        TitleText.Text = snap.Title;
        ArtistText.Text = snap.Artist;
        BigArt.Background = MediaVisuals.ArtBrush(snap);
        Bars.IsPlaying = snap.IsPlaying;
        Wave.IsPlaying = snap.IsPlaying;
        PlayGlyph.Visibility = snap.IsPlaying ? Visibility.Collapsed : Visibility.Visible;
        PauseGlyph.Visibility = snap.IsPlaying ? Visibility.Visible : Visibility.Collapsed;
        ApplyAccent();
        Tick();
    }

    public void Tick()
    {
        if (_snap == null) return;
        var dur = _snap.Duration;
        var pos = _snap.CurrentPosition;
        bool has = dur > TimeSpan.Zero;
        Wave.Progress = has ? Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1) : 0;
        ElapsedText.Text = has ? MediaVisuals.Format(pos) : "";
        RemainingText.Text = has ? "-" + MediaVisuals.Format(dur - pos) : "";
    }

    public void PlayEnterAnimation(double speed)
    {
        double Ms(double ms) => ms / speed;
        var grow = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(Ms(460)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.22 },
        };
        BigArtScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        BigArtScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        DetailsShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-24, 0, TimeSpan.FromMilliseconds(Ms(420))) { BeginTime = TimeSpan.FromMilliseconds(Ms(90)), EasingFunction = ease });
        Details.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Ms(300))) { BeginTime = TimeSpan.FromMilliseconds(Ms(90)) });
        Glow.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Ms(600))) { BeginTime = TimeSpan.FromMilliseconds(Ms(160)) });
    }

    private void ApplyAccent()
    {
        var c = _settings.AccentFromArtwork && _snap != null ? _snap.Accent : ColorExtractor.Fallback;
        Bars.BarColor = c;
        GlowInner.Color = Color.FromArgb(0x70, c.R, c.G, c.B);
        GlowOuter.Color = Color.FromArgb(0x00, c.R, c.G, c.B);
    }

    private void Lyrics_Click(object sender, RoutedEventArgs e) => LyricsClicked?.Invoke();

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        var tl = BigArt.PointToScreen(new Point(0, 0));
        var br = BigArt.PointToScreen(new Point(BigArt.ActualWidth, BigArt.ActualHeight));
        FullscreenRequested?.Invoke(new Rect(tl, br));
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _media.TogglePlayPauseAsync();
    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void Prev_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    private async void Track_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_snap == null || _snap.Duration <= TimeSpan.Zero || Track.ActualWidth <= 0) return;
        e.Handled = true;
        double f = Math.Clamp(e.GetPosition(Track).X / Track.ActualWidth, 0, 1);
        await _media.SeekAsync(TimeSpan.FromSeconds(_snap.Duration.TotalSeconds * f));
    }
}
