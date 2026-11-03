using NightTech.Core.BurnIn;
using NightTech.Core.Diagnostics;

namespace NightTech.Core.Tests;

public class BurnInTests
{
    [Fact]
    public void Cpu_stress_gets_the_same_answer_every_time()
    {
        var cpu = new CpuStress();
        Assert.True(cpu.SelfCheck());
        cpu.Run(2, TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True(cpu.Iterations > 0);
        Assert.Equal(0, cpu.Errors);
    }

    [Fact]
    public void Memory_test_passes_good_memory_and_catches_a_flipped_bit()
    {
        var r = MemoryTest.Run(2 * MemoryTest.BlockBytes, 1, CancellationToken.None);
        Assert.Equal(1, r.PassesDone);
        Assert.Equal(0, r.Errors);
        Assert.Equal(2 * MemoryTest.BlockBytes, r.BytesTested);

        for (int pattern = 0; pattern < MemoryTest.Patterns; pattern++)
        {
            var a = new long[4096];
            MemoryTest.FillOnly(a, 3, pattern, 12345);
            Assert.Equal(0, MemoryTest.VerifyOnly(a, 3, pattern, 12345).Errors);
            MemoryTest.Corrupt(a, 777);
            var bad = MemoryTest.VerifyOnly(a, 3, pattern, 12345);
            Assert.Equal(1, bad.Errors);
            Assert.Contains("offset 6,216", bad.ErrorSamples[0]);
        }
    }

    [Fact]
    public void Memory_test_always_leaves_room_for_windows()
    {
        const long GB = 1024L * 1024 * 1024;
        Assert.Equal(0, MemoryTest.SafeBytes(GB));                  // 1 GB free: too little to test safely
        Assert.True(MemoryTest.SafeBytes(8 * GB) <= 6 * GB);        // leaves at least a quarter
        Assert.True(MemoryTest.SafeBytes(4 * GB) <= (long)(2.5 * GB)); // and at least 1.5 GB
        Assert.Equal(0, MemoryTest.SafeBytes(8 * GB) % MemoryTest.BlockBytes);
    }

    [Fact]
    public void Drive_test_writes_reads_back_and_removes_its_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ntburn-" + Guid.NewGuid().ToString("N"));
        try
        {
            var r = DriveTest.Run(dir, 3L * DriveTest.Chunk, CancellationToken.None);
            Assert.Equal(3L * DriveTest.Chunk, r.BytesTested);
            Assert.Equal(0, r.Errors);
            Assert.True(r.WriteMBps > 0 && r.ReadMBps > 0);
            Assert.Empty(Directory.GetFileSystemEntries(dir));   // its random folder and file are gone
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Drive_test_never_fills_the_drive()
    {
        const long GB = 1024L * 1024 * 1024;
        Assert.Equal(0, DriveTest.SafeBytes(5 * GB));               // would leave under 5 GB
        Assert.Equal(2 * GB, DriveTest.SafeBytes(500 * GB));         // default 2 GB
        Assert.True(DriveTest.SafeBytes(20 * GB) <= 2 * GB);         // at most a tenth of free space
    }

    [Fact]
    public void Grading_fails_on_errors_and_passes_a_clean_run()
    {
        var good = new BurnInReport
        {
            Machine = new MachineInfo { RamBytes = 16L << 30, Disks = { new DiskRecord { Name = "Samsung SSD", Health = "Healthy", WearPercent = 5 } }, Battery = new BatteryRecord { DesignCapacityMWh = 50000, FullChargeCapacityMWh = 45000 } },
            Cpu = new CpuResult { Threads = 8, Iterations = 1000, Ran = TimeSpan.FromMinutes(30), MaxTempC = 78, MinTempC = 45, PerfStartPercent = 140, PerfEndPercent = 95 },
            Started = new DateTime(2026, 11, 3, 18, 0, 0), Finished = new DateTime(2026, 11, 3, 18, 30, 0), Planned = TimeSpan.FromMinutes(30),
            CpuSelected = true, MemorySelected = true, DrivesSelected = 1,
            Memory = new MemoryResult { BytesTested = 8L << 30, PassesDone = 2 },
            Drives = { new DriveResult { Folder = @"C:\", BytesTested = 2L << 30, WriteMBps = 400, ReadMBps = 500, Uncached = true } },
            EventLogChecked = true,
        };
        good.Grade();
        Assert.Equal(Verdict.Pass, good.Overall);

        good.Cpu.MaxTempC = 93; good.Grade();
        Assert.Equal(Verdict.Warning, good.Overall);

        good.Memory.Errors = 3; good.Memory.ErrorSamples.Add("block 1, offset 8"); good.Grade();
        Assert.Equal(Verdict.Fail, good.Overall);
        Assert.Contains(good.Checks, c => c.Area == "Memory" && c.Result == Verdict.Fail);

        var bad = new BurnInReport { Cpu = new CpuResult { Iterations = 10, Errors = 1, FirstError = "x" }, HardwareErrorsDuring = 2, EventLogChecked = true, CpuSelected = true };
        bad.Grade();
        Assert.Equal(Verdict.Fail, bad.Overall);
        Assert.Equal(2, bad.Checks.Count(c => c.Result == Verdict.Fail));

        Assert.Equal(Verdict.NotRun, new BurnInReport().Overall);
    }

    [Fact]
    public void Throttling_and_a_worn_battery_are_noted_not_hidden()
    {
        var r = new BurnInReport
        {
            Cpu = new CpuResult { Iterations = 5, Ran = TimeSpan.FromMinutes(10), PerfStartPercent = 100, PerfEndPercent = 55 }, CpuSelected = true,
            Machine = new MachineInfo { Battery = new BatteryRecord { DesignCapacityMWh = 50000, FullChargeCapacityMWh = 20000 } },
        };
        r.Grade();
        Assert.Contains(r.Checks, c => c.Title.Contains("slowed down") && c.Result == Verdict.Warning);
        Assert.Contains(r.Checks, c => c.Area == "Battery" && c.Result == Verdict.Fail);
    }

    [Fact]
    public void Sheet_round_trips_and_is_safe_html()
    {
        var r = new BurnInReport { Customer = "<script>x</script>", Ticket = "1042", Machine = new MachineInfo { Name = "DESK-1" }, Cpu = new CpuResult { Iterations = 1, Threads = 1 }, Started = new DateTime(2026, 11, 3, 18, 0, 0), Finished = new DateTime(2026, 11, 3, 19, 0, 0), Planned = TimeSpan.FromHours(1) };
        r.Grade();
        var back = BurnInReport.FromJson(r.ToJson())!;
        Assert.Equal("1042", back.Ticket);
        Assert.Equal(TimeSpan.FromHours(1), back.Planned);
        Assert.Equal(r.Checks.Count, back.Checks.Count);
        Assert.DoesNotContain("<script>x", r.ToHtml());
        Assert.Contains("BURN-IN TEST", r.ToText());
        var dir = Path.Combine(Path.GetTempPath(), "ntburnrep-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (json, text, html) = r.Save(dir);
            Assert.True(File.Exists(json) && File.Exists(text) && File.Exists(html));
            Assert.StartsWith("burnin-1042-", Path.GetFileName(json));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Stopping_early_or_running_nothing_is_never_a_pass()
    {
        var healthy = new MachineInfo { Disks = { new DiskRecord { Name = "SSD", Health = "Healthy" } } };
        var none = new BurnInReport { Machine = healthy, CpuSelected = true, MemorySelected = true, EventLogChecked = true, Stopped = true,
            Started = DateTime.Today, Finished = DateTime.Today.AddSeconds(5), Planned = TimeSpan.FromHours(4) };
        none.Grade();
        Assert.Equal(Verdict.NotRun, none.Overall);                  // health checks alone don't make a pass

        var shortRun = new BurnInReport { Machine = healthy, CpuSelected = true, Stopped = true, EventLogChecked = true,
            Cpu = new CpuResult { Iterations = 500, Threads = 8, Ran = TimeSpan.FromSeconds(20) },
            Started = DateTime.Today, Finished = DateTime.Today.AddSeconds(20), Planned = TimeSpan.FromHours(4) };
        shortRun.Grade();
        Assert.Equal(Verdict.Warning, shortRun.Overall);
        Assert.Contains(shortRun.Checks, c => c.Area == "Length");

        var missingDrive = new BurnInReport { CpuSelected = true, DrivesSelected = 2, Cpu = new CpuResult { Iterations = 9, Ran = TimeSpan.FromHours(1) },
            Drives = { new DriveResult { Folder = "C:\\", BytesTested = 0, Note = "Couldn't test" } },
            Started = DateTime.Today, Finished = DateTime.Today.AddHours(1), Planned = TimeSpan.FromHours(1) };
        missingDrive.Grade();
        Assert.Equal(Verdict.Warning, missingDrive.Overall);
    }

    [Fact]
    public void Corrected_errors_and_a_frozen_sensor_do_not_fail_a_good_machine()
    {
        var r = new BurnInReport { CpuSelected = true, EventLogChecked = true, CorrectedErrorsDuring = 40,
            Cpu = new CpuResult { Iterations = 9, Ran = TimeSpan.FromHours(1), MaxTempC = 27.8, MinTempC = 27.8, PerfEndPercent = 98 },
            Started = DateTime.Today, Finished = DateTime.Today.AddHours(1), Planned = TimeSpan.FromHours(1) };
        r.Grade();
        Assert.Equal(Verdict.Warning, r.Overall);
        Assert.DoesNotContain(r.Checks, c => c.Title.Contains("27"));       // frozen sensor ignored
        Assert.Contains(r.Checks, c => c.Title.Contains("kept its rated speed"));
    }

    [Fact]
    public void Drive_test_never_writes_through_a_planted_link_and_cleans_up_leftovers()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "ntburnlink-" + Guid.NewGuid().ToString("N"));
        try
        {
            var victim = Path.Combine(root, "victim.txt");
            Directory.CreateDirectory(root);
            File.WriteAllText(victim, "keep me");
            var linked = Path.Combine(root, "linked");
            Directory.CreateSymbolicLink(linked, root);
            var r = DriveTest.Run(linked, DriveTest.Chunk, CancellationToken.None);
            Assert.Equal(0, r.BytesTested);
            Assert.Equal("keep me", File.ReadAllText(victim));

            var stale = Path.Combine(root, DriveTest.Prefix + "old");
            Directory.CreateDirectory(stale);
            File.WriteAllBytes(Path.Combine(stale, DriveTest.Prefix + "test.tmp"), new byte[10]);
            File.WriteAllText(Path.Combine(stale, "not-ours.txt"), "x");
            Assert.Equal(1, DriveTest.RemoveLeftovers(root));
            Assert.True(File.Exists(Path.Combine(stale, "not-ours.txt")));   // only our own files go
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
