using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using NightTech.Core.Diagnostics;

namespace NightTech.FieldKit;

/// <summary>
/// Reads the computer's condition. Everything here is read-only: it looks, it never changes settings or files.
/// Each check is independent, so one failure (for example missing rights) never stops the others.
/// </summary>
public static class Collector
{
    public static bool IsAdministrator
    {
        get
        {
            try
            {
                using var id = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }
    }

    public static void CollectAll(DiagnosticReport r, IProgress<string> progress)
    {
        Step(r, progress, "the computer and Windows", () => Computer(r));
        Step(r, progress, "memory", () => Memory(r));
        Step(r, progress, "Windows updates", () => Updates(r));
        Step(r, progress, "drives", () => Disks(r));
        Step(r, progress, "drive space", () => Volumes(r));
        Step(r, progress, "the battery", () => Battery(r));
        Step(r, progress, "antivirus", () => Antivirus(r));
        Step(r, progress, "the Windows event log", () => Events(r));
        Step(r, progress, "the network", () => Network(r));
        Step(r, progress, "programs that start with Windows", () => Startup(r));
        Step(r, progress, "installed programs", () => Programs(r));
        if (!r.RanAsAdministrator)
            r.Notes.Add("Drive temperature, wear and error counts, and BitLocker, need administrator rights (Restart as administrator).");
        progress.Report("Checks finished.");
    }

    private static readonly TimeSpan StepTimeLimit = TimeSpan.FromSeconds(90);

    private static void Step(DiagnosticReport r, IProgress<string> progress, string label, Action check)
    {
        progress.Report($"Checking {label}...");
        try
        {
            // A failing drive or a damaged Windows can make a check hang. Give up on it and move on.
            var task = Task.Run(check);
            if (!task.Wait(StepTimeLimit))
                r.Notes.Add($"Checking {label} took too long and was skipped.");
        }
        catch (AggregateException ex) { r.Notes.Add($"Couldn't check {label}: {ex.InnerException?.Message ?? ex.Message}"); }
        catch (Exception ex) { r.Notes.Add($"Couldn't check {label}: {ex.Message}"); }
    }

    // ---------------- WMI helpers ----------------

    private static List<ManagementBaseObject> Query(string scope, string wql)
    {
        var list = new List<ManagementBaseObject>();
        using var searcher = new ManagementObjectSearcher(scope, wql);
        searcher.Options.Timeout = TimeSpan.FromSeconds(30);
        using var results = searcher.Get();
        foreach (ManagementBaseObject o in results) list.Add(o);
        return list;
    }

    private static object? Get(ManagementBaseObject o, string prop)
    {
        try { return o[prop]; } catch { return null; }
    }

    private static string Str(ManagementBaseObject o, string prop) => Get(o, prop)?.ToString()?.Trim() ?? "";

    private static long? Long(ManagementBaseObject o, string prop)
    {
        var v = Get(o, prop);
        if (v is null) return null;
        try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return null; }
    }

    private static int? Int(ManagementBaseObject o, string prop) => Long(o, prop) is { } l ? (int)l : null;

    private static DateTime? CimDate(ManagementBaseObject o, string prop)
    {
        var s = Str(o, prop);
        if (s.Length == 0) return null;
        try { return ManagementDateTimeConverter.ToDateTime(s); } catch { return null; }
    }

    // ---------------- Checks ----------------

