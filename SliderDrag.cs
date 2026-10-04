using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Oynatici;

/// <summary>
/// Çubuğun herhangi bir yerine basıp basılı tutarak kaydırmayı sağlar.
/// WPF'in kendi Slider'ı tıklanan yere atlar ama sürüklemek için ayrıca
/// tutamağı tutmak gerekir; burada basma, kaydırma ve bırakma tek harekettir.
/// </summary>
public sealed class SliderDrag
{
    // Tutamak genişliğinin yarısı: iz kenarlardan bu kadar içeride başlar
    const double ThumbHalf = 10;
    // Hassas modda "ters piramit": imleç çubuğun üstündeyken (DeadZone içinde) değer fareyi
    // birebir izler; yukarı kaldırdıkça her Step pikselde aynı yatay hareket daha küçük bir
    // aralığa denk gelir. MinGain en ince ayar sınırı (1/3: en fazla 3 kat ince).
    const double DeadZone = 16, Step = 50, MinGain = 1.0 / 3;

    readonly Slider _slider;
    double _lastX;
    Point _downPos;

    /// <summary>Açıksa imleç çubuktan yukarı kaldırıldıkça değer daha yavaş değişir (ince ayar).</summary>
    public bool Precise { get; set; }

    public bool IsDragging { get; private set; }

    /// <summary>Basıldıktan sonra fare gerçekten yer değiştirdi mi (WPF hareketsiz de olay gönderiyor).</summary>
    public bool HasMoved { get; private set; }
    public event Action? Started;
    public event Action<double>? Moved;
    public event Action<double>? Completed;

    public SliderDrag(Slider slider)
    {
        _slider = slider;
        _slider.IsMoveToPointEnabled = false;
        _slider.PreviewMouseLeftButtonDown += OnDown;
        _slider.PreviewMouseMove += OnMove;
        _slider.PreviewMouseLeftButtonUp += OnUp;
        _slider.LostMouseCapture += (_, _) => { if (IsDragging) Finish(); };
    }

    double TrackWidth => Math.Max(1, _slider.ActualWidth - 2 * ThumbHalf);
    double Range => _slider.Maximum - _slider.Minimum;

    public double ValueAt(double x)
    {
        var ratio = Math.Clamp((x - ThumbHalf) / TrackWidth, 0, 1);
        return _slider.Minimum + ratio * Range;
    }

    double ThumbX => ThumbHalf + (_slider.Value - _slider.Minimum) / Math.Max(double.Epsilon, Range) * TrackWidth;

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (!_slider.IsEnabled) return;
        IsDragging = true;
        _slider.CaptureMouse();
        Started?.Invoke();

        var x = e.GetPosition(_slider).X;
        _lastX = x;
        _downPos = e.GetPosition(_slider);
        HasMoved = false;
        // Tutamağın üstüne basıldıysa atlama; olduğu yerden ince ayar yapılabilsin
        if (!(Precise && Math.Abs(x - ThumbX) <= ThumbHalf)) SetValue(ValueAt(x));
        else Moved?.Invoke(_slider.Value);
        e.Handled = true;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (!IsDragging) return;
        if ((e.GetPosition(_slider) - _downPos).Length > 1) HasMoved = true;
        var x = e.GetPosition(_slider).X;
        if (!Precise)
        {
            SetValue(ValueAt(x));
            return;
        }

        var dx = x - _lastX;
        _lastX = x;
        var above = Math.Max(0, _slider.ActualHeight / 2 - e.GetPosition(_slider).Y - DeadZone);
        var gain = Math.Max(MinGain, 1 / (1 + above / Step));
        if (dx == 0) { Moved?.Invoke(_slider.Value); return; }

        // Fare çubuğun dışına çıkıp geri dönerken değer uç noktada beklesin
        var value = _slider.Value + dx * gain * Range / TrackWidth;
        if (x < ThumbHalf && dx > 0 || x > ThumbHalf + TrackWidth && dx < 0) return;
        SetValue(Math.Clamp(value, _slider.Minimum, _slider.Maximum));
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsDragging) return;
        _slider.ReleaseMouseCapture(); // LostMouseCapture -> Finish
        e.Handled = true;
    }

    void SetValue(double value)
    {
        _slider.Value = value;
        Moved?.Invoke(value);
    }

    void Finish()
    {
        IsDragging = false;
        Completed?.Invoke(_slider.Value);
    }
}
