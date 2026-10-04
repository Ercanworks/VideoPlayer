using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Oynatici;

/// <summary>
/// mpv'nin görüntüyü çizdiği siyah arka planlı yerel pencere (mpv'ye tanıtıcısı verilir).
/// </summary>
public sealed class VideoHost : HwndHost
{
    const string ClassName = "OynaticiVideo";
    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x02000000, WS_CLIPSIBLINGS = 0x04000000;

    // Pencere sınıfının WndProc'u çöp toplayıcıya gitmesin diye statik
    static readonly WndProcDelegate DefProc = DefWindowProc;
    static bool _registered;

    public IntPtr VideoHandle { get; private set; }

    /// <summary>Pencere oluşunca (motor buna bağlanabilir) bir kez çağrılır.</summary>
    public event Action<IntPtr>? HandleCreated;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        RegisterOnce();
        VideoHandle = CreateWindowEx(0, ClassName, "", WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        var handle = VideoHandle;
        Dispatcher.BeginInvoke(() => HandleCreated?.Invoke(handle));
        return new HandleRef(this, VideoHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) => DestroyWindow(hwnd.Handle);

    static void RegisterOnce()
    {
        if (_registered) return;
        var wc = new WNDCLASSEX
        {
            Size = Marshal.SizeOf<WNDCLASSEX>(),
            WndProc = Marshal.GetFunctionPointerForDelegate(DefProc),
            Instance = GetModuleHandle(null),
            Background = GetStockObject(4 /* BLACK_BRUSH */),
            ClassName = ClassName,
        };
        RegisterClassEx(ref wc);
        _registered = true;
    }

    delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int Size; public uint Style; public IntPtr WndProc; public int ClsExtra, WndExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName; public string ClassName; public IntPtr IconSmall;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(int exStyle, string className, string title, int style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("gdi32.dll")] static extern IntPtr GetStockObject(int index);
}
