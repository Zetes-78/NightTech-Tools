using NightTech.Core.Diagnostics;

namespace NightTech.Core.Tests;

public class DiagnosticsTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 18, 30, 0);
    private const long GB = 1024L * 1024 * 1024;

    private static DiagnosticReport Healthy() => new()
    {
        ToolVersion = "0.1.0",
        Ticket = "NT-0012",
        Customer = "Pat Example",
        Started = Now,
        Finished = Now.AddMinutes(1),
        Computer = new ComputerInfo
        {
            Name = "DESKTOP-ABC123", Manufacturer = "Dell Inc.", Model = "Inspiron 15", OsName = "Microsoft Windows 11 Home",
            RamBytes = 16 * GB, LastBoot = Now.AddDays(-2), LastUpdateInstalled = Now.AddDays(-10),
        },
        Disks = { new DiskRecord { Name = "Samsung SSD 980", MediaType = "SSD", BusType = "NVMe", Health = "Healthy", WearPercent = 3, TemperatureC = 38, SizeBytes = 500 * GB } },
        Volumes = { new VolumeRecord { Name = "C:\\", IsSystem = true, SizeBytes = 475 * GB, FreeBytes = 200 * GB, FileSystem = "NTFS" } },
        Network = new NetworkRecord { GatewayReachable = true, InternetReachable = true, DnsWorks = true },
        Antivirus = { AntivirusProduct.FromProductState("Microsoft Defender Antivirus", 397568) },
        Events = new EventSummary { UnexpectedShutdowns = 0, BlueScreens = 0, DiskErrors = 0 },
    };

    private static List<Finding> Eval(DiagnosticReport r) => HealthRules.Evaluate(r, Now);

    [Fact]
    public void Healthy_computer_gets_a_single_good_result()
    {
        var f = Eval(Healthy());
        Assert.DoesNotContain(f, x => x.Severity >= Severity.Warning);
        Assert.Contains(f, x => x.Title == "No problems found in these checks");
    }

    [Theory]
    [InlineData(397568u, true, true)]   // Defender on, up to date
    [InlineData(393472u, false, true)]  // off, up to date
    [InlineData(397584u, true, false)]  // on, out of date
    public void Security_center_state_is_decoded(uint state, bool enabled, bool upToDate)
    {
        var a = AntivirusProduct.FromProductState("AV", state);
        Assert.Equal(enabled, a.Enabled);
        Assert.Equal(upToDate, a.UpToDate);
    }

    [Fact]
    public void Failing_drive_is_a_problem_and_sorted_first()
    {
        var r = Healthy();
        r.Disks[0].Health = "Unhealthy";
        r.Computer.LastBoot = Now.AddDays(-30); // adds an Info finding that must sort below
        var f = Eval(r);
        Assert.Equal(Severity.Problem, f[0].Severity);
        Assert.Contains("failing", f[0].Title);
        Assert.Contains(f, x => x.Severity == Severity.Info && x.Title == "No full restart in a while" && x.Detail.Contains("Fast Startup"));
    }

    [Theory]
    [InlineData(69, null)]
    [InlineData(70, Severity.Warning)]
    [InlineData(95, Severity.Problem)]
    public void Ssd_wear_thresholds(int wear, Severity? expected)
    {
        var r = Healthy();
        r.Disks[0].WearPercent = wear;
        var hit = Eval(r).FirstOrDefault(x => x.Area == "Drives" && x.Title.Contains("worn"));
        Assert.Equal(expected, hit?.Severity);
    }

    [Fact]
    public void Uncorrected_read_errors_and_logged_disk_errors_are_problems()
    {
        var r = Healthy();
        r.Disks[0].ReadErrorsUncorrected = 4;
        r.Events.DiskErrors = 2;
        var f = Eval(r);
        Assert.Contains(f, x => x.Severity == Severity.Problem && x.Title.Contains("read errors"));
        Assert.Contains(f, x => x.Severity == Severity.Problem && x.Title == "Windows found bad blocks on a drive");
    }

    [Fact]
    public void Nearly_full_windows_drive_is_a_problem_other_drives_a_warning()
    {
        var r = Healthy();
        r.Volumes[0].FreeBytes = 10 * GB; // about 2%
        r.Volumes.Add(new VolumeRecord { Name = "D:\\", SizeBytes = 100 * GB, FreeBytes = 5 * GB });
        var f = Eval(r);
        Assert.Contains(f, x => x.Severity == Severity.Problem && x.Title.StartsWith("C:\\ (Windows drive) is almost full"));
        Assert.Contains(f, x => x.Severity == Severity.Warning && x.Title == "D:\\ is low on space");
    }

    [Theory]
    [InlineData(40000, 50000, Severity.Good)]     // 80%
    [InlineData(30000, 50000, Severity.Warning)]  // 60%
    [InlineData(20000, 50000, Severity.Problem)]  // 40%
    public void Battery_wear(long full, long design, Severity expectedIfWorn)
    {
        var r = Healthy();
        r.Battery = new BatteryRecord { DesignCapacityMWh = design, FullChargeCapacityMWh = full, CycleCount = 300 };
        var f = Eval(r).Single(x => x.Area == "Battery");
        var pct = full * 100 / design;
        if (pct >= HealthRules.BatteryWarnPercent) Assert.Equal(Severity.Good, f.Severity);
        else Assert.Equal(expectedIfWorn, f.Severity);
        Assert.Contains(pct + "%", f.Detail);
    }

    [Fact]
    public void No_active_antivirus_is_a_problem()
    {
        var r = Healthy();
        r.Antivirus = new() { AntivirusProduct.FromProductState("Microsoft Defender Antivirus", 393472) };
        Assert.Contains(Eval(r), x => x.Severity == Severity.Problem && x.Title == "No antivirus is turned on");
    }

    [Fact]
    public void Windows_10_after_end_of_support_is_flagged()
    {
        var r = Healthy();
        r.Computer.OsName = "Microsoft Windows 10 Pro";
        Assert.Contains(Eval(r), x => x.Area == "Windows" && x.Title.StartsWith("Windows 10 no longer"));
    }

    [Fact]
    public void No_internet_but_router_answers_points_at_the_provider()
    {
        var r = Healthy();
        r.Network.Adapters.Add(new NetAdapter { Name = "Wi-Fi", Gateways = { "192.168.1.1" } });
        r.Network.InternetReachable = false;
        r.Network.DnsWorks = false;
        var f = Eval(r).Single(x => x.Area == "Network");
        Assert.Equal(Severity.Problem, f.Severity);
        Assert.Contains("internet provider", f.Detail);
    }

    [Fact]
    public void Disk_retries_alone_are_a_warning_and_name_the_drive()
    {
        var r = Healthy();
        r.Events.DiskWarnings = 3;
        r.Events.DiskEventDrives = new() { "Samsung SSD 980" };
        var f = Eval(r).Single(x => x.Area == "Drives");
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains("Samsung SSD 980", f.Detail);
    }

    [Theory]
    [InlineData("SSD", "NVMe", 65, false)]
    [InlineData("SSD", "NVMe", 72, true)]
    [InlineData("HDD", "SATA", 56, true)]
    [InlineData("HDD", "SATA", 50, false)]
    public void Drive_heat_limit_depends_on_the_kind_of_drive(string media, string bus, int temp, bool hot)
    {
        var r = Healthy();
        r.Disks[0].MediaType = media;
        r.Disks[0].BusType = bus;
        r.Disks[0].TemperatureC = temp;
        Assert.Equal(hot, Eval(r).Any(x => x.Title.EndsWith("is running hot")));
    }

    [Fact]
    public void Windows_10_ltsc_is_still_supported()
    {
        var r = Healthy();
        r.Computer.OsName = "Microsoft Windows 10 Enterprise LTSC";
        Assert.DoesNotContain(Eval(r), x => x.Title.StartsWith("Windows 10 no longer"));
    }

    [Fact]
    public void No_router_means_not_connected()
    {
        var r = Healthy();
        r.Network = new NetworkRecord { InternetReachable = false, DnsWorks = false };
        Assert.Contains(Eval(r), x => x.Severity == Severity.Problem && x.Title == "Not connected to a network");
    }

    [Fact]
    public void Blocked_tests_with_working_dns_are_only_a_warning()
    {
        var r = Healthy();
        r.Network.Adapters.Add(new NetAdapter { Name = "Ethernet", Gateways = { "10.0.0.1" } });
        r.Network.InternetReachable = false;
        r.Network.DnsWorks = true;
        var f = Eval(r).Single(x => x.Area == "Network");
        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Equal("Internet tests were blocked", f.Title);
    }

    [Fact]
    public void Bitlocker_suspended_still_reminds_about_the_key()
    {
        var r = Healthy();
        r.RanAsAdministrator = true;
        r.Volumes[0].BitLocker = "Encrypted, protection suspended";
        var f = Eval(r).Single(x => x.Title.StartsWith("BitLocker is on"));
        Assert.Contains("suspended", f.Detail);
    }

    [Fact]
    public void Bitlocker_not_checked_without_admin_says_so()
    {
        var r = Healthy();
        r.RanAsAdministrator = false;
        Assert.Contains(Eval(r), x => x.Title == "BitLocker wasn't checked");
        r.RanAsAdministrator = true;
        r.Volumes[0].BitLocker = "Off";
        Assert.DoesNotContain(Eval(r), x => x.Title.StartsWith("BitLocker"));
    }

    [Fact]
    public void Bitlocker_on_reminds_the_tech_to_get_the_key()
    {
        var r = Healthy();
        r.Volumes[0].BitLocker = "On";
        Assert.Contains(Eval(r), x => x.Title.StartsWith("BitLocker is on") && x.Detail.Contains("recovery key"));
    }

    [Fact]
    public void Html_report_escapes_everything_it_shows()
    {
        var r = Healthy();
        r.Customer = "<script>alert(1)</script>";
        r.Computer.Model = "Model & \"Co\"";
        r.Findings = HealthRules.Evaluate(r, Now);
        var html = ReportWriter.ToHtml(r);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Model &amp; &quot;Co&quot;", html);
        Assert.Contains(ReportWriter.Disclaimer, html);
    }

    [Fact]
    public void Reports_are_saved_and_json_round_trips()
    {
        var r = Healthy();
        r.Findings = HealthRules.Evaluate(r, Now);
        var dir = Path.Combine(Path.GetTempPath(), "ntfk-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = ReportWriter.Save(r, dir);
            Assert.EndsWith("NT-0012-DESKTOP-ABC123-20261101-183000.html", files.Html);
            Assert.True(File.Exists(files.Text));
            var json = File.ReadAllText(files.Json);
            Assert.Contains("\"tool\": \"Night Tech Field Kit\"", json);
            Assert.Contains("\"severity\": \"Good\"", json);
            var back = ReportWriter.FromJson(json)!;
            Assert.Equal("DESKTOP-ABC123", back.Computer.Name);
            Assert.Equal(r.Findings.Count, back.Findings.Count);
            Assert.Contains("No problems found", File.ReadAllText(files.Text));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Theory]
    [InlineData("NT-0012 DESKTOP/ABC:1", "NT-0012-DESKTOPABC1")]
    [InlineData("  ..  ", "computer")]
    [InlineData("Pat's PC.local", "Pats-PC-local")]
    public void Safe_file_names(string input, string expected) => Assert.Equal(expected, Format.SafeFileName(input));

    [Theory]
    [InlineData(512L, "512 bytes")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(500L * 1024 * 1024 * 1024, "500 GB")]
    [InlineData(16L * 1024 * 1024 * 1024, "16 GB")]
    public void Byte_sizes(long bytes, string expected) => Assert.Equal(expected, Format.Bytes(bytes));
}
