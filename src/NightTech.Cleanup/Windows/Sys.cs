using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using NightTech.Core.Cleanup;

namespace NightTech.Cleanup.Windows;

public sealed record ProcResult(int ExitCode, string Output);

public static class Proc
{
    /// <summary>A Windows program by full path, so a look-alike earlier on the PATH is never run as administrator.</summary>
    public static string Sys(string exe) => Path.Combine(Environment.SystemDirectory, exe);

    /// <summary>Runs a program without a window, collecting what it prints, with a time limit. Lines are passed to <paramref name="line"/> as they arrive.</summary>
    public static ProcResult Run(string file, string args, TimeSpan timeout, Action<string>? line = null, Encoding? encoding = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (encoding is not null) { psi.StandardOutputEncoding = encoding; psi.StandardErrorEncoding = encoding; }
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start " + file);
        var output = new StringBuilder();
        void Got(string? d)
        {
            if (d is null) return;
            d = d.Replace("\0", "").TrimEnd('\r');
            lock (output) output.AppendLine(d);
            if (d.Trim().Length > 0) line?.Invoke(d.Trim());
        }
        p.OutputDataReceived += (_, e) => Got(e.Data);
        p.ErrorDataReceived += (_, e) => Got(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        var sw = Stopwatch.StartNew();
        while (!p.WaitForExit(500))
        {
            if (ct.IsCancellationRequested || sw.Elapsed > timeout)
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                lock (output) return new ProcResult(-1, output + (ct.IsCancellationRequested ? "\n(stopped)" : "\n(timed out)"));
            }
        }
        Thread.Sleep(200);
        lock (output) return new ProcResult(p.ExitCode, output.ToString());
    }

    /// <summary>Runs a PowerShell script as an encoded command, so quotes and spaces can't break it.</summary>
    public static ProcResult PowerShell(string script, TimeSpan timeout, CancellationToken ct = default)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Stop'; " + script));
        return Run(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded, timeout, ct: ct);
    }

    public static string Ps(string s) => "'" + s.Replace("'", "''") + "'";

    /// <summary>Runs a PowerShell expression that ends with ConvertTo-Json, and parses the result (null if it failed or printed nothing).</summary>
    public static JsonElement? PowerShellJson(string script, TimeSpan timeout)
    {
        var r = PowerShell(script + " | ConvertTo-Json -Depth 4 -Compress", timeout);
        if (r.ExitCode != 0) { App.Log.Warn("PowerShell failed: " + Trim(r.Output)); return null; }
        var text = r.Output.Trim();
        if (text.Length == 0) return null;
        try { using var d = JsonDocument.Parse(text); return d.RootElement.Clone(); }
        catch (JsonException) { App.Log.Warn("Unexpected PowerShell output: " + Trim(text)); return null; }
    }

    public static string Trim(string s, int max = 400) { s = s.Trim(); return s.Length <= max ? s : s[..max] + "..."; }

    /// <summary>A JSON value as a list: PowerShell returns a lone object instead of a one-item list.</summary>
    public static IEnumerable<JsonElement> Items(JsonElement? e) =>
        e is not { } v ? Enumerable.Empty<JsonElement>() : v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : new[] { v };

    public static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? p.ToString() : "";
}

/// <summary>This computer: user profiles, measurements, restore point.</summary>
public static class Machine
{
    public static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public static string SystemDrive => Path.GetPathRoot(WindowsDir) ?? @"C:\";

    /// <summary>Every real user profile on the PC (from the registry's profile list), not system accounts.</summary>
    public static List<string> UserProfiles()
    {
        var list = new List<string>();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (k is not null)
                foreach (var sid in k.GetSubKeyNames().Where(s => s.StartsWith("S-1-5-21-", StringComparison.Ordinal)))
                {
                    using var p = k.OpenSubKey(sid);
                    if (p?.GetValue("ProfileImagePath") is string path)
                    {
                        path = Environment.ExpandEnvironmentVariables(path);
                        if (Directory.Exists(path) && !Junk.IsLink(path)) list.Add(path);
                    }
                }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { }
        var me = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (list.Count == 0 && me.Length > 0) list.Add(me);
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx m);

