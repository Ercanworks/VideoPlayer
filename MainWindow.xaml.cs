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
    // Segoe MDL2 Assets icons
    const string IconPlay = "\uE768", IconPause = "\uE769", IconMute = "\uE74F";
    const string IconFull = "\uE740", IconExitFull = "\uE73F", IconCheck = "\uE73E";
    const string IconBack = "\uED3C", IconForward = "\uED3D", IconPrev = "\uE892", IconNext = "\uE893";
    const string IconMaximize = "\uE922", IconRestore = "\uE923";

    const int BackSeconds = 10, ForwardSeconds = 30;
    const double ResizeEdge = 6;
    // YouTube's speed steps
    static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 1.75, 2 };

    readonly Settings _settings = Settings.Load();
    readonly FolderPlaylist _playlist = new();
    readonly MpvEngine _player;
    readonly Window _overlayWindow;
    readonly SliderDrag _seekDrag, _volumeDrag;

    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

    // Like Movies & TV, the right side shows the remaining time by default
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

    // Press-and-drag and double click on the video
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

        // Engine events come from its own thread; touching the player from there
        // can cause deadlocks, so always marshal to the UI thread
        _player.Playing += () => Dispatcher.BeginInvoke(OnPlaying);
        _player.Paused += () => Dispatcher.BeginInvoke(UpdatePlayState);
        _player.Stopped += () => Dispatcher.BeginInvoke(UpdatePlayState);
        _player.EndReached += () => Dispatcher.BeginInvoke(OnEnded);
        _player.Error += () => Dispatcher.BeginInvoke(() =>
        {
            // Stay on screen until another video is opened
            ShowToast("\uE783", L.CannotPlay, sticky: true);
            UpdatePlayState();
        });

        // Move the controls to the transparent window on top of the video
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

        // Since the controls are moved to a separate window on top of the video, ElementName
        // bindings in XAML don't work; set the targets here
        VolumePopup.PlacementTarget = VolumeButton;
        SpeedPopup.PlacementTarget = SpeedButton;
        MorePopup.PlacementTarget = MoreButton;
        TracksPopup.PlacementTarget = TracksButton;
        // A click made to close a menu must not trigger anything else
        foreach (var popup in AllPopups)
            // The Closed event fires after the closing animation; catch the moment of closing right away
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
        // Like Movies & TV, the time bubble only shows while dragging

        _volumeDrag = new SliderDrag(VolumeSlider);
        // Dragging took the mouse from the popup; give it back so clicking outside closes the popup
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
        // Windows' subtitle settings may have changed (e.g. after going to Settings from the menu and back)
        Activated += (_, _) => ApplySubtitleStyle();

        _tick.Start();
    }

    // ---------------------------------------------------------------- Opening files

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
            // If a folder is dropped, its first video; if a subtitle file is dropped, it is added to the open video
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
