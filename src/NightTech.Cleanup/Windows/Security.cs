using System.Text.Json;
using Microsoft.Win32;

namespace NightTech.Cleanup.Windows;

public sealed record DefenderStatus(bool Available, bool Active, bool RealTime, int SignatureAgeDays, string Other, string Message);

/// <summary>Microsoft Defender (built into Windows): status, signature update, scans and what it found.</summary>
public static class Defender
{
    private static string MpCmdRun => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");

    public static DefenderStatus Status()
    {
        var j = Proc.PowerShellJson(@"
$o = [pscustomobject]@{ Ok = $false; Av = $false; Rt = $false; Age = -1; Other = '' }
try { $m = Get-MpComputerStatus; $o.Ok = $true; $o.Av = [bool]$m.AntivirusEnabled; $o.Rt = [bool]$m.RealTimeProtectionEnabled; $o.Age = [int]$m.AntivirusSignatureAge } catch {}
try { $o.Other = ((Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct | Where-Object displayName -notmatch 'Defender').displayName -join ', ') } catch {}
$o", TimeSpan.FromMinutes(1));
        if (j is not { } e || Proc.Str(e, "Ok") != "True")
            return new DefenderStatus(false, false, false, -1, j is { } x ? Proc.Str(x, "Other") : "", "Microsoft Defender isn't available on this PC.");
        bool av = Proc.Str(e, "Av") == "True", rt = Proc.Str(e, "Rt") == "True";
        int.TryParse(Proc.Str(e, "Age"), out var age);
        var other = Proc.Str(e, "Other");
        var msg = av
            ? $"Microsoft Defender is on{(rt ? "" : ", but real-time protection is OFF")}. Virus definitions are {age} day(s) old."
            : other.Length > 0 ? $"Another antivirus is in charge ({other}); Defender is standing by." : "Microsoft Defender is turned off and no other antivirus was found.";
        return new DefenderStatus(true, av, rt, age, other, msg);
    }

    public static ProcResult UpdateSignatures(Action<string>? line, CancellationToken ct) =>
        Proc.Run(MpCmdRun, "-SignatureUpdate", TimeSpan.FromMinutes(15), line, ct: ct);

    /// <summary>Quick scan (about 5-15 minutes) or full scan (an hour or more).</summary>
    public static ProcResult Scan(bool full, Action<string>? line, CancellationToken ct) =>
        Proc.Run(MpCmdRun, $"-Scan -ScanType {(full ? 2 : 1)}", full ? TimeSpan.FromHours(8) : TimeSpan.FromHours(1), line, ct: ct);

    /// <summary>Threats Defender detected since a time, with what it did about each.</summary>
    public static List<string> ThreatsSince(DateTime since)
    {
        var list = new List<string>();
        var j = Proc.PowerShellJson($@"
$since = [datetime]::Parse({Proc.Ps(since.ToString("o"))})
Get-MpThreatDetection | Where-Object {{ $_.InitialDetectionTime -ge $since }} | ForEach-Object {{
  $t = Get-MpThreat -ThreatID $_.ThreatID -ErrorAction SilentlyContinue
  [pscustomobject]@{{ Name = $t.ThreatName; Ok = $_.ActionSuccess; Where = (($_.Resources | Select-Object -First 2) -join '; ') }}
}}", TimeSpan.FromMinutes(2));
        foreach (var e in Proc.Items(j))
            list.Add($"{Proc.Str(e, "Name")} ({(Proc.Str(e, "Ok") == "True" ? "removed" : "NOT removed: check Windows Security")}){(Proc.Str(e, "Where") is { Length: > 0 } w ? " in " + Proc.Trim(w, 160) : "")}");
        return list;
    }
}

/// <summary>Checks for the usual signs of a hijacked browser or connection. Reports first; changes only when asked.</summary>
public static class Hijacks
{
    private static readonly string[] GoodSearch = { "google.", "bing.com", "duckduckgo.com", "yahoo.com", "ecosia.org", "startpage.com", "brave.com" };

    /// <summary>Chrome, Edge and Brave: search engine, start pages and extensions, for every profile of every user.</summary>
    public static List<string> Browsers(IEnumerable<string> userProfiles)
    {
        var found = new List<string>();
        foreach (var home in userProfiles)
        {
            var user = Path.GetFileName(home);
            foreach (var (browser, rel) in new[] { ("Chrome", @"Google\Chrome\User Data"), ("Edge", @"Microsoft\Edge\User Data"), ("Brave", @"BraveSoftware\Brave-Browser\User Data") })
            {
                var ud = Path.Combine(home, "AppData", "Local", rel);
                if (!Directory.Exists(ud)) continue;
                foreach (var profile in Directory.GetDirectories(ud).Where(d => File.Exists(Path.Combine(d, "Preferences"))))
                {
                    var where = $"{browser} ({user}, {Path.GetFileName(profile)})";
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, "Preferences")));
                        var root = doc.RootElement;
                        if (root.TryGetProperty("default_search_provider_data", out var dsp) && dsp.TryGetProperty("template_url_data", out var t)
                            && t.TryGetProperty("url", out var url) && url.GetString() is { } u && !GoodSearch.Any(g => u.Contains(g, StringComparison.OrdinalIgnoreCase)))
                            found.Add($"{where}: search engine set to {Proc.Trim(u, 80)}. Usually a hijacker; reset it in the browser's settings.");
                        if (root.TryGetProperty("session", out var ses) && ses.TryGetProperty("startup_urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
                            foreach (var s in urls.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0 && !GoodSearch.Any(g => s.Contains(g, StringComparison.OrdinalIgnoreCase))))
                                found.Add($"{where}: opens {Proc.Trim(s, 80)} at start. Confirm the customer chose this.");
                    }
                    catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
                    foreach (var name in Extensions(profile))
                        found.Add($"{where}: extension \"{name}\". Confirm the customer uses it; remove unknown ones in the browser.");
                }
            }
        }
        return found;
    }

    /// <summary>Extension names in a Chromium profile (built-in ones skipped).</summary>
    private static IEnumerable<string> Extensions(string profile)
    {
        var dir = Path.Combine(profile, "Extensions");
        if (!Directory.Exists(dir)) yield break;
        foreach (var ext in Directory.GetDirectories(dir))
        {
            var version = Directory.GetDirectories(ext).OrderByDescending(d => d).FirstOrDefault();
            if (version is null) continue;
            string? name = null;
            try
            {
                using var m = JsonDocument.Parse(File.ReadAllText(Path.Combine(version, "manifest.json")));
                name = m.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is not null && name.StartsWith("__MSG_", StringComparison.Ordinal))
                {
                    var key = name[6..].TrimEnd('_');
                    var loc = new[] { "en", "en_US", "en_GB" }.Select(l => Path.Combine(version, "_locales", l, "messages.json")).FirstOrDefault(File.Exists);
                    if (loc is not null)
                    {
                        using var msgs = JsonDocument.Parse(File.ReadAllText(loc));
                        foreach (var p in msgs.RootElement.EnumerateObject())
                            if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetProperty("message", out var mv)) { name = mv.GetString(); break; }
                    }
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { continue; }
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("__MSG_", StringComparison.Ordinal)) name = Path.GetFileName(ext);
            if (name is "Chrome Web Store Payments" or "Google Docs Offline" or "Microsoft Edge PDF Viewer" or "Web Store") continue;
            yield return name;
        }
    }

    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <summary>A proxy or auto-config script set for this user (a common way adware reads web traffic). Null if none.</summary>
    public static string? Proxy()
    {
        using var k = Registry.CurrentUser.OpenSubKey(InternetSettings);
        if (k is null) return null;
        var on = k.GetValue("ProxyEnable") is int e && e == 1;
        var server = k.GetValue("ProxyServer") as string ?? "";
        var pac = k.GetValue("AutoConfigURL") as string ?? "";
        if (pac.Length > 0) return $"Web traffic is set to follow an automatic proxy script ({Proc.Trim(pac, 100)}).";
        if (on && server.Length > 0) return $"Web traffic goes through a proxy ({Proc.Trim(server, 100)}).";
        return null;
    }

    public static void ResetProxy()
    {
        using var k = Registry.CurrentUser.OpenSubKey(InternetSettings, true);
        if (k is null) return;
        k.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        k.DeleteValue("AutoConfigURL", false);
        k.DeleteValue("ProxyServer", false);
    }

    public static string HostsPath => Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");

    /// <summary>Lines in the hosts file that send a web address somewhere other than this computer.</summary>
    public static List<string> HostsEntries()
    {
        try
        {
            return File.ReadAllLines(HostsPath)
                .Select(l => l.Split('#')[0].Trim())
                .Where(l => l.Length > 0)
                .Where(l =>
                {
                    var parts = l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length >= 2 && !(parts[1].Equals("localhost", StringComparison.OrdinalIgnoreCase) && parts[0] is "127.0.0.1" or "::1");
                })
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>Saves a copy, then puts back Windows' default hosts file.</summary>
    public static string ResetHosts()
    {
        var backup = HostsPath + ".nighttech-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.Copy(HostsPath, backup, false);
        File.WriteAllText(HostsPath, "# Copyright (c) 1993-2009 Microsoft Corp.\r\n#\r\n# This is a sample HOSTS file used by Microsoft TCP/IP for Windows.\r\n#\r\n# localhost name resolution is handled within DNS itself.\r\n#\t127.0.0.1       localhost\r\n#\t::1             localhost\r\n");
        return backup;
    }
}

/// <summary>Windows' own repair and maintenance tools.</summary>
public static class Repairs
{
    /// <summary>System File Checker. Returns a plain-language result.</summary>
    public static (bool Ok, string Summary) Sfc(Action<string>? line, CancellationToken ct)
    {
        var r = Proc.Run(Proc.Sys("sfc.exe"), "/scannow", TimeSpan.FromHours(2), line, System.Text.Encoding.Unicode, ct);
        var o = r.Output;
        if (o.Contains("did not find any integrity violations", StringComparison.OrdinalIgnoreCase)) return (true, "No damaged Windows files found.");
        if (o.Contains("successfully repaired", StringComparison.OrdinalIgnoreCase)) return (true, "Found damaged Windows files and repaired them.");
        if (o.Contains("unable to fix", StringComparison.OrdinalIgnoreCase)) return (false, "Found damaged Windows files it couldn't fix. Run the Windows image repair, then this again.");
        if (o.Contains("pending", StringComparison.OrdinalIgnoreCase)) return (false, "A restart is pending. Restart the PC, then run this again.");
        return (r.ExitCode == 0, r.ExitCode == 0 ? "Finished." : "Didn't finish: " + Proc.Trim(o, 200));
    }

    /// <summary>DISM: repairs the Windows image (needs the internet; 10-30 minutes).</summary>
    public static (bool Ok, string Summary) RestoreHealth(Action<string>? line, CancellationToken ct)
    {
        var r = Proc.Run(Proc.Sys("dism.exe"), "/Online /Cleanup-Image /RestoreHealth", TimeSpan.FromHours(2), line, ct: ct);
        return r.ExitCode == 0 ? (true, "Windows image checked and repaired where needed.") : (false, $"Didn't finish (code {r.ExitCode}). " + LastLine(r.Output));
    }

    /// <summary>DISM: removes old versions of updated Windows components.</summary>
    public static (bool Ok, string Summary) ComponentCleanup(Action<string>? line, CancellationToken ct)
    {
        var r = Proc.Run(Proc.Sys("dism.exe"), "/Online /Cleanup-Image /StartComponentCleanup", TimeSpan.FromHours(2), line, ct: ct);
        return r.ExitCode == 0 ? (true, "Old Windows component versions removed.") : (false, $"Didn't finish (code {r.ExitCode}). " + LastLine(r.Output));
    }

    /// <summary>Optimizes the Windows drive: TRIM on an SSD, defragment on a hard disk (Windows picks).</summary>
    public static (bool Ok, string Summary) Optimize(Action<string>? line, CancellationToken ct)
    {
        var drive = Machine.SystemDrive.TrimEnd('\\');
        var r = Proc.Run(Proc.Sys("defrag.exe"), $"{drive} /O /U", TimeSpan.FromHours(4), line, ct: ct);
        return r.ExitCode == 0 ? (true, $"Drive {drive} optimized.") : (false, $"Didn't finish (code {r.ExitCode}).");
    }

    /// <summary>Online file system check: reads only, changes nothing.</summary>
    public static (bool Ok, string Summary) CheckDisk(Action<string>? line, CancellationToken ct)
    {
        var drive = Machine.SystemDrive.TrimEnd('\\');
        var r = Proc.Run(Proc.Sys("chkdsk.exe"), $"{drive} /scan", TimeSpan.FromHours(2), line, ct: ct);
        if (r.Output.Contains("found no problems", StringComparison.OrdinalIgnoreCase)) return (true, "No file system problems found.");
        return r.ExitCode == 0 ? (true, "Finished.") : (false, "Problems were found. Schedule a full check (chkdsk /f) at the next restart and back up first.");
    }

    public static (bool Ok, string Summary) FlushDns(CancellationToken ct)
    {
        var r = Proc.Run(Proc.Sys("ipconfig.exe"), "/flushdns", TimeSpan.FromMinutes(1), ct: ct);
        return (r.ExitCode == 0, r.ExitCode == 0 ? "Saved web addresses cleared." : "Didn't finish.");
    }

    private static string LastLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault(l => l.Length > 3) ?? "";
}

/// <summary>Junk that needs Windows' help: the update cache (its services must pause), Delivery Optimization and the Recycle Bin.</summary>
public static class SpecialJunk
{
    public static void PauseUpdates() { Proc.Run(Proc.Sys("net.exe"), "stop wuauserv /y", TimeSpan.FromMinutes(2)); Proc.Run(Proc.Sys("net.exe"), "stop bits /y", TimeSpan.FromMinutes(2)); }
    public static void ResumeUpdates() { Proc.Run(Proc.Sys("net.exe"), "start bits", TimeSpan.FromMinutes(2)); Proc.Run(Proc.Sys("net.exe"), "start wuauserv", TimeSpan.FromMinutes(2)); }

    public static bool ClearDeliveryOptimization() => Proc.PowerShell("Delete-DeliveryOptimizationCache -Force", TimeSpan.FromMinutes(5)).ExitCode == 0;

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4)]
    private struct ShQueryRbInfo { public int cbSize; public long i64Size; public long i64NumItems; }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? root, ref ShQueryRbInfo info);

    public static (long Bytes, long Items) RecycleBinSize()
    {
        var i = new ShQueryRbInfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBin(null, ref i) == 0 ? (i.i64Size, i.i64NumItems) : (0, 0);
    }

    /// <summary>Empties the Recycle Bin on every drive, without confirmations or sounds.</summary>
    public static bool EmptyRecycleBin() => SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4) is 0 or unchecked((int)0x8000FFFF);
}