    public static Snapshot Measure(int startupOn)
    {
        var s = new Snapshot { Taken = DateTime.Now, StartupItemsOn = startupOn, Processes = Process.GetProcesses().Length, System = WindowsName() };
        try
        {
            var d = new DriveInfo(SystemDrive);
            s.FreeBytes = d.AvailableFreeSpace;
            s.TotalBytes = d.TotalSize;
        }
        catch (IOException) { }
        var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (GlobalMemoryStatusEx(ref m)) { s.MemoryUsedPercent = m.dwMemoryLoad; s.RamGb = Math.Round(m.ullTotalPhys / 1073741824.0, 1); }

        var info = Proc.PowerShellJson(@"
$boot = $null
try {
  $e = Get-WinEvent -LogName 'Microsoft-Windows-Diagnostics-Performance/Operational' -FilterXPath '*[System[EventID=100]]' -MaxEvents 1
  $boot = [int](([xml]$e.ToXml()).Event.EventData.Data | Where-Object Name -eq 'BootTime').'#text'
} catch {}
$kind = ''
try { $kind = [string](Get-Partition -DriveLetter $env:SystemDrive.Substring(0,1) | Get-Disk | Get-PhysicalDisk | Select-Object -First 1).MediaType } catch {}
$prot = $null
try { $mp = Get-MpComputerStatus; $prot = [bool]($mp.AntivirusEnabled -and $mp.RealTimeProtectionEnabled -and $mp.AntivirusSignatureAge -le 7) } catch {}
if ($prot -eq $null -or $prot -eq $false) {
  try { $av = Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct | Where-Object { ($_.productState -band 0x1000) -ne 0 -and $_.displayName -notmatch 'Defender' }; if ($av) { $prot = $true } } catch {}
}
[pscustomobject]@{ BootMs = $boot; Kind = $kind; Protected = $prot }", TimeSpan.FromSeconds(60));
        if (info is { } i)
        {
            if (double.TryParse(Proc.Str(i, "BootMs"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms > 0) s.BootSeconds = Math.Round(ms / 1000.0);
            var kind = Proc.Str(i, "Kind");
            s.DriveKind = kind switch { "SSD" or "4" => "SSD", "HDD" or "3" => "HDD", _ => "" };
            s.Protection = Proc.Str(i, "Protected") switch { "True" or "true" => true, "False" or "false" => false, _ => null };
        }
        return s;
    }

    public static string WindowsName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = k?.GetValue("ProductName") as string ?? "Windows";
            var build = k?.GetValue("CurrentBuildNumber") as string ?? "";
            if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
            return $"{product} {k?.GetValue("DisplayVersion") as string} (build {build})".Replace("  ", " ").Trim();
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { return "Windows"; }
    }

    public static bool IsWindows10 => int.TryParse(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "0") as string, out var b) && b is > 0 and < 22000;

    /// <summary>
    /// Makes a restore point before any change. Windows normally allows only one a day; the limit is lifted just for
    /// this one and put back. Returns null on success, or why it couldn't.
    /// </summary>
    public static string? CreateRestorePoint()
    {
        const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
        object? old = null;
        try
        {
            using (var k = Registry.LocalMachine.CreateSubKey(key, true))
            {
                old = k.GetValue("SystemRestorePointCreationFrequency");
                k.SetValue("SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);
            }
            var r = Proc.PowerShell("Checkpoint-Computer -Description 'Before Night Tech Cleanup' -RestorePointType 'MODIFY_SETTINGS'", TimeSpan.FromMinutes(5));
            if (r.ExitCode == 0) return null;
            if (r.Output.Contains("disabled", StringComparison.OrdinalIgnoreCase) || r.Output.Contains("0x80042302", StringComparison.OrdinalIgnoreCase))
                return "System Restore is turned off on this PC.";
            return "Windows couldn't make a restore point: " + Proc.Trim(r.Output, 200);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return "Couldn't make a restore point: " + e.Message;
        }
        finally
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(key, true);
                if (k is not null)
                {
                    if (old is null) k.DeleteValue("SystemRestorePointCreationFrequency", false);
                    else k.SetValue("SystemRestorePointCreationFrequency", old, RegistryValueKind.DWord);
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
    }

    /// <summary>Turns System Restore on for the Windows drive (only when the technician chooses to).</summary>
    public static bool EnableSystemRestore() =>
        Proc.PowerShell($"Enable-ComputerRestore -Drive {Proc.Ps(SystemDrive)}", TimeSpan.FromMinutes(2)).ExitCode == 0;
}
