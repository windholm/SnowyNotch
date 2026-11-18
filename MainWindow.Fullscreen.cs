using System.Windows;
using SnowyNotch.Views;

namespace SnowyNotch;

/// <summary>Полноэкранный плеер: открывается, если вытащить обложку из острова, или кнопкой на большой обложке.</summary>
public partial class MainWindow
{
    private FullscreenPlayer? _fullscreenPlayer;

    /// <summary>Обложку отпустили: над самим островом — отмена, в любом другом месте — плеер во весь экран.</summary>
    private bool ArtworkDropped(Point screenPx, Rect ghostPx)
    {
        if (IsOverIsland((int)screenPx.X, (int)screenPx.Y)) return false;
        return OpenFullscreen(screenPx, ghostPx);
    }

    private bool OpenFullscreen(Point screenPx, Rect? fromPx)
    {
        if (_snap == null) return false;
        if (_fullscreenPlayer != null)
        {
            _fullscreenPlayer.Activate();
            return true;
        }

        var player = new FullscreenPlayer(_media, _lyricsService, _settings, screenPx, fromPx);
        player.Closed += (_, _) =>
        {
            if (_fullscreenPlayer == player) _fullscreenPlayer = null;
        };
        _fullscreenPlayer = player;
        player.Update(_snap);
        player.Show();

        // Остров уступает сцену: сворачивается в пилюлю.
        if (_view == View.Artwork) CloseArtwork();
        if (_pinned) ClosePinned();
        _hoverActive = _peeking = _sticky = false;
        UpdateState();
        return true;
    }

#if DEBUG
    /// <summary>Для скриншотов: плеер во весь экран с демо-текстом.</summary>
    public FullscreenPlayer? DebugOpenFullscreen(System.Collections.Generic.IReadOnlyList<string> lyrics, int current)
    {
        var area = SystemParameters.WorkArea;
        OpenFullscreen(new Point(10, 10), new Rect(area.Width / 2 - 60, 40, 120, 120));
        _fullscreenPlayer?.ShowDemo(lyrics, current);
        return _fullscreenPlayer;
    }
#endif
}
