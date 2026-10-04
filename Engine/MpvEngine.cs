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
        // Ekranın yenileme hızına senkron çiz: video karesi gelmese de her yenilemede yeniden
        // çizer, böylece tutup çekerken kayma video kare hızıyla değil ekran hızıyla akar
        SetOption("video-sync", "display-resample");
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
        Command("loadfile", path, "replace");
    }

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
        set => CommandAsync("seek", (value / 1000.0).ToString("0.000", CultureInfo.InvariantCulture), "absolute+exact");
    }

    public long Length => (long)(GetDouble("duration") * 1000);
    public int Volume { set => SetProperty("volume", value.ToString(CultureInfo.InvariantCulture)); }
    public bool Mute { set => SetProperty("mute", value ? "yes" : "no"); }

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

        public const int FormatFlag = 3, FormatDouble = 5;
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
