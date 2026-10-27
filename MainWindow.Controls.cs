using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Oynatici.Engine;

namespace Oynatici;

public partial class MainWindow
{
    // ---------------------------------------------------------------- UI updates

    void UpdateTime()
    {
        if (_playlist.Current == null || _seekDrag.IsDragging) return;
        long length = _player.Length, time = _player.Time;
        if (length <= 0) return;

        Seek.Maximum = length;
        if (_player.State != PlayerState.Ended) Seek.Value = Math.Clamp(time, 0, length);

        var shown = (long)Seek.Value;
        TimeText.Text = FormatTime(shown);
        LengthText.Text = FormatTime(_showRemaining ? length - shown : length);
    }

    // Picture quality watchdog: if the GPU cannot keep up with the high quality settings and drops frames
    // (e.g. 4K on weak laptop GPUs), the settings are lightened step by step. Short stutters after seeking,
    // opening a file or resizing the window, and fast playback, are not counted.
    readonly Queue<(long Tick, long Drops)> _dropSamples = new();
    long _lastResizeTick;

    void WatchQuality()
    {
        var now = Environment.TickCount64;
        var steady = _player.IsPlaying && !_seekDrag.IsDragging && _rate <= 1.25
            && WindowState != WindowState.Minimized
            && now - _player.LastSeekTick > 3000 && now - _lastResizeTick > 2000;
        if (!steady) { _dropSamples.Clear(); return; }

        var drops = _player.DroppedFrames;
        if (_dropSamples.Count > 0 && drops < _dropSamples.Peek().Drops) _dropSamples.Clear();
        _dropSamples.Enqueue((now, drops));
        while (now - _dropSamples.Peek().Tick > 5000) _dropSamples.Dequeue();
        var first = _dropSamples.Peek();
        // More than 10 dropped frames in 4-5 seconds: it is permanently falling behind
        if (now - first.Tick >= 4000 && drops - first.Drops >= 10)
        {
            _dropSamples.Clear();
            if (_player.ReduceQuality())
                Debug.WriteLine($"Kare atlanıyor; görüntü kalitesi {_player.QualityLevel}. kademeye indirildi");
        }
    }

    void UpdatePlayState()
    {
        // The video is paused temporarily while dragging; the button must not change meanwhile
        var playing = _player.IsPlaying || _resumeAfterScrub;
        PlayButton.Content = playing ? IconPause : IconPlay;
        PlayButton.ToolTip = playing ? L.PauseTip : L.PlayTip;
        var hasFile = _playlist.Current != null;
        BackButton.IsEnabled = ForwardButton.IsEnabled = hasFile;
        ShowInFolderButton.IsEnabled = hasFile;
        if (!playing) { ShowControls(); ShowTitle(); }
        else { HideTitle(); RestartHideTimer(); }
        UpdateMediaControls();
    }

    void UpdateMediaControls() =>
        _media?.Update(_playlist.Current is { } path ? Path.GetFileNameWithoutExtension(path) : null,
            _player.IsPlaying || _resumeAfterScrub, _playlist.HasPrevious, _playlist.HasNext);

    void UpdatePlaylistButtons()
    {
        // Like Movies & TV: if there is no video in that direction, the arrow is not shown at all
        PrevButton.Visibility = _playlist.HasPrevious ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = _playlist.HasNext ? Visibility.Visible : Visibility.Collapsed;
        PrevButton.ToolTip = _playlist.PeekPrevious() is { } p ? L.PreviousNamedTip(Path.GetFileName(p)) : L.PreviousVideoTip;
        NextButton.ToolTip = _playlist.PeekNext() is { } n ? L.NextNamedTip(Path.GetFileName(n)) : L.NextVideoTip;
        UpdateMediaControls();
    }

    void UpdateWindowButtons()
    {
        var maximized = WindowState == WindowState.Maximized && !_fullscreen;
        MaxButton.Content = maximized ? IconRestore : IconMaximize;
        MaxButton.ToolTip = maximized ? L.Restore : L.Maximize;
        // No window buttons needed in fullscreen and mini view
        CaptionButtons.Visibility = _fullscreen || _mini ? Visibility.Collapsed : Visibility.Visible;
    }

