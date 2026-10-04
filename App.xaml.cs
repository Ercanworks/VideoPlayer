using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Oynatici;

public partial class App : Application
{
    // Farklı klasörlerdeki kopyalar (ör. eski ve yeni sürüm) birbirine karışmasın
    static readonly string InstanceKey = Convert.ToHexString(
        System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(
            AppContext.BaseDirectory.ToLowerInvariant())))[..12];
    static readonly string MutexName = "Oynatici.TekPencere." + InstanceKey;
    static readonly string PipeName = "Oynatici.Dosya." + InstanceKey;

    Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var file = e.Args.FirstOrDefault(File.Exists);

        // Zaten açık bir pencere varsa dosyayı ona gönder ve çık
        _mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            if (SendToRunningInstance(file ?? "")) { Shutdown(); return; }
        }

        ApplyAccentColor();
        FileAssociation.RepairIfMoved();
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        if (file != null) window.OpenFile(file);
        if (first) ListenForFiles(window);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }

    static bool SendToRunningInstance(string file)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(1500);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(file);
            return true;
        }
        catch { return false; }
    }

    static void ListenForFiles(MainWindow window)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    pipe.WaitForConnection();
                    using var reader = new StreamReader(pipe);
                    var file = reader.ReadLine();
                    window.Dispatcher.BeginInvoke(() => window.BringToFrontAndOpen(file));
                }
                catch { Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "Dosya dinleyici" };
        thread.Start();
    }

    /// <summary>Windows'ta seçili vurgu rengini kullan (Filmler ve TV gibi).</summary>
    void ApplyAccentColor()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
            {
                var c = Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
                Resources["AccentBrush"] = new SolidColorBrush(c);
            }
        }
        catch { }
    }
}
