using System.Globalization;

namespace NightTech.Core.Diagnostics;

/// <summary>
/// Turns raw readings into plain-language findings for the tech and the customer.
/// Every rule only describes what the checks saw; nothing here promises a repair outcome.
/// </summary>
public static class HealthRules
{
    public const int SsdWearWarn = 70, SsdWearProblem = 90;
    public const int DiskTempWarnC = 60;
    public const int BatteryWarnPercent = 70, BatteryProblemPercent = 50;
    public const double LowSpaceWarnPercent = 10, LowSpaceProblemPercent = 5;
    public const long LowSpaceWarnBytes = 10L * 1024 * 1024 * 1024;
    public const int UptimeInfoDays = 14;
    public const int UpdatesWarnDays = 45;
    public const int UnexpectedShutdownsWarn = 3;
    public const int StartupItemsInfo = 15;
    public static readonly DateTime Windows10EndOfSupport = new(2025, 10, 14);

    public static List<Finding> Evaluate(DiagnosticReport r, DateTime now)
    {
        var f = new List<Finding>();
        Disks(r, f);
        Volumes(r, f);
        Memory(r, f);
        Battery(r, f);
        Events(r, f);
        Security(r, f);
        Windows(r, f, now);
        Network(r, f);
        Software(r, f);

        if (!f.Any(x => x.Severity >= Severity.Warning))
            f.Add(new Finding(Severity.Good, "Overall", "No problems found in these checks",
                "Drives, memory, Windows, security and network all looked normal when checked."));

        // Most serious first; keep the rule order within each level.
        return f.Select((x, i) => (x, i)).OrderByDescending(p => p.x.Severity).ThenBy(p => p.i).Select(p => p.x).ToList();
    }

