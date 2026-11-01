using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
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
            r.Notes.Add("Run as administrator to also check drive temperature and wear, and BitLocker.");
        progress.Report("Checks finished.");
    }

    private static void Step(DiagnosticReport r, IProgress<string> progress, string label, Action check)
    {
        progress.Report($"Checking {label}...");
        try { check(); }
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
        var byId = new Dictionary<string, DiskRecord>();
        try
        {
            foreach (var o in Query(@"root\Microsoft\Windows\Storage",
                         "SELECT DeviceId, FriendlyName, SerialNumber, MediaType, BusType, HealthStatus, OperationalStatus, Size FROM MSFT_PhysicalDisk"))
            {
                var d = new DiskRecord
                {
                    Name = Str(o, "FriendlyName"),
                    Serial = Str(o, "SerialNumber"),
                    SizeBytes = Long(o, "Size"),
                    MediaType = Int(o, "MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Unknown" },
                    BusType = BusName(Int(o, "BusType")),
                    Health = Int(o, "HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unknown" },
                    OperationalStatus = OperationalText(Get(o, "OperationalStatus")),
                };
                r.Disks.Add(d);
                byId[Str(o, "DeviceId")] = d;
            }
        }
        catch (ManagementException)
        {
            // Older Windows without the Storage namespace: fall back to the basic drive list.
            foreach (var o in Query(@"root\CIMV2", "SELECT Model, SerialNumber, Size, Status, InterfaceType FROM Win32_DiskDrive"))
            {
                var status = Str(o, "Status");
                r.Disks.Add(new DiskRecord
                {
                    Name = Str(o, "Model"),
                    Serial = Str(o, "SerialNumber"),
                    SizeBytes = Long(o, "Size"),
                    BusType = Str(o, "InterfaceType"),
                    Health = status.Equals("OK", StringComparison.OrdinalIgnoreCase) ? "Healthy"
                        : status.Equals("Pred Fail", StringComparison.OrdinalIgnoreCase) ? "Unhealthy" : "Unknown",
                    OperationalStatus = status,
                });
            }
            return;
        }

        if (!r.RanAsAdministrator || byId.Count == 0) return;
        try
        {
            foreach (var o in Query(@"root\Microsoft\Windows\Storage",
                         "SELECT DeviceId, Temperature, Wear, PowerOnHours, ReadErrorsUncorrected, ReadErrorsTotal FROM MSFT_StorageReliabilityCounter"))
            {
                if (!byId.TryGetValue(Str(o, "DeviceId"), out var d)) continue;
                d.TemperatureC = Int(o, "Temperature") is > 0 and var t ? t : null;
                if (d.MediaType == "SSD") d.WearPercent = Int(o, "Wear");
                d.PowerOnHours = Long(o, "PowerOnHours") is > 0 and var h ? h : null;
                d.ReadErrorsUncorrected = Long(o, "ReadErrorsUncorrected");
                d.ReadErrorsTotal = Long(o, "ReadErrorsTotal");
            }
        }
        catch (Exception ex)
        {
            r.Notes.Add("Couldn't read drive temperature and wear: " + ex.Message);
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
            foreach (var o in Query(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
                         "SELECT DriveLetter, ProtectionStatus FROM Win32_EncryptableVolume"))
            {
                var letter = Str(o, "DriveLetter");
                var v = r.Volumes.FirstOrDefault(x => x.Name.StartsWith(letter, StringComparison.OrdinalIgnoreCase) && letter.Length > 0);
                if (v is null) continue;
                v.BitLocker = Int(o, "ProtectionStatus") switch { 1 => "On", 0 => "Off", _ => "" };
            }
        }
        catch (ManagementException)
        {
            // BitLocker isn't available on this edition of Windows (for example Home): nothing to report.
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
            b.DesignCapacityMWh = Query(@"root\WMI", "SELECT DesignedCapacity FROM BatteryStaticData")
                .Select(o => Long(o, "DesignedCapacity")).FirstOrDefault(v => v > 0);
            b.FullChargeCapacityMWh = Query(@"root\WMI", "SELECT FullChargedCapacity FROM BatteryFullChargedCapacity")
                .Select(o => Long(o, "FullChargedCapacity")).FirstOrDefault(v => v > 0);
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

    private static void Events(DiagnosticReport r)
    {
        var ms = (long)TimeSpan.FromDays(r.Events.Days).TotalMilliseconds;
        string Q(string provider, string ids) =>
            $"*[System[Provider[@Name='{provider}'] and ({ids}) and TimeCreated[timediff(@SystemTime) <= {ms}]]]";

        r.Events.UnexpectedShutdowns = Count(Q("Microsoft-Windows-Kernel-Power", "EventID=41"));
        r.Events.BlueScreens = Count(Q("Microsoft-Windows-WER-SystemErrorReporting", "EventID=1001"));
        r.Events.DiskErrors = Count(Q("disk", "EventID=7 or EventID=51 or EventID=153"));
    }

    private static int Count(string xpath)
    {
        var query = new EventLogQuery("System", PathType.LogName, xpath);
        using var reader = new EventLogReader(query);
        var n = 0;
        while (n < 10000)
        {
            using var e = reader.ReadEvent();
            if (e is null) break;
            n++;
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
        n.InternetReachable = PingHost("1.1.1.1", out ms) || PingHost("8.8.8.8", out ms) || TcpOpen("1.1.1.1", 443) || TcpOpen("8.8.8.8", 443);
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
