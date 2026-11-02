using NightTech.Core.Imaging;
using NightTech.Core.Wipe;

namespace NightTech.Core.Tests;

/// <summary>An in-memory drive that can be told to fail in the ways real drives do.</summary>
internal sealed class FakeWipeTarget : IWipeTarget
{
    public readonly byte[] Data;
    public HashSet<long> BadWriteSectors { get; } = new();   // write throws
    public HashSet<long> SilentSectors { get; } = new();     // write "succeeds" but nothing changes
    public HashSet<long> BadReadSectors { get; } = new();    // read throws
    public long GoneAfterBytesWritten { get; set; } = -1;    // drive unplugged
    public Action<long>? OnWrite { get; set; }
    public int Flushes { get; private set; }
    private long _written;

    public FakeWipeTarget(int sectors, int sectorSize = 512, byte fill = 0xAB)
    {
        SectorSize = sectorSize;
        Data = Enumerable.Repeat(fill, sectors * sectorSize).ToArray();
    }

    public string Description => "Fake drive";
    public string? SerialNumber => "FAKE123";
    public long Length => Data.Length;
    public int SectorSize { get; }

    public void WriteExactly(long offset, byte[] buffer, int count)
    {
        OnWrite?.Invoke(offset);
        if (GoneAfterBytesWritten >= 0 && _written >= GoneAfterBytesWritten) throw new DeviceGoneException("unplugged");
        for (long s = offset; s < offset + count; s += SectorSize)
            if (BadWriteSectors.Contains(s / SectorSize)) throw new IOException("bad sector");
        for (long s = offset; s < offset + count; s += SectorSize)
            if (!SilentSectors.Contains(s / SectorSize))
                Array.Copy(buffer, (int)(s - offset), Data, s, SectorSize);
        _written += count;
    }

    public void ReadExactly(long offset, byte[] buffer, int count)
    {
        for (long s = offset; s < offset + count; s += SectorSize)
            if (BadReadSectors.Contains(s / SectorSize)) throw new IOException("unreadable");
        Array.Copy(Data, offset, buffer, 0, count);
    }

    public void Flush() => Flushes++;
    public void Dispose() { }
}

public class WipeEngineTests
{
    private static WipeOptions Opts(WipePattern p = WipePattern.Zeros, bool verify = true) =>
        new() { Pattern = p, Verify = verify, BlockSize = 4096, Seed = 42 };

    [Fact]
    public void Zero_wipe_overwrites_every_byte_and_verifies()
    {
        var d = new FakeWipeTarget(100);
        var r = WipeEngine.Run(d, Opts(), null, CancellationToken.None);
        Assert.Equal(WipeStatus.Completed, r.Status);
        Assert.True(r.FullyVerified);
        Assert.All(d.Data, b => Assert.Equal(0, b));
        Assert.Equal(d.Length, r.BytesWritten);
        Assert.Equal(d.Length, r.BytesVerified);
        Assert.Equal(1, d.Flushes);
    }

    [Fact]
    public void Random_wipe_writes_the_pattern_the_check_expects()
    {
        var d = new FakeWipeTarget(100, fill: 0);
        var r = WipeEngine.Run(d, Opts(WipePattern.Random), null, CancellationToken.None);
        Assert.True(r.FullyVerified);
        Assert.True(d.Data.Count(b => b == 0) < d.Data.Length / 50, "random data isn't mostly zeros");
        // The pattern depends only on seed and position, not on block size.
        var whole = new byte[d.Data.Length];
        WipeEngine.Fill(whole, whole.Length, 0, Opts(WipePattern.Random));
        Assert.Equal(whole, d.Data);
    }

