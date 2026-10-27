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
    // YouTube'daki hız adımları
    static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2 };

    readonly Settings _settings = Settings.Load();
    readonly FolderPlaylist _playlist = new();
    readonly MpvEngine _player;
    readonly Window _overlayWindow;
    readonly SliderDrag _seekDrag, _volumeDrag;

    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    // Filmler ve TV gibi sağda varsayılan olarak kalan süre
    bool _showRemaining = true, _controlsVisible = true;
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
            // Başka bir video açılana kadar ekranda kalsın
            ShowToast("\uE783", L.CannotPlay, sticky: true);
            UpdatePlayState();
        });

        // Kontrolleri videonun üstündeki saydam pencereye taşı
        Root.Children.Remove(Overlay);
        _overlayWindow = new Window
        {
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

        _tick.Tick += (_, _) => { UpdateTime(); WatchQuality(); };
        _hideTimer.Tick += (_, _) => TryHideControls();
        _titleTimer.Tick += (_, _) => HideTitle();
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Fade(Toast, 0, 250); };

        // Kontroller videonun üstündeki ayrı pencereye taşındığı için XAML'daki
        // ElementName bağlamaları çalışmıyor; hedefleri burada ver
        VolumePopup.PlacementTarget = VolumeButton;
        SpeedPopup.PlacementTarget = SpeedButton;
        MorePopup.PlacementTarget = MoreButton;
        TracksPopup.PlacementTarget = TracksButton;
        // Menüyü kapatmak için yapılan tık başka bir şeyi tetiklemesin
        foreach (var popup in AllPopups)
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
        // Zaman balonu Filmler ve TV gibi sadece sürüklerken görünür

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
        Overlay.MouseMove += (_, e) => UpdateRevealLight(e);
        Overlay.MouseLeave += (_, _) => UpdateRevealLight(null);
        SetupRevealLight();
        Overlay.PreviewMouseLeftButtonDown += Overlay_PreviewMouseLeftButtonDown;
        Overlay.MouseLeftButtonDown += Overlay_MouseLeftButtonDown;
        Overlay.MouseLeftButtonUp += Overlay_MouseLeftButtonUp;
        Overlay.MouseRightButtonUp += Overlay_MouseRightButtonUp;
        Overlay.MouseDown += Overlay_MouseDown;
        Overlay.MouseWheel += (_, e) => ChangeVolume(e.Delta > 0 ? 5 : -5);
        Overlay.MouseLeave += (_, _) => { if (!_mouseDown) RestartHideTimer(); };
        PreviewKeyDown += OnKeyDown;
        StateChanged += (_, _) => { UpdateWindowButtons(); FitToScreenEdges(); };
        SizeChanged += (_, _) => { FitToScreenEdges(); _lastResizeTick = Environment.TickCount64; };
        Overlay.SizeChanged += (_, _) => UpdateSubtitleLift(0);
        _subLiftTimer.Tick += (_, _) => StepSubtitleLift();
        // Windows'un altyazı ayarları değişmiş olabilir (ör. menüden Ayarlar'a gidip dönünce)
        Activated += (_, _) => ApplySubtitleStyle();

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
        HideToast();

        Title = Path.GetFileName(path);
        HideTitle(instant: true);
        BigTitle.Text = Path.GetFileNameWithoutExtension(path);
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
            var path = files[0];
            // Klasör bırakılırsa içindeki ilk video; altyazı dosyası bırakılırsa açık videoya eklenir
            if (Directory.Exists(path)) path = FolderPlaylist.FirstVideoIn(path) ?? "";
            if (FolderPlaylist.IsSubtitle(path))
            {
                if (_playlist.Current != null) AddSubtitle(path);
            }
            else OpenFile(path);
            Activate();
        }
        e.Handled = true;
    }
}