    private static void Computer(DiagnosticReport r)
    {
        var c = r.Computer;
        c.Name = Environment.MachineName;
        foreach (var o in Query(@"root\CIMV2", "SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem"))
        {
            c.Manufacturer = Str(o, "Manufacturer");
            c.Model = Str(o, "Model");
            c.RamBytes = Long(o, "TotalPhysicalMemory");
        }
        foreach (var o in Query(@"root\CIMV2", "SELECT SerialNumber, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS"))
        {
            c.SerialNumber = Str(o, "SerialNumber");
            c.BiosVersion = Str(o, "SMBIOSBIOSVersion");
            c.BiosDate = CimDate(o, "ReleaseDate");
        }
        foreach (var o in Query(@"root\CIMV2", "SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem"))
        {
            c.OsName = Str(o, "Caption");
            c.OsVersion = Str(o, "Version");
            c.OsBuild = Str(o, "BuildNumber");
            c.OsArchitecture = Str(o, "OSArchitecture");
            c.OsInstallDate = CimDate(o, "InstallDate");
            c.LastBoot = CimDate(o, "LastBootUpTime");
        }
        var cpus = Query(@"root\CIMV2", "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
        if (cpus.Count > 0)
        {
            c.Cpu = Str(cpus[0], "Name") + (cpus.Count > 1 ? $" (x{cpus.Count})" : "");
            c.CpuCores = cpus.Sum(o => Int(o, "NumberOfCores") ?? 0);
            c.CpuThreads = cpus.Sum(o => Int(o, "NumberOfLogicalProcessors") ?? 0);
        }
    }

    private static void Memory(DiagnosticReport r)
    {
        foreach (var o in Query(@"root\CIMV2", "SELECT DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber FROM Win32_PhysicalMemory"))
        {
            r.Computer.Memory.Add(new MemoryModule
            {
                Slot = Str(o, "DeviceLocator"),
                CapacityBytes = Long(o, "Capacity") ?? 0,
                SpeedMhz = Int(o, "ConfiguredClockSpeed") is > 0 and var cfg ? cfg : Int(o, "Speed"),
                Manufacturer = Str(o, "Manufacturer"),
                PartNumber = Str(o, "PartNumber"),
            });
        }
        // Installed memory, not what's left after built-in graphics takes its share.
        var installed = r.Computer.Memory.Sum(m => m.CapacityBytes);
        if (installed > 0) r.Computer.RamBytes = installed;
    }

    private static void Updates(DiagnosticReport r)
    {
        DateTime? newest = null;
        foreach (var o in Query(@"root\CIMV2", "SELECT InstalledOn FROM Win32_QuickFixEngineering"))
        {
            var s = Str(o, "InstalledOn");
            if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var d) ||
                DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out d))
            {
                if (d <= DateTime.Now.AddDays(1) && (newest is null || d > newest)) newest = d;
            }
        }
        r.Computer.LastUpdateInstalled = newest;
    }

    private static void Disks(DiagnosticReport r)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_PhysicalDisk");
            searcher.Options.Timeout = TimeSpan.FromSeconds(30);
            using var results = searcher.Get();
            string? counterError = null;
            foreach (ManagementObject o in results)
            {
                var d = new DiskRecord
                {
                    Number = int.TryParse(Str(o, "DeviceId"), out var num) ? num : null,
                    Name = Str(o, "FriendlyName"),
                    Serial = Str(o, "SerialNumber"),
                    SizeBytes = Long(o, "Size"),
                    MediaType = Int(o, "MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unknown" },
                    BusType = BusName(Int(o, "BusType")),
                    Health = Int(o, "HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
                    OperationalStatus = OperationalText(Get(o, "OperationalStatus")),
                };
                r.Disks.Add(d);

                // Temperature, wear and error counts: Windows only gives these per disk, and only to administrators.
                if (!r.RanAsAdministrator) continue;
                try
                {
                    using var counters = o.GetRelated("MSFT_StorageReliabilityCounter");
                    foreach (ManagementBaseObject c in counters)
                    {
                        d.TemperatureC = Int(c, "Temperature") is > 0 and var t ? t : null;
                        if (d.MediaType == "SSD") d.WearPercent = Int(c, "Wear");
                        d.PowerOnHours = Long(c, "PowerOnHours") is > 0 and var h ? h : null;
                        d.ReadErrorsUncorrected = Long(c, "ReadErrorsUncorrected");
                        d.ReadErrorsTotal = Long(c, "ReadErrorsTotal");
                        break;
                    }
                }
                catch (Exception ex) { counterError ??= ex.Message; }
            }
            if (counterError is not null)
                r.Notes.Add("Couldn't read temperature and wear for every drive: " + counterError);
        }
        catch (ManagementException)
        {
            // Older Windows without the Storage namespace: fall back to the basic drive list.
            foreach (var o in Query(@"root\CIMV2", "SELECT Index, Model, SerialNumber, Size, Status, InterfaceType FROM Win32_DiskDrive"))
            {
                var status = Str(o, "Status");
                r.Disks.Add(new DiskRecord
                {
                    Number = Int(o, "Index"),
                    Name = Str(o, "Model"),
                    Serial = Str(o, "SerialNumber"),
                    SizeBytes = Long(o, "Size"),
                    BusType = Str(o, "InterfaceType"),
                    Health = status.Equals("OK", StringComparison.OrdinalIgnoreCase) ? "Healthy"
                        : status.Equals("Pred Fail", StringComparison.OrdinalIgnoreCase) ? "Unhealthy" : "Unknown",
                    OperationalStatus = status,
                });
            }
        }
    }

    private static string BusName(int? bus) => bus switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "FireWire", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID",
        9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 15 => "Virtual", 16 => "Storage Spaces",
        17 => "NVMe", _ => "",
    };

    private static string OperationalText(object? value)
    {
        if (value is not ushort[] codes) return "";
        return string.Join(", ", codes.Select(c => c switch
        {
            2 => "OK", 3 => "Degraded", 5 => "Predictive failure", 6 => "Error", 10 => "Stopped", 11 => "In service",
            13 => "Lost communication", 0xD00D => "Removed", _ => "Code " + c,
        }));
    }

    private static void Volumes(DiagnosticReport r)
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        foreach (var d in DriveInfo.GetDrives())
        {
            if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
            r.Volumes.Add(new VolumeRecord
            {
                Name = d.Name,
                Label = SafeGet(() => d.VolumeLabel),
                FileSystem = SafeGet(() => d.DriveFormat),
                SizeBytes = d.TotalSize,
                FreeBytes = d.TotalFreeSpace,
                IsSystem = string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase),
            });
        }

        if (!r.RanAsAdministrator) return;
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption", "SELECT * FROM Win32_EncryptableVolume");
            searcher.Options.Timeout = TimeSpan.FromSeconds(30);
            using var results = searcher.Get();
            foreach (ManagementObject o in results)
            {
                var letter = Str(o, "DriveLetter");
                if (letter.Length == 0) continue;
                var v = r.Volumes.FirstOrDefault(x => x.Name.StartsWith(letter, StringComparison.OrdinalIgnoreCase));
                if (v is null) continue;
                if (Int(o, "ProtectionStatus") == 1) { v.BitLocker = "On"; continue; }

                // Protection off can still mean an encrypted drive with protection suspended
                // (common during updates, and on Windows Home "device encryption" before sign-in).
                int? conversion = null;
                try
                {
                    using var outParams = o.InvokeMethod("GetConversionStatus", null, null);
                    conversion = outParams is null ? null : Int(outParams, "ConversionStatus");
                }
                catch { /* older Windows: leave it unknown */ }
                v.BitLocker = conversion switch
                {
                    0 => "Off",
                    null => "Off (encryption state unknown)",
                    _ => "Encrypted, protection suspended",
                };
            }
        }
        catch (ManagementException)
        {
            // This Windows doesn't offer BitLocker information: nothing to report.
        }
    }

    private static string SafeGet(Func<string> f)
    {
        try { return f(); } catch { return ""; }
    }

    private static void Battery(DiagnosticReport r)
    {
        var batteries = Query(@"root\CIMV2", "SELECT Name, EstimatedChargeRemaining FROM Win32_Battery");
        if (batteries.Count == 0) return; // desktop: no battery
        var b = new BatteryRecord { Name = Str(batteries[0], "Name"), ChargePercent = Int(batteries[0], "EstimatedChargeRemaining") };
        r.Battery = b;
        try
        {
            // Some laptops have two batteries: add them up so design and current capacity match.
            var design = Query(@"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData").Sum(o => Long(o, "DesignedCapacity") ?? 0);
            var full = Query(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity").Sum(o => Long(o, "FullChargedCapacity") ?? 0);
            b.DesignCapacityMWh = design > 0 ? design : null;
            b.FullChargeCapacityMWh = full > 0 ? full : null;
            b.CycleCount = Query(@"root\WMI", "SELECT CycleCount FROM BatteryCycleCount")
                .Select(o => Int(o, "CycleCount")).FirstOrDefault(v => v > 0);
        }
        catch (Exception ex)
        {
            r.Notes.Add("Couldn't read battery wear: " + ex.Message + " (Tools tab: Battery report has full details.)");
        }
    }

    private static void Antivirus(DiagnosticReport r)
    {
        foreach (var o in Query(@"root\SecurityCenter2", "SELECT displayName, productState FROM AntiVirusProduct"))
        {
            var state = Long(o, "productState") ?? 0;
            r.Antivirus.Add(AntivirusProduct.FromProductState(Str(o, "displayName"), (uint)state));
        }
    }

    private static readonly Regex DiskNumber = new(@"Harddisk(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void Events(DiagnosticReport r)
    {
        var ms = (long)TimeSpan.FromDays(r.Events.Days).TotalMilliseconds;
        string Q(string provider, string ids) =>
            $"*[System[Provider[@Name='{provider}'] and ({ids}) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";

        r.Events.UnexpectedShutdowns = Count(Q("Microsoft-Windows-Kernel-Power", "EventID=41"), _ => true);
        r.Events.BlueScreens = Count(Q("Microsoft-Windows-WER-SystemErrorReporting", "EventID=1001"), _ => true);

        // Disk events also fire for USB sticks and card readers (including this kit's own stick when it's pulled out),
        // so only count the ones about the computer's own drives.
        var drives = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Internal(EventRecord e)
        {
            var text = string.Join(" ", e.Properties.Select(p => p.Value?.ToString() ?? ""));
            var m = DiskNumber.Match(text);
            if (!m.Success) return true; // can't tell which drive: count it
            var disk = r.Disks.FirstOrDefault(d => d.Number == int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
            if (disk is null) return true;
            if (disk.BusType is "USB" or "SD" or "MMC") return false;
            drives.Add(string.IsNullOrWhiteSpace(disk.Name) ? "disk " + disk.Number : disk.Name);
            return true;
        }
        r.Events.DiskErrors = Count(Q("disk", "EventID=7"), Internal);
        r.Events.DiskWarnings = Count(Q("disk", "EventID=51 or EventID=153"), Internal);
        r.Events.DiskEventDrives = drives.ToList();
    }

    private static int Count(string xpath, Func<EventRecord, bool> include)
    {
        var query = new EventLogQuery("System", PathType.LogName, xpath);
        using var reader = new EventLogReader(query);
        var n = 0;
        for (var seen = 0; seen < 10000; seen++)
        {
            using var e = reader.ReadEvent();
            if (e is null) break;
            if (include(e)) n++;
        }
        return n;
    }

    private static void Network(DiagnosticReport r)
    {
        var n = r.Network;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var ip = nic.GetIPProperties();
            var a = new NetAdapter
            {
                Name = nic.Name,
                Description = nic.Description,
                Type = nic.NetworkInterfaceType.ToString(),
                Mac = string.Join("-", nic.GetPhysicalAddress().GetAddressBytes().Select(x => x.ToString("X2"))),
                SpeedMbps = nic.Speed > 0 ? nic.Speed / 1_000_000 : null,
                IPv4 = ip.UnicastAddresses.Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork).Select(u => u.Address.ToString()).ToList(),
                Gateways = ip.GatewayAddresses.Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                    .Select(g => g.Address.ToString()).ToList(),
                Dns = ip.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()).ToList(),
            };
            try { a.Dhcp = ip.GetIPv4Properties()?.IsDhcpEnabled; } catch { /* not available on every adapter */ }
            n.Adapters.Add(a);
        }

        var gateway = n.Adapters.SelectMany(a => a.Gateways).FirstOrDefault();
        n.GatewayReachable = gateway is null ? null : PingHost(gateway, out _);

        long ms = 0;
        n.InternetReachable = PingHost("1.1.1.1", out ms) || PingHost("8.8.8.8", out ms) ||
                              WebCheck() || TcpOpen("1.1.1.1", 443) || TcpOpen("8.8.8.8", 443);
        n.InternetPingMs = n.InternetReachable == true && ms > 0 ? ms : null;

        try
        {
            var lookup = Dns.GetHostAddressesAsync("www.microsoft.com");
            n.DnsWorks = lookup.Wait(TimeSpan.FromSeconds(6)) && lookup.Result.Length > 0;
        }
        catch { n.DnsWorks = false; }
    }

    private static bool PingHost(string host, out long ms)
    {
        ms = 0;
        try
        {
            using var p = new Ping();
            for (var i = 0; i < 2; i++)
            {
                var reply = p.Send(host, 1500);
                if (reply.Status == IPStatus.Success) { ms = reply.RoundtripTime; return true; }
            }
        }
        catch { /* blocked or no network */ }
        return false;
    }

    /// <summary>Windows' own internet check page; works on networks that block ping.</summary>
    private static bool WebCheck()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var text = http.GetStringAsync("http://www.msftconnecttest.com/connecttest.txt").GetAwaiter().GetResult();
            return text.Contains("Microsoft Connect Test", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool TcpOpen(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            return client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(3)) && client.Connected;
        }
        catch { return false; }
    }

    // ---------------- Startup and installed programs (registry, read only) ----------------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    private static void Startup(DiagnosticReport r)
    {
        ReadRun(r, RegistryHive.CurrentUser, RegistryView.Default, RunKey, "Run (this user)", RegistryHive.CurrentUser, "Run");
        ReadRun(r, RegistryHive.LocalMachine, RegistryView.Registry64, RunKey, "Run (all users)", RegistryHive.LocalMachine, "Run");
        if (Environment.Is64BitOperatingSystem)
            ReadRun(r, RegistryHive.LocalMachine, RegistryView.Registry32, RunKey, "Run (all users, 32-bit)", RegistryHive.LocalMachine, "Run32");
        ReadFolder(r, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Startup folder (this user)", RegistryHive.CurrentUser);
        ReadFolder(r, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Startup folder (all users)", RegistryHive.LocalMachine);
    }

    private static void ReadRun(DiagnosticReport r, RegistryHive hive, RegistryView view, string path, string where,
        RegistryHive approvedHive, string approvedName)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(path);
        if (key is null) return;
        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(name) || TurnedOff(approvedHive, approvedName, name)) continue;
            r.Startup.Add(new StartupItem { Name = name, Command = key.GetValue(name)?.ToString() ?? "", Location = where });
        }
    }

    private static void ReadFolder(DiagnosticReport r, string folder, string where, RegistryHive approvedHive)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            if (TurnedOff(approvedHive, "StartupFolder", name)) continue;
            r.Startup.Add(new StartupItem { Name = Path.GetFileNameWithoutExtension(file), Command = file, Location = where });
        }
    }

    /// <summary>True when the item was turned off in Task Manager's Startup tab.</summary>
    private static bool TurnedOff(RegistryHive hive, string list, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(ApprovedKey + list);
            return key?.GetValue(name) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
        }
        catch { return false; }
    }

    private static void Programs(DiagnosticReport r)
    {
        const string uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default),
                 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(uninstall);
            if (key is null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(sub);
                if (app?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
                if (app.GetValue("SystemComponent") is int sc && sc == 1) continue;
                if (app.GetValue("ParentKeyName") is not null) continue;
                var release = app.GetValue("ReleaseType")?.ToString() ?? "";
                if (release.Contains("Update", StringComparison.OrdinalIgnoreCase) || release.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)) continue;
                var version = app.GetValue("DisplayVersion")?.ToString() ?? "";
                if (!seen.Add(name.Trim() + "|" + version)) continue;
                r.Programs.Add(new InstalledProgram
                {
                    Name = name.Trim(),
                    Version = version,
                    Publisher = app.GetValue("Publisher")?.ToString() ?? "",
                    InstallDate = app.GetValue("InstallDate")?.ToString() ?? "",
                });
            }
        }
        r.Programs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }
}
