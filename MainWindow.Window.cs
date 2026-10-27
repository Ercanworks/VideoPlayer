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
    // ---------------------------------------------------------------- Maximize, fullscreen, mini view

    void ToggleMaximize()
    {
        if (_fullscreen) return;
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    void ToggleFullscreen()
    {
        if (_mini) ToggleMini();

        // We cover the monitor and come back in one go without changing the window state
        // (normal / maximized). Changing the state first shrinks and then enlarges the window,
        // which made the transition stutter.
        if (!_fullscreen)
        {
            GetWindowRect(_hwnd, out _rectBeforeFullscreen);
            _fullscreen = true;
            var monitor = CurrentMonitor().Monitor;
            MoveWindowTo(monitor);
            // Windows keeps a window with a caption style below the taskbar, so
            // remove the style in fullscreen (no visible difference since we are borderless)
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
        ApplyControlLayout();
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
        // Keep only the basic buttons in mini view
        var extra = _mini ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = ForwardButton.Visibility = extra;
        FullButton.Visibility = MoreButton.Visibility = VolumeButton.Visibility = SpeedButton.Visibility = extra;
        TracksButton.Visibility = extra;
        if (_mini) HideTitle(instant: true);
        EdgeArrows.Visibility = extra;
        MiniButton.Content = _mini ? "\uE73F" : "\uE8A7";
        MiniButton.ToolTip = _mini ? L.ExitMiniTip : L.MiniTip;
        UpdateWindowButtons();
        ShowControls();
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

        // Media keys and the Windows media flyout; the buttons arrive on a Windows thread
        _media = new SystemMedia(_hwnd);
        _media.Play += () => Dispatcher.BeginInvoke(() => { if (!_player.IsPlaying) TogglePlay(); });
        _media.Pause += () => Dispatcher.BeginInvoke(() => { if (_player.IsPlaying) TogglePlay(); });
        _media.NextVideo += () => Dispatcher.BeginInvoke(Next);
        _media.PreviousVideo += () => Dispatcher.BeginInvoke(Previous);
        UpdateMediaControls();
    }

    SystemMedia? _media;

    /// <summary>
    /// In a borderless window, "maximize" must not overlap the taskbar;
    /// in fullscreen it covers the whole monitor, taskbar included.
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
    /// Windows pushes a maximized window off screen by the border width. For a borderless
    /// window that means the content's edges get cut off; leave padding equal to the overflow.
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
    /// The controls live in a separate transparent window on top of the video.
    /// Keyboard shortcuts should work while that window is active too.
    /// </summary>
    void PrepareOverlayWindow()
    {
        _overlayWindow.Owner = this;
        _overlayWindow.PreviewKeyDown += OnKeyDown;
        // Don't list it as a separate window in screen sharing (Discord, OBS etc.): those lists
        // don't show captionless tool windows (the window is already absent from the taskbar and Alt+Tab)
        _overlayHwnd = new WindowInteropHelper(_overlayWindow).EnsureHandle();
        SetWindowLong(_overlayHwnd, GWL_EXSTYLE, GetWindowLong(_overlayHwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        _overlayWindow.Show();
        PositionOverlay();
    }

    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    /// <summary>Place the overlay window exactly over the video area.</summary>
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
    /// Move the control overlay at the same time while the window is being moved or resized.
    /// Normally the overlay follows in WPF's layout pass after the window has settled and
    /// lags one step behind, which made resizing look jerky.
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
        // If closed in fullscreen, save the size from before fullscreen
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
        // Stopping can sometimes freeze the UI; do it in the background and wait at most 1 s
        Task.Run(() => { _player.Stop(); _player.Dispose(); }).Wait(1500);
        base.OnClosing(e);
    }
}