    private static void Disks(DiagnosticReport r, List<Finding> f)
    {
        foreach (var d in r.Disks)
        {
            var name = string.IsNullOrWhiteSpace(d.Name) ? "A drive" : d.Name.Trim();
            if (d.Health.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase))
                f.Add(new Finding(Severity.Problem, "Drives", $"{name} reports it is failing",
                    "Windows marks this drive Unhealthy. Back up important files first and plan to replace the drive. " +
                    "If it clicks, grinds or drops out, stop using it: it should go to a recovery lab."));
            else if (d.Health.Equals("Warning", StringComparison.OrdinalIgnoreCase))
                f.Add(new Finding(Severity.Warning, "Drives", $"{name} reports a warning",
                    "Windows reports a health warning on this drive. Back up important files and check it again."));

            if (d.ReadErrorsUncorrected is > 0)
                f.Add(new Finding(Severity.Problem, "Drives", $"{name} has read errors it couldn't fix",
                    $"{d.ReadErrorsUncorrected} uncorrected read error(s). Some data on this drive may already be unreadable. Back up first."));

            if (d.WearPercent is { } wear)
            {
                if (wear >= SsdWearProblem)
                    f.Add(new Finding(Severity.Problem, "Drives", $"{name} is nearly worn out",
                        $"The SSD reports {wear}% of its rated life used. Plan to replace it."));
                else if (wear >= SsdWearWarn)
                    f.Add(new Finding(Severity.Warning, "Drives", $"{name} is getting worn",
                        $"The SSD reports {wear}% of its rated life used."));
            }

            if (d.TemperatureC is { } t && t >= DiskTempWarnC)
                f.Add(new Finding(Severity.Warning, "Drives", $"{name} is running hot",
                    $"{t} °C. Check airflow and fans; heat shortens drive life."));
        }
    }

    private static void Volumes(DiagnosticReport r, List<Finding> f)
    {
        foreach (var v in r.Volumes.Where(v => v.SizeBytes > 0))
        {
            var label = v.IsSystem ? $"{v.Name} (Windows drive)" : v.Name;
            var free = Format.Bytes(v.FreeBytes);
            if (v.IsSystem && v.FreePercent < LowSpaceProblemPercent)
                f.Add(new Finding(Severity.Problem, "Storage", $"{label} is almost full",
                    $"Only {free} free ({v.FreePercent:0}%). Windows slows down and updates fail when the system drive is this full."));
            else if (v.FreePercent < LowSpaceWarnPercent || (v.IsSystem && v.FreeBytes < LowSpaceWarnBytes))
                f.Add(new Finding(Severity.Warning, "Storage", $"{label} is low on space",
                    $"{free} free ({v.FreePercent:0}%)."));

            if (v.IsSystem && v.BitLocker.Equals("On", StringComparison.OrdinalIgnoreCase))
                f.Add(new Finding(Severity.Info, "Security", $"BitLocker is on for {v.Name}",
                    "Get the BitLocker recovery key (usually in the customer's Microsoft account) before any drive, BIOS or motherboard work."));
        }
    }

    private static void Memory(DiagnosticReport r, List<Finding> f)
    {
        if (r.Computer.RamBytes is not { } ram) return;
        var gb = ram / (1024.0 * 1024 * 1024);
        if (gb < 3.5)
            f.Add(new Finding(Severity.Warning, "Memory", $"Only {Format.Bytes(ram)} of memory",
                "Windows 10 and 11 run slowly with less than 4 GB. A memory upgrade would help if the computer supports it."));
        else if (gb < 7.5)
            f.Add(new Finding(Severity.Info, "Memory", $"{Format.Bytes(ram)} of memory",
                "Fine for light use. 8 GB or more helps with many browser tabs and newer programs."));
    }

    private static void Battery(DiagnosticReport r, List<Finding> f)
    {
        if (r.Battery?.HealthPercent is not { } h) return;
        var cycles = r.Battery.CycleCount is > 0 ? $", {r.Battery.CycleCount} charge cycles" : "";
        if (h < BatteryProblemPercent)
            f.Add(new Finding(Severity.Problem, "Battery", "Battery is badly worn",
                $"It holds about {h}% of its original charge{cycles}. A replacement battery would bring back normal run time."));
        else if (h < BatteryWarnPercent)
            f.Add(new Finding(Severity.Warning, "Battery", "Battery is worn",
                $"It holds about {h}% of its original charge{cycles}."));
        else
            f.Add(new Finding(Severity.Good, "Battery", "Battery holds a good charge",
                $"About {h}% of its original capacity{cycles}."));
    }

    private static void Events(DiagnosticReport r, List<Finding> f)
    {
        var e = r.Events;
        if (e.DiskErrors is > 0)
            f.Add(new Finding(Severity.Problem, "Drives", "Windows logged disk errors",
                $"{e.DiskErrors} disk error(s) in the last {e.Days} days. This often means a failing drive or cable. Back up first."));
        if (e.BlueScreens is > 0)
            f.Add(new Finding(Severity.Warning, "Stability", "Blue screen crashes",
                $"{e.BlueScreens} crash(es) in the last {e.Days} days."));
        if (e.UnexpectedShutdowns is { } u && u >= UnexpectedShutdownsWarn)
            f.Add(new Finding(Severity.Warning, "Stability", "Unexpected shutdowns",
                $"The computer lost power or reset without shutting down {u} times in the last {e.Days} days. " +
                "Common causes: power supply, overheating, battery, or a crash."));
    }

    private static void Security(DiagnosticReport r, List<Finding> f)
    {
        if (r.Antivirus.Count == 0) return; // Security Center isn't available (for example on Windows Server)
        var on = r.Antivirus.Where(a => a.Enabled).ToList();
        if (on.Count == 0)
            f.Add(new Finding(Severity.Problem, "Security", "No antivirus is turned on",
                "Windows Security Center reports no active antivirus: " + string.Join(", ", r.Antivirus.Select(a => a.Name)) + "."));
        else if (on.Any(a => !a.UpToDate))
            f.Add(new Finding(Severity.Warning, "Security", "Antivirus is out of date",
                string.Join(", ", on.Where(a => !a.UpToDate).Select(a => a.Name)) + " reports old virus definitions."));
    }

    private static void Windows(DiagnosticReport r, List<Finding> f, DateTime now)
    {
        var c = r.Computer;
        if (c.OsName.Contains("Windows 10", StringComparison.OrdinalIgnoreCase) && now >= Windows10EndOfSupport)
            f.Add(new Finding(Severity.Warning, "Windows", "Windows 10 no longer gets regular security updates",
                "Microsoft ended Windows 10 support on October 14, 2025, unless the PC is enrolled in Extended Security Updates. " +
                "Check whether it can upgrade to Windows 11."));

        if (c.LastUpdateInstalled is { } upd && (now - upd).TotalDays > UpdatesWarnDays)
            f.Add(new Finding(Severity.Warning, "Windows", "Windows updates are behind",
                $"The newest installed update is from {upd.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}."));

        if (c.LastBoot is { } boot && (now - boot).TotalDays > UptimeInfoDays)
            f.Add(new Finding(Severity.Info, "Windows", "Not restarted in a while",
                $"Running for {(int)(now - boot).TotalDays} days. A restart finishes updates and clears slowdowns."));
    }

    private static void Network(DiagnosticReport r, List<Finding> f)
    {
        var n = r.Network;
        if (n.InternetReachable == false)
            f.Add(new Finding(Severity.Problem, "Network", "No internet connection",
                n.GatewayReachable == true
                    ? "The router answers, but the internet doesn't. The problem is likely the modem or internet provider."
                    : "The router didn't answer either. Check the Wi-Fi or cable connection."));
        else if (n.DnsWorks == false)
            f.Add(new Finding(Severity.Warning, "Network", "Website names don't look up",
                "The internet answers, but name lookups (DNS) failed. Check the DNS settings or the router."));
    }

    private static void Software(DiagnosticReport r, List<Finding> f)
    {
        if (r.Startup.Count > StartupItemsInfo)
            f.Add(new Finding(Severity.Info, "Software", $"{r.Startup.Count} programs start with Windows",
                "Turning off ones the customer doesn't need speeds up startup."));
    }
}
