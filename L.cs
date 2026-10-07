using System.Globalization;

namespace Oynatici;

/// <summary>
/// Arayüz metinleri. Windows'un görüntü dili Türkçe ise Türkçe, değilse İngilizce.
/// VIDEOPLAYER_LANG ortam değişkeni ("tr" / "en") ile zorlanabilir (test için).
/// XAML'da {x:Static local:L.Ad} ile, kodda doğrudan kullanılır.
/// </summary>
public static class L
{
    public static readonly bool Turkish =
        (Environment.GetEnvironmentVariable("VIDEOPLAYER_LANG") ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) == "tr";

    static string T(string tr, string en) => Turkish ? tr : en;

    // Açılış ekranı
    public static string DropHere => T("Bir videoyu buraya sürükleyin", "Drag a video here");
    public static string Or => T("veya", "or");
    public static string ChooseFile => T("Dosya seç", "Choose file");

    // Pencere düğmeleri
    public static string Minimize => T("Simge durumuna küçült", "Minimize");
    public static string Maximize => T("Ekranı kapla", "Maximize");
    public static string Restore => T("Önceki boyuta getir", "Restore down");
    public static string Close => T("Kapat", "Close");
    public static string ControlsWindowTitle => T("Video Player denetimleri", "Video Player controls");

    // Kontrol çubuğu
    public static string RemainingOrTotal => T("Kalan süre (tıklayınca toplam süre)", "Remaining time (click for total)");
    public static string VolumeTip => T("Ses (Yukarı/Aşağı ok)", "Volume (Up/Down arrow)");
    public static string PreviousVideoTip => T("Önceki video (P)", "Previous video (P)");
    public static string NextVideoTip => T("Sonraki video (N)", "Next video (N)");
    public static string PreviousNamedTip(string name) => T($"Önceki: {name} (P)", $"Previous: {name} (P)");
    public static string NextNamedTip(string name) => T($"Sonraki: {name} (N)", $"Next: {name} (N)");
    public static string BackTip => T("10 saniye geri (Sol ok)", "Back 10 seconds (Left arrow)");
    public static string ForwardTip => T("30 saniye ileri (Sağ ok)", "Forward 30 seconds (Right arrow)");
    public static string PlayTip => T("Oynat (Boşluk)", "Play (Space)");
    public static string PauseTip => T("Duraklat (Boşluk)", "Pause (Space)");
    public static string MiniTip => T("Mini görünümde oynat", "Play in mini view");
    public static string ExitMiniTip => T("Mini görünümden çık", "Exit mini view");
    public static string FullscreenTip => T("Tam ekran (F11 / çift tık)", "Full screen (F11 / double-click)");
    public static string ExitFullscreenTip => T("Tam ekrandan çık (Esc)", "Exit full screen (Esc)");
    public static string MoreTip => T("Diğer seçenekler", "More options");
    public static string MuteTip => T("Sesi kapat (M)", "Mute (M)");
    public static string SubtitlesTip => T("Altyazı ve ses menüsünü göster (C)", "Show subtitles and audio menu (C)");

    // Altyazı ve ses menüsü
    public static string Subtitles => T("Altyazılar", "Subtitles");
    public static string Audio => T("Ses", "Audio");
    public static string Off => T("Kapalı", "Off");
    public static string ChooseSubtitleFile => T("Altyazı dosyası seç", "Choose subtitle file");
    public static string SubtitleSettings => T("Altyazı ayarları", "Subtitle settings");
    public static string Forced => T("zorunlu", "forced");
    public static string Channels(int n) => n switch
    {
        6 => "5.1",
        8 => "7.1",
        _ => T($"{n} kanal", n == 1 ? "1 channel" : $"{n} channels"),
    };
    public static string SubtitleFiles => T("Altyazı dosyaları", "Subtitle files");
    public static string SubtitleOn(string name) => T($"Altyazı: {name}", $"Subtitles: {name}");
    public static string SubtitlesOff => T("Altyazı kapalı", "Subtitles off");
    public static string NoSubtitles => T("Bu videoda altyazı yok", "This video has no subtitles");
    public static string AudioTrack(string name) => T($"Ses: {name}", $"Audio: {name}");

    // Diğer seçenekler menüsü
    public static string SpeedTip => T("Oynatma hızı (Shift + . / Shift + ,)", "Playback speed (Shift + . / Shift + ,)");
    public static string PlaybackSpeed => T("Oynatma hızı", "Playback speed");
    public static string WhenVideoEnds => T("Video bitince", "When the video ends");
    public static string EndStop => T("Dur", "Stop");
    public static string EndNext => T("Klasördeki sonraki videoya geç", "Play next video in folder");
    public static string EndRepeat => T("Tekrarla", "Repeat");
    public static string OpenFile => T("Dosya aç…", "Open file…");
    public static string OpenFileLocation => T("Dosya konumunu aç", "Open file location");
    public static string MakeDefault => T("Varsayılan video oynatıcısı yap", "Make default video player");

