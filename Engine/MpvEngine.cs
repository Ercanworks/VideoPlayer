using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Oynatici.Engine;

public enum PlayerState { Idle, Opening, Playing, Paused, Ended, Error }

/// <summary>
/// libmpv (mpv'nin kütüphane hâli) ile oynatma. Olaylar mpv'nin olay iş parçacığından gelir;
/// dinleyen taraf arayüz iş parçacığına aktarmalıdır.
/// </summary>
public sealed class MpvEngine : IDisposable
{
    readonly IntPtr _ctx;
    Thread? _eventThread;
    volatile bool _disposed, _initialized;
    string? _pendingOpen;

    // mpv'den gelen durum
    volatile bool _opening, _loaded, _paused, _eof, _error;

    public event Action? Playing, Paused, Stopped, EndReached, Error;

    /// <summary>Sürüklerken en fazla bu sıklıkta sarılır (ms).</summary>
    public int ScrubIntervalMs => 50;

    /// <param name="headless">Görüntü ve ses çıkışı olmadan (testler için).</param>
    public MpvEngine(bool headless = false)
    {
        _ctx = Native.mpv_create();
        if (_ctx == IntPtr.Zero) throw new InvalidOperationException("mpv başlatılamadı");

        // Kullanıcının kendi mpv.conf'u ve mpv'nin kendi arayüzü/kısayolları devre dışı;
        // her şeyi oynatıcı yönetiyor
        SetOption("config", "no");
        SetOption("terminal", "no");
        SetOption("osc", "no");
        SetOption("osd-level", "0");
        SetOption("input-default-bindings", "no");
        SetOption("input-vo-keyboard", "no");
        SetOption("input-cursor", "no");
        SetOption("cursor-autohide", "no");
        SetOption("hwdec", "auto-safe");
        // Ekranın yenileme hızına senkron çiz. 144 Hz'de ölçüldü (ekranı saniyede 240 kez
        // yakalayıp kayan çubuğun konumu izlenerek): kare atlamadan, ses saatine göre zamanlamadan
        // daha düzgün ritim (60 fps: 3,2 → 2,55 px sapma); 23,976 fps filmler 24'e hafifçe
        // hızlanıp 144 Hz'e tam oturur. Ara kare üretme de bunu gerektirir.
        // (1.9.2'deki "kare kaybı" ölçümü hatalıydı: ekranı tam 144 Hz'de örnekleyen yakalama,
        // her yenilemede çizim yapan bu modda iki güncellemeyi bir örneğe düşürüp kayıp sanıyordu.)
        SetOption("video-sync", "display-resample");
        // Görüntü kalitesi (ölçülerek seçildi): mpv'nin yüksek kalite profili (EWA Lanczos ile
        // büyütme ve renk kanalı ölçekleme, daha az halkalanma, HDR'de ayrıntı geri kazanımı).
        // HDR video SDR ekranda ITU BT.2390 eğrisi ve tonu koruyan gam eşlemesiyle gösterilir:
        // varsayılanlar orta tonları ~12 birim açıyor ve doygun renkleri solduruyordu (47 birime
        // kadar sapma); bunlarla gri tonlar aynen kalıyor, SDR gamı içindeki renkler en çok 18 birim
        SetOption("profile", "high-quality");
        SetOption("tone-mapping", "bt.2390");
        SetOption("gamut-mapping-mode", "relative");
        // Altyazılar: videoyla aynı adla başlayan dosyalar da yüklensin (Film.tr.srt, Film.Turkish.srt)
        // ve yaygın alt klasörlerde de aransın; Windows dilindeki altyazı seçilsin ama ses de o
        // dildeyse (ör. Türkçe filmde Türkçe altyazı) yalnızca "zorunlu" altyazılar açılsın
        SetOption("sub-auto", "fuzzy");
        // Windows büyük/küçük harf ayırmıyor; "Subs" ve "subs" ikisi de yazılırsa altyazı iki kez yüklenir
        SetOption("sub-file-paths", "Subs;Subtitles;Sub;Altyazı;Altyazılar;Altyazilar");
        SetOption("slang", DefaultSubtitleLanguages);
        SetOption("subs-with-matching-audio", "forced");
        // Video bitince dosyayı kapatma, son karede bekle (geri sarılabilsin)
        SetOption("keep-open", "yes");
        SetOption("idle", "yes");
        SetOption("force-window", "yes");
        if (headless)
        {
            SetOption("vo", "null");
            SetOption("ao", "null");
            SetOption("force-window", "no");
        }
    }