    static string FormatTime(long ms)
    {
        // Like Movies & TV, always hours:minutes:seconds
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <param name="sticky">Do not disappear on its own (e.g. a file that cannot be played).</param>
    void ShowToast(string icon, string text, bool sticky = false)
    {
        ToastIcon.Text = icon;
        ToastText.Text = text;
        Toast.BeginAnimation(OpacityProperty, null);
        Toast.Opacity = 1;
        _toastTimer.Stop();
        if (!sticky) _toastTimer.Start();
    }

    void HideToast()
    {
        _toastTimer.Stop();
        Toast.BeginAnimation(OpacityProperty, null);
        Toast.Opacity = 0;
    }

    static void Fade(UIElement element, double to, int ms) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new QuadraticEase(),
        });

    // Measured from Movies & TV: text and controls fade in over 300 ms and out over 700 ms, linearly
    const int FadeInMs = 300, FadeOutMs = 700;

    static void FadeLinear(UIElement element, double to, int ms) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)));

    // ---------------------------------------------------------------- Video title (while paused)

    // Like Movies & TV: the title never shows while playing; while paused it shows for ~2.8 s when
    // the mouse moves or on pausing, and the shade below darkens at the same time
    readonly DispatcherTimer _titleTimer = new() { Interval = TimeSpan.FromMilliseconds(3000) }; // from the start of fading in to the start of fading out
    bool _titleVisible;

    void ShowTitle()
    {
        if (_player.IsPlaying || _playlist.Current == null || _mini || _seekDrag.IsDragging || _resumeAfterScrub)
            return;
        if (!_titleVisible)
        {
            _titleVisible = true;
            FadeLinear(BigTitle, 1, FadeInMs);
            FadeLinear(TitleShade, 1, FadeInMs);
            FadeLinear(ControlsShade, 0, FadeInMs);
            UpdateSubtitleLift(FadeInMs);
        }
        _titleTimer.Stop();
        _titleTimer.Start();
    }

    /// <param name="instant">When a new video opens, remove the old title at once without fading.</param>
    void HideTitle(bool instant = false)
    {
        _titleTimer.Stop();
        if (!_titleVisible) return;
        _titleVisible = false;
        var ms = instant ? 0 : FadeOutMs;
        FadeLinear(BigTitle, 0, ms);
        FadeLinear(TitleShade, 0, ms);
        FadeLinear(ControlsShade, 1, ms);
        UpdateSubtitleLift(ms);
    }

    /// <summary>In fullscreen Movies & TV moves the controls and the title up by 16 px.</summary>
    void ApplyControlLayout()
    {
        var m = ButtonRow.Margin;
        ButtonRow.Margin = new Thickness(m.Left, m.Top, m.Right, _fullscreen ? 32 : 16);
        BigTitle.FontSize = 34;
        BigTitle.Margin = new Thickness(23, 0, 24, _fullscreen ? 163 : 147);
    }

    // ---------------------------------------------------------------- Hiding the controls

    void ShowControls()
    {
        if (!_controlsVisible)
        {
            _controlsVisible = true;
            Controls.IsHitTestVisible = true;
            CaptionButtons.IsHitTestVisible = true;
            EdgeArrows.IsHitTestVisible = true;
            FadeLinear(EdgeArrows, 1, FadeInMs);
            FadeLinear(Controls, 1, FadeInMs);
            FadeLinear(TopBar, 1, FadeInMs);
            Overlay.Cursor = null;
            UpdateSubtitleLift(FadeInMs);
        }
        RestartHideTimer();
    }

    void RestartHideTimer()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    void TryHideControls()
    {
        _hideTimer.Stop();
        if (!_player.IsPlaying || AllPopups.Any(p => p.IsOpen) || _mouseDown || _seekDrag.IsDragging)
            return;
        if (Controls.IsMouseOver || CaptionButtons.IsMouseOver || EdgeArrows.IsMouseOver) { RestartHideTimer(); return; }

        _controlsVisible = false;
        Controls.IsHitTestVisible = false;
        CaptionButtons.IsHitTestVisible = false;
        EdgeArrows.IsHitTestVisible = false;
        FadeLinear(EdgeArrows, 0, FadeOutMs);
        FadeLinear(Controls, 0, FadeOutMs);
        FadeLinear(TopBar, 0, FadeOutMs);
        SeekTip.Visibility = Visibility.Collapsed;
        // If the mouse is over the video, hide the cursor too
        if (Overlay.IsMouseOver) Overlay.Cursor = Cursors.None;
        UpdateSubtitleLift(FadeOutMs);
    }

    Popup[] AllPopups => new[] { VolumePopup, MorePopup, SpeedPopup, TracksPopup };

    void CloseAllPopups()
    {
        foreach (var popup in AllPopups) popup.IsOpen = false;
    }

    // ---------------------------------------------------------------- Subtitles moving out of the controls' way

    // While the controls (and, when paused, the video title) are visible, subtitles move up above them,
    // and when they hide, go back down (to the video's bottom margin); in the same time the controls fade in and out
    readonly DispatcherTimer _subLiftTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    double _subLift, _subLiftFrom, _subLiftTo;
    long _subLiftStart;
    int _subLiftMs;

    void UpdateSubtitleLift(int ms)
    {
        double h = Overlay.ActualHeight;
        if (h <= 0 || PresentationSource.FromVisual(Overlay) == null) return;
        // Leave a few pixels between the subtitles' bottom edge and the bar / title
        const double gap = 6;
        double clearance = 0;
        if (_controlsVisible)
            clearance = h - Seek.TranslatePoint(new Point(0, 0), Overlay).Y + gap;
        if (_titleVisible)
            clearance = Math.Max(clearance, h - BigTitle.TranslatePoint(new Point(0, 0), Overlay).Y + gap);
        var target = Math.Clamp(clearance / h, 0, 0.7);

        _subLiftFrom = _subLift;
        _subLiftTo = target;
        _subLiftStart = Environment.TickCount64;
        _subLiftMs = ms;
        StepSubtitleLift();
        if (ms > 0 && Math.Abs(_subLiftTo - _subLift) > 0.0001) _subLiftTimer.Start();
    }

    void StepSubtitleLift()
    {
        var t = _subLiftMs <= 0 ? 1 : Math.Min(1, (Environment.TickCount64 - _subLiftStart) / (double)_subLiftMs);
        var eased = 1 - (1 - t) * (1 - t);
        _subLift = _subLiftFrom + (_subLiftTo - _subLiftFrom) * eased;
        _player.SetSubtitleClearance(_subLift);
        if (t >= 1) _subLiftTimer.Stop();
    }

    // ---------------------------------------------------------------- Light effect (Reveal)

    // As in Movies & TV: a soft light centered on the cursor lights up the borders of nearby
    // square buttons. Each button's border is a radial gradient computed from its own position;
    // as the mouse moves, the gradient's center moves to the cursor.
    // Values measured from Movies & TV: constant within ~30 px of the cursor, half at ~45 px,
    // fully gone at ~70 px
    const double RevealRadius = 72;
    readonly List<(Button Button, RadialGradientBrush Light)> _revealButtons = new();
    static readonly Point FarAway = new(-10000, -10000);

    void SetupRevealLight()
    {
        // Bottom bar buttons: 19% within ~30 px of the cursor, 11% at ~45 px, gone at ~72 px
        var barStops = new[] { (0.0, 0x30), (0.42, 0x30), (0.62, 0x1C), (1.0, 0x00) };
        foreach (var button in new[] { VolumeButton, TracksButton, BackButton, PlayButton, ForwardButton,
                                        SpeedButton, MiniButton, FullButton, MoreButton })
            AddRevealLight(button, RevealRadius, barStops);

        // The edge arrows glow brighter (measured from Movies & TV): 25% up to ~18 px,
        // 15% at 38 px, 11% at 47 px, 6.5% at 58 px, gone at ~76 px
        var edgeStops = new[] { (0.0, 0x40), (0.24, 0x40), (0.50, 0x25), (0.62, 0x1D), (0.68, 0x19), (0.76, 0x11), (1.0, 0x00) };
        foreach (var button in new[] { PrevButton, NextButton })
            AddRevealLight(button, 76, edgeStops);
    }

    void AddRevealLight(Button button, double radius, (double Offset, int Alpha)[] stops)
    {
        var light = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            RadiusX = radius,
            RadiusY = radius,
            Center = FarAway,
            GradientOrigin = FarAway,
        };
        foreach (var (offset, alpha) in stops)
            light.GradientStops.Add(new GradientStop(Color.FromArgb((byte)alpha, 255, 255, 255), offset));
        button.BorderBrush = light;
        _revealButtons.Add((button, light));
    }

    void UpdateRevealLight(MouseEventArgs? e)
    {
        foreach (var (button, light) in _revealButtons)
        {
            var p = e != null && button.IsVisible ? e.GetPosition(button) : FarAway;
            light.Center = p;
            light.GradientOrigin = p;
        }
    }
}
