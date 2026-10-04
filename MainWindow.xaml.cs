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

public partial class MainWindow : Window
{
    // Segoe MDL2 Assets ikonları
    const string IconPlay = "\uE768", IconPause = "\uE769", IconMute = "\uE74F";
    const string IconFull = "\uE740", IconExitFull = "\uE73F", IconCheck = "\uE73E";
    const string IconBack = "\uED3C", IconForward = "\uED3D", IconPrev = "\uE892", IconNext = "\uE893";
    const string IconMaximize = "\uE922", IconRestore = "\uE923";

    const int BackSeconds = 10, ForwardSeconds = 30;
    const double ResizeEdge = 6;
    static readonly double[] Speeds = { 0.5, 0.75, 1, 1.25, 1.5, 2 };

    readonly Settings _settings = Settings.Load();
    readonly FolderPlaylist _playlist = new();
    readonly MpvEngine _player;
    readonly Window _overlayWindow;
    readonly SliderDrag _seekDrag, _volumeDrag;

    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    bool _showRemaining, _controlsVisible = true;
    bool _fullscreen, _mini;
    RECT _rectBeforeFullscreen;
    IntPtr _overlayHwnd;
    Popup? _lastClosedPopup;
    Rect _boundsBeforeMini;
    double _rate = 1;
    long _pendingSeek = -1, _lastScrubSeek;
    bool _resumeAfterScrub;
    long _scrubTarget;
    readonly DispatcherTimer _scrubTimer = new();
    Point _lastMouse;
    IntPtr _hwnd;

    // Videonun üstünde tutup sürükleme ve çift tık
    bool _mouseDown, _swiping;
    Point _downPoint, _lastClickPos;
    long _lastClickTick, _popupClosedTick;
    double _shiftDip;

    public MainWindow()
    {
        InitializeComponent();
        RestoreSavedBounds();

        try
        {
            _player = new MpvEngine { Interpolation = _settings.Interpolation };
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            MessageBox.Show(L.MpvMissing, "Video Player", MessageBoxButton.OK, MessageBoxImage.Error);
            Environment.Exit(1);
            throw;
        }
        Video.HandleCreated += hwnd => _player.Attach(hwnd);

        // Motor olayları kendi iş parçacığından gelir; oynatıcıya buradan dokunmak
        // kilitlenmeye yol açabilir, o yüzden hep arayüz iş parçacığına aktar
        _player.Playing += () => Dispatcher.BeginInvoke(OnPlaying);
        _player.Paused += () => Dispatcher.BeginInvoke(UpdatePlayState);
        _player.Stopped += () => Dispatcher.BeginInvoke(UpdatePlayState);
        _player.EndReached += () => Dispatcher.BeginInvoke(OnEnded);
        _player.Error += () => Dispatcher.BeginInvoke(() =>
        {
            ShowToast("\uE783", L.CannotPlay);
            UpdatePlayState();
        });

        // Kontrolleri videonun üstündeki saydam pencereye taşı
        Root.Children.Remove(Overlay);
        _overlayWindow = new Window
        {
            Title = L.ControlsWindowTitle,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Width = 1,
            Height = 1,
            Content = Overlay,
        };
        Loaded += (_, _) => PrepareOverlayWindow();
        LocationChanged += (_, _) => PositionOverlay();
        Video.SizeChanged += (_, _) => PositionOverlay();

        _tick.Tick += (_, _) => UpdateTime();
        _hideTimer.Tick += (_, _) => TryHideControls();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Fade(Toast, 0, 250); };