    public void Attach(IntPtr hwnd)
    {
        if (_initialized) return;
        if (hwnd != IntPtr.Zero) SetOption("wid", hwnd.ToInt64().ToString(CultureInfo.InvariantCulture));
        Check(Native.mpv_initialize(_ctx), "mpv_initialize");
        _initialized = true;

        Native.mpv_observe_property(_ctx, 1, U("pause"), Native.FormatFlag);
        Native.mpv_observe_property(_ctx, 2, U("eof-reached"), Native.FormatFlag);
        Native.mpv_observe_property(_ctx, 3, U("video-params/aspect"), Native.FormatDouble);
        Native.mpv_observe_property(_ctx, 4, U("video-params/rotate"), Native.FormatInt64);
        Native.mpv_observe_property(_ctx, 5, U("video-params/average-bpp"), Native.FormatInt64);
        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "mpv olayları" };
        _eventThread.Start();

        if (_pendingOpen != null) Open(_pendingOpen);
        _pendingOpen = null;
    }

    public void Open(string path)
    {
        if (!_initialized) { _pendingOpen = path; return; }
        _opening = true;
        _loaded = false;
        _eof = false;
        _error = false;
        SetProperty("pause", "no");
        SetPan(0);
        LastSeekTick = Environment.TickCount64;
        ApplyTrackPreferences();
        Command("loadfile", path, "replace");
    }

    // ---------------------------------------------------------------- Altyazı ve ses izleri

    static readonly string DefaultSubtitleLanguages =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr,tur" : "en,eng";

    /// <summary>
    /// Kullanıcının bu oturumda yaptığı seçim sonraki videolara da uygulanır: altyazıyı
    /// kapattıysa kapalı açılır, bir dil seçtiyse o dil, bir ses dili seçtiyse o ses tercih edilir.
    /// </summary>
    public bool SubtitlesOff { get; set; }
    public string? PreferredSubtitleLanguage { get; set; }
    public string? PreferredAudioLanguage { get; set; }

    void ApplyTrackPreferences()
    {
        SetProperty("sid", SubtitlesOff ? "no" : "auto");
        SetProperty("slang", PreferredSubtitleLanguage ?? DefaultSubtitleLanguages);
        SetProperty("aid", "auto");
        SetProperty("alang", PreferredAudioLanguage ?? "");
    }

    public sealed record Track(int Id, string Type, string? Language, string? Title, string? Codec,
        int Channels, bool Selected, bool External, bool Forced);

    /// <summary>Açık dosyadaki altyazı ve ses izleri (mpv'nin sırasıyla).</summary>
    public List<Track> Tracks
    {
        get
        {
            var list = new List<Track>();
            var json = GetString("track-list");
            if (string.IsNullOrEmpty(json)) return list;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                foreach (var t in doc.RootElement.EnumerateArray())
                {
                    string? Str(string name) => t.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
                    bool Flag(string name) => t.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
                    int channels = t.TryGetProperty("demux-channel-count", out var ch) && ch.TryGetInt32(out var c) ? c : 0;
                    list.Add(new Track(t.GetProperty("id").GetInt32(), Str("type") ?? "", Str("lang"), Str("title"),
                        Str("codec"), channels, Flag("selected"), Flag("external"), Flag("forced")));
                }
            }
            catch (System.Text.Json.JsonException) { }
            return list;
        }
    }

    /// <summary>Altyazı izini seçer; null altyazıyı kapatır.</summary>
    public void SelectSubtitle(int? id) => SetProperty("sid", id?.ToString(CultureInfo.InvariantCulture) ?? "no");
    public void SelectAudio(int id) => SetProperty("aid", id.ToString(CultureInfo.InvariantCulture));

    /// <summary>Dışarıdan bir altyazı dosyası ekleyip seçer.</summary>
    public void AddSubtitle(string path) => CommandAsync("sub-add", path, "select");

    const int DefaultSubMargin = 34; // mpv'nin varsayılanı, 720 satırlık ölçekte

    /// <summary>
    /// Altyazıları pencerenin altından en az bu oran kadar yukarıda tutar (0-1); kontroller
    /// görünürken altyazı düğmelerin altında kalmasın diye. mpv'nin birimi 720 satırlık ölçek.
    /// </summary>
    public void SetSubtitleClearance(double fractionOfHeight)
    {
        var margin = Math.Max(DefaultSubMargin, (int)Math.Round(fractionOfHeight * 720));
        if (margin == _subMargin || !_initialized) return;
        _subMargin = margin;
        SetProperty("sub-margin-y", margin.ToString(CultureInfo.InvariantCulture));
    }

    int _subMargin = DefaultSubMargin;

    /// <summary>Altyazı görünümü (Windows'un altyazı ayarlarından); null olanlar mpv'nin varsayılanı.</summary>
    public sealed record SubtitleStyle(double Scale, string? Font, string? Color, string? BackColor,
        double? OutlineSize, double? ShadowOffset);

    public void ApplySubtitleStyle(SubtitleStyle s)
    {
        SetProperty("sub-scale", s.Scale.ToString("0.###", CultureInfo.InvariantCulture));
        SetProperty("sub-font", s.Font ?? "sans-serif");
        SetProperty("sub-color", s.Color ?? "#FFFFFFFF");
        SetProperty("sub-border-style", s.BackColor != null ? "background-box" : "outline-and-shadow");
        SetProperty("sub-back-color", s.BackColor ?? "#AF000000");
        SetProperty("sub-outline-size", (s.OutlineSize ?? 1.65).ToString("0.##", CultureInfo.InvariantCulture));
        SetProperty("sub-shadow-offset", (s.ShadowOffset ?? 0).ToString("0.##", CultureInfo.InvariantCulture));
    }

    // ---------------------------------------------------------------- Görüntü kalitesi

    volatile bool _eightBit;

    /// <summary>0: yüksek kalite, 1: mpv'nin varsayılan ölçekleyicileri, 2: hızlı (zayıf ekran kartları).</summary>
    public int QualityLevel { get; private set; }

    /// <summary>
    /// 8 bitlik videolarda yumuşak renk geçişlerindeki (gökyüzü, karanlık sahneler) basamakları
    /// giderir. 10 bitlik videolarda bu sorun olmadığından ince ayrıntıya dokunmamak için kapalı.
    /// </summary>
    void UpdateDeband() => CommandAsync("set", "deband", _eightBit && QualityLevel == 0 ? "yes" : "no");

    /// <summary>
    /// Ekran kartı yüksek kalite ayarlarına yetişemiyorsa (kare düşüyorsa) bir kademe hafifletir.
    /// Daha düşürülecek kademe kalmadıysa false.
    /// </summary>
    public bool ReduceQuality()
    {
        if (QualityLevel >= 2 || !_initialized) return false;
        QualityLevel++;
        if (QualityLevel == 1)
        {
            CommandAsync("set", "scale", "lanczos");
            CommandAsync("set", "cscale", "lanczos");
            CommandAsync("set", "scale-antiring", "0");
            CommandAsync("set", "hdr-contrast-recovery", "0");
            CommandAsync("set", "hdr-peak-percentile", "100");
        }
        else
        {
            CommandAsync("apply-profile", "fast");
        }
        UpdateDeband();
        return true;
    }

    /// <summary>
    /// Ekran kartı veya çözücü yetişemediği için atlanan ya da geciken kareler (dosya başına).
    /// Ekran senkronlu çizimde yetişemeyen kareler "geciken" olarak sayılıyor.
    /// </summary>
    public long DroppedFrames => (long)(GetDouble("frame-drop-count") + GetDouble("vo-delayed-frame-count"));

    /// <summary>Son sarma veya dosya açma anı (Environment.TickCount64); sonrasındaki kısa takılmalar sayılmasın.</summary>
    public long LastSeekTick { get; private set; }

    public void SetPause(bool pause) => SetProperty("pause", pause ? "yes" : "no");

    public void Stop()
    {
        if (_initialized) Command("stop");
    }

    public PlayerState State =>
        _error ? PlayerState.Error
        : _opening ? PlayerState.Opening
        : !_loaded ? PlayerState.Idle
        : _eof ? PlayerState.Ended
        : _paused ? PlayerState.Paused
        : PlayerState.Playing;

    public bool IsPlaying => _loaded && !_paused && !_eof;
    public bool IsSeekable => _loaded && GetFlag("seekable");

    public long Time
    {
        get => (long)(GetDouble("time-pos") * 1000);
        // Tam kareye sar (mpv bunu hızlı yapıyor); arayüzü bekletmemek için eşzamansız
        set
        {
            LastSeekTick = Environment.TickCount64;
            CommandAsync("seek", (value / 1000.0).ToString("0.000", CultureInfo.InvariantCulture), "absolute+exact");
        }
    }

    public long Length => (long)(GetDouble("duration") * 1000);
    /// <summary>Duraklatılmışken bir kare ileri (1) veya geri (-1).</summary>
    public void FrameStep(int dir) => CommandAsync(dir > 0 ? "frame-step" : "frame-back-step");

    public int Volume { set => SetProperty("volume", value.ToString(CultureInfo.InvariantCulture)); }
    public bool Mute { set => SetProperty("mute", value ? "yes" : "no"); }

    /// <summary>
    /// Ara kare üretme: ekran yenilemesi iki video karesinin arasına düştüğünde kareleri
    /// zamanına göre harmanlar; 60 fps videonun 144 Hz ekrandaki 2-3 yenilemelik düzensiz
    /// kare süreleri kaybolur (ölçüldü: 2,55 → 2,0 px sapma; 30 fps'te 4,5 → 3,55).
    /// Ekran senkronlu çizimle çalışır. Oynatırken açılıp kapatılabilir.
    /// </summary>
    public bool Interpolation
    {
        set
        {
            if (_initialized) SetProperty("interpolation", value ? "yes" : "no");
            else SetOption("interpolation", value ? "yes" : "no");
        }
    }

    public double Rate
    {
        get => _initialized ? GetDouble("speed") : 1;
        set => SetProperty("speed", value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Videoyu pencere içinde yatayda kaydırır (piksel; eksi sola). Boşalan yer siyah kalır.
    /// Video alanının boyutu da verilir ki her seferinde mpv'ye sormak gerekmesin.
    /// </summary>
    public void SetShift(double pixels, double viewWidth, double viewHeight)
    {
        // mpv'nin kaydırma birimi, ekranda gösterilen video genişliğinin oranı.
        // Gösterilen genişlik: video alanına en-boy oranı korunarak sığdırılmış video.
        // En-boy oranı olay döngüsünde izleniyor; burada mpv'ye hiç soru sorulmuyor ki
        // mpv meşgulken arayüz beklemesin.
        var aspect = _aspect;
        // Telefonla dik çekilmiş videolarda mpv döndürmeden önceki oranı bildiriyor
        if (_rotate % 180 == 90 && aspect > 0) aspect = 1 / aspect;
        var shown = aspect > 0 && viewHeight > 0 ? Math.Min(viewWidth, viewHeight * aspect) : viewWidth;
        SetPan(shown > 0 ? pixels / shown : 0);
    }

    void SetPan(double pan)
    {
        if (!_initialized) return;
        Native.mpv_set_property_async(_ctx, 0, PanName, Native.FormatDouble, ref pan);
    }

    static readonly byte[] PanName = U("video-pan-x");
    volatile float _aspect;
    volatile int _rotate;

    void EventLoop()
    {
        while (!_disposed)
        {
            var evPtr = Native.mpv_wait_event(_ctx, -1);
            if (evPtr == IntPtr.Zero) continue;
            var ev = Marshal.PtrToStructure<Native.Event>(evPtr);
            switch (ev.EventId)
            {
                case Native.EventShutdown:
                    return;
                case Native.EventFileLoaded:
                    _opening = false;
                    _loaded = true;
                    Playing?.Invoke();
                    break;
                case Native.EventEndFile:
                    var end = Marshal.PtrToStructure<Native.EndFile>(ev.Data);
                    _opening = false;
                    if (end.Reason == Native.EndReasonError)
                    {
                        _error = true;
                        Error?.Invoke();
                    }
                    else if (end.Reason == Native.EndReasonStop || end.Reason == Native.EndReasonQuit)
                    {
                        _loaded = false;
                        Stopped?.Invoke();
                    }
                    break;
                case Native.EventPropertyChange:
                    var prop = Marshal.PtrToStructure<Native.EventProperty>(ev.Data);
                    if (ev.ReplyUserdata == 3)
                    {
                        _aspect = prop.Format == Native.FormatDouble && prop.Data != IntPtr.Zero
                            ? (float)BitConverter.Int64BitsToDouble(Marshal.ReadInt64(prop.Data)) : 0;
                        break;
                    }
                    if (ev.ReplyUserdata == 4)
                    {
                        _rotate = prop.Format == Native.FormatInt64 && prop.Data != IntPtr.Zero
                            ? (int)Marshal.ReadInt64(prop.Data) : 0;
                        break;
                    }
                    if (ev.ReplyUserdata == 5)
                    {
                        // 8 bit 4:2:0 videoda piksel başına ortalama 12 bit; 10 bitte 24 (p010)
                        var bpp = prop.Format == Native.FormatInt64 && prop.Data != IntPtr.Zero
                            ? Marshal.ReadInt64(prop.Data) : 0;
                        _eightBit = bpp is > 0 and <= 12;
                        UpdateDeband();
                        break;
                    }
                    if (prop.Format != Native.FormatFlag || prop.Data == IntPtr.Zero) break;
                    var flag = Marshal.ReadInt32(prop.Data) != 0;
                    if (ev.ReplyUserdata == 1)
                    {
                        _paused = flag;
                        if (!_loaded) break;
                        if (flag) Paused?.Invoke(); else if (!_eof) Playing?.Invoke();
                    }
                    else if (ev.ReplyUserdata == 2 && flag != _eof)
                    {
                        _eof = flag;
                        if (flag && _loaded) EndReached?.Invoke();
                    }
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- libmpv yardımcıları

    static byte[] U(string s) => Encoding.UTF8.GetBytes(s + "\0");

    static void Check(int result, string what)
    {
        if (result < 0) throw new InvalidOperationException($"{what} başarısız: {result}");
    }

    void SetOption(string name, string value) => Native.mpv_set_option_string(_ctx, U(name), U(value));

    void SetProperty(string name, string value)
    {
        if (_initialized) Native.mpv_set_property_string(_ctx, U(name), U(value));
    }

    double GetDouble(string name)
    {
        if (!_initialized) return 0;
        return Native.mpv_get_property_double(_ctx, U(name), Native.FormatDouble, out var v) >= 0 ? v : 0;
    }

    string? GetString(string name)
    {
        if (!_initialized) return null;
        var p = Native.mpv_get_property_string(_ctx, U(name));
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { Native.mpv_free(p); }
    }

    bool GetFlag(string name)
    {
        if (!_initialized) return false;
        return Native.mpv_get_property_flag(_ctx, U(name), Native.FormatFlag, out var v) >= 0 && v != 0;
    }

    int Command(params string[] args) => WithArgs(args, a => Native.mpv_command(_ctx, a));
    int CommandAsync(params string[] args) => !_initialized ? -1 : WithArgs(args, a => Native.mpv_command_async(_ctx, 0, a));

    static int WithArgs(string[] args, Func<IntPtr[], int> call)
    {
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            return call(ptrs);
        }
        finally
        {
            foreach (var p in ptrs) if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Olay döngüsü mpv_wait_event'te beklerken mpv yok edilirse çöker;
        // önce uyandırıp döngünün bitmesini bekle
        Native.mpv_wakeup(_ctx);
        _eventThread?.Join(1000);
        Native.mpv_terminate_destroy(_ctx);
    }

    static class Native
    {
        const string Lib = "libmpv-2.dll";

        public const int FormatFlag = 3, FormatInt64 = 4, FormatDouble = 5;
        public const int EventShutdown = 1, EventEndFile = 7, EventFileLoaded = 8, EventPropertyChange = 22;
        public const int EndReasonStop = 2, EndReasonQuit = 3, EndReasonError = 4;

        [StructLayout(LayoutKind.Sequential)]
        public struct Event { public int EventId; public int Error; public ulong ReplyUserdata; public IntPtr Data; }

        [StructLayout(LayoutKind.Sequential)]
        public struct EventProperty { public IntPtr Name; public int Format; public IntPtr Data; }

        [StructLayout(LayoutKind.Sequential)]
        public struct EndFile { public int Reason; public int Error; }

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_create();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_initialize(IntPtr ctx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_terminate_destroy(IntPtr ctx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_wakeup(IntPtr ctx);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_option_string(IntPtr ctx, byte[] name, byte[] value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_set_property_string(IntPtr ctx, byte[] name, byte[] value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_get_property_string(IntPtr ctx, byte[] name);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_free(IntPtr data);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
        public static extern int mpv_get_property_double(IntPtr ctx, byte[] name, int format, out double value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
        public static extern int mpv_get_property_flag(IntPtr ctx, byte[] name, int format, out int value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        public static extern int mpv_set_property_async(IntPtr ctx, ulong userdata, byte[] name, int format, ref double value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_command(IntPtr ctx, IntPtr[] args);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_command_async(IntPtr ctx, ulong userdata, IntPtr[] args);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_observe_property(IntPtr ctx, ulong userdata, byte[] name, int format);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_wait_event(IntPtr ctx, double timeout);
    }
}
