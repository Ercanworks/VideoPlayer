using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Oynatici.Engine;

/// <summary>
/// Finds fake H.264 parameter sets in MP4 files (e.g. NVIDIA Instant Replay clips where part of the
/// replay buffer was saved damaged). Damaged data that happens to look like SPS/PPS headers overwrites
/// the decoder's real ones; since those are stored only once in the file header, everything after it
/// would decode as mush. Turning those NAL units into filler data (which decoders ignore) leaves the
/// damaged part playing as well as it can, and the intact parts clean from their first keyframe.
/// </summary>
public static class DamagedVideo
{
    /// <summary>NAL header byte that replaces a fake parameter set's: filler data (type 12), which decoders skip.</summary>
    public const byte Filler = 0x0C;

    /// <summary>
    /// File offsets (ascending) of the header bytes of in-band SPS/PPS NAL units that differ from the ones
    /// in the file header, or null if there are none or the file is not an H.264 (avc1) MP4.
    /// </summary>
    public static long[]? FakeParameterSets(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            var moov = ReadMoov(fs);
            if (moov == null || FindBox(moov, 0, moov.Length, "mvex", out _) >= 0) return null; // fragmented: not handled
            var t = VideoTrack(moov);
            if (t == null) return null;

            var found = new List<long>();
            var hdr = new byte[5];
            for (int i = 0; i < t.Sizes.Length; i++) Scan(fs, t, t.Offsets[i], t.Sizes[i], hdr, found);
            return found.Count > 0 ? found.ToArray() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Walks a sample's NAL units the way the decoder splits them (length-prefixed).</summary>
    static void Scan(FileStream fs, Track t, long offset, int size, byte[] hdr, List<long> found)
    {
        int ls = t.LengthSize, off = 0;
        while (off + ls < size)
        {
            fs.Position = offset + off;
            if (fs.Read(hdr, 0, ls + 1) < ls + 1) return;
            long len = 0;
            for (int k = 0; k < ls; k++) len = (len << 8) | hdr[k];
            if (len == 0) { off += ls; continue; }
            // A unit longer than the sample: the decoder takes the rest of the sample as one unit
            len = Math.Min(len, size - off - ls);
            if ((hdr[ls] & 0x1f) is 7 or 8 && !IsKnown(fs, t, offset + off + ls, (int)len)) found.Add(offset + off + ls);
            off += ls + (int)len;
        }
    }

    static bool IsKnown(FileStream fs, Track t, long pos, int len)
    {
        if (!t.ParamSets.Any(p => p.Length == len)) return false;
        var data = new byte[len];
        fs.Position = pos;
        fs.ReadExactly(data);
        return t.ParamSets.Any(p => p.AsSpan().SequenceEqual(data));
    }

    // ---------------------------------------------------------------- MP4 index

    sealed class Track
    {
        public int LengthSize;
        public readonly List<byte[]> ParamSets = [];
        public long[] Offsets = [];
        public int[] Sizes = [];
    }

    static byte[]? ReadMoov(FileStream fs)
    {
        long pos = 0, len = fs.Length;
        var b = new byte[16];
        while (pos + 8 <= len)
        {
            fs.Position = pos;
            fs.ReadExactly(b, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(b);
            int head = 8;
            if (size == 1) { fs.ReadExactly(b, 8, 8); size = (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(8)); head = 16; }
            else if (size == 0) size = len - pos;
            if (size < head) return null;
            if (Encoding.ASCII.GetString(b, 4, 4) == "moov")
            {
                if (size > 256 << 20) return null;
                var data = new byte[size - head];
                fs.ReadExactly(data);
                return data;
            }
            pos += size;
        }
        return null;
    }

    /// <summary>Position of the payload of the first child box named <paramref name="type"/> in [start, end), or -1; payload length in <paramref name="length"/>.</summary>
    static int FindBox(byte[] d, int start, int end, string type, out int length)
    {
        length = 0;
        int p = start;
        while (p + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));
            int head = 8;
            if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(p + 8)); head = 16; }
            else if (size == 0) size = end - p;
            if (size < head || p + size > end) return -1;
            if (Encoding.ASCII.GetString(d, p + 4, 4) == type) { length = (int)size - head; return p + head; }
            p += (int)size;
        }
        return -1;
    }

    static int Find(byte[] d, int start, int end, out int length, params string[] types)
    {
        length = end - start;
        foreach (var type in types)
        {
            start = FindBox(d, start, start + length, type, out length);
            if (start < 0) return -1;
        }
        return start;
    }

    static uint U32(byte[] d, int p) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p));

    static Track? VideoTrack(byte[] m)
    {
        int p = 0;
        while (true)
        {
            int trak = FindBox(m, p, m.Length, "trak", out var trakLen);
            if (trak < 0) return null;
            p = trak + trakLen;
            int hdlr = Find(m, trak, trak + trakLen, out _, "mdia", "hdlr");
            if (hdlr < 0 || Encoding.ASCII.GetString(m, hdlr + 8, 4) != "vide") continue;
            var t = ParseTrack(m, trak, trakLen);
            if (t != null) return t;
        }
    }

    static Track? ParseTrack(byte[] m, int trak, int trakLen)
    {
        int stbl = Find(m, trak, trak + trakLen, out var stblLen, "mdia", "minf", "stbl");
        if (stbl < 0) return null;
        int stblEnd = stbl + stblLen;
        var t = new Track();

        // Sample description: avc1 with its avcC. (avc3 carries its parameter sets only in-band, so
        // real ones can't be told apart from fake ones there.)
        int stsd = FindBox(m, stbl, stblEnd, "stsd", out _);
        if (stsd < 0 || U32(m, stsd + 4) < 1) return null;
        int entry = stsd + 8;
        if (Encoding.ASCII.GetString(m, entry + 4, 4) != "avc1") return null;
        int avcC = FindBox(m, entry + 8 + 78, entry + (int)U32(m, entry), "avcC", out _);
        if (avcC < 0) return null;
        t.LengthSize = (m[avcC + 4] & 3) + 1;
        int q = avcC + 5;
        for (int list = 0; list < 2; list++)
        {
            int count = list == 0 ? m[q++] & 0x1f : m[q++];
            for (int i = 0; i < count; i++)
            {
                int len = (m[q] << 8) | m[q + 1];
                t.ParamSets.Add(m.AsSpan(q + 2, len).ToArray());
                q += 2 + len;
            }
        }

        // Sample sizes
        int stsz = FindBox(m, stbl, stblEnd, "stsz", out _);
        if (stsz < 0) return null;
        int fixedSize = (int)U32(m, stsz + 4), n = (int)U32(m, stsz + 8);
        t.Sizes = new int[n];
        for (int i = 0; i < n; i++) t.Sizes[i] = fixedSize != 0 ? fixedSize : (int)U32(m, stsz + 12 + 4 * i);

        // Chunk offsets + sample-to-chunk → sample offsets
        int stco = FindBox(m, stbl, stblEnd, "stco", out _);
        int co64 = stco < 0 ? FindBox(m, stbl, stblEnd, "co64", out _) : -1;
        int stsc = FindBox(m, stbl, stblEnd, "stsc", out _);
        if ((stco < 0 && co64 < 0) || stsc < 0) return null;
        int chunks = (int)U32(m, (stco >= 0 ? stco : co64) + 4);
        long Chunk(int c) => stco >= 0 ? U32(m, stco + 8 + 4 * c) : (long)BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(co64 + 8 + 8 * c));
        int entries = (int)U32(m, stsc + 4);
        t.Offsets = new long[n];
        int sample = 0;
        for (int e = 0; e < entries && sample < n; e++)
        {
            int first = (int)U32(m, stsc + 8 + 12 * e) - 1;
            int next = e + 1 < entries ? (int)U32(m, stsc + 8 + 12 * (e + 1)) - 1 : chunks;
            int perChunk = (int)U32(m, stsc + 8 + 12 * e + 4);
            for (int c = first; c < next && sample < n; c++)
            {
                long o = Chunk(c);
                for (int k = 0; k < perChunk && sample < n; k++) { t.Offsets[sample] = o; o += t.Sizes[sample++]; }
            }
        }
        return sample == n ? t : null;
    }
}
