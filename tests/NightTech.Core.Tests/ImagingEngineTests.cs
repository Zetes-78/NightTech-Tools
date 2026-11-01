using System.Security.Cryptography;
using NightTech.Core.Imaging;

namespace NightTech.Core.Tests;

public sealed class ImagingEngineTests : IDisposable
{
    private const int Sector = 512;
    private readonly string _dir;

    public ImagingEngineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "nt-imaging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Img => Path.Combine(_dir, "case.img");
    private string Map => Path.Combine(_dir, "case.map.json");

    private static ImagingOptions Opts(int blockSectors = 8, int retry = 1, int maxSkip = 4) => new()
    {
        BlockSize = blockSectors * Sector,
        RetryPasses = retry,
        MaxSkipBlocks = maxSkip,
        SaveInterval = TimeSpan.FromMilliseconds(1),
    };

    private static byte[] Expected(byte[] data, IEnumerable<long> badSectors)
    {
        var e = (byte[])data.Clone();
        foreach (var s in badSectors) Array.Clear(e, (int)(s * Sector), Sector);
        return e;
    }

    [Fact]
    public void CleanDrive_IsCopiedExactly_AndHashed()
    {
        var data = FakeDevice.Pattern(Sector * 100);
        var r = new ImagingEngine().Run(new FakeDevice(data), Img, Map, Opts());

        Assert.Equal(ImagingStatus.Completed, r.Status);
        Assert.Equal(data, File.ReadAllBytes(Img));
        Assert.Equal(data.LongLength, r.Map.BytesGood);
        Assert.Equal(0, r.Map.BytesBad);
        Assert.Equal(0, r.Map.BytesPending);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), r.ImageSha256);
    }

    [Fact]
    public void PartialLastBlock_IsHandled()
    {
        var data = FakeDevice.Pattern(Sector * 21); // 21 sectors, blocks of 8 -> last block is 5 sectors
        var r = new ImagingEngine().Run(new FakeDevice(data), Img, Map, Opts());
        Assert.Equal(ImagingStatus.Completed, r.Status);
        Assert.Equal(data, File.ReadAllBytes(Img));
        Assert.Equal(3, r.Map.BlockCount);
    }

    [Fact]
    public void BadSectors_AreZeroFilled_AndListed_RestIsCopied()
    {
        var data = FakeDevice.Pattern(Sector * 200);
        long[] bad = { 3, 50, 51, 52, 199 };
        var r = new ImagingEngine().Run(new FakeDevice(data, bad: bad), Img, Map, Opts());

        Assert.Equal(ImagingStatus.CompletedWithBadSectors, r.Status);
        Assert.Equal(bad, r.Map.BadSectors.ToArray());
        Assert.Equal(Expected(data, bad), File.ReadAllBytes(Img));
        Assert.Equal(bad.Length * (long)Sector, r.Map.BytesBad);
        Assert.Equal(data.LongLength - bad.Length * (long)Sector, r.Map.BytesGood);
        Assert.Equal(0, r.Map.BytesPending);
    }

    [Fact]
    public void SkippedAreas_AfterDamage_AreStillRecovered()
    {
        // One bad sector early causes a skip-ahead over good blocks; the scrape pass must recover them.
        var data = FakeDevice.Pattern(Sector * 400);
        long[] bad = { 9 };
        var r = new ImagingEngine().Run(new FakeDevice(data, bad: bad), Img, Map, Opts(maxSkip: 16));
        Assert.Equal(bad, r.Map.BadSectors.ToArray());
        Assert.Equal(Expected(data, bad), File.ReadAllBytes(Img));
    }

    [Fact]
    public void FlakySector_IsRecoveredOnRetry()
    {
        var data = FakeDevice.Pattern(Sector * 64);
        // Fails: fast pass (block read), scrape whole-block read, scrape sector read. Succeeds on retry.
        var flaky = new Dictionary<long, int> { [10] = 3 };
        var r = new ImagingEngine().Run(new FakeDevice(data, flaky: flaky), Img, Map, Opts(retry: 1));

        Assert.Equal(ImagingStatus.Completed, r.Status);
        Assert.Empty(r.Map.BadSectors);
        Assert.Equal(data, File.ReadAllBytes(Img));
        Assert.Equal(BlockState.Good, r.Map.GetState(r.Map.BlockOfSector(10)));
    }

    [Fact]
    public void NoRetries_LeavesFlakySectorBad()
    {
        var data = FakeDevice.Pattern(Sector * 64);
        var flaky = new Dictionary<long, int> { [10] = 3 };
        var r = new ImagingEngine().Run(new FakeDevice(data, flaky: flaky), Img, Map, Opts(retry: 0));
        Assert.Equal(ImagingStatus.CompletedWithBadSectors, r.Status);
        Assert.Equal(new long[] { 10 }, r.Map.BadSectors.ToArray());
    }

    [Fact]
    public void Cancel_ThenResume_ProducesSameImage_WithoutRereadingGoodAreas()
    {
        var data = FakeDevice.Pattern(Sector * 160);
        long[] bad = { 77 };
        using var cts = new CancellationTokenSource();
        var dev1 = new FakeDevice(data, bad: bad) { OnRead = (_, _) => { } };
        dev1.OnRead = (off, _) => { if (off >= Sector * 64) cts.Cancel(); };

        var r1 = new ImagingEngine().Run(dev1, Img, Map, Opts(), ct: cts.Token);
        Assert.Equal(ImagingStatus.Cancelled, r1.Status);
        Assert.True(File.Exists(Map));
        Assert.True(r1.Map.BytesPending > 0);
        long goodBefore = r1.Map.BytesGood;
        Assert.True(goodBefore >= Sector * 64);

        var dev2 = new FakeDevice(data, bad: bad);
        long lowestRead = long.MaxValue;
        dev2.OnRead = (off, _) => lowestRead = Math.Min(lowestRead, off);
        var r2 = new ImagingEngine().Run(dev2, Img, Map, Opts());

        Assert.Equal(ImagingStatus.CompletedWithBadSectors, r2.Status);
        Assert.Equal(Expected(data, bad), File.ReadAllBytes(Img));
        Assert.True(lowestRead >= Sector * 64, "resume must not re-read areas already copied");
    }

    [Fact]
    public void DeviceGone_StopsSafely_AndCanResume()
    {
        var data = FakeDevice.Pattern(Sector * 120);
        var dev1 = new FakeDevice(data) { GoneAfterReads = 5 };
        var r1 = new ImagingEngine().Run(dev1, Img, Map, Opts());
        Assert.Equal(ImagingStatus.DeviceLost, r1.Status);
        Assert.Equal(5L * 8 * Sector, r1.Map.BytesGood);
        Assert.Empty(r1.Map.BadSectors); // a vanished drive must not be recorded as bad sectors

        var r2 = new ImagingEngine().Run(new FakeDevice(data), Img, Map, Opts());
        Assert.Equal(ImagingStatus.Completed, r2.Status);
        Assert.Equal(data, File.ReadAllBytes(Img));
    }

    [Fact]
    public void Resume_RejectsDifferentDrive()
    {
        var data = FakeDevice.Pattern(Sector * 32);
        new ImagingEngine().Run(new FakeDevice(data), Img, Map, Opts());

        var other = FakeDevice.Pattern(Sector * 48);
        Assert.Throws<InvalidOperationException>(() =>
            new ImagingEngine().Run(new FakeDevice(other), Img, Map, Opts()));

        Assert.Throws<InvalidOperationException>(() =>
            new ImagingEngine().Run(new FakeDevice(data, serial: "DIFFERENT"), Img, Map, Opts()));
    }

    [Fact]
    public void Map_RoundTrips_WithCounters()
    {
        var data = FakeDevice.Pattern(Sector * 96);
        long[] bad = { 1, 2, 90 };
        var r = new ImagingEngine().Run(new FakeDevice(data, bad: bad), Img, Map, Opts());
        var loaded = RescueMap.Load(Map);

        Assert.Equal(r.Map.BytesGood, loaded.BytesGood);
        Assert.Equal(r.Map.BytesBad, loaded.BytesBad);
        Assert.Equal(r.Map.BytesPending, loaded.BytesPending);
        Assert.Equal(bad, loaded.BadSectors.ToArray());
        Assert.Equal(r.ImageSha256, loaded.ImageSha256);
        for (int b = 0; b < r.Map.BlockCount; b++) Assert.Equal(r.Map.GetState(b), loaded.GetState(b));
    }

    [Fact]
    public void Report_IsWritten()
    {
        var data = FakeDevice.Pattern(Sector * 40);
        var r = new ImagingEngine().Run(new FakeDevice(data, bad: new long[] { 5 }), Img, Map, Opts());
        var (txt, json) = ImagingReport.Write(r, new CaseInfo("case", "NT-0001", "Jason", null), "0.1.0");
        var text = File.ReadAllText(txt);
        Assert.Contains("NT-0001", text);
        Assert.Contains("FAKE-123", text);
        Assert.Contains(r.ImageSha256!, text);
        Assert.Contains("can't be guaranteed", text);
        Assert.Contains("\"badSectorCount\": 1", File.ReadAllText(json));
    }

    [Fact]
    public void Options_Validation()
    {
        var data = FakeDevice.Pattern(Sector * 8);
        Assert.Throws<ArgumentException>(() =>
            new ImagingEngine().Run(new FakeDevice(data), Img, Map, new ImagingOptions { BlockSize = 1000 }));
        Assert.Throws<ArgumentException>(() =>
            new ImagingEngine().Run(new FakeDevice(data), Img, Img, Opts()));
    }

    [Theory]
    [InlineData("NT-0042 / John's laptop", "NT-0042 - John's laptop")]
    [InlineData("  ...  ", null)]
    public void CaseNames_AreSafe(string input, string? expected)
    {
        var s = CaseFiles.SafeName(input);
        if (expected is null) Assert.StartsWith("case-", s);
        else Assert.Equal(expected, s);
        Assert.DoesNotContain('/', s);
    }
}
