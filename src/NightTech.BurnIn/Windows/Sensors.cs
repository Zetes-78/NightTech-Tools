using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;

namespace NightTech.BurnIn.Windows;

/// <summary>Readings Windows itself provides: temperature, processor speed, hardware errors. All read only.</summary>
public static class Sensors
{
    /// <summary>
    /// The hottest thermal zone Windows reports, in °C (null if this computer doesn't report one; many desktops don't).
    /// It's the motherboard's reading of the processor area, so treat it as a guide.
    /// </summary>
    public static double? TemperatureC()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            s.Options.Timeout = TimeSpan.FromSeconds(5);
            double? max = null;
            foreach (ManagementBaseObject o in s.Get())
            {
                if (o["CurrentTemperature"] is null) continue;
                var c = Convert.ToDouble(o["CurrentTemperature"], CultureInfo.InvariantCulture) / 10.0 - 273.15;
                if (c is > 5 and < 125 && (max is null || c > max)) max = c; // ignore obviously bogus zones
            }
            return max;
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException or FormatException) { return null; }
    }

    /// <summary>Processor speed as a percent of its rated speed (above 100 when boosting). Null if not available.</summary>
    public static double? PerformancePercent()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\CIMV2", "SELECT Name, PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation WHERE Name='_Total'");
            s.Options.Timeout = TimeSpan.FromSeconds(5);
            foreach (ManagementBaseObject o in s.Get())
                if (o["PercentProcessorPerformance"] is { } v) return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException or FormatException) { }
        return null;
    }

    /// <summary>Uncorrected hardware errors (WHEA critical/error: processor, memory, PCIe) since a time. Null if the log couldn't be read.</summary>
    public static int? HardwareErrorsSince(DateTime since) =>
        Count($"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2) and TimeCreated[@SystemTime>='{since.ToUniversalTime():o}']]]");

    /// <summary>Errors the hardware corrected itself (WHEA warnings) since a time.</summary>
    public static int? CorrectedErrorsSince(DateTime since) =>
        Count($"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and Level=3 and TimeCreated[@SystemTime>='{since.ToUniversalTime():o}']]]");

    /// <summary>"The system rebooted without cleanly shutting down first" (Kernel-Power 41) since a time.</summary>
    public static int? UnexpectedShutdownsSince(DateTime since) =>
        Count($"*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41 and TimeCreated[@SystemTime>='{since.ToUniversalTime():o}']]]");

    private static int? Count(string xpath)
    {
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, xpath));
            int n = 0;
            for (var e = reader.ReadEvent(); e is not null && n < 10000; e = reader.ReadEvent()) { e.Dispose(); n++; }
            return n;
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException) { return null; }
    }

    [Flags] private enum Exec : uint { Continuous = 0x80000000, System = 0x00000001, Display = 0x00000002 }
    [DllImport("kernel32.dll")] private static extern uint SetThreadExecutionState(Exec flags);

    /// <summary>Stops the computer sleeping (and the screen turning off) while a test runs. Call from the thread that runs the test.</summary>
    public static void KeepAwake(bool on) => SetThreadExecutionState(on ? Exec.Continuous | Exec.System | Exec.Display : Exec.Continuous);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExtended;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus s);

    /// <summary>Installed memory, and how much can be used now without squeezing Windows: the smaller of free memory and remaining commit (page file included).</summary>
    public static (long Total, long Available) Memory()
    {
        var s = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref s) ? ((long)s.TotalPhys, (long)Math.Min(s.AvailPhys, s.AvailPage)) : (0, 0);
    }

    [StructLayout(LayoutKind.Sequential)] private struct PowerThrottling { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottling info, uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();

    /// <summary>Asks Windows not to move the test onto power-saving cores or slow it down when the window isn't in front.</summary>
    public static void FullSpeed()
    {
        try
        {
            var p = new PowerThrottling { Version = 1, ControlMask = 1 /* execution speed */, StateMask = 0 /* off */ };
            SetProcessInformation(GetCurrentProcess(), 4 /* ProcessPowerThrottling */, ref p, (uint)Marshal.SizeOf<PowerThrottling>());
        }
        catch (EntryPointNotFoundException) { }
    }

    [StructLayout(LayoutKind.Sequential)] private struct PowerStatus { public byte AcLine, Battery, Percent, Reserved; public int LifeTime, FullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus s);

    /// <summary>True on mains power, false on battery, null if unknown (desktops report "on mains").</summary>
    public static bool? OnMains() => GetSystemPowerStatus(out var s) ? s.AcLine switch { 1 => true, 0 => false, _ => null } : null;
}
