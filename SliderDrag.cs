using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Oynatici;

/// <summary>
/// Lets you press anywhere on the bar and hold to slide.
/// WPF's own Slider jumps to the clicked spot, but dragging requires grabbing
/// the thumb separately; here press, slide and release are one motion.
/// </summary>
public sealed class SliderDrag
{
    // Half the thumb width: the track starts this far in from the edges
    const double ThumbHalf = 10;
    // "Inverted pyramid" in precise mode: while the cursor is on the bar (inside DeadZone) the value follows
    // the mouse one-to-one; moving up, every Step pixels the same horizontal motion maps to a smaller
    // range. MinGain is the finest limit (1/3: at most 3 times finer).
    const double DeadZone = 16, Step = 50, MinGain = 1.0 / 3;

    readonly Slider _slider;
    double _lastX;
    Point _downPos;

    /// <summary>When on, the value changes more slowly as the cursor is lifted above the bar (fine adjustment).</summary>
    public bool Precise { get; set; }

    public bool IsDragging { get; private set; }

    /// <summary>Whether the mouse actually moved after pressing (WPF also sends events without motion).</summary>
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
        // If pressed on the thumb, don't jump; allow fine adjustment from where it is
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

        // When the mouse leaves the bar and comes back, the value waits at the end point
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
