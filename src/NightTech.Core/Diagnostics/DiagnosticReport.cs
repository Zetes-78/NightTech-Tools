using System.Text.Json.Serialization;

namespace NightTech.Core.Diagnostics;

/// <summary>Everything Night Tech Field Kit learned about one computer in one run.</summary>
public sealed class DiagnosticReport
{
    public string Tool { get; set; } = "Night Tech Field Kit";
    public string ToolVersion { get; set; } = "";
    public string Ticket { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Technician { get; set; } = "";
    public DateTime Started { get; set; }
    public DateTime Finished { get; set; }
    public bool RanAsAdministrator { get; set; }

    public ComputerInfo Computer { get; set; } = new();
    public List<DiskRecord> Disks { get; set; } = new();
    public List<VolumeRecord> Volumes { get; set; } = new();
    public BatteryRecord? Battery { get; set; }
    public NetworkRecord Network { get; set; } = new();
    public List<AntivirusProduct> Antivirus { get; set; } = new();
    public EventSummary Events { get; set; } = new();
    public List<StartupItem> Startup { get; set; } = new();
    public List<InstalledProgram> Programs { get; set; } = new();

    /// <summary>Checks that couldn't be completed, in plain words (for example "Drive health needs administrator rights").</summary>
    public List<string> Notes { get; set; } = new();

    public List<Finding> Findings { get; set; } = new();
}

public sealed class ComputerInfo
{
    public string Name { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string BiosVersion { get; set; } = "";
    public DateTime? BiosDate { get; set; }
    public string OsName { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string OsBuild { get; set; } = "";
    public string OsArchitecture { get; set; } = "";
    public DateTime? OsInstallDate { get; set; }
    public DateTime? LastBoot { get; set; }
    public string Cpu { get; set; } = "";
    public int? CpuCores { get; set; }
    public int? CpuThreads { get; set; }
    public long? RamBytes { get; set; }
    public List<MemoryModule> Memory { get; set; } = new();
    public DateTime? LastUpdateInstalled { get; set; }
}

public sealed class MemoryModule
{
    public string Slot { get; set; } = "";
    public long CapacityBytes { get; set; }
    public int? SpeedMhz { get; set; }
    public string Manufacturer { get; set; } = "";
    public string PartNumber { get; set; } = "";
}

public sealed class DiskRecord
{
    /// <summary>Windows disk number (the N in \\.\PhysicalDriveN), when known.</summary>
    public int? Number { get; set; }
    public string Name { get; set; } = "";
    public string Serial { get; set; } = "";
    public long? SizeBytes { get; set; }
    /// <summary>HDD, SSD, or Unknown.</summary>
    public string MediaType { get; set; } = "Unknown";
    /// <summary>SATA, NVMe, USB, and so on.</summary>
    public string BusType { get; set; } = "";
    /// <summary>Windows' own verdict: Healthy, Warning, Unhealthy or Unknown.</summary>
    public string Health { get; set; } = "Unknown";
    public string OperationalStatus { get; set; } = "";
    public int? TemperatureC { get; set; }
    /// <summary>SSD wear used up, 0 to 100 percent (100 = worn out).</summary>
    public int? WearPercent { get; set; }
    public long? PowerOnHours { get; set; }
    public long? ReadErrorsUncorrected { get; set; }
    public long? ReadErrorsTotal { get; set; }
}

public sealed class VolumeRecord
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public long SizeBytes { get; set; }
    public long FreeBytes { get; set; }
    public bool IsSystem { get; set; }
    /// <summary>"On", "Encrypted, protection suspended", "Off", or empty when not checked (needs administrator rights).</summary>
    public string BitLocker { get; set; } = "";

    [JsonIgnore]
    public bool IsEncrypted => BitLocker.StartsWith("On", StringComparison.OrdinalIgnoreCase) ||
                               BitLocker.StartsWith("Encrypted", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public double FreePercent => SizeBytes > 0 ? FreeBytes * 100.0 / SizeBytes : 0;
}

public sealed class BatteryRecord
{
    public string Name { get; set; } = "";
    public long? DesignCapacityMWh { get; set; }
    public long? FullChargeCapacityMWh { get; set; }
    public int? CycleCount { get; set; }
    public int? ChargePercent { get; set; }

    /// <summary>How much of its original capacity the battery still holds, in percent.</summary>
    public int? HealthPercent =>
        DesignCapacityMWh is > 0 && FullChargeCapacityMWh is >= 0
            ? (int)Math.Round(FullChargeCapacityMWh.Value * 100.0 / DesignCapacityMWh.Value)
            : null;
}

public sealed class NetworkRecord
{
    public List<NetAdapter> Adapters { get; set; } = new();
    public bool? GatewayReachable { get; set; }
    public bool? InternetReachable { get; set; }
    public long? InternetPingMs { get; set; }
    public bool? DnsWorks { get; set; }
}

public sealed class NetAdapter
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "";
    public string Mac { get; set; } = "";
    public long? SpeedMbps { get; set; }
    public bool? Dhcp { get; set; }
    public List<string> IPv4 { get; set; } = new();
    public List<string> Gateways { get; set; } = new();
    public List<string> Dns { get; set; } = new();
}

public sealed class AntivirusProduct
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public bool UpToDate { get; set; }

    /// <summary>Decodes Windows Security Center's productState number.</summary>
    public static AntivirusProduct FromProductState(string name, uint state) => new()
    {
        Name = name,
        Enabled = ((state >> 12) & 0xF) == 1,
        UpToDate = ((state >> 4) & 0xF) == 0,
    };
}

public sealed class EventSummary
{
    public int Days { get; set; } = 7;
    /// <summary>Times the PC lost power or reset without shutting down (Kernel-Power 41).</summary>
    public int? UnexpectedShutdowns { get; set; }
    /// <summary>Blue screens (BugCheck 1001).</summary>
    public int? BlueScreens { get; set; }
    /// <summary>Bad blocks Windows found on the computer's own drives (disk event 7). USB drives are left out.</summary>
    public int? DiskErrors { get; set; }
    /// <summary>Paging errors and retried reads on the computer's own drives (disk events 51 and 153). USB drives are left out.</summary>
    public int? DiskWarnings { get; set; }
    /// <summary>Names of the drives the disk events were about.</summary>
    public List<string> DiskEventDrives { get; set; } = new();
}

public sealed class StartupItem
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
    public string Location { get; set; } = "";
}

public sealed class InstalledProgram
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string InstallDate { get; set; } = "";
}

[JsonConverter(typeof(JsonStringEnumConverter<Severity>))]
public enum Severity
{
    Good = 0,
    Info = 1,
    Warning = 2,
    Problem = 3,
}

public sealed class Finding
{
    public Severity Severity { get; set; }
    public string Area { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";

    public Finding() { }

    public Finding(Severity severity, string area, string title, string detail = "")
    {
        Severity = severity;
        Area = area;
        Title = title;
        Detail = detail;
    }
}
