using System.IO;
using System.Text.Json;

namespace Oynatici;

public enum EndAction { Stop, Next, Repeat }

/// <summary>%APPDATA%\Oynatici\settings.json içinde saklanan kullanıcı ayarları.</summary>
public sealed class Settings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1100;
    public double Height { get; set; } = 650;
    public bool Maximized { get; set; }
    public int Volume { get; set; } = 80;
    public bool Muted { get; set; }
    public EndAction EndAction { get; set; } = EndAction.Stop;
    /// <summary>"mpv" veya "vlc"; değişiklik oynatıcı yeniden açılınca geçerli olur.</summary>
    public string Engine { get; set; } = "mpv";

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Oynatici");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }
}