        // Kontroller videonun üstündeki ayrı pencereye taşındığı için XAML'daki
        // ElementName bağlamaları çalışmıyor; hedefleri burada ver
        VolumePopup.PlacementTarget = VolumeButton;
        MorePopup.PlacementTarget = MoreButton;
        // Menüyü kapatmak için yapılan tık başka bir şeyi tetiklemesin
        foreach (var popup in new[] { VolumePopup, MorePopup })
            // Closed olayı kapanma animasyonu bitince geliyor; kapanma anını hemen yakala
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Popup.IsOpenProperty, typeof(Popup))
                .AddValueChanged(popup, (_, _) =>
                {
                    if (popup.IsOpen) return;
                    _popupClosedTick = Environment.TickCount64;
                    _lastClosedPopup = popup;
                });

        BuildMoreMenu();
        VolumeSlider.Value = _settings.Volume;
        UpdateVolumeIcons();
        UpdatePlaylistButtons();
        UpdatePlayState();

        _seekDrag = new SliderDrag(Seek) { Precise = true };
        _seekDrag.Started += OnSeekDragStarted;
        _scrubTimer.Tick += (_, _) => { if (_seekDrag.IsDragging) ScrubTo(_scrubTarget); else _scrubTimer.Stop(); };
        _seekDrag.Moved += OnSeekDragMoved;
        _seekDrag.Completed += OnSeekDragCompleted;
        Seek.MouseMove += Seek_MouseMove;
        Seek.MouseLeave += (_, _) => { if (!_seekDrag.IsDragging) SeekTip.Visibility = Visibility.Collapsed; };

        _volumeDrag = new SliderDrag(VolumeSlider);
        // Sürükleme fareyi popup'tan aldı; geri ver ki dışarı tıklayınca popup kapansın
        _volumeDrag.Completed += _ =>
        {
            if (VolumePopup.IsOpen) Mouse.Capture(VolumePopup.Child, CaptureMode.SubTree);
        };

        foreach (UIElement target in new UIElement[] { this, Overlay })
        {
            target.DragOver += OnDragOver;
            target.Drop += OnDrop;
        }
        Overlay.MouseMove += Overlay_MouseMove;
        Overlay.PreviewMouseLeftButtonDown += Overlay_PreviewMouseLeftButtonDown;
        Overlay.MouseLeftButtonDown += Overlay_MouseLeftButtonDown;
        Overlay.MouseLeftButtonUp += Overlay_MouseLeftButtonUp;
        Overlay.MouseRightButtonUp += Overlay_MouseRightButtonUp;
        Overlay.MouseDown += Overlay_MouseDown;
        Overlay.MouseWheel += (_, e) => ChangeVolume(e.Delta > 0 ? 5 : -5);
        Overlay.MouseLeave += (_, _) => { if (!_mouseDown) RestartHideTimer(); };
        PreviewKeyDown += OnKeyDown;
        StateChanged += (_, _) => { UpdateWindowButtons(); FitToScreenEdges(); };
        SizeChanged += (_, _) => FitToScreenEdges();

        _tick.Start();
    }

    // ---------------------------------------------------------------- Dosya açma

    public void OpenFile(string path)
    {
        if (!File.Exists(path)) return;
        _playlist.Load(path);
        PlayCurrent();
    }

    public void BringToFrontAndOpen(string? path)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (!string.IsNullOrEmpty(path)) OpenFile(path);
    }

    void PlayCurrent(long startAt = -1)
    {
        var path = _playlist.Current;
        if (path == null) return;

        _pendingSeek = startAt;
        _rate = 1;
        UpdateSpeedButtons();

        _player.Open(path);

        Title = Path.GetFileName(path);
        TitleText.Text = Path.GetFileNameWithoutExtension(path);
        Welcome.Visibility = Visibility.Collapsed;
        Seek.IsEnabled = true;
        UpdatePlaylistButtons();
        ShowControls();
    }

    void OpenWithDialog()
    {
        var exts = string.Join(";", FolderPlaylist.VideoExtensions.Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = L.OpenVideoTitle,
            Filter = $"{L.VideoFiles}|{exts}|{L.AllFiles}|*.*",
        };
        if (_playlist.Current is { } cur) dialog.InitialDirectory = Path.GetDirectoryName(cur);
        if (dialog.ShowDialog(this) == true) OpenFile(dialog.FileName);
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            OpenFile(files[0]);
            Activate();
        }
        e.Handled = true;
    }

    // ---------------------------------------------------------------- Oynatma

    void OnPlaying()
    {
        _player.Volume = (int)VolumeSlider.Value;
        _player.Mute = _settings.Muted;
        if (Math.Abs(_player.Rate - _rate) > 0.001) _player.Rate = _rate;
        if (_pendingSeek > 0) _player.Time = _pendingSeek;
        _pendingSeek = -1;
        UpdatePlayState();
    }

    void OnEnded()
    {
        switch (_settings.EndAction)
        {
            case EndAction.Next when _playlist.HasNext:
                Next();
                return;
            case EndAction.Repeat:
                PlayCurrent();
                return;
        }
        // Sonda kal: çubuk sonda dursun, oynat düğmesi baştan başlatsın
        Seek.Value = Seek.Maximum;
        UpdatePlayState();
        ShowControls();
    }

    bool IsFinished => _player.State is PlayerState.Ended or PlayerState.Idle or PlayerState.Error;

    void TogglePlay()
    {
        if (_playlist.Current == null) { OpenWithDialog(); return; }
        if (IsFinished) { PlayCurrent(); return; }

        _player.SetPause(_player.IsPlaying);
    }

    void SkipBy(int seconds)
    {
        if (!_player.IsSeekable) return;
        var length = _player.Length;
        _player.Time = Math.Clamp(_player.Time + seconds * 1000L, 0, Math.Max(0, length - 500));
        ShowToast(seconds < 0 ? IconBack : IconForward, seconds < 0 ? L.SecondsBack(-seconds) : L.SecondsForward(seconds));
        UpdateTime();
    }

    void Next()
    {
        if (!_playlist.HasNext) { ShowToast(IconNext, L.LastInFolder); return; }
        _playlist.MoveNext();
        PlayCurrent();
    }

    void Previous()
    {
        if (!_playlist.HasPrevious) { ShowToast(IconPrev, L.FirstInFolder); return; }
        _playlist.MovePrevious();
        PlayCurrent();
    }

    void SetRate(double rate)
    {
        _rate = rate;
        _player.Rate = rate;
        UpdateSpeedButtons();
        ShowToast("\uEC4A", L.Speed(rate));
    }

    // ---------------------------------------------------------------- İlerleme çubuğu

    void OnSeekDragMoved(double value)
    {
        TimeText.Text = FormatTime((long)value);
        ShowSeekTip(value);
        // Sürüklerken videoyu da takip ettir ama her harekette değil; motoru saniyede
        // onlarca kez sardırmak görüntüyü takıltır. Atlanan son konum, fare durunca
        // zamanlayıcıyla sarılır ki görüntü tam fareyle aynı yerde kalsın.
        _scrubTarget = (long)value;
        if (IsFinished || !_player.IsSeekable) return;
        var now = Environment.TickCount64;
        if (now - _lastScrubSeek < _player.ScrubIntervalMs)
        {
            _scrubTimer.Interval = TimeSpan.FromMilliseconds(_player.ScrubIntervalMs);
            _scrubTimer.Stop();
            _scrubTimer.Start();
            return;
        }
        ScrubTo(_scrubTarget);
    }

    void ScrubTo(long ms)
    {
        _scrubTimer.Stop();
        _lastScrubSeek = Environment.TickCount64;
        _player.Time = ms;
    }

    /// <summary>
    /// Sürüklerken video duraklasın; fare durduğunda video o noktadan oynamaya başlayıp
    /// bir sonraki harekette geri sarılınca aynı sahne tekrar tekrar görünüyordu.
    /// Böylece fare nerede durursa tam o andaki kare görünür.
    /// </summary>
    void OnSeekDragStarted()
    {
        _resumeAfterScrub = _player.IsPlaying;
        if (_resumeAfterScrub) _player.SetPause(true);
    }

    void OnSeekDragCompleted(double value)
    {
        SeekTip.Visibility = Seek.IsMouseOver ? Visibility.Visible : Visibility.Collapsed;
        _scrubTimer.Stop();
        var resume = _resumeAfterScrub;
        _resumeAfterScrub = false;
        if (IsFinished) { PlayCurrent((long)value); return; }
        if (_player.IsSeekable) _player.Time = (long)value;
        if (resume) _player.SetPause(false);
    }

    void Seek_MouseMove(object sender, MouseEventArgs e)
    {
        if (Seek.Maximum <= 1 || _seekDrag.IsDragging) return;
        ShowSeekTip(_seekDrag.ValueAt(e.GetPosition(Seek).X));
    }

    void ShowSeekTip(double value)
    {
        if (Seek.Maximum <= 1) return;
        SeekTipText.Text = FormatTime((long)value);
        SeekTip.Visibility = Visibility.Visible;
        SeekTip.UpdateLayout();
        var x = 10 + (value - Seek.Minimum) / (Seek.Maximum - Seek.Minimum) * (Seek.ActualWidth - 20);
        Canvas.SetLeft(SeekTip, Math.Clamp(x - SeekTip.ActualWidth / 2, 0, Math.Max(0, Seek.ActualWidth - SeekTip.ActualWidth)));
    }

    // ---------------------------------------------------------------- Ses

    void ChangeVolume(int delta)
    {
        if (_settings.Muted && delta > 0) SetMuted(false);
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);
        ShowToast(VolumeIcon(), L.Volume((int)VolumeSlider.Value));
    }

    void SetMuted(bool muted)
    {
        _settings.Muted = muted;
        _player.Mute = muted;
        SaveSettingsSoon();
        UpdateVolumeIcons();
    }

    void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var v = (int)Math.Round(e.NewValue);
        _settings.Volume = v;
        SaveSettingsSoon();
        if (_player != null) _player.Volume = v;
        VolumeText.Text = v.ToString();
        UpdateVolumeIcons();
    }

    /// <summary>
    /// Ses ayarını değiştirince kısa bir süre sonra kaydet. Önceden sadece oynatıcı kapanırken
    /// kaydediliyordu; düzgün kapanmazsa son ses ayarı kayboluyordu. Ses çubuğu sürüklenirken
    /// her adımda diske yazmamak için son değişiklikten 1 sn sonra yazılır.
    /// </summary>
    void SaveSettingsSoon()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _settings.Save(); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    DispatcherTimer? _saveTimer;

    string VolumeIcon()
    {
        var v = VolumeSlider.Value;
        if (_settings.Muted || v <= 0) return IconMute;
        return v < 34 ? "\uE993" : v < 67 ? "\uE994" : "\uE995";
    }

    void UpdateVolumeIcons()
    {
        var icon = VolumeIcon();
        VolumeButton.Content = icon;
        MuteButton.Content = icon;
    }

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
        LengthText.Text = _showRemaining ? "-" + FormatTime(length - shown) : FormatTime(length);
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
        if (!playing) ShowControls();
        else RestartHideTimer();
    }

    void UpdatePlaylistButtons()
    {
        PrevButton.IsEnabled = _playlist.HasPrevious;
        NextButton.IsEnabled = _playlist.HasNext;
        PrevButton.ToolTip = _playlist.PeekPrevious() is { } p ? L.PreviousNamedTip(Path.GetFileName(p)) : L.PreviousVideoTip;
        NextButton.ToolTip = _playlist.PeekNext() is { } n ? L.NextNamedTip(Path.GetFileName(n)) : L.NextVideoTip;
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
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    void ShowToast(string icon, string text)
    {
        ToastIcon.Text = icon;
        ToastText.Text = text;
        Toast.BeginAnimation(OpacityProperty, null);
        Toast.Opacity = 1;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    static void Fade(UIElement element, double to, int ms) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new QuadraticEase(),
        });

    // ---------------------------------------------------------------- Kontrollerin gizlenmesi

    void ShowControls()
    {
        if (!_controlsVisible)
        {
            _controlsVisible = true;
            Controls.IsHitTestVisible = true;
            CaptionButtons.IsHitTestVisible = true;
            Fade(Controls, 1, 150);
            Fade(TopBar, 1, 150);
            Overlay.Cursor = null;
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
        if (!_player.IsPlaying || VolumePopup.IsOpen || MorePopup.IsOpen || _mouseDown || _seekDrag.IsDragging)
            return;
        if (Controls.IsMouseOver || CaptionButtons.IsMouseOver) { RestartHideTimer(); return; }

        _controlsVisible = false;
        Controls.IsHitTestVisible = false;
        CaptionButtons.IsHitTestVisible = false;
        Fade(Controls, 0, 300);
        Fade(TopBar, 0, 300);
        SeekTip.Visibility = Visibility.Collapsed;
        // Fare videonun üstündeyse imleci de gizle
        if (Overlay.IsMouseOver) Overlay.Cursor = Cursors.None;
    }

    // ---------------------------------------------------------------- Fare

    static bool IsDescendant(DependencyObject parent, object? source)
    {
        var child = source as DependencyObject;
        while (child != null)
        {
            if (child == parent) return true;
            child = child is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }
        return false;
    }

    bool CanResize => WindowState == WindowState.Normal && !_fullscreen;

    /// <summary>Fare pencere kenarındaysa Windows'un boyutlandırma kodunu döndürür.</summary>
    int EdgeHit(Point p)
    {
        if (!CanResize) return 0;
        double w = Overlay.ActualWidth, h = Overlay.ActualHeight;
        bool left = p.X < ResizeEdge, right = p.X > w - ResizeEdge;
        bool top = p.Y < ResizeEdge, bottom = p.Y > h - ResizeEdge;
        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        return 0;
    }

    static Cursor? EdgeCursor(int hit) => hit switch
    {
        HTLEFT or HTRIGHT => Cursors.SizeWE,
        HTTOP or HTBOTTOM => Cursors.SizeNS,
        HTTOPLEFT or HTBOTTOMRIGHT => Cursors.SizeNWSE,
        HTTOPRIGHT or HTBOTTOMLEFT => Cursors.SizeNESW,
        _ => null,
    };

    double SwipeThreshold => Math.Clamp(Overlay.ActualWidth * 0.18, 80, 220);

    void Overlay_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(Overlay);
        // WPF düzen değişince de MouseMove gönderir; gerçekten hareket edildiyse göster
        if ((pos - _lastMouse).Length > 2) ShowControls();
        _lastMouse = pos;

        if (!_mouseDown)
        {
            var edgeCursor = EdgeCursor(EdgeHit(pos));
            Overlay.Cursor = edgeCursor ?? (_controlsVisible ? null : Cursors.None);
            // Kenardayken (köşedeki pencere düğmelerinin üstü dahil) boyutlandırma imleci görünsün
            Overlay.ForceCursor = edgeCursor != null;
            return;
        }
        if (_playlist.Current == null) return;

        var d = pos - _downPoint;
        if (!_swiping && Math.Abs(d.X) > 16 && Math.Abs(d.X) > Math.Abs(d.Y) * 1.5)
        {
            _swiping = true;
            BeginSwipeVisual();
        }
        if (!_swiping) return;

        // Video fareyi birebir izleyerek pencere içinde kayar, boşalan taraf siyah kalır;
        // ekrandan tamamen çıkana kadar çekilebilir. mpv videonun kendisini kaydırdığı için
        // video oynamaya devam eder. Her fare hareketinde hemen gönderilir, mpv ekranın her
        // yenilemesinde en güncelini çizer.
        _shiftDip = d.X;
        SendShift(d.X);
    }

    /// <summary>
    /// Kenardan boyutlandırma. Önizleme olayında yapılıyor ki sağ üst köşedeki kapat düğmesi
    /// gibi kenara değen düğmeler tıklamayı almadan önce yakalansın.
    /// </summary>
    void Overlay_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var edge = EdgeHit(e.GetPosition(Overlay));
        if (edge == 0) return;
        StartSystemDrag(edge);
        e.Handled = true;
    }

    void Overlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(Overlay);

        if (IsDescendant(Controls, e.OriginalSource) || IsDescendant(CaptionButtons, e.OriginalSource)
            || IsDescendant(Welcome, e.OriginalSource))
            return;
        if (Environment.TickCount64 - _popupClosedTick < 300) { e.Handled = true; return; }

        // Fare yakalama ClickCount'u sıfırladığı için çift tıkı kendimiz algılıyoruz
        var now = Environment.TickCount64;
        var isDouble = now - _lastClickTick <= GetDoubleClickTime() && (pos - _lastClickPos).Length < 8;
        _lastClickTick = isDouble ? 0 : now;
        _lastClickPos = pos;

        var onTopBar = IsDescendant(TopBar, e.OriginalSource) && !_fullscreen;
        if (isDouble)
        {
            // Üst şeritte çift tık ekranı kaplar, videoda çift tık tam ekran yapar
            if (onTopBar && !_mini) ToggleMaximize();
            else ToggleFullscreen();
            e.Handled = true;
            return;
        }

        // Üst şeritten tutunca pencereyi taşı (Windows'un kendi taşıma/yapıştırma davranışıyla)
        if (onTopBar)
        {
            StartSystemDrag(HTCAPTION);
            e.Handled = true;
            return;
        }

        _mouseDown = true;
        _swiping = false;
        _downPoint = pos;
        Overlay.CaptureMouse();
        e.Handled = true;
    }

    void Overlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_mouseDown) return;
        _mouseDown = false;
        Overlay.ReleaseMouseCapture();
        if (!_swiping) return;

        // Videoya tek tıklamak bir şey yapmaz; sadece yana çekme sonraki/önceki videoya geçer
        _swiping = false;
        EndSwipe((e.GetPosition(Overlay) - _downPoint).X);
    }

    /// <summary>Çekme başladı: önceki animasyonu durdur, video alanının boyutunu ölç.</summary>
    void BeginSwipeVisual()
    {
        StopShiftAnimation();
        CacheShiftGeometry();
    }

    void EndSwipe(double dx)
    {
        var toNext = dx < 0;
        var target = toNext ? _playlist.PeekNext() : _playlist.PeekPrevious();
        var success = target != null && Math.Abs(dx) >= SwipeThreshold;
        Action go = toNext ? Next : Previous;

        if (success)
        {
            // Mevcut video çekilen yönde pencereden tamamen kayıp çıkar,
            // sonra sonraki video kaymadan yerinde başlar
            AnimateShift(Math.Sign(dx) * Math.Max(Overlay.ActualWidth, Math.Abs(_shiftDip)),
                TimeSpan.FromMilliseconds(200), new QuadraticEase { EasingMode = EasingMode.EaseIn }, () =>
                {
                    // Open kaydırmayı sıfırlıyor
                    _shiftDip = 0;
                    go();
                });
        }
        else
        {
            // Yeterince çekilmediyse hafifçe yaylanarak yerine döner
            AnimateShift(0, TimeSpan.FromMilliseconds(380), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });
        }
    }

    // Kaydırmada kullanılan video alanı boyutu (piksel); çekme başlarken bir kez ölçülür
    double _shiftViewW, _shiftViewH, _shiftDpi = 1;
    int _shiftAnimId;

    [DllImport("dwmapi.dll")] static extern int DwmFlush();

    void CacheShiftGeometry()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _shiftDpi = dpi.DpiScaleX;
        _shiftViewW = Video.ActualWidth * dpi.DpiScaleX;
        _shiftViewH = Video.ActualHeight * dpi.DpiScaleY;
    }

    void SendShift(double dip) => _player.SetShift(dip * _shiftDpi, _shiftViewW, _shiftViewH);

    /// <summary>
    /// Bırakınca yapılan kaydırma animasyonu (kayıp çıkma, yaylanma). WPF'in kendi döngüsü
    /// saniyede 60 kareyle sınırlı olduğu için ayrı bir iş parçacığında, ekranın her
    /// yenilemesinde (DwmFlush) bir adım ilerletilir; 144 Hz ekranda 144 adım.
    /// </summary>
    void AnimateShift(double to, TimeSpan duration, IEasingFunction ease, Action? completed = null)
    {
        var id = Interlocked.Increment(ref _shiftAnimId);
        var from = _shiftDip;
        CacheShiftGeometry();
        // Yumuşatma eğrisi bir WPF nesnesi; başka iş parçacığından kullanabilmek için dondur
        if (ease is Freezable freezable && freezable.CanFreeze) freezable.Freeze();

        new Thread(() =>
        {
            var clock = Stopwatch.StartNew();
            while (Volatile.Read(ref _shiftAnimId) == id) // yeni bir çekme başlarsa dur
            {
                var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / duration.TotalMilliseconds);
                _shiftDip = from + (to - from) * ease.Ease(t);
                SendShift(_shiftDip);
                if (t >= 1)
                {
                    if (completed != null)
                        Dispatcher.BeginInvoke(() => { if (Volatile.Read(ref _shiftAnimId) == id) completed(); });
                    return;
                }
                if (DwmFlush() != 0) Thread.Sleep(4);
            }
        }) { IsBackground = true, Name = "Kaydırma animasyonu" }.Start();
    }

    void StopShiftAnimation() => Interlocked.Increment(ref _shiftAnimId);

    void Overlay_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsDescendant(Controls, e.OriginalSource)) return;
        ShowMoreMenu(atMouse: true);
        e.Handled = true;
    }

    void Overlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // Farenin yan tuşları: geri = önceki, ileri = sonraki
        if (e.ChangedButton == MouseButton.XButton1) { Previous(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { Next(); e.Handled = true; }
    }

    /// <summary>Taşıma veya boyutlandırmayı Windows'a bırak (yapıştırma, Aero Snap dahil).</summary>
    void StartSystemDrag(int hitTest)
    {
        Overlay.ReleaseMouseCapture();
        ReleaseCapture();
        SendMessage(_hwnd, WM_NCLBUTTONDOWN, (IntPtr)hitTest, IntPtr.Zero);
    }

    // ---------------------------------------------------------------- Klavye

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        if (key == Key.Escape && (VolumePopup.IsOpen || MorePopup.IsOpen))
        {
            VolumePopup.IsOpen = MorePopup.IsOpen = false;
            e.Handled = true;
            return;
        }

        var handled = true;
        switch (key)
        {
            // Kontrollerin durduğu pencere etkinken Alt+F4 o pencereyi değil oynatıcıyı kapatsın
            case Key.F4 when alt: Close(); break;
            case Key.Space or Key.K or Key.MediaPlayPause or Key.Play or Key.Pause: TogglePlay(); break;
            case Key.Left or Key.J: SkipBy(-BackSeconds); break;
            case Key.Right or Key.L: SkipBy(ForwardSeconds); break;
            case Key.Up: ChangeVolume(5); break;
            case Key.Down: ChangeVolume(-5); break;
            case Key.M or Key.VolumeMute:
                SetMuted(!_settings.Muted);
                ShowToast(VolumeIcon(), _settings.Muted ? L.Muted : L.Unmuted);
                break;
            case Key.F or Key.F11: ToggleFullscreen(); break;
            case Key.Enter when alt: ToggleFullscreen(); break;
            case Key.Escape when _fullscreen: ToggleFullscreen(); break;
            case Key.Escape when _mini: ToggleMini(); break;
            case Key.N or Key.PageDown or Key.MediaNextTrack: Next(); break;
            case Key.P or Key.PageUp or Key.MediaPreviousTrack: Previous(); break;
            case Key.O when ctrl: OpenWithDialog(); break;
            case Key.Home: if (_player.IsSeekable) _player.Time = 0; break;
            case Key.OemPeriod or Key.Decimal: StepSpeed(+1); break;
            case Key.OemComma: StepSpeed(-1); break;
            default: handled = false; break;
        }
        if (handled)
        {
            e.Handled = true;
            if (key is not (Key.Space or Key.K or Key.F4)) ShowControls();
        }
    }

    void StepSpeed(int dir)
    {
        var i = Array.IndexOf(Speeds, _rate);
        if (i < 0) i = Array.IndexOf(Speeds, 1.0);
        SetRate(Speeds[Math.Clamp(i + dir, 0, Speeds.Length - 1)]);
    }

    // ---------------------------------------------------------------- Ekranı kaplama, tam ekran, mini görünüm

    void ToggleMaximize()
    {
        if (_fullscreen) return;
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    void ToggleFullscreen()
    {
        if (_mini) ToggleMini();

        // Pencerenin durumunu (normal / ekranı kaplamış) değiştirmeden tek hamlede monitörü
        // kaplatıp geri getiriyoruz. Durum değiştirmek pencereyi önce küçültüp sonra
        // büyüttüğü için geçiş takılıyordu.
        if (!_fullscreen)
        {
            GetWindowRect(_hwnd, out _rectBeforeFullscreen);
            _fullscreen = true;
            var monitor = CurrentMonitor().Monitor;
            MoveWindowTo(monitor);
            // Başlık stili olan pencereyi Windows görev çubuğunun altında tuttuğu için
            // tam ekranda stili kaldır (kenarlıksız olduğumuzdan görünüşte fark yok)
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            MoveWindowTo(monitor);
        }
        else
        {
            _fullscreen = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            MoveWindowTo(_rectBeforeFullscreen);
        }
        FullButton.Content = _fullscreen ? IconExitFull : IconFull;
        FullButton.ToolTip = _fullscreen ? L.ExitFullscreenTip : L.FullscreenTip;
        UpdateWindowButtons();
        ShowControls();
    }

    void ToggleMini()
    {
        if (_fullscreen) ToggleFullscreen();

        if (!_mini)
        {
            _mini = true;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            _boundsBeforeMini = new Rect(Left, Top, Width, Height);
            var area = SystemParameters.WorkArea;
            Width = 520;
            Height = 300;
            Left = area.Right - Width - 16;
            Top = area.Bottom - Height - 16;
            Topmost = true;
        }
        else
        {
            _mini = false;
            Topmost = false;
            Left = _boundsBeforeMini.Left;
            Top = _boundsBeforeMini.Top;
            Width = _boundsBeforeMini.Width;
            Height = _boundsBeforeMini.Height;
        }
        // Mini görünümde sadece temel düğmeler kalsın
        var extra = _mini ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = ForwardButton.Visibility = extra;
        FullButton.Visibility = MoreButton.Visibility = VolumeButton.Visibility = extra;
        TitleText.Visibility = extra;
        MiniButton.Content = _mini ? "\uE73F" : "\uE8A7";
        MiniButton.ToolTip = _mini ? L.ExitMiniTip : L.MiniTip;
        UpdateWindowButtons();
        ShowControls();
    }

    // ---------------------------------------------------------------- Diğer seçenekler menüsü

    void BuildMoreMenu()
    {
        foreach (var s in Speeds)
        {
            var b = new Button
            {
                Style = (Style)FindResource("MenuButton"),
                MinWidth = 0,
                Width = 52,
                Height = 34,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                Content = $"{s:0.##}x",
                Tag = s,
                Margin = new Thickness(2),
            };
            b.Click += (_, _) => SetRate((double)b.Tag);
            SpeedPanel.Children.Add(b);
        }
        UpdateSpeedButtons();
        UpdateEndActionButtons();
        UpdateInterpolationButton();
    }

    void UpdateInterpolationButton() =>
        SetCheckItem(InterpolationButton, L.Interpolation, _settings.Interpolation);

    void Interpolation_Click(object sender, RoutedEventArgs e)
    {
        _settings.Interpolation = !_settings.Interpolation;
        _player.Interpolation = _settings.Interpolation;
        _settings.Save();
        UpdateInterpolationButton();
        ShowToast("\uE916", _settings.Interpolation ? L.InterpolationOn : L.InterpolationOff);
    }

    void UpdateSpeedButtons()
    {
        var normal = ((Style)FindResource("MenuButton")).Setters.OfType<Setter>()
            .First(x => x.Property == TemplateProperty).Value as ControlTemplate;
        foreach (Button b in SpeedPanel.Children)
            b.Template = (double)b.Tag == _rate ? SelectedMenuTemplate() : normal;
    }

    ControlTemplate? _selectedTemplate;
    ControlTemplate SelectedMenuTemplate()
    {
        if (_selectedTemplate != null) return _selectedTemplate;
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        return _selectedTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    void UpdateEndActionButtons()
    {
        SetCheckItem(EndStop, L.EndStop, _settings.EndAction == EndAction.Stop);
        SetCheckItem(EndNext, L.EndNext, _settings.EndAction == EndAction.Next);
        SetCheckItem(EndRepeat, L.EndRepeat, _settings.EndAction == EndAction.Repeat);
    }

    static void SetCheckItem(Button button, string text, bool isChecked)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = isChecked ? IconCheck : "",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            Width = 28,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(new TextBlock { Text = text });
        button.Content = panel;
    }

    void ShowMoreMenu(bool atMouse)
    {
        VolumePopup.IsOpen = false;
        if (atMouse)
        {
            MorePopup.Placement = PlacementMode.MousePoint;
        }
        else
        {
            // Menünün sağ kenarı düğmenin sağ kenarıyla hizalı, düğmenin üstünde
            MorePopup.Placement = PlacementMode.Custom;
            MorePopup.CustomPopupPlacementCallback = (popup, target, _) => new[]
            {
                new CustomPopupPlacement(new Point(target.Width - popup.Width, -popup.Height - 6), PopupPrimaryAxis.Horizontal),
            };
        }
        MorePopup.IsOpen = true;
    }

    void EndAction_Click(object sender, RoutedEventArgs e)
    {
        _settings.EndAction = Enum.Parse<EndAction>((string)((Button)sender).Tag);
        UpdateEndActionButtons();
    }

    void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        OpenWithDialog();
    }

    void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        if (_playlist.Current is { } path)
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    void MakeDefault_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        try
        {
            FileAssociation.Register();
            FileAssociation.OpenDefaultAppsSettings();
            MessageBox.Show(this, L.DefaultPlayerHelp, L.DefaultPlayerTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.RegistrationFailed(ex.Message), L.DefaultPlayerTitle,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------------------------------------------------------- Düğmeler

    void OpenButton_Click(object sender, RoutedEventArgs e) => OpenWithDialog();
    void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlay();
    void BackButton_Click(object sender, RoutedEventArgs e) => SkipBy(-BackSeconds);
    void ForwardButton_Click(object sender, RoutedEventArgs e) => SkipBy(ForwardSeconds);
    void PrevButton_Click(object sender, RoutedEventArgs e) => Previous();
    void NextButton_Click(object sender, RoutedEventArgs e) => Next();
    void FullButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();
    void MiniButton_Click(object sender, RoutedEventArgs e) => ToggleMini();
    void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (MorePopup.IsOpen) { MorePopup.IsOpen = false; return; }
        if (JustClosed(MorePopup)) return;
        ShowMoreMenu(atMouse: false);
    }
    void MuteButton_Click(object sender, RoutedEventArgs e) => SetMuted(!_settings.Muted);
    void MinButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void MaxButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    void VolumeButton_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        if (JustClosed(VolumePopup)) return;
        VolumePopup.IsOpen = !VolumePopup.IsOpen;
    }

    /// <summary>
    /// Menü açıkken düğmesine basınca menü fare basılır basılmaz kendiliğinden kapanıyor,
    /// ardından gelen tıklama onu yeniden açıyordu. Az önce kapandıysa tekrar açma.
    /// </summary>
    bool JustClosed(Popup popup) =>
        _lastClosedPopup == popup && Environment.TickCount64 - _popupClosedTick < 400;

    void LengthText_Click(object sender, MouseButtonEventArgs e)
    {
        _showRemaining = !_showRemaining;
        UpdateTime();
    }

    // ---------------------------------------------------------------- Pencere

    const int WM_GETMINMAXINFO = 0x0024, WM_WINDOWPOSCHANGING = 0x0046, WM_NCLBUTTONDOWN = 0x00A1;
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010,
        SWP_FRAMECHANGED = 0x0020, SWP_NOOWNERZORDER = 0x0200;
    const int HTCAPTION = 2, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
        HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPOS { public IntPtr Hwnd, InsertAfter; public int X, Y, Cx, Cy; public uint Flags; }

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
    }

    /// <summary>
    /// Kenarlıksız pencerede "ekranı kapla" görev çubuğunun üstüne taşmasın;
    /// tam ekranda ise görev çubuğu dahil tüm monitörü kaplasın.
    /// </summary>
    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                var area = _fullscreen ? info.Monitor : info.Work;
                mmi.MaxPosition.X = area.Left - info.Monitor.Left;
                mmi.MaxPosition.Y = area.Top - info.Monitor.Top;
                mmi.MaxSize.X = area.Right - area.Left;
                mmi.MaxSize.Y = area.Bottom - area.Top;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
        }
        else if (msg == WM_WINDOWPOSCHANGING)
        {
            SyncOverlay(lParam);
        }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// <summary>
    /// Windows ekranı kaplayan pencereyi kenarlık payı kadar ekran dışına taşırır. Kenarlıksız
    /// pencerede bu, içeriğin kenarlarının kesilmesi demek; taşan kısım kadar içeride boşluk bırak.
    /// </summary>
    void FitToScreenEdges()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (WindowState != WindowState.Maximized)
        {
            Video.Margin = new Thickness(0);
            return;
        }
        var monitor = MonitorFromWindow(_hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(_hwnd, out var r)) return;
        var area = _fullscreen ? info.Monitor : info.Work;
        var dpi = VisualTreeHelper.GetDpi(this);
        Video.Margin = new Thickness(
            Math.Max(0, area.Left - r.Left) / dpi.DpiScaleX,
            Math.Max(0, area.Top - r.Top) / dpi.DpiScaleY,
            Math.Max(0, r.Right - area.Right) / dpi.DpiScaleX,
            Math.Max(0, r.Bottom - area.Bottom) / dpi.DpiScaleY);
    }

    /// <summary>
    /// Kontroller, videonun üstündeki ayrı bir saydam pencerede durur.
    /// Klavye kısayolları o pencere etkinken de çalışsın.
    /// </summary>
    void PrepareOverlayWindow()
    {
        _overlayWindow.Owner = this;
        _overlayWindow.PreviewKeyDown += OnKeyDown;
        _overlayWindow.Show();
        _overlayHwnd = new WindowInteropHelper(_overlayWindow).Handle;
        PositionOverlay();
    }

    /// <summary>Katman penceresini video alanının tam üstüne yerleştir.</summary>
    void PositionOverlay()
    {
        if (_overlayHwnd == IntPtr.Zero || PresentationSource.FromVisual(Video) == null) return;
        var topLeft = Video.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(this);
        SetWindowPos(_overlayHwnd, IntPtr.Zero, (int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
            (int)Math.Round(Video.ActualWidth * dpi.DpiScaleX), (int)Math.Round(Video.ActualHeight * dpi.DpiScaleY),
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    MONITORINFO CurrentMonitor()
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(_hwnd, 2 /* MONITOR_DEFAULTTONEAREST */), ref info);
        return info;
    }

    void MoveWindowTo(RECT r) =>
        SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

    /// <summary>
    /// Pencere taşınırken veya boyutlandırılırken kontrol katmanını aynı anda taşı.
    /// Normalde katman, pencere yerleştikten sonra WPF'in düzen turunda takip ediyor ve
    /// bir adım geride kalıyor; bu da boyutlandırmayı takıntılı gösteriyordu.
    /// </summary>
    void SyncOverlay(IntPtr windowPos)
    {
        if (_overlayHwnd == IntPtr.Zero) return;
        var wp = Marshal.PtrToStructure<WINDOWPOS>(windowPos);
        if ((wp.Flags & SWP_NOMOVE) != 0 && (wp.Flags & SWP_NOSIZE) != 0) return;

        GetWindowRect(_hwnd, out var cur);
        int x = (wp.Flags & SWP_NOMOVE) != 0 ? cur.Left : wp.X;
        int y = (wp.Flags & SWP_NOMOVE) != 0 ? cur.Top : wp.Y;
        int w = (wp.Flags & SWP_NOSIZE) != 0 ? cur.Right - cur.Left : wp.Cx;
        int h = (wp.Flags & SWP_NOSIZE) != 0 ? cur.Bottom - cur.Top : wp.Cy;

        var dpi = VisualTreeHelper.GetDpi(this);
        var m = Video.Margin;
        int l = (int)Math.Round(m.Left * dpi.DpiScaleX), t = (int)Math.Round(m.Top * dpi.DpiScaleY);
        int r = (int)Math.Round(m.Right * dpi.DpiScaleX), b = (int)Math.Round(m.Bottom * dpi.DpiScaleY);
        SetWindowPos(_overlayHwnd, IntPtr.Zero, x + l, y + t, Math.Max(0, w - l - r), Math.Max(0, h - t - b),
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    void RestoreSavedBounds()
    {
        Width = _settings.Width;
        Height = _settings.Height;
        var r = new Rect(_settings.Left, _settings.Top, _settings.Width, _settings.Height);
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!double.IsNaN(r.Left) && screen.Contains(new Point(r.Left + 60, r.Top + 20)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = r.Left;
            Top = r.Top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (_settings.Maximized) WindowState = WindowState.Maximized;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Tam ekranda kapatılırsa tam ekrandan önceki boyut kaydedilsin
        if (_fullscreen) ToggleFullscreen();

        Rect bounds = _mini ? _boundsBeforeMini
            : WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            _settings.Left = bounds.Left;
            _settings.Top = bounds.Top;
            _settings.Width = bounds.Width;
            _settings.Height = bounds.Height;
        }
        _settings.Maximized = WindowState == WindowState.Maximized;
        _settings.Save();

        _tick.Stop();
        _hideTimer.Stop();
        // Durdurma bazen arayüzü kilitleyebiliyor; arka planda yap, en fazla 1 sn bekle
        Task.Run(() => { _player.Stop(); _player.Dispose(); }).Wait(1500);
        base.OnClosing(e);
    }
}