    // Bildirimler
    public static string CannotPlay => T("Bu dosya oynatılamadı", "This file couldn't be played");
    public static string SecondsBack(int s) => T($"{s} sn geri", $"{s} s back");
    public static string SecondsForward(int s) => T($"{s} sn ileri", $"{s} s forward");
    public static string LastInFolder => T("Klasördeki son video", "Last video in folder");
    public static string FirstInFolder => T("Klasördeki ilk video", "First video in folder");
    public static string Speed(double rate) => T($"Hız {rate:0.##}x", $"Speed {rate:0.##}x");
    public static string Volume(int v) => T($"Ses {v}", $"Volume {v}");
    public static string Muted => T("Ses kapalı", "Muted");
    public static string Unmuted => T("Ses açık", "Unmuted");

    // Dosya açma
    public static string OpenVideoTitle => T("Video aç", "Open video");
    public static string VideoFiles => T("Video dosyaları", "Video files");
    public static string AllFiles => T("Tüm dosyalar", "All files");

    // Varsayılan oynatıcı
    public static string DefaultPlayerTitle => T("Varsayılan oynatıcı", "Default player");
    public static string DefaultPlayerHelp => T(
        "Açılan Ayarlar penceresinde \"Video oynatıcısı\" başlığına tıklayıp listeden \"Video Player\"ı seçin.",
        "In the Settings window that opens, click \"Video player\" and choose \"Video Player\" from the list.");
    public static string RegistrationFailed(string error) => T($"Kayıt yapılamadı: {error}", $"Registration failed: {error}");
    public static string VideoFileType => T("Video dosyası", "Video file");
    public static string AppDescription => T("mpv altyapılı video oynatıcı", "Video player powered by mpv");

    // Dil adları (video dosyalarındaki ISO 639 kodlarından)

    // Matroska'da sık görülen ISO 639-2/B kodları; .NET yalnızca 639-2/T kodlarını tanıyor
    static readonly Dictionary<string, string> BibliographicCodes = new()
    {
        ["ger"] = "de", ["fre"] = "fr", ["dut"] = "nl", ["chi"] = "zh", ["cze"] = "cs", ["gre"] = "el",
        ["per"] = "fa", ["rum"] = "ro", ["slo"] = "sk", ["baq"] = "eu", ["arm"] = "hy", ["geo"] = "ka",
        ["ice"] = "is", ["mac"] = "mk", ["may"] = "ms", ["alb"] = "sq", ["bur"] = "my", ["wel"] = "cy",
        ["tib"] = "bo", ["scc"] = "sr", ["scr"] = "hr",
    };

    /// <summary>"tur" / "tr" → "Türkçe" (Windows diliyle); bilinmeyen kod olduğu gibi, boşsa null.</summary>
    public static string? LanguageName(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        code = code.Trim().ToLowerInvariant();
        if (code is "und" or "zxx" or "mis" or "mul") return null;
        if (BibliographicCodes.TryGetValue(code, out var two)) code = two;
        try
        {
            var culture = code.Length == 3
                ? CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(c => c.ThreeLetterISOLanguageName == code)
                : CultureInfo.GetCultureInfo(code);
            if (culture == null || string.Equals(culture.DisplayName, code, StringComparison.OrdinalIgnoreCase)) return code;
            var name = culture.DisplayName;
            return char.ToUpper(name[0], CultureInfo.CurrentUICulture) + name[1..];
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>Bir dil kodunun 2 ve 3 harfli halleri ("tur" → "tur,tr"); mpv'nin dil tercihine verilir.</summary>
    public static string LanguageCodes(string code)
    {
        code = code.Trim().ToLowerInvariant();
        var codes = new List<string> { code };
        if (BibliographicCodes.TryGetValue(code, out var two)) codes.Add(two);
        try
        {
            var culture = code.Length == 3
                ? CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(c => c.ThreeLetterISOLanguageName == code)
                : CultureInfo.GetCultureInfo(code);
            if (culture != null)
            {
                codes.Add(culture.TwoLetterISOLanguageName);
                codes.Add(culture.ThreeLetterISOLanguageName);
            }
        }
        catch (CultureNotFoundException) { }
        foreach (var (b, t) in BibliographicCodes)
            if (codes.Contains(t)) codes.Add(b);
        return string.Join(",", codes.Distinct());
    }

    // Hata
    public static string MpvMissing => T(
        "mpv kütüphanesi (libmpv-2.dll) bulunamadı veya açılamadı. Uygulama klasöründe olduğundan emin olun.",
        "The mpv library (libmpv-2.dll) could not be found or loaded. Make sure it is in the application folder.");
}