    [Fact]
    public void Drive_size_that_isnt_a_whole_number_of_blocks_is_fully_covered()
    {
        var d = new FakeWipeTarget(13); // 13 sectors, 4096-byte blocks = 8 sectors per block
        var r = WipeEngine.Run(d, Opts(), null, CancellationToken.None);
        Assert.True(r.FullyVerified);
        Assert.All(d.Data, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Bad_sector_is_retried_alone_and_the_rest_of_its_block_still_gets_erased()
    {
        var d = new FakeWipeTarget(32);
        d.BadWriteSectors.Add(10);
        var r = WipeEngine.Run(d, Opts(), null, CancellationToken.None);
        Assert.Equal(WipeStatus.CompletedWithErrors, r.Status);
        Assert.False(r.FullyVerified);
        Assert.Equal(1, r.WriteErrorSectors);
        Assert.Contains(r.Problems, p => p.Offset == 10 * 512 && p.Kind == "write failed");
        for (int s = 0; s < 32; s++)
        {
            if (s == 10) continue;
            Assert.True(d.Data.Skip(s * 512).Take(512).All(b => b == 0), $"sector {s} erased");
        }
    }

    [Fact]
    public void Sector_that_silently_keeps_old_data_is_caught_by_the_check()
    {
        var d = new FakeWipeTarget(32);
        d.SilentSectors.Add(5);
        var r = WipeEngine.Run(d, Opts(), null, CancellationToken.None);
        Assert.Equal(WipeStatus.CompletedWithErrors, r.Status);
        Assert.Equal(1, r.VerifyMismatchSectors);
        Assert.Contains(r.Problems, p => p.Offset == 5 * 512 && p.Kind == "didn't hold the new data");
    }

    [Fact]
    public void Unreadable_sector_during_the_check_is_counted()
    {
        var d = new FakeWipeTarget(32);
        d.BadReadSectors.Add(20);
        var r = WipeEngine.Run(d, Opts(), null, CancellationToken.None);
        Assert.Equal(WipeStatus.CompletedWithErrors, r.Status);
        Assert.Equal(1, r.VerifyReadErrorSectors);
        Assert.Equal(0, r.VerifyMismatchSectors);
    }

    [Fact]
    public void Unplugged_drive_stops_the_wipe_as_failed()
    {
        var d = new FakeWipeTarget(64) { GoneAfterBytesWritten = 8192 };
        var r = WipeEngine.Run(d, Opts(), null, CancellationToken.None);
        Assert.Equal(WipeStatus.Failed, r.Status);
        Assert.False(r.FullyVerified);
        Assert.Contains("partly erased", r.Message);
    }

    [Fact]
    public void Cancel_stops_the_wipe_and_says_its_only_partly_erased()
    {
        var d = new FakeWipeTarget(64);
        using var cts = new CancellationTokenSource();
        d.OnWrite = off => { if (off >= 8192) cts.Cancel(); };
        var r = WipeEngine.Run(d, Opts(), null, cts.Token);
        Assert.Equal(WipeStatus.Cancelled, r.Status);
        Assert.False(r.FullyVerified);
        Assert.Contains("partly erased", r.Message);
    }

    [Fact]
    public void Without_the_check_the_wipe_is_not_fully_verified()
    {
        var r = WipeEngine.Run(new FakeWipeTarget(16), Opts(verify: false), null, CancellationToken.None);
        Assert.Equal(WipeStatus.Completed, r.Status);
        Assert.False(r.FullyVerified);
    }

    [Fact]
    public void Progress_reaches_the_end_of_both_passes()
    {
        var seen = new List<WipeProgress>();
        var d = new FakeWipeTarget(64);
        WipeEngine.Run(d, Opts(), new SyncProgress<WipeProgress>(seen.Add), CancellationToken.None);
        Assert.Contains(seen, p => p.Phase == "Erasing" && p.BytesDone == d.Length);
        Assert.Contains(seen, p => p.Phase == "Checking" && p.BytesDone == d.Length);
    }

    [Fact]
    public void Report_round_trips_and_spells_out_ssd_limits()
    {
        var d = new FakeWipeTarget(16);
        var o = Opts();
        var r = WipeEngine.Run(d, o, null, CancellationToken.None);
        var disk = new WipeDisk { Model = "Samsung SSD 870", Serial = "S5Y1NX0R123", SizeBytes = d.Length, SectorSize = 512, Bus = "SATA", MediaType = "SSD" };
        var rep = WipeReport.From(r, o, disk, "0.1.0", "NT-0042", "Jason", "");
        Assert.Equal("Overwrite, 1 pass of zeros, every sector read back and checked", rep.Method);
        Assert.Contains(rep.Limitations, l => l.Contains("solid-state"));
        var json = rep.ToJson();
        Assert.Contains("\"tool\": \"Night Tech Wipe\"", json);
        Assert.Contains("\"status\": \"Completed\"", json);
        Assert.Contains("\"fullyVerified\": true", json);
        var back = WipeReport.FromJson("﻿" + json)!;
        Assert.Equal("S5Y1NX0R123", back.Disk.Serial);
        Assert.True(back.FullyVerified);
        Assert.StartsWith("NT-0042-S5Y1NX0R123-wipe-", rep.BaseName());
        Assert.Contains("not a guarantee", rep.ToText());
    }

    [Fact]
    public void Failed_wipe_report_says_not_to_treat_the_drive_as_erased()
    {
        var d = new FakeWipeTarget(16);
        d.BadWriteSectors.Add(3);
        var o = Opts();
        var r = WipeEngine.Run(d, o, null, CancellationToken.None);
        var rep = WipeReport.From(r, o, new WipeDisk { Model = "Old HDD", MediaType = "HDD", Bus = "SATA" }, "0.1.0", "", "", "");
        Assert.False(rep.FullyVerified);
        Assert.Contains("Do not treat", rep.ToText());
        Assert.DoesNotContain(rep.Limitations, l => l.Contains("solid-state"));
    }

    private sealed class SyncProgress<T>(Action<T> act) : IProgress<T>
    {
        public void Report(T value) => act(value);
    }
}
