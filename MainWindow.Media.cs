using System;
using System.Windows;
using System.Windows.Media;
using SnowyNotch.Services;

namespace SnowyNotch;

public partial class MainWindow
{
    private void OnMediaChanged(MediaSnapshot? snap)
    {
        var prev = _snap;
        _snap = snap;

        if (snap != null)
        {
            CompactArt.Background = Widgets.MediaVisuals.ArtBrush(snap);
            CompactBars.IsPlaying = snap.IsPlaying;
        }
        else
        {
            CompactBars.IsPlaying = false;
        }
        ApplyCompactAccent();

        foreach (var mw in MediaWidgets()) mw.Update(snap);

        bool newTrack = snap != null && prev != null && (prev.Title != snap.Title || prev.Artist != snap.Artist);
        if (newTrack && _settings.PeekOnTrackChange && _state != IslandState.Expanded && !IsGhosted)
        {
            if (_settings.HomeWidgets.Contains("media")) ShowView(View.Home);
            else OpenFocus("media");
            _sticky = false;
            Peek(_settings.PeekDurationSec);
        }

        UpdateState();
        if (_state == IslandState.Expanded) SetTargetsAfterLayout();
    }

    private void Peek(double seconds)
    {
        if (IsGhosted) return;
        _peeking = true;
        _peekTimer.Stop();
        _peekTimer.Interval = TimeSpan.FromSeconds(seconds);
        _peekTimer.Start();
        UpdateState();
    }

    private void ApplyCompactAccent() =>
        CompactBars.BarColor = _settings.AccentFromArtwork && _snap != null ? _snap.Accent : ColorExtractor.Fallback;
}
