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
    // ---------------------------------------------------------------- Arayüz güncelleme

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

    // Görüntü kalitesi bekçisi: ekran kartı yüksek kalite ayarlarına yetişemeyip kare atlıyorsa
    // (ör. zayıf dizüstü kartlarında 4K) ayarlar kademeli olarak hafifletilir. Sarma, dosya açma,
    // pencere boyutlandırma sonrasındaki kısa takılmalar ve hızlı oynatma sayılmaz.
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
        // 4-5 saniyede 10'dan fazla atlanan kare: kalıcı bir yetişememe
        if (now - first.Tick >= 4000 && drops - first.Drops >= 10)
        {
            _dropSamples.Clear();
            if (_player.ReduceQuality())
                Debug.WriteLine($"Kare atlanıyor; görüntü kalitesi {_player.QualityLevel}. kademeye indirildi");
        }
    }

    void UpdatePlayState()
    {
        // Sürüklerken video geçici olarak duraklatılıyor; düğme o sırada değişmesin
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
        // Filmler ve TV gibi: o yönde video yoksa ok hiç görünmez
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
        // Tam ekranda ve mini görünümde pencere düğmelerine gerek yok
        CaptionButtons.Visibility = _fullscreen || _mini ? Visibility.Collapsed : Visibility.Visible;
    }

    static string FormatTime(long ms)
    {
        // Filmler ve TV gibi her zaman saat:dakika:saniye
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    /// <param name="sticky">Kendiliğinden kaybolmasın (ör. oynatılamayan dosya).</param>
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

    // Filmler ve TV'den ölçüldü: yazılar ve kontroller 300 ms'de belirir, 700 ms'de söner, doğrusal
    const int FadeInMs = 300, FadeOutMs = 700;

    static void FadeLinear(UIElement element, double to, int ms) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)));

    // ---------------------------------------------------------------- Video adı (duraklatılmışken)

    // Filmler ve TV gibi: video oynarken ad hiç görünmez; duraklatılmışken fare oynayınca veya
    // duraklatınca ~2,8 sn görünür, alttaki gölge de o sırada koyulaşır
    readonly DispatcherTimer _titleTimer = new() { Interval = TimeSpan.FromMilliseconds(3000) }; // belirmenin başından sönmenin başına
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

    /// <param name="instant">Yeni video açılırken eski ad solmadan hemen kalksın.</param>
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

    /// <summary>Tam ekranda Filmler ve TV kontrolleri ve adı 16 px yukarı alıyor.</summary>
    void ApplyControlLayout()
    {
        var m = ButtonRow.Margin;
        ButtonRow.Margin = new Thickness(m.Left, m.Top, m.Right, _fullscreen ? 32 : 16);
        BigTitle.FontSize = 34;
        BigTitle.Margin = new Thickness(23, 0, 24, _fullscreen ? 163 : 147);
    }

    // ---------------------------------------------------------------- Kontrollerin gizlenmesi

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
        // Fare videonun üstündeyse imleci de gizle
        if (Overlay.IsMouseOver) Overlay.Cursor = Cursors.None;
        UpdateSubtitleLift(FadeOutMs);
    }

    Popup[] AllPopups => new[] { VolumePopup, MorePopup, SpeedPopup, TracksPopup };

    void CloseAllPopups()
    {
        foreach (var popup in AllPopups) popup.IsOpen = false;
    }

    // ---------------------------------------------------------------- Altyazının kontrollerden kaçması

    // Kontroller (duraklatılmışken video adı da) görünürken altyazı onların üstüne kayar,
    // gizlenince yerine (videonun alt boşluğuna) iner; kontrollerin belirip sönmesiyle aynı sürede
    readonly DispatcherTimer _subLiftTimer = new() { Interval = TimeSpan.FromMilliseconds(15) };
    double _subLift, _subLiftFrom, _subLiftTo;
    long _subLiftStart;
    int _subLiftMs;

    void UpdateSubtitleLift(int ms)
    {
        double h = Overlay.ActualHeight;
        if (h <= 0 || PresentationSource.FromVisual(Overlay) == null) return;
        // Altyazının alt kenarı ile çubuk / ad arasında birkaç piksel boşluk kalsın
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

    // ---------------------------------------------------------------- Işık efekti (Reveal)

    // Filmler ve TV'deki gibi: imleci merkez alan yumuşak bir ışık, yakındaki kare düğmelerin
    // çerçevelerini aydınlatır. Her düğmenin çerçevesi kendi konumuna göre hesaplanan dairesel
    // bir renk geçişi; fare hareket ettikçe geçişin merkezi imlece taşınır.
    // Değerler Filmler ve TV'den ölçüldü: imlecin ~30 px çevresinde sabit, ~45 px'te yarıya
    // iner, ~70 px'te tamamen söner
    const double RevealRadius = 72;
    readonly List<(Button Button, RadialGradientBrush Light)> _revealButtons = new();
    static readonly Point FarAway = new(-10000, -10000);

    void SetupRevealLight()
    {
        // Alt çubuk düğmeleri: imlecin ~30 px çevresinde %19, ~45 px'te %11, ~72 px'te söner
        var barStops = new[] { (0.0, 0x30), (0.42, 0x30), (0.62, 0x1C), (1.0, 0x00) };
        foreach (var button in new[] { VolumeButton, TracksButton, BackButton, PlayButton, ForwardButton,
                                        SpeedButton, MiniButton, FullButton, MoreButton })
            AddRevealLight(button, RevealRadius, barStops);

        // Kenardaki oklar daha parlak parlar (Filmler ve TV'den ölçüldü): ~18 px'e kadar %25,
        // 38 px'te %15, 47 px'te %11, 58 px'te %6,5, ~76 px'te söner
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
