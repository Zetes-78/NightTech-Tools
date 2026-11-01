using NightTech.Core.Imaging;

namespace NightTech.Core.Tests;

/// <summary>
/// An in-memory "drive" for tests. Bad sectors fail every read that touches them; flaky sectors
/// fail a set number of times and then read fine; the device can "disappear" after N reads.
/// Any read touching a bad sector fails as a whole, like a real drive.
/// </summary>
internal sealed class FakeDevice : ISourceDevice
{
    private readonly byte[] _data;
    private readonly HashSet<long> _bad;
    private readonly Dictionary<long, int> _flaky;

    public int ReadCount { get; private set; }
    public int? GoneAfterReads { get; set; }
    public Action<long, int>? OnRead { get; set; }

    public FakeDevice(byte[] data, int sectorSize = 512, IEnumerable<long>? bad = null,
        IDictionary<long, int>? flaky = null, string? serial = "FAKE-123")
    {
        _data = data;
        SectorSize = sectorSize;
        _bad = new HashSet<long>(bad ?? Array.Empty<long>());
        _flaky = new Dictionary<long, int>(flaky ?? new Dictionary<long, int>());
        SerialNumber = serial;
    }

    public string Description => "Test drive";
    public string? SerialNumber { get; }
    public long Length => _data.LongLength;
    public int SectorSize { get; }

    public void ReadExactly(long offset, byte[] buffer, int count)
    {
        Assert.True(offset % SectorSize == 0, "offset must be sector aligned");
        Assert.True(count % SectorSize == 0, "count must be sector aligned");
        Assert.True(offset >= 0 && offset + count <= Length, "read past end");

        ReadCount++;
        OnRead?.Invoke(offset, count);
        if (GoneAfterReads is int g && ReadCount > g)
            throw new DeviceGoneException("unplugged");

        long first = offset / SectorSize, last = (offset + count) / SectorSize - 1;
        bool fail = false;
        for (long s = first; s <= last; s++)
        {
            if (_bad.Contains(s)) fail = true;
            if (_flaky.TryGetValue(s, out var left) && left > 0) { _flaky[s] = left - 1; fail = true; }
        }
        if (fail) throw new IOException("CRC error");
        Array.Copy(_data, offset, buffer, 0, count);
    }

    public void Dispose() { }

    public static byte[] Pattern(int length, int seed = 7)
    {
        var rnd = new Random(seed);
        var d = new byte[length];
        rnd.NextBytes(d);
        return d;
    }
}
