using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NightTech.Core.Cleanup;

namespace NightTech.MacCleanup;

public sealed record Run(int Code, string Out);

/// <summary>macOS's own command-line tools, always by full path.</summary>
public static class Sh
{
    public static Run Exec(string file, string args, int timeoutSeconds = 120, string? stdin = null)
    {
        if (!File.Exists(file)) return new Run(-2, $"{file} isn't on this Mac.");
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin is not null };
        using var p = Process.Start(psi)!;
        var sb = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (stdin is not null) { p.StandardInput.Write(stdin); p.StandardInput.Close(); }
        if (!p.WaitForExit(timeoutSeconds * 1000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return new Run(-1, sb + "(timed out)"); }
        Thread.Sleep(100);
        lock (sb) return new Run(p.ExitCode, sb.ToString());
    }

    /// <summary>A command's printed text, trimmed ("" if it failed).</summary>
    public static string Text(string file, string args, int timeoutSeconds = 60) { var r = Exec(file, args, timeoutSeconds); return r.Code == 0 ? r.Out.Trim() : ""; }

    [DllImport("libc")] private static extern uint geteuid();
    public static bool IsRoot => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux() ? geteuid() == 0 : false;
}

/// <summary>This Mac: people, measurements and security settings.</summary>
public static class Machine
{
    /// <summary>Home folders of real people (/Users/*, not Shared or hidden accounts).</summary>
    public static List<string> Homes()
    {
        var list = new List<string>();
        try
        {
            foreach (var d in Directory.GetDirectories("/Users"))
            {
                var n = Path.GetFileName(d);
                if (n.StartsWith('.') || n is "Shared" or "Guest" or "Deleted Users" || Junk.IsLink(d)) continue;
                if (Directory.Exists(Path.Combine(d, "Library"))) list.Add(d);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var me = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (list.Count == 0 && Directory.Exists(me)) list.Add(me);
        return list;
    }

    public static string MacOS()
    {
        var name = Sh.Text("/usr/bin/sw_vers", "-productName");
        var ver = Sh.Text("/usr/bin/sw_vers", "-productVersion");
        return $"{(name.Length > 0 ? name : "macOS")} {ver}".Trim();
    }

    public static string Model() => Sh.Text("/usr/sbin/sysctl", "-n hw.model");
    public static string Chip() { var c = Sh.Text("/usr/sbin/sysctl", "-n machdep.cpu.brand_string"); return c.Length > 0 ? c : RuntimeInformation.OSArchitecture.ToString(); }

    public static Snapshot Measure(int loginItemsOn)
    {
        var s = new Snapshot { Taken = DateTime.Now, StartupItemsOn = loginItemsOn, System = MacOS() };
        try { var d = new DriveInfo("/System/Volumes/Data"); s.FreeBytes = d.AvailableFreeSpace; s.TotalBytes = d.TotalSize; }
        catch (Exception e) when (e is IOException or ArgumentException) { try { var d = new DriveInfo("/"); s.FreeBytes = d.AvailableFreeSpace; s.TotalBytes = d.TotalSize; } catch (IOException) { } }
        if (long.TryParse(Sh.Text("/usr/sbin/sysctl", "-n hw.memsize"), out var mem)) s.RamGb = Math.Round(mem / 1073741824.0, 1);
        s.Processes = Sh.Text("/bin/ps", "-A -o pid=").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        var mp = Sh.Text("/usr/bin/memory_pressure", "-Q");
        var m = System.Text.RegularExpressions.Regex.Match(mp, @"free percentage:\s*(\d+)%");
        if (m.Success) s.MemoryUsedPercent = 100 - int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var info = Sh.Text("/usr/sbin/diskutil", "info /");
        s.DriveKind = info.Contains("Solid State:               Yes", StringComparison.Ordinal) || info.Contains("Solid State: Yes", StringComparison.Ordinal) ? "SSD"
                    : info.Contains("Solid State:               No", StringComparison.Ordinal) ? "HDD" : "";
        s.Protection = Sh.Text("/usr/sbin/spctl", "--status").Contains("enabled", StringComparison.OrdinalIgnoreCase);
        return s;
    }

    public sealed record Check(string Name, bool? Ok, string Detail);

    /// <summary>Security settings, read only.</summary>
    public static List<Check> Security(bool checkUpdates)
    {
        var list = new List<Check>();
        var fv = Sh.Text("/usr/bin/fdesetup", "status");
        list.Add(new("FileVault (disk encryption)", fv.Contains("On", StringComparison.Ordinal) ? true : fv.Length > 0 ? false : null,
            fv.Length > 0 ? fv.Split('\n')[0] : "couldn't check"));
        var gk = Sh.Text("/usr/sbin/spctl", "--status");
        list.Add(new("Gatekeeper (blocks unknown apps)", gk.Contains("enabled") ? true : gk.Length > 0 ? false : null, gk.Length > 0 ? gk : "couldn't check"));
        var sip = Sh.Text("/usr/bin/csrutil", "status");
        list.Add(new("System Integrity Protection", sip.Contains("enabled.") ? true : sip.Contains("disabled") ? false : null, sip.Length > 0 ? sip : "couldn't check"));
        var fw = Sh.Text("/usr/libexec/ApplicationFirewall/socketfilterfw", "--getglobalstate");
        list.Add(new("Firewall", fw.Contains("enabled") ? true : fw.Contains("disabled") ? false : null, fw.Length > 0 ? fw : "couldn't check"));
        var tm = Sh.Text("/usr/bin/tmutil", "latestbackup");
        list.Add(new("Time Machine backup", tm.Length > 0 ? true : false, tm.Length > 0 ? "latest: " + Path.GetFileName(tm) : "no Time Machine backup found"));
        var auto = Sh.Text("/usr/bin/defaults", "read /Library/Preferences/com.apple.SoftwareUpdate AutomaticallyInstallMacOSUpdates");
        list.Add(new("Automatic macOS updates", auto == "1" ? true : auto == "0" ? false : null, auto == "1" ? "on" : auto == "0" ? "off" : "not set (macOS default)"));
        if (checkUpdates)
        {
            var su = Sh.Exec("/usr/sbin/softwareupdate", "-l", 180);
            var pending = su.Out.Split('\n').Count(l => l.TrimStart().StartsWith("* Label:", StringComparison.Ordinal));
            list.Add(new("Pending updates", pending == 0, pending == 0 ? "none" : $"{pending} update(s) waiting: install them in System Settings > General > Software Update"));
        }
        return list;
    }

    /// <summary>A proxy set for the network (a common adware trick). Null if none.</summary>
    public static string? Proxy()
    {
        var p = Sh.Text("/usr/sbin/scutil", "--proxy");
        if (p.Contains("ProxyAutoConfigEnable : 1")) return "Web traffic follows an automatic proxy script. Check System Settings > Network > Details > Proxies.";
        if (p.Contains("HTTPEnable : 1") || p.Contains("HTTPSEnable : 1")) return "Web traffic goes through a proxy. Check System Settings > Network > Details > Proxies.";
        return null;
    }

    /// <summary>Configuration profiles: adware sometimes installs one to lock the browser's search engine.</summary>
    public static List<string> Profiles()
    {
        var r = Sh.Exec("/usr/bin/profiles", "list", 30);
        return r.Out.Split('\n').Where(l => l.Contains("profileIdentifier", StringComparison.Ordinal)).Select(l => l.Split(':').Last().Trim()).ToList();
    }

    public static (bool Ok, string Summary) VerifyDisk()
    {
        var r = Sh.Exec("/usr/sbin/diskutil", "verifyVolume /", 1800);
        if (r.Code == 0 && r.Out.Contains("appears to be OK", StringComparison.OrdinalIgnoreCase)) return (true, "The startup disk appears to be OK.");
        return r.Code == 0 ? (true, "Finished.") : (false, "Problems were found. Back up, then run First Aid from macOS Recovery.");
    }

    public static (bool Ok, string Summary) FlushDns()
    {
        var a = Sh.Exec("/usr/bin/dscacheutil", "-flushcache", 30);
        var b = Sh.Exec("/usr/bin/killall", "-HUP mDNSResponder", 30);
        return (a.Code == 0 && b.Code == 0, a.Code == 0 && b.Code == 0 ? "Saved web addresses cleared." : "Needs administrator rights (run with sudo).");
    }
}
