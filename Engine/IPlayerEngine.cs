namespace Oynatici.Engine;

public enum PlayerState { Idle, Opening, Playing, Paused, Ended, Error }

/// <summary>
/// Oynatma motoru (VLC veya mpv). Olaylar motorun kendi iş parçacığından gelir;
/// dinleyen taraf arayüz iş parçacığına aktarmalıdır.
/// </summary>
public interface IPlayerEngine : IDisposable
{
    event Action? Playing;
    event Action? Paused;
    event Action? Stopped;
    event Action? EndReached;
    event Action? Error;

    string Name { get; }

    /// <summary>Sürüklerken en fazla bu sıklıkta sarılır (ms); motor ne kadar hızlı sarabiliyorsa o kadar küçük.</summary>
    int ScrubIntervalMs { get; }

    /// <summary>Görüntünün çizileceği pencereyi verir; dosya açmadan önce çağrılmalı.</summary>
    void Attach(IntPtr hwnd);

    void Open(string path);
    void SetPause(bool pause);
    void Stop();

    PlayerState State { get; }
    bool IsPlaying { get; }
    bool IsSeekable { get; }
    /// <summary>Milisaniye.</summary>
    long Time { get; set; }
    long Length { get; }
    int Volume { set; }
    bool Mute { set; }
    double Rate { get; set; }

    /// <summary>Videoyu canlı olarak pencere içinde kaydırabiliyor mu (tutup çekme efekti için).</summary>
    bool SupportsLiveShift { get; }

    /// <summary>
    /// Videoyu pencere içinde yatayda kaydırır (piksel; eksi sola). Boşalan yer siyah kalır.
    /// Video alanının boyutu da verilir ki motor her seferinde kendisi sormak zorunda kalmasın.
    /// </summary>
    void SetShift(double pixels, double viewWidth, double viewHeight);

    /// <summary>O anki kareyi dosyaya kaydeder (arka plan iş parçacığından çağrılabilir).</summary>
    bool TakeSnapshot(string path);
}
