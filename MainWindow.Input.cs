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
        if ((pos - _lastMouse).Length > 2) { ShowControls(); ShowTitle(); }
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
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (key == Key.Escape && AllPopups.Any(p => p.IsOpen))
        {
            CloseAllPopups();
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
            // YouTube gibi: Shift + . hızlandırır, Shift + , yavaşlatır;
            // tek başına . ve , duraklatılmışken bir kare ileri / geri gider
            case Key.OemPeriod when shift: StepSpeed(+1); break;
            case Key.OemComma when shift: StepSpeed(-1); break;
            case Key.OemPeriod or Key.Decimal: FrameStep(+1); break;
            case Key.OemComma: FrameStep(-1); break;
            // YouTube gibi: C altyazıyı açıp kapatır, 0-9 videonun %0-90'ına atlar
            case Key.C when !ctrl: ToggleSubtitles(); break;
            case >= Key.D0 and <= Key.D9 when !ctrl && !alt && !shift: JumpToPercent((key - Key.D0) * 10); break;
            case >= Key.NumPad0 and <= Key.NumPad9: JumpToPercent((key - Key.NumPad0) * 10); break;
            default: handled = false; break;
        }
        if (handled)
        {
            e.Handled = true;
            if (key is not (Key.Space or Key.K or Key.F4)) ShowControls();
        }
    }

    void JumpToPercent(int percent)
    {
        if (!_player.IsSeekable) return;
        _player.Time = _player.Length * percent / 100;
        UpdateTime();
    }

    void FrameStep(int dir)
    {
        if (_player.IsPlaying || IsFinished) return;
        _player.FrameStep(dir);
    }

    void StepSpeed(int dir)
    {
        var i = Array.IndexOf(Speeds, _rate);
        if (i < 0) i = Array.IndexOf(Speeds, 1.0);
        SetRate(Speeds[Math.Clamp(i + dir, 0, Speeds.Length - 1)]);
    }
}
