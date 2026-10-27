using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Oynatici.Engine;

public enum PlayerState { Idle, Opening, Playing, Paused, Ended, Error }

/// <summary>
/// Playback with libmpv (mpv as a library). Events come from mpv's event thread;
/// the listener must marshal to the UI thread.
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

    /// <summary>While dragging, seek at most this often (ms).</summary>
    public int ScrubIntervalMs => 50;

    /// <param name="headless">Without video and audio output (for tests).</param>
    public MpvEngine(bool headless = false)
    {
        _ctx = Native.mpv_create();
        if (_ctx == IntPtr.Zero) throw new InvalidOperationException("mpv başlatılamadı");

        // The user's own mpv.conf and mpv's own UI/shortcuts are disabled;
        // the player manages everything
        SetOption("config", "no");
        SetOption("terminal", "no");
        SetOption("osc", "no");
        SetOption("osd-level", "0");
        SetOption("input-default-bindings", "no");
        SetOption("input-vo-keyboard", "no");
        SetOption("input-cursor", "no");
        SetOption("cursor-autohide", "no");
        SetOption("hwdec", "auto-safe");
        // Draw in sync with the display refresh rate. Measured at 144 Hz (by capturing the screen 240 times
        // a second and tracking a moving bar): no dropped frames and a smoother cadence than timing by the
        // audio clock (60 fps: 3.2 → 2.55 px deviation); 23.976 fps films speed up slightly to 24
        // and fit 144 Hz exactly. Interpolation requires it too.
        // (The "frame loss" measurement in 1.9.2 was wrong: a capture sampling the screen at exactly 144 Hz
        // merged two updates into one sample in this draw-every-refresh mode and took it for a loss.)
        SetOption("video-sync", "display-resample");
        // Picture quality (chosen by measurement): mpv's high quality profile (EWA Lanczos for
        // upscaling and chroma scaling, less ringing, detail recovery in HDR).
        // HDR video on an SDR display uses the ITU BT.2390 curve and tone-preserving gamut mapping:
        // the defaults brightened mid-tones by ~12 units and washed out saturated colors (up to 47 units
        // of deviation); with these, grays stay the same and colors inside the SDR gamut shift by at most 18 units
        SetOption("profile", "high-quality");
        SetOption("tone-mapping", "bt.2390");
        SetOption("gamut-mapping-mode", "relative");
        // Subtitles: also load files starting with the video's name (Film.tr.srt, Film.Turkish.srt)
        // and search common subfolders; pick subtitles in the Windows language, but if the audio is in
        // that language too (e.g. Turkish subtitles on a Turkish film), only enable "forced" subtitles
        SetOption("sub-auto", "fuzzy");
        // Windows is case-insensitive; if both "Subs" and "subs" are listed, subtitles load twice
        SetOption("sub-file-paths", "Subs;Subtitles;Sub;Altyazı;Altyazılar;Altyazilar");
        SetOption("slang", DefaultSubtitleLanguages);
        SetOption("subs-with-matching-audio", "forced");
        // Don't close the file when the video ends, wait on the last frame (so it can be seeked back)
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

    // ---------------------------------------------------------------- Subtitle and audio tracks

    static readonly string DefaultSubtitleLanguages =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr,tur" : "en,eng";

    /// <summary>
    /// The choice the user makes in this session applies to the next videos too: if subtitles were
    /// turned off they open off, if a language was chosen that language, if an audio language was chosen that audio is preferred.
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

    /// <summary>Subtitle and audio tracks in the open file (in mpv's order).</summary>
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

    /// <summary>Selects a subtitle track; null turns subtitles off.</summary>
    public void SelectSubtitle(int? id) => SetProperty("sid", id?.ToString(CultureInfo.InvariantCulture) ?? "no");
    public void SelectAudio(int id) => SetProperty("aid", id.ToString(CultureInfo.InvariantCulture));

    /// <summary>Adds an external subtitle file and selects it.</summary>
    public void AddSubtitle(string path) => CommandAsync("sub-add", path, "select");

    const int DefaultSubMargin = 34; // mpv's default, on the 720-line scale

    /// <summary>
    /// Keeps subtitles at least this fraction above the bottom of the window (0-1), so they don't
    /// end up under the buttons while the controls are visible. mpv's unit is the 720-line scale.
    /// </summary>
    public void SetSubtitleClearance(double fractionOfHeight)
    {
        var margin = Math.Max(DefaultSubMargin, (int)Math.Round(fractionOfHeight * 720));
        if (margin == _subMargin || !_initialized) return;
        _subMargin = margin;
        SetProperty("sub-margin-y", margin.ToString(CultureInfo.InvariantCulture));
    }

    int _subMargin = DefaultSubMargin;

    /// <summary>Subtitle look (from Windows' caption settings); null values keep mpv's default.</summary>
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

    // ---------------------------------------------------------------- Picture quality

    volatile bool _eightBit;

    /// <summary>0: high quality, 1: mpv's default scalers, 2: fast (weak GPUs).</summary>
    public int QualityLevel { get; private set; }

    /// <summary>
    /// Removes banding in smooth gradients (sky, dark scenes) in 8-bit videos.
    /// Off for 10-bit videos, which don't have this problem, to leave fine detail untouched.
    /// </summary>
    void UpdateDeband() => CommandAsync("set", "deband", _eightBit && QualityLevel == 0 ? "yes" : "no");

    /// <summary>
    /// If the GPU cannot keep up with the high quality settings (drops frames), lightens them one level.
    /// False if there is no lower level left.
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
    /// Frames dropped or delayed because the GPU or decoder could not keep up (per file).
    /// In display-synced drawing, frames that could not keep up are counted as "delayed".
    /// </summary>
    public long DroppedFrames => (long)(GetDouble("frame-drop-count") + GetDouble("vo-delayed-frame-count"));

    /// <summary>Time of the last seek or file open (Environment.TickCount64); short stutters right after don't count.</summary>
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
        // Exact seek (mpv does this fast); asynchronous so the UI does not wait
        set
        {
            LastSeekTick = Environment.TickCount64;
            CommandAsync("seek", (value / 1000.0).ToString("0.000", CultureInfo.InvariantCulture), "absolute+exact");
        }
    }

    public long Length => (long)(GetDouble("duration") * 1000);
    /// <summary>One frame forward (1) or back (-1) while paused.</summary>
    public void FrameStep(int dir) => CommandAsync(dir > 0 ? "frame-step" : "frame-back-step");

    public int Volume { set => SetProperty("volume", value.ToString(CultureInfo.InvariantCulture)); }
    public bool Mute { set => SetProperty("mute", value ? "yes" : "no"); }

    /// <summary>
    /// Interpolation: when a display refresh falls between two video frames, frames are blended
    /// by their timing; the uneven 2-3 refresh frame durations of 60 fps video on a 144 Hz screen
    /// disappear (measured: 2.55 → 2.0 px deviation; at 30 fps 4.5 → 3.55).
    /// Works with display-synced drawing. Can be toggled during playback.
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
    /// Shifts the video horizontally inside the window (pixels; negative is left). The uncovered area stays black.
    /// The video area size is passed in too, so mpv doesn't have to be asked every time.
    /// </summary>
    public void SetShift(double pixels, double viewWidth, double viewHeight)
    {
        // mpv's shift unit is a fraction of the displayed video width.
        // Displayed width: the video fitted into the video area keeping its aspect ratio.
        // The aspect ratio is tracked in the event loop; mpv is not queried here at all, so
        // the UI doesn't wait while mpv is busy.
        var aspect = _aspect;
        // For portrait videos shot on a phone, mpv reports the ratio before rotation
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
                        // 8-bit 4:2:0 video averages 12 bits per pixel; 10-bit 24 (p010)
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

    // ---------------------------------------------------------------- libmpv helpers

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
        // If mpv is destroyed while the event loop waits in mpv_wait_event, it crashes;
        // wake it up first and wait for the loop to finish
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
