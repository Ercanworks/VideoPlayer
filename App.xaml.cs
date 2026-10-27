using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Oynatici;

public partial class App : Application
{
    // Copies in different folders (e.g. an old and a new version) must not get mixed up
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

        // If a window is already open, send the file to it and exit
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

    /// <summary>Use the accent color selected in Windows (like Movies & TV).</summary>
    void ApplyAccentColor()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM", "AccentColor", null) is int abgr)
            {
                var c = Color.FromRgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
                Resources["AccentBrush"] = new SolidColorBrush(c);
            }
            // The palette's 3rd color (index 2) is the "light 1" shade; Movies & TV uses it for the bars
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent",
                    "AccentPalette", null) is byte[] { Length: >= 12 } palette)
                Resources["AccentLightBrush"] = new SolidColorBrush(Color.FromRgb(palette[8], palette[9], palette[10]));
        }
        catch { }
    }
}
