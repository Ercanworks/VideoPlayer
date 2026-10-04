using LibVLCSharp.Shared;

namespace Oynatici.Engine;

public sealed class VlcEngine : IPlayerEngine
{
    readonly LibVLC _libVlc;
    readonly MediaPlayer _player;

    public event Action? Playing, Paused, Stopped, EndReached, Error;

    public string Name => "VLC";
    public int ScrubIntervalMs => 120;

    public VlcEngine()
    {
        Core.Initialize();
        _libVlc = new LibVLC("--no-osd", "--no-video-title-show", "--no-snapshot-preview",
            "--snapshot-format=jpg", "--quiet");
        _player = new MediaPlayer(_libVlc)
        {
            EnableHardwareDecoding = true,
            EnableMouseInput = false,
            EnableKeyInput = false,
        };
        _player.Playing += (_, _) => Playing?.Invoke();
        _player.Paused += (_, _) => Paused?.Invoke();
        _player.Stopped += (_, _) => Stopped?.Invoke();
        _player.EndReached += (_, _) => EndReached?.Invoke();
        _player.EncounteredError += (_, _) => Error?.Invoke();
    }

    public void Attach(IntPtr hwnd) => _player.Hwnd = hwnd;

    public void Open(string path)
    {
        using var media = new Media(_libVlc, path, FromType.FromPath);
        _player.Play(media);
    }

    public void SetPause(bool pause) => _player.SetPause(pause);
    public void Stop() => _player.Stop();

    public PlayerState State => _player.State switch
    {
        VLCState.Opening or VLCState.Buffering => PlayerState.Opening,
        VLCState.Playing => PlayerState.Playing,
        VLCState.Paused => PlayerState.Paused,
        VLCState.Ended => PlayerState.Ended,
        VLCState.Error => PlayerState.Error,
        _ => PlayerState.Idle,
    };

    public bool IsPlaying => _player.IsPlaying;
    public bool IsSeekable => _player.IsSeekable;
    public long Time { get => _player.Time; set => _player.Time = value; }
    public long Length => _player.Length;
    public int Volume { set => _player.Volume = value; }
    public bool Mute { set => _player.Mute = value; }
    public double Rate { get => _player.Rate; set => _player.SetRate((float)value); }

    // VLC görüntüyü canlı kaydıramıyor; oynatıcı bunun yerine kare görüntüsünü kaydırıyor
    public bool SupportsLiveShift => false;
    public void SetShift(double pixels, double viewWidth, double viewHeight) { }

    public bool TakeSnapshot(string path) => _player.TakeSnapshot(0, path, 0, 0);

    public void Dispose()
    {
        _player.Dispose();
        _libVlc.Dispose();
    }
}
