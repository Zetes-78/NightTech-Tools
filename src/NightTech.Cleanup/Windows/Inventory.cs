using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using NightTech.Core.Cleanup;

namespace NightTech.Cleanup.Windows;

/// <summary>Startup programs, read and switched on/off the way Task Manager does.</summary>
public static class StartupReader
{
    public static List<StartupItem> Read()
    {
        var list = new List<StartupItem>();
        void FromKey(RegistryKey hive, string path, StartupSource src)
        {
            try
            {
                using var k = hive.OpenSubKey(path);
                if (k is null) return;
                using var approved = (Startup.IsMachineWide(src) ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(Startup.ApprovedKey(src));
                foreach (var name in k.GetValueNames())
                {
                    if (name.Length == 0 || k.GetValue(name) is not string cmd) continue;
                    list.Add(Item(name, cmd, src, approved?.GetValue(name) as byte[]));
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { App.Log.Warn($"Couldn't read {path}: {e.Message}"); }
        }
        FromKey(Registry.CurrentUser, Startup.UserRunKey, StartupSource.UserRun);
        FromKey(Registry.LocalMachine, Startup.UserRunKey.Replace("Software", "SOFTWARE"), StartupSource.MachineRun);
        FromKey(Registry.LocalMachine, Startup.MachineRun32Key, StartupSource.MachineRun32);

        void FromFolder(string folder, StartupSource src)
        {
            if (!Directory.Exists(folder)) return;
            using var approved = (Startup.IsMachineWide(src) ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(Startup.ApprovedKey(src));
            foreach (var f in Directory.GetFiles(folder))
            {
                var name = Path.GetFileName(f);
                if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(Item(name, f, src, approved?.GetValue(name) as byte[]));
            }
        }
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.UserStartupFolder);
        FromFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.CommonStartupFolder);
        return list.OrderBy(i => i.Advice).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static StartupItem Item(string name, string cmd, StartupSource src, byte[]? approved)
    {
        var item = new StartupItem { Name = name, Command = cmd, Source = src, Enabled = Startup.IsEnabled(approved) };
        try
        {
            var exe = item.Exe;
            if (File.Exists(exe)) item.Publisher = FileVersionInfo.GetVersionInfo(exe).CompanyName?.Trim() ?? "";
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
        Startup.Advise(item);
        return item;
    }

    /// <summary>Turns an item on or off. The program's own startup entry is left in place, so this can always be undone.</summary>
    public static void Set(StartupItem item, bool enabled)
    {
        var hive = Startup.IsMachineWide(item.Source) ? Registry.LocalMachine : Registry.CurrentUser;
        using var k = hive.CreateSubKey(Startup.ApprovedKey(item.Source), true);
        k.SetValue(item.Name, enabled ? Startup.EnabledValue() : Startup.DisabledValue(DateTime.UtcNow), RegistryValueKind.Binary);
        item.Enabled = enabled;
    }
}

/// <summary>Installed programs (desktop and Microsoft Store), and running their own uninstallers.</summary>
public static class ProgramReader
{
    public static List<InstalledProgram> Desktop()
    {
        var list = new List<InstalledProgram>();
        void From(RegistryKey hive, string path)
        {
            try
            {
                using var k = hive.OpenSubKey(path);
                if (k is null) return;
                foreach (var sub in k.GetSubKeyNames())
                {
                    using var p = k.OpenSubKey(sub);
                    if (p is null) continue;
                    var name = (p.GetValue("DisplayName") as string ?? "").Trim();
                    if (name.Length == 0 || p.GetValue("SystemComponent") is int sc && sc == 1 || p.GetValue("ParentKeyName") is string) continue;
                    if (p.GetValue("ReleaseType") is string rt && (rt.Contains("Update") || rt.Contains("Hotfix"))) continue;
                    var cmd = p.GetValue("UninstallString") as string ?? "";
                    if (cmd.Length == 0) continue;
                    DateTime? installed = DateTime.TryParseExact(p.GetValue("InstallDate") as string, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                    long size = p.GetValue("EstimatedSize") is int kb ? kb * 1024L : 0;
                    list.Add(new InstalledProgram
                    {
                        Name = name, Publisher = (p.GetValue("Publisher") as string ?? "").Trim(), Version = p.GetValue("DisplayVersion") as string ?? "",
                        Installed = installed, SizeBytes = size, UninstallCommand = cmd,
                    });
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { App.Log.Warn($"Couldn't read {path}: {e.Message}"); }
        }
        const string u = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        From(Registry.LocalMachine, u);
        From(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall");
        From(Registry.CurrentUser, u);
        return list.GroupBy(p => p.Name + "|" + p.Version).Select(g => g.First()).ToList();
    }

    /// <summary>Microsoft Store apps that look unwanted (games and trials that came with the PC). Everything else is left out of the list.</summary>
    public static List<InstalledProgram> StoreAppsWorthReviewing()
    {
        var list = new List<InstalledProgram>();
        var json = Proc.PowerShellJson("Get-AppxPackage -AllUsers | Where-Object { -not $_.IsFramework -and $_.SignatureKind -eq 'Store' } | Select-Object Name, PackageFullName, Publisher, Version", TimeSpan.FromMinutes(2));
        foreach (var e in Proc.Items(json))
        {
            var name = Proc.Str(e, "Name");
            if (Programs.Flag(name, "") is not { } f) continue;
            list.Add(new InstalledProgram { Name = name, Publisher = Proc.Str(e, "Publisher"), Version = Proc.Str(e, "Version"), IsStoreApp = true, PackageName = Proc.Str(e, "PackageFullName"), Level = f.Level, Why = f.Why });
        }
        return list.GroupBy(p => p.Name).Select(g => g.First()).ToList();
    }

    /// <summary>Program and arguments from an uninstall command. MsiExec /I (repair) becomes /X (remove).</summary>
    public static (string File, string Args) Split(string command)
    {
        var exe = Startup.ExecutablePath(command);
        var c = command.Trim();
        string rest = c.StartsWith('"') ? c[(c.IndexOf('"', 1) + 1)..].Trim() : c.Length > exe.Length && c.StartsWith(exe, StringComparison.OrdinalIgnoreCase) ? c[exe.Length..].Trim() : "";
        if (Path.GetFileName(exe).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("MsiExec.exe", StringComparison.OrdinalIgnoreCase))
        {
            exe = Proc.Sys("msiexec.exe");
            rest = System.Text.RegularExpressions.Regex.Replace(rest, @"/I\s*(\{)", "/X$1", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return (exe, rest);
    }

    /// <summary>Opens the program's own uninstaller and waits for it to close (up to 30 minutes). The technician clicks through it.</summary>
    public static int Uninstall(InstalledProgram p, CancellationToken ct)
    {
        if (p.IsStoreApp)
        {
            var r = Proc.PowerShell($"Get-AppxPackage -AllUsers | Where-Object PackageFullName -eq {Proc.Ps(p.PackageName)} | Remove-AppxPackage -AllUsers", TimeSpan.FromMinutes(5), ct);
            return r.ExitCode;
        }
        var (file, args) = Split(p.UninstallCommand);
        using var proc = Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        if (proc is null) return -1;
        var sw = Stopwatch.StartNew();
        while (!proc.WaitForExit(1000))
        {
            if (ct.IsCancellationRequested || sw.Elapsed > TimeSpan.FromMinutes(30)) return -1;
        }
        return proc.ExitCode;
    }
}
