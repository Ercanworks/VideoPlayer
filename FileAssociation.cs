using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Oynatici;

/// <summary>
/// Registers Video Player with Windows as a video player (for the current user only).
/// Windows 10 does not let a program change the default itself; after registering,
/// the Default apps settings page opens and the user makes the choice.
/// </summary>
public static class FileAssociation
{
    // The IDs keep the old name: the user's default player choice is tied to this ProgId
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
            prog.SetValue("", L.VideoFileType);
            prog.SetValue("FriendlyTypeName", L.VideoFileType);
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

        // Show up in the "Open with" list
        foreach (var ext in FolderPlaylist.VideoExtensions)
        {
            using var openWith = classes.CreateSubKey(ext + @"\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        // Show up in Settings > Default apps
        using (var caps = Registry.CurrentUser.CreateSubKey(CapabilitiesKey))
        {
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", L.AppDescription);
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
    /// If it was registered before but the registered exe no longer exists (moved or renamed), point
    /// the registration at this exe; the user's default player choice keeps working unchanged.
    /// If the registered exe still exists, leave it alone, otherwise any other copy would take over the registration.
    /// </summary>
    public static void RepairIfMoved()
    {
        try
        {
            using var cmd = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ProgId}\shell\open\command");
            if (cmd?.GetValue("") is not string current) return; // never registered
            var parts = current.Split('"', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && !System.IO.File.Exists(parts[0])) Register();
        }
        catch { }
    }

    public static void OpenDefaultAppsSettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
}
