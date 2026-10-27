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
    // ---------------------------------------------------------------- Diğer seçenekler menüsü

    void BuildMoreMenu()
    {
        foreach (var speed in Speeds)
        {
            var b = new Button { Style = (Style)FindResource("MenuButton"), MinWidth = 150, Tag = speed };
            b.Click += (_, _) => { SetRate((double)b.Tag); SpeedPopup.IsOpen = false; };
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
        ShowToast("", _settings.Interpolation ? L.InterpolationOn : L.InterpolationOff);
    }

    void UpdateSpeedButtons()
    {
        foreach (Button b in SpeedPanel.Children)
        {
            var speed = (double)b.Tag;
            SetCheckItem(b, speed == 1 ? $"{speed:0.##}x (normal)" : $"{speed:0.##}x", speed == _rate);
        }
        SpeedButton.Content = $"{_rate:0.##}x";
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
        VolumePopup.IsOpen = TracksPopup.IsOpen = SpeedPopup.IsOpen = false;
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
        SaveSettingsSoon();
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

    void SpeedButton_Click(object sender, RoutedEventArgs e)
    {
        VolumePopup.IsOpen = MorePopup.IsOpen = TracksPopup.IsOpen = false;
        if (SpeedPopup.IsOpen) { SpeedPopup.IsOpen = false; return; }
        if (JustClosed(SpeedPopup)) return;
        // Menünün sağ kenarı düğmenin sağ kenarıyla hizalı, düğmenin üstünde
        SpeedPopup.Placement = PlacementMode.Custom;
        SpeedPopup.CustomPopupPlacementCallback = (popup, target, _) => new[]
        {
            new CustomPopupPlacement(new Point(target.Width - popup.Width, -popup.Height - 6), PopupPrimaryAxis.Horizontal),
        };
        SpeedPopup.IsOpen = true;
    }

    void VolumeButton_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = SpeedPopup.IsOpen = TracksPopup.IsOpen = false;
        if (JustClosed(VolumePopup)) return;
        VolumePopup.IsOpen = !VolumePopup.IsOpen;
    }

    // ---------------------------------------------------------------- Altyazı ve ses menüsü

    void TracksButton_Click(object sender, RoutedEventArgs e)
    {
        VolumePopup.IsOpen = MorePopup.IsOpen = SpeedPopup.IsOpen = false;
        if (TracksPopup.IsOpen) { TracksPopup.IsOpen = false; return; }
        if (JustClosed(TracksPopup)) return;
        BuildTracksMenu();
        // Filmler ve TV gibi düğmenin üstünde ortalı
        TracksPopup.Placement = PlacementMode.Custom;
        TracksPopup.CustomPopupPlacementCallback = (popup, target, _) => new[]
        {
            new CustomPopupPlacement(new Point((target.Width - popup.Width) / 2, -popup.Height - 6), PopupPrimaryAxis.Horizontal),
        };
        TracksPopup.IsOpen = true;
    }

    /// <summary>
    /// Filmler ve TV'deki menü: "Altyazılar" (kapalı, izler, dosyadan altyazı, Windows altyazı
    /// ayarları) ve "Ses" (ses izleri). Seçili olanın yanında onay işareti.
    /// </summary>
    void BuildTracksMenu()
    {
        TracksPanel.Children.Clear();
        var tracks = _playlist.Current != null ? _player.Tracks : new();
        var subs = tracks.Where(t => t.Type == "sub").ToList();
        var audio = tracks.Where(t => t.Type == "audio").ToList();

        AddMenuHeader(L.Subtitles);
        AddMenuItem(L.Off, !subs.Any(t => t.Selected), () => SelectSubtitle(null));
        for (int i = 0; i < subs.Count; i++)
        {
            var track = subs[i];
            AddMenuItem($"{i + 1}. {TrackName(track)}", track.Selected, () => SelectSubtitle(track));
        }
        AddMenuItem(L.ChooseSubtitleFile, false, ChooseSubtitleFile).IsEnabled = _playlist.Current != null;
        AddMenuItem(L.SubtitleSettings, false, () => { TracksPopup.IsOpen = false; SystemMedia.OpenSubtitleSettings(); });

        if (audio.Count == 0) return;
        AddMenuHeader(L.Audio);
        for (int i = 0; i < audio.Count; i++)
        {
            var track = audio[i];
            var parts = new[] { TrackName(track), CodecName(track.Codec), track.Channels > 0 ? L.Channels(track.Channels) : null };
            AddMenuItem($"{i + 1}. {string.Join(" - ", parts.Where(p => !string.IsNullOrEmpty(p)))}", track.Selected,
                () => SelectAudio(track));
        }
    }

    void AddMenuHeader(string text) =>
        TracksPanel.Children.Add(new TextBlock { Text = text, FontSize = 12, Opacity = 0.6, Margin = new Thickness(12, 8, 12, 4) });

    Button AddMenuItem(string text, bool isChecked, Action click)
    {
        // Filmler ve TV'deki bu menüde satırlar 30 px
        var button = new Button { Style = (Style)FindResource("MenuButton"), Height = 30 };
        SetCheckItem(button, text, isChecked);
        button.Click += (_, _) => click();
        TracksPanel.Children.Add(button);
        return button;
    }

    /// <summary>"Türkçe", "Yönetmen yorumu - İngilizce", dosyadan yüklendiyse dosya adı.</summary>
    static string TrackName(MpvEngine.Track t)
    {
        var lang = L.LanguageName(t.Language);
        var title = string.IsNullOrWhiteSpace(t.Title) ? null : t.Title.Trim();
        // Dosyadan yüklenen altyazıda mpv başlığa dosya adının videodan sonraki kısmını koyuyor ("English.srt")
        if (t.External && title != null) title = Path.GetFileNameWithoutExtension(title) is { Length: > 0 } n ? n : null;
        var parts = new List<string>();
        // Dosyadan yüklenen altyazının başlığı dosya adı; dili biliniyorsa dil yeterli
        if (title != null && !(t.External && lang != null) && !string.Equals(title, lang, StringComparison.OrdinalIgnoreCase))
            parts.Add(title);
        if (lang != null) parts.Add(lang);
        if (parts.Count == 0) parts.Add(CodecName(t.Codec) ?? "?");
        var name = string.Join(" - ", parts);
        return t.Forced ? $"{name} ({L.Forced})" : name;
    }

    static string? CodecName(string? codec) => codec switch
    {
        null or "" => null,
        "ac3" => "AC-3",
        "eac3" => "E-AC-3",
        "truehd" => "TrueHD",
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        "subrip" => "SRT",
        "hdmv_pgs_subtitle" => "PGS",
        "dvd_subtitle" => "VobSub",
        "webvtt" => "WebVTT",
        "mov_text" => "Text",
        _ when codec.StartsWith("pcm_") => "PCM",
        _ => codec.ToUpperInvariant(),
    };

    int? _lastSubtitleId;

    void SelectSubtitle(MpvEngine.Track? track)
    {
        TracksPopup.IsOpen = false;
        _player.SelectSubtitle(track?.Id);
        // Sonraki videolarda da aynı tercih: kapattıysa kapalı, bir dil seçtiyse o dil
        _player.SubtitlesOff = track == null;
        if (track?.Language is { } lang) _player.PreferredSubtitleLanguage = L.LanguageCodes(lang);
        if (track != null) _lastSubtitleId = track.Id;
    }

    void SelectAudio(MpvEngine.Track track)
    {
        TracksPopup.IsOpen = false;
        _player.SelectAudio(track.Id);
        if (track.Language is { } lang) _player.PreferredAudioLanguage = L.LanguageCodes(lang);
    }

    /// <summary>C tuşu: altyazıyı kapatır ya da (en son açık olanı veya ilkini) açar.</summary>
    void ToggleSubtitles()
    {
        if (_playlist.Current == null) return;
        var subs = _player.Tracks.Where(t => t.Type == "sub").ToList();
        if (subs.Count == 0) { ShowToast("", L.NoSubtitles); return; }
        if (subs.FirstOrDefault(t => t.Selected) is { } current)
        {
            _lastSubtitleId = current.Id;
            SelectSubtitle(null);
            ShowToast("", L.SubtitlesOff);
            return;
        }
        var track = subs.FirstOrDefault(t => t.Id == _lastSubtitleId) ?? subs[0];
        SelectSubtitle(track);
        ShowToast("", L.SubtitleOn(TrackName(track)));
    }

    void ChooseSubtitleFile()
    {
        TracksPopup.IsOpen = false;
        var exts = string.Join(";", FolderPlaylist.SubtitleExtensions.Select(e => "*" + e));
        var dialog = new OpenFileDialog
        {
            Title = L.ChooseSubtitleFile,
            Filter = $"{L.SubtitleFiles}|{exts}|{L.AllFiles}|*.*",
        };
        if (_playlist.Current is { } cur) dialog.InitialDirectory = Path.GetDirectoryName(cur);
        if (dialog.ShowDialog(this) == true) AddSubtitle(dialog.FileName);
    }

    void AddSubtitle(string path)
    {
        _player.AddSubtitle(path);
        _player.SubtitlesOff = false;
        ShowToast("", L.SubtitleOn(Path.GetFileName(path)));
    }

    void ApplySubtitleStyle() => _player.ApplySubtitleStyle(SystemMedia.ReadSubtitleStyle());

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
}
