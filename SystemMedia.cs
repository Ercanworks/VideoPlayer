using System.Diagnostics;
using Windows.Media;
using Windows.Media.ClosedCaptioning;
using Oynatici.Engine;

namespace Oynatici;

/// <summary>
/// Windows'un medya denetimi (Filmler ve TV gibi): klavyedeki ve kulaklıktaki medya tuşları
/// pencere seçili değilken de çalışır, ses düğmelerine basınca çıkan Windows medya
/// penceresinde video adı ve oynat/duraklat görünür.
/// </summary>
public sealed class SystemMedia
{
    readonly SystemMediaTransportControls? _smtc;

    /// <summary>Tuşlar Windows'un iş parçacığından gelir; dinleyen arayüze aktarmalı.</summary>
    public event Action? Play, Pause, NextVideo, PreviousVideo;

    public SystemMedia(IntPtr hwnd)
    {
        try
        {
            _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsStopEnabled = false;
            _smtc.ButtonPressed += (_, e) =>
            {
                switch (e.Button)
                {
                    case SystemMediaTransportControlsButton.Play: Play?.Invoke(); break;
                    case SystemMediaTransportControlsButton.Pause: Pause?.Invoke(); break;
                    case SystemMediaTransportControlsButton.Next: NextVideo?.Invoke(); break;
                    case SystemMediaTransportControlsButton.Previous: PreviousVideo?.Invoke(); break;
                }
            };
            _smtc.IsEnabled = false;
        }
        catch (Exception ex)
        {
            // Medya denetimi olmadan da oynatıcı çalışır (tuşlar pencere seçiliyken çalışmaya devam eder)
            Debug.WriteLine(ex);
            _smtc = null;
        }
    }

    /// <summary>Oynatma durumunu ve video adını Windows'a bildirir; title null ise kapalı.</summary>
    public void Update(string? title, bool playing, bool hasPrevious, bool hasNext)
    {
        if (_smtc == null) return;
        try
        {
            if (title == null)
            {
                _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                _smtc.IsEnabled = false;
                return;
            }
            _smtc.IsEnabled = true;
            _smtc.IsNextEnabled = hasNext;
            _smtc.IsPreviousEnabled = hasPrevious;
            _smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            if (title != _title)
            {
                _title = title;
                var display = _smtc.DisplayUpdater;
                display.Type = MediaPlaybackType.Video;
                display.VideoProperties.Title = title;
                display.Update();
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    string? _title;

    // ---------------------------------------------------------------- Windows altyazı ayarları

    /// <summary>
    /// Ayarlar > Erişim Kolaylığı > Altyazılar'daki seçimler (Filmler ve TV de bunları kullanıyor).
    /// "Varsayılan" bırakılanlar mpv'nin kendi görünümünde kalır.
    /// </summary>
    public static MpvEngine.SubtitleStyle ReadSubtitleStyle()
    {
        try
        {
            double scale = ClosedCaptionProperties.FontSize switch
            {
                ClosedCaptionSize.FiftyPercent => 0.5,
                ClosedCaptionSize.OneHundredFiftyPercent => 1.5,
                ClosedCaptionSize.TwoHundredPercent => 2,
                _ => 1,
            };
            string? font = ClosedCaptionProperties.FontStyle switch
            {
                ClosedCaptionStyle.MonospacedWithSerifs => "Courier New",
                ClosedCaptionStyle.ProportionalWithSerifs => "Times New Roman",
                ClosedCaptionStyle.MonospacedWithoutSerifs => "Consolas",
                ClosedCaptionStyle.ProportionalWithoutSerifs => "Arial",
                ClosedCaptionStyle.Casual => "Comic Sans MS",
                ClosedCaptionStyle.Cursive => "Segoe Script",
                ClosedCaptionStyle.SmallCapitals => "Arial",
                _ => null,
            };

            string? color = null;
            if (ClosedCaptionProperties.FontColor != ClosedCaptionColor.Default
                || ClosedCaptionProperties.FontOpacity != ClosedCaptionOpacity.Default)
                color = Argb(ClosedCaptionProperties.ComputedFontColor, ClosedCaptionProperties.FontOpacity);

            string? back = null;
            if ((ClosedCaptionProperties.BackgroundColor != ClosedCaptionColor.Default
                 || ClosedCaptionProperties.BackgroundOpacity != ClosedCaptionOpacity.Default)
                && ClosedCaptionProperties.BackgroundOpacity != ClosedCaptionOpacity.ZeroPercent)
                back = Argb(ClosedCaptionProperties.ComputedBackgroundColor, ClosedCaptionProperties.BackgroundOpacity);

            (double? outline, double? shadow) = ClosedCaptionProperties.FontEffect switch
            {
                ClosedCaptionEdgeEffect.None => (0, 0),
                ClosedCaptionEdgeEffect.Uniform => (2.5, 0),
                ClosedCaptionEdgeEffect.DropShadow => (0, 2),
                ClosedCaptionEdgeEffect.Raised => (0.8, 1.5),
                ClosedCaptionEdgeEffect.Depressed => (1.2, 0),
                _ => ((double?)null, (double?)null),
            };
            return new MpvEngine.SubtitleStyle(scale, font, color, back, outline, shadow);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            return new MpvEngine.SubtitleStyle(1, null, null, null, null, null);
        }
    }

    static string Argb(Windows.UI.Color c, ClosedCaptionOpacity opacity)
    {
        byte a = opacity switch
        {
            ClosedCaptionOpacity.SeventyFivePercent => 191,
            ClosedCaptionOpacity.TwentyFivePercent => 64,
            ClosedCaptionOpacity.ZeroPercent => 0,
            _ => 255,
        };
        return $"#{a:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    public static void OpenSubtitleSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:easeofaccess-closedcaptioning") { UseShellExecute = true });
}
