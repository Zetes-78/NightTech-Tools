namespace NightTech.Core.Carving;

/// <summary>Read-only access to a drive image (or a test buffer).</summary>
public interface IImageReader : IDisposable
{
    long Length { get; }

    /// <summary>Reads up to buffer.Length bytes at offset; returns how many were read (fewer only at the end of the image).</summary>
    int Read(long offset, Span<byte> buffer);
}

/// <summary>A drive image file opened read-only. The image is never changed.</summary>
public sealed class FileImageReader : IImageReader
{
    private readonly FileStream _fs;
    private readonly object _gate = new();

    public FileImageReader(string path)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);
    }

    public long Length => _fs.Length;

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= Length) return 0;
        lock (_gate)
        {
            _fs.Position = offset;
            int total = 0;
            while (total < buffer.Length)
            {
                int n = _fs.Read(buffer[total..]);
                if (n == 0) break;
                total += n;
            }
            return total;
        }
    }

    public void Dispose() => _fs.Dispose();
}

/// <summary>An image held in memory (tests).</summary>
public sealed class MemoryImageReader : IImageReader
{
    private readonly byte[] _data;
    public MemoryImageReader(byte[] data) => _data = data;
    public long Length => _data.Length;

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= _data.Length) return 0;
        int n = (int)Math.Min(buffer.Length, _data.Length - offset);
        _data.AsSpan((int)offset, n).CopyTo(buffer);
        return n;
    }

    public void Dispose() { }
}

/// <summary>
/// Random-access reads with a small cache, for the format parsers. Reads past the end of the image return zeros,
/// so callers check positions against <see cref="Length"/>.
/// </summary>
internal sealed class ByteSource
{
    private readonly IImageReader _r;
    private readonly byte[] _cache = new byte[64 * 1024];
    private long _cacheStart = -1;
    private int _cacheLen;

    public ByteSource(IImageReader r) => _r = r;
    public long Length => _r.Length;

    /// <summary>A plain block read, for parsers that stream through a lot of data.</summary>
    public int Read(long pos, Span<byte> dest) => _r.Read(pos, dest);

    public int Byte(long pos)
    {
        if (pos < 0 || pos >= _r.Length) return -1;
        if (_cacheStart < 0 || pos < _cacheStart || pos >= _cacheStart + _cacheLen)
        {
            _cacheStart = pos;
            _cacheLen = _r.Read(pos, _cache);
            if (_cacheLen <= 0) { _cacheStart = -1; return -1; }
        }
        return _cache[pos - _cacheStart];
    }

    public bool Bytes(long pos, Span<byte> dest)
    {
        for (int i = 0; i < dest.Length; i++)
        {
            int b = Byte(pos + i);
            if (b < 0) return false;
            dest[i] = (byte)b;
        }
        return true;
    }

    public uint U32BE(long pos)
    {
        Span<byte> b = stackalloc byte[4];
        return Bytes(pos, b) ? (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]) : uint.MaxValue;
    }

    public ulong U64BE(long pos) => (ulong)U32BE(pos) << 32 | U32BE(pos + 4);
    public int U16BE(long pos) => Byte(pos) is var a and >= 0 && Byte(pos + 1) is var b and >= 0 ? a << 8 | b : -1;
    public int U16LE(long pos) => Byte(pos) is var a and >= 0 && Byte(pos + 1) is var b and >= 0 ? b << 8 | a : -1;
    public long U32LE(long pos)
    {
        Span<byte> b = stackalloc byte[4];
        return Bytes(pos, b) ? (uint)(b[3] << 24 | b[2] << 16 | b[1] << 8 | b[0]) : -1;
    }

    // The scan moves forward through the image, so earlier searches answer most later ones. Without this, every false
    // start (random bytes that look like "%PDF-" or "PK..") would search hundreds of MB ahead again.
    // For each pattern: every start position in [Base, SearchedTo) has been checked, and Hits lists the matches there.
    private sealed class Searched { public long Base, SearchedTo, LastUsed; public readonly List<long> Hits = new(); }
    private readonly Dictionary<string, List<Searched>> _searched = new();
    private long _uses;
    private readonly byte[] _block = new byte[1024 * 1024];

    /// <summary>Position of the next occurrence of a byte pattern at or after <paramref name="from"/>, before <paramref name="limit"/>; -1 if none.</summary>
    public long Find(ReadOnlySpan<byte> pattern, long from, long limit)
    {
        limit = Math.Min(limit, _r.Length);
        if (from >= limit) return -1;
        var key = Convert.ToHexString(pattern);
        if (!_searched.TryGetValue(key, out var records)) _searched[key] = records = new();
        // A few records per pattern: a parser that skips a stray match and searches on must not wipe what the
        // next false start will ask about.
        var c = records.FirstOrDefault(r => from >= r.Base && from <= r.SearchedTo);
        if (c is null)
        {
            if (records.Count >= 8) records.Remove(records.MinBy(r => r.LastUsed)!);
            records.Add(c = new Searched { Base = from, SearchedTo = from });
        }
        c.LastUsed = ++_uses;
        while (true)
        {
            int i = c.Hits.BinarySearch(from);
            if (i < 0) i = ~i;
            if (i < c.Hits.Count) return c.Hits[i] < limit ? c.Hits[i] : -1;  // the first match at or after from
            if (c.SearchedTo >= limit) return -1;
            long hit = FindRaw(pattern, c.SearchedTo, limit);
            if (hit < 0) { c.SearchedTo = limit; return -1; }
            c.Hits.Add(hit);
            c.SearchedTo = hit + 1;
        }
    }

    private long FindRaw(ReadOnlySpan<byte> pattern, long from, long limit)
    {
        var block = _block;
        long pos = from;
        while (pos < limit)
        {
            int n = _r.Read(pos, block.AsSpan(0, (int)Math.Min(block.Length, limit - pos + pattern.Length - 1)));
            if (n < pattern.Length) return -1;
            int i = block.AsSpan(0, n).IndexOf(pattern);
            if (i >= 0 && pos + i < limit) return pos + i;
            // Starts up to pos + n - pattern.Length have been checked; stop only once that covers the limit.
            if (pos + n - (pattern.Length - 1) >= limit || n < block.Length) return -1;
            pos += n - (pattern.Length - 1);
        }
        return -1;
    }
}
