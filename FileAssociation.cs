using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Oynatici;

/// <summary>
/// Video Player'ı Windows'a video oynatıcısı olarak tanıtır (sadece bu kullanıcı için).
/// Windows 10 varsayılanı programın değiştirmesine izin vermez; kayıttan sonra
/// Varsayılan uygulamalar ayarı açılır ve seçimi kullanıcı yapar.
/// </summary>
public static class FileAssociation
{
    // Kimlikler eski adla kalıyor: kullanıcının varsayılan oynatıcı seçimi bu ProgId'ye bağlı
    const string ProgId = "Oynatici.Video";
    const string AppName = "Video Player";
    const string OldAppName = "Oynatıcı";
    const string CapabilitiesKey = @"Software\Oynatici\Capabilities";

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    public static void Register()
    {
        var exe = Environment.ProcessPath!;
        var command = $"\"{exe}\" \"%1\"";
        using var classes = Registry.CurrentUser.CreateSubKey(@"Software\Classes");

        using (var prog = classes.CreateSubKey(ProgId))
        {
            prog.SetValue("", "Video dosyası");
            prog.SetValue("FriendlyTypeName", "Video dosyası");
            using (var icon = prog.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            using (var cmd = prog.CreateSubKey(@"shell\open\command")) cmd.SetValue("", command);
        }

        using (var app = classes.CreateSubKey(@"Applications\" + System.IO.Path.GetFileName(exe)))
        {
            app.SetValue("FriendlyAppName", AppName);
            using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue("", command);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var ext in FolderPlaylist.VideoExtensions) types.SetValue(ext, "");
        }

        // "Birlikte aç" listesinde görünsün
        foreach (var ext in FolderPlaylist.VideoExtensions)
        {
            using var openWith = classes.CreateSubKey(ext + @"\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        // Ayarlar > Varsayılan uygulamalar listesinde görünsün
        using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", "mpv altyapılı video oynatıcı");
            caps.SetValue("ApplicationIcon", $"\"{exe}\",0");
            using var assoc = caps.CreateSubKey("FileAssociations");
            foreach (var ext in FolderPlaylist.VideoExtensions) assoc.SetValue(ext, ProgId);
        }
        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
        {
            registered.SetValue(AppName, CapabilitiesKey);
            registered.DeleteValue(OldAppName, throwOnMissingValue: false);
        }

        const int SHCNE_ASSOCCHANGED = 0x08000000;
        SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Daha önce kayıt yapıldıysa ama kayıtlı exe artık yoksa (taşındı veya adı değişti) kaydı
    /// bu exe'ye güncelle; kullanıcının varsayılan oynatıcı seçimi bozulmadan çalışmaya devam eder.
    /// Kayıtlı exe hâlâ duruyorsa dokunma, yoksa başka bir kopya açıldığında kaydı kendine çekerdi.
    /// </summary>
    public static void RepairIfMoved()
    {
        try
        {
            using var cmd = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            if (cmd?.GetValue("") is not string current) return; // hiç kayıt yapılmamış
            var parts = current.Split('"', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && !System.IO.File.Exists(parts[0])) Register();
        }
        catch { }
    }

    public static void OpenDefaultAppsSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
}
