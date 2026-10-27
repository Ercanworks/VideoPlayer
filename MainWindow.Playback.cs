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
    // ---------------------------------------------------------------- Oynatma

    void OnPlaying()
    {
        _player.Volume = (int)VolumeSlider.Value;
        _player.Mute = _settings.Muted;
        if (Math.Abs(_player.Rate - _rate) > 0.001) _player.Rate = _rate;
        if (_pendingSeek > 0) _player.Time = _pendingSeek;
        _pendingSeek = -1;
        ApplySubtitleStyle();
        UpdateSubtitleLift(0);
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
        // Sonda kal: çubuk videonun bittiği yerde dursun (dosya yarım kalmışsa yarıda),
        // oynat düğmesi baştan başlatsın
        long length = _player.Length, time = _player.Time;
        Seek.Value = time >= length - 1000 ? Seek.Maximum : Math.Max(0, time);
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
        // Zaman balonu basınca değil, sürüklemeye başlayınca görünür (Filmler ve TV'de basılı
        // tutmak balon göstermiyor)
        if (_seekDrag.HasMoved) ShowSeekTip(value);
        // Sürüklerken videoyu da takip ettir ama her harekette değil; motoru saniyede
        // onlarca kez sardırmak görüntüyü takıltır. Atlanan son konum, fare durunca
        // zamanlayıcıyla sarılır ki görüntü tam fareyle aynı yerde kalsın.
        _scrubTarget = (long)value;
        if (!CanScrub) return;
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
    /// <summary>
    /// Video bitmiş olsa da sarılabilir: mpv dosyayı kapatmıyor, son karede bekliyor (keep-open).
    /// Sadece dosya yokken veya açılamadıysa sarılamaz.
    /// </summary>
    bool CanScrub => _player.State is not (PlayerState.Idle or PlayerState.Error) && _player.IsSeekable;

    void OnSeekDragStarted()
    {
        // Bitmiş videoda çubuğu sürükleyip bırakınca o noktadan oynamaya devam etsin
        _resumeAfterScrub = _player.IsPlaying || _player.State == PlayerState.Ended;
        if (_player.IsPlaying) _player.SetPause(true);
    }

    void OnSeekDragCompleted(double value)
    {
        SeekTip.Visibility = Visibility.Collapsed;
        _scrubTimer.Stop();
        var resume = _resumeAfterScrub;
        _resumeAfterScrub = false;
        if (!CanScrub) { if (_playlist.Current != null) PlayCurrent((long)value); return; }
        _player.Time = (long)value;
        if (resume) _player.SetPause(false);
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
}
