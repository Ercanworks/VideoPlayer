using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Oynatici.Engine;

/// <summary>
/// Lets mpv read a file through libmpv's stream callbacks with some bytes replaced on the fly
/// (the header bytes of fake parameter sets found by <see cref="DamagedVideo"/>). The file on disk is untouched.
/// </summary>
static class PatchedStream
{
    const string Protocol = "oynaticionarim";
    static readonly ConcurrentDictionary<string, (string Path, long[] Patches)> Files = new();
    static int _nextId;

    /// <summary>Registers the protocol with an mpv instance (after mpv_initialize).</summary>
    public static void Register(IntPtr ctx) => mpv_stream_cb_add_ro(ctx, Encoding.UTF8.GetBytes(Protocol + "\0"), IntPtr.Zero, OpenFn);

    /// <summary>URL that plays <paramref name="path"/> with <paramref name="patches"/> (ascending file offsets) applied.</summary>
    public static string Url(string path, long[] patches)
    {
        // Only one file plays at a time; earlier entries are no longer needed
        Files.Clear();
        var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Files[id] = (path, patches);
        return $"{Protocol}://{id}";
    }

    sealed class Reader(string path, long[] patches)
    {
        public readonly FileStream File = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        public readonly long[] Patches = patches;
        public byte[] Buffer = new byte[1 << 16];
    }

    static int Open(IntPtr userData, IntPtr uri, IntPtr info)
    {
        var url = Marshal.PtrToStringUTF8(uri) ?? "";
        if (!Files.TryGetValue(url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..], out var entry)) return LoadingFailed;
        Reader reader;
        try { reader = new Reader(entry.Path, entry.Patches); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return LoadingFailed; }
        Marshal.StructureToPtr(new StreamCbInfo
        {
            Cookie = GCHandle.ToIntPtr(GCHandle.Alloc(reader)),
            Read = Marshal.GetFunctionPointerForDelegate(ReadFn),
            Seek = Marshal.GetFunctionPointerForDelegate(SeekFn),
            Size = Marshal.GetFunctionPointerForDelegate(SizeFn),
            Close = Marshal.GetFunctionPointerForDelegate(CloseFn),
        }, info, false);
        return 0;
    }

    static long Read(IntPtr cookie, IntPtr buf, ulong nbytes)
    {
        var r = (Reader)GCHandle.FromIntPtr(cookie).Target!;
        try
        {
            int want = (int)Math.Min(nbytes, 1 << 24);
            if (r.Buffer.Length < want) r.Buffer = new byte[want];
            long pos = r.File.Position;
            int got = r.File.Read(r.Buffer, 0, want);
            int i = Array.BinarySearch(r.Patches, pos);
            for (i = i < 0 ? ~i : i; i < r.Patches.Length && r.Patches[i] < pos + got; i++)
                r.Buffer[r.Patches[i] - pos] = DamagedVideo.Filler;
            Marshal.Copy(r.Buffer, 0, buf, got);
            return got;
        }
        catch (IOException) { return -1; }
    }

    static long Seek(IntPtr cookie, long offset)
    {
        var r = (Reader)GCHandle.FromIntPtr(cookie).Target!;
        try { return r.File.Position = offset; }
        catch (IOException) { return GenericError; }
    }

    static long Size(IntPtr cookie) => ((Reader)GCHandle.FromIntPtr(cookie).Target!).File.Length;

    static void Close(IntPtr cookie)
    {
        var handle = GCHandle.FromIntPtr(cookie);
        ((Reader)handle.Target!).File.Dispose();
        handle.Free();
    }

    // ---------------------------------------------------------------- libmpv stream_cb API

    const int LoadingFailed = -13, GenericError = -20; // MPV_ERROR_LOADING_FAILED, MPV_ERROR_GENERIC

    [StructLayout(LayoutKind.Sequential)]
    struct StreamCbInfo { public IntPtr Cookie, Read, Seek, Size, Close, Cancel; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int OpenDelegate(IntPtr userData, IntPtr uri, IntPtr info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long ReadDelegate(IntPtr cookie, IntPtr buf, ulong nbytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long SeekDelegate(IntPtr cookie, long offset);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate long SizeDelegate(IntPtr cookie);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void CloseDelegate(IntPtr cookie);

    // Static so the delegates mpv calls are never garbage collected
    static readonly OpenDelegate OpenFn = Open;
    static readonly ReadDelegate ReadFn = Read;
    static readonly SeekDelegate SeekFn = Seek;
    static readonly SizeDelegate SizeFn = Size;
    static readonly CloseDelegate CloseFn = Close;

    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    static extern int mpv_stream_cb_add_ro(IntPtr ctx, byte[] protocol, IntPtr userData, OpenDelegate open);
}
