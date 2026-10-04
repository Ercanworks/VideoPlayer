using System.IO;
using System.Runtime.InteropServices;

namespace Oynatici;

/// <summary>Açılan videonun klasöründeki diğer videolar, Dosya Gezgini sırasıyla.</summary>
public sealed class FolderPlaylist
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".ts", ".m2ts",
        ".mts", ".mpg", ".mpeg", ".3gp", ".ogv", ".vob", ".divx", ".asf",
    };

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int StrCmpLogicalW(string a, string b);

    sealed class ExplorerOrder : IComparer<string>
    {
        public int Compare(string? a, string? b) =>
            StrCmpLogicalW(Path.GetFileName(a ?? ""), Path.GetFileName(b ?? ""));
    }

    List<string> _files = new();
    public int Index { get; private set; } = -1;
    public string? Current => Index >= 0 && Index < _files.Count ? _files[Index] : null;
    public bool HasNext => Index >= 0 && Index < _files.Count - 1;
    public bool HasPrevious => Index > 0;
    public int Count => _files.Count;

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public void Load(string file)
    {
        file = Path.GetFullPath(file);
        var files = new List<string>();
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (dir != null)
                files.AddRange(Directory.EnumerateFiles(dir).Where(IsVideo));
        }
        catch { }

        if (!files.Contains(file, StringComparer.OrdinalIgnoreCase)) files.Add(file);
        files.Sort(new ExplorerOrder());
        _files = files;
        Index = _files.FindIndex(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
    }

    public string? MoveNext() => HasNext ? _files[++Index] : null;
    public string? MovePrevious() => HasPrevious ? _files[--Index] : null;
    public string? PeekNext() => HasNext ? _files[Index + 1] : null;
    public string? PeekPrevious() => HasPrevious ? _files[Index - 1] : null;
}
