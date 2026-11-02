using NightTech.Core.Cleanup;

namespace NightTech.Core.Tests;

public class CleanupTests
{
    private static string Temp() => Path.Combine(Path.GetTempPath(), "ntclean-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = DateTime.Now;

    private static string Make(string path, int bytes = 100, double ageHours = 48)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        var t = Now.AddHours(-ageHours);
        File.SetLastWriteTime(path, t);
        File.SetCreationTime(path, t);
        return path;
    }

    [Fact]
    public void Temp_files_older_than_a_day_go_newer_ones_stay()
    {
        var root = Temp();
        try
        {
            var home = Path.Combine(root, "Users", "ann");
            var old = Make(Path.Combine(home, "AppData", "Local", "Temp", "old.tmp"), 1000, 48);
            var deep = Make(Path.Combine(home, "AppData", "Local", "Temp", "sub", "deep.tmp"), 500, 72);
            var fresh = Make(Path.Combine(home, "AppData", "Local", "Temp", "fresh.tmp"), 300, 1);
            var locs = Junk.Locations(Path.Combine(root, "Windows"), Path.Combine(root, "ProgramData"), new[] { home });
            var cat = Junk.Category("user-temp");
            var scan = Junk.Scan(cat, locs, Now, new[] { home });
            Assert.Equal(2, scan.Files);
            Assert.Equal(1500, scan.Bytes);
            var r = Junk.Clean(cat, locs, Now, new[] { home });
            Assert.Equal(1500, r.BytesFreed);
            Assert.False(File.Exists(old));
            Assert.False(File.Exists(deep));
            Assert.False(Directory.Exists(Path.GetDirectoryName(deep)));         // emptied subfolder removed
            Assert.True(File.Exists(fresh));
            Assert.True(Directory.Exists(Path.Combine(home, "AppData", "Local", "Temp"))); // the Temp folder itself stays
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void A_link_inside_a_junk_folder_is_never_followed()
    {
        if (OperatingSystem.IsWindows()) return; // symlinks need extra rights on Windows; the rule is the same
        var root = Temp();
        try
        {
            var home = Path.Combine(root, "Users", "ann");
            var precious = Make(Path.Combine(home, "Documents", "taxes.pdf"), 100, 1000);
            var temp = Path.Combine(home, "AppData", "Local", "Temp");
            Directory.CreateDirectory(temp);
            Directory.CreateSymbolicLink(Path.Combine(temp, "trap"), Path.Combine(home, "Documents"));
            File.CreateSymbolicLink(Path.Combine(temp, "trap.pdf"), precious);
            var locs = Junk.Locations(Path.Combine(root, "Windows"), Path.Combine(root, "ProgramData"), new[] { home });
            Junk.Clean(Junk.Category("user-temp"), locs, Now, new[] { home });
            Assert.True(File.Exists(precious));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Personal_folders_are_protected_whatever_a_location_says()
    {
        var home = Path.Combine(Path.GetTempPath(), "Users", "ann");
        Assert.True(Junk.IsProtected(Path.Combine(home, "Documents", "x"), new[] { home }));
        Assert.True(Junk.IsProtected(Path.Combine(home, "Desktop"), new[] { home }));
        Assert.True(Junk.IsProtected(home, new[] { home }));
        Assert.False(Junk.IsProtected(Path.Combine(home, "AppData", "Local", "Temp"), new[] { home }));
        var root = Temp();
        try
        {
            var h = Path.Combine(root, "ann");
            var doc = Make(Path.Combine(h, "Downloads", "setup.exe"), 10, 500);
            var bad = new[] { new JunkLocation("user-temp", Path.Combine(h, "Downloads")) };
            var r = Junk.Clean(Junk.Category("user-temp"), bad, Now, new[] { h });
            Assert.Equal(0, r.FilesRemoved);
            Assert.True(File.Exists(doc));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Browser_caches_are_found_in_every_profile_but_nothing_else_is_touched()
    {
        var root = Temp();
        try
        {
            var home = Path.Combine(root, "ann");
            var ud = Path.Combine(home, "AppData", "Local", "Google", "Chrome", "User Data");
            var c1 = Make(Path.Combine(ud, "Default", "Cache", "Cache_Data", "f_000001"), 2000, 0.1);
            var c2 = Make(Path.Combine(ud, "Profile 1", "Code Cache", "js", "a"), 3000, 0.1);
            var pw = Make(Path.Combine(ud, "Default", "Login Data"), 50, 100);
            var hist = Make(Path.Combine(ud, "Default", "History"), 50, 100);
            var locs = Junk.Locations(Path.Combine(root, "W"), Path.Combine(root, "PD"), new[] { home });
            var r = Junk.Clean(Junk.Category("browser-cache"), locs, Now, new[] { home });
            Assert.Equal(5000, r.BytesFreed);
            Assert.False(File.Exists(c1));
            Assert.False(File.Exists(c2));
            Assert.True(File.Exists(pw));
            Assert.True(File.Exists(hist));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Mac_app_caches_skip_browser_folders_so_nothing_is_counted_twice()
    {
        var root = Temp();
        try
        {
            var home = Path.Combine(root, "ann");
            Make(Path.Combine(home, "Library", "Caches", "com.spotify.client", "data"), 4000, 48);
            Make(Path.Combine(home, "Library", "Caches", "com.apple.Safari", "WebKitCache", "x"), 1000, 48);
            var locs = Junk.MacLocations(new[] { home });
            var app = Junk.Scan(Junk.Category("mac-user-cache"), locs, Now, new[] { home });
            var web = Junk.Scan(Junk.Category("mac-browser-cache"), locs, Now, new[] { home });
            Assert.Equal(4000, app.Bytes);
            Assert.Equal(1000, web.Bytes);
            Assert.False(Junk.Category("mac-trash").OnByDefault);
            Assert.False(Junk.Category("recycle-bin").OnByDefault);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Startup_approved_flags_match_task_manager()
    {
        Assert.True(Startup.IsEnabled(null));
        Assert.True(Startup.IsEnabled(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.True(Startup.IsEnabled(new byte[] { 6, 0, 0, 0 }));
        Assert.False(Startup.IsEnabled(new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.False(Startup.IsEnabled(new byte[] { 7, 0, 0, 0 }));
        var off = Startup.DisabledValue(new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(12, off.Length);
        Assert.False(Startup.IsEnabled(off));
        Assert.Equal(new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc), DateTime.FromFileTimeUtc(BitConverter.ToInt64(off, 4)));
        Assert.True(Startup.IsEnabled(Startup.EnabledValue()));
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", Startup.ApprovedKey(StartupSource.MachineRun32));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Steam\\steam.exe\" -silent", "C:\\Program Files\\Steam\\steam.exe")]
    [InlineData("C:\\Program Files\\Some App\\app.exe --tray", "C:\\Program Files\\Some App\\app.exe")]
    [InlineData("C:\\Windows\\system32\\rundll32.exe C:\\Program Files\\X\\x.dll,Start", "C:\\Program Files\\X\\x.dll")]
    [InlineData("notepad", "notepad")]
    [InlineData("C:\\my.company\\app\\uninstall.exe /S", "C:\\my.company\\app\\uninstall.exe")]
    public void Startup_commands_resolve_to_their_program(string command, string exe) =>
        Assert.Equal(exe, Startup.ExecutablePath(command));

    [Fact]
    public void Startup_advice_keeps_security_and_drivers_and_turns_off_launchers()
    {
        StartupItem I(string name, string cmd, string pub = "") { var i = new StartupItem { Name = name, Command = cmd, Publisher = pub }; Startup.Advise(i); return i; }
        Assert.Equal(StartupAdvice.Keep, I("SecurityHealth", "%windir%\\system32\\SecurityHealthSystray.exe").Advice);
        Assert.Equal(StartupAdvice.Keep, I("RtkAudUService", "C:\\Windows\\System32\\RtkAudUService64.exe").Advice);
        Assert.Equal(StartupAdvice.TurnOff, I("Steam", "\"C:\\Program Files (x86)\\Steam\\steam.exe\" -silent").Advice);
        Assert.Equal(StartupAdvice.TurnOff, I("Wave Browser", "C:\\Users\\a\\AppData\\Local\\WaveBrowser\\wavebrowser.exe").Advice);
        Assert.Equal(StartupAdvice.Optional, I("Acme Updater", "C:\\Acme\\update.exe").Advice);   // never switched off automatically
        Assert.Equal(StartupAdvice.Unknown, I("xq7731", "C:\\Users\\a\\AppData\\Roaming\\xq7731.exe").Advice);
    }

    [Fact]
    public void Unwanted_programs_are_flagged_and_runtimes_never_are()
    {
        var list = new List<InstalledProgram>
        {
            new() { Name = "Driver Booster 11" },
            new() { Name = "Wave Browser" },
            new() { Name = "McAfee LiveSafe" },
            new() { Name = "Norton 360" },
            new() { Name = "Microsoft Visual C++ 2015-2022 Redistributable (x64)" },
            new() { Name = "AnyDesk" },
            new() { Name = "Google Chrome" },
        };
        var notes = Programs.Review(list);
        Assert.Equal(FlagLevel.Unwanted, list[0].Level);
        Assert.Equal(FlagLevel.Unwanted, list[1].Level);
        Assert.Equal(FlagLevel.Review, list[2].Level);
        Assert.Equal(FlagLevel.None, list[4].Level);
        Assert.Equal(FlagLevel.Review, list[5].Level);
        Assert.Equal(FlagLevel.None, list[6].Level);
        Assert.Contains(notes, n => n.Contains("More than one antivirus"));
    }

    [Fact]
    public void Recommendations_come_from_the_measurements()
    {
        var r = CleanupReport.Recommend(new Snapshot { RamGb = 4, DriveKind = "HDD", FreeBytes = 10, TotalBytes = 100, BootSeconds = 120, Protection = false }, windows10: true);
        Assert.Contains(r, x => x.StartsWith("Memory"));
        Assert.Contains(r, x => x.Contains("SSD"));
        Assert.Contains(r, x => x.StartsWith("Space"));
        Assert.Contains(r, x => x.Contains("October 14, 2025"));
        Assert.Contains(r, x => x.StartsWith("Protection"));
        Assert.Empty(CleanupReport.Recommend(new Snapshot { RamGb = 16, DriveKind = "SSD", FreeBytes = 50, TotalBytes = 100, BootSeconds = 20, Protection = true }));
    }

    [Fact]
    public void Report_round_trips_and_the_page_is_safe()
    {
        var rep = new CleanupReport
        {
            Customer = "Ann <script>", Ticket = "NT-0101", Computer = "DESKTOP-1", Started = Now, Finished = Now, RestorePoint = true,
            Before = new Snapshot { FreeBytes = 10L << 30, TotalBytes = 100L << 30, StartupItemsOn = 14, Processes = 210, MemoryUsedPercent = 80, BootSeconds = 95 },
            After = new Snapshot { FreeBytes = 18L << 30, TotalBytes = 100L << 30, StartupItemsOn = 6, Processes = 160, MemoryUsedPercent = 55 },
            Junk = { new JunkCleanResult { CategoryId = "user-temp", Name = "Temporary files (each person)", BytesFreed = 8L << 30, FilesRemoved = 4000, FilesSkipped = 12 } },
            Tasks = { new TaskLine { Area = "Startup", What = "Turned off Steam", Result = ItemResult.Done } },
        };
        var dir = Temp();
        try
        {
            var (json, text, html) = rep.Save(dir);
            var back = CleanupReport.FromJson(File.ReadAllText(json))!;
            Assert.Equal("pc-cleanup", back.Kind);
            Assert.Equal(8L << 30, back.BytesFreed);
            var t = File.ReadAllText(text);
            Assert.Contains("JUNK REMOVED: 8 GB", t);
            Assert.Contains("14", t);
            Assert.Contains("not a guarantee", t);
            var h = File.ReadAllText(html);
            Assert.DoesNotContain("<script>", h);
            Assert.Contains("Ann &lt;script&gt;", h);
            Assert.Contains("startup program turned off", h);
            Assert.Contains("measured at next restart", h);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Advice_matches_whole_words_and_never_turns_off_security_software()
    {
        StartupItem I(string name, string cmd = "", string pub = "") { var i = new StartupItem { Name = name, Command = cmd, Publisher = pub }; Startup.Advise(i); return i; }
        Assert.Equal(StartupAdvice.Keep, I("Sophos AutoUpdate", "C:\\Program Files\\Sophos\\AutoUpdate\\update.exe").Advice);
        Assert.Equal(StartupAdvice.Keep, I("TrendMicro Update").Advice);
        Assert.NotEqual(StartupAdvice.TurnOff, I("KnowledgeBase", "C:\\KB\\kb.exe").Advice);
        Assert.NotEqual(StartupAdvice.TurnOff, I("NicholasBackup", "C:\\NB\\nb.exe").Advice);
        Assert.NotEqual(StartupAdvice.TurnOff, I("Original Prusa", "C:\\Prusa\\prusa.exe").Advice);
        Assert.NotEqual(StartupAdvice.Keep, I("ResetHelper", "C:\\R\\reset.exe").Advice);
        Assert.NotEqual(StartupAdvice.Keep, I("Widget", "C:\\Program Files\\amd64\\widget.exe").Advice); // folder names don't count
        Assert.Equal(StartupAdvice.TurnOff, I("MicrosoftEdgeAutoLaunch_123", "\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --no-startup-window").Advice);
        Assert.Equal(StartupAdvice.TurnOff, I("Origin", "C:\\Origin\\Origin.exe").Advice);
        Assert.Null(Programs.Flag("NicholasBackup", ""));
        Assert.Equal(FlagLevel.Review, Programs.Flag("Disney+", "")!.Level);
        Assert.Equal(FlagLevel.Unwanted, Programs.Flag("PCKeeper", "")!.Level);
    }

    [Fact]
    public void A_link_higher_up_the_path_stops_the_cleanup()
    {
        if (OperatingSystem.IsWindows()) return;
        var root = Temp();
        try
        {
            var other = Path.Combine(root, "other");
            var victim = Make(Path.Combine(other, "Logs", "keep.log"), 100, 1000);
            var home = Path.Combine(root, "ann");
            Directory.CreateDirectory(home);
            Directory.CreateSymbolicLink(Path.Combine(home, "Library"), other);   // ~/Library is a link
            var locs = Junk.MacLocations(new[] { home });
            var r = Junk.Clean(Junk.Category("mac-user-logs"), locs, Now, new[] { home });
            Assert.Equal(0, r.FilesRemoved);
            Assert.True(File.Exists(victim));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void Mac_system_caches_and_half_databases_are_left_alone()
    {
        var root = Temp();
        try
        {
            var home = Path.Combine(root, "ann");
            var caches = Path.Combine(home, "Library", "Caches");
            var icloud = Make(Path.Combine(caches, "com.apple.bird", "x.db"), 100, 1000);
            var kit = Make(Path.Combine(caches, "CloudKit", "y"), 100, 1000);
            var db = Make(Path.Combine(caches, "com.example.app", "Cache.db"), 100, 1000);
            var wal = Make(Path.Combine(caches, "com.example.app", "Cache.db-wal"), 100, 1);   // in use: keep the pair
            var junk = Make(Path.Combine(caches, "com.example.app", "old.bin"), 100, 1000);
            var r = Junk.Clean(Junk.Category("mac-user-cache"), Junk.MacLocations(new[] { home }), Now, new[] { home });
            Assert.True(File.Exists(icloud));
            Assert.True(File.Exists(kit));
            Assert.True(File.Exists(db));
            Assert.True(File.Exists(wal));
            Assert.False(File.Exists(junk));
            Assert.Equal(1, r.FilesRemoved);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\unins000.exe\" /SILENT", "C:\\Program Files\\App\\unins000.exe", "/SILENT")]
    [InlineData("C:\\Program Files\\App Two\\uninstall.exe /S", "C:\\Program Files\\App Two\\uninstall.exe", "/S")]
    [InlineData("C:\\my.company\\app\\uninstall.exe /S", "C:\\my.company\\app\\uninstall.exe", "/S")]
    [InlineData("rundll32.exe C:\\Windows\\x.dll,Uninstall /q", "rundll32.exe", "C:\\Windows\\x.dll,Uninstall /q")]
    [InlineData("RunDll32 C:\\PROGRA~2\\COMMON~1\\INSTAL~1\\PROFES~1\\RunTime\\11\\50\\Intel32\\Ctor.dll,LaunchSetup \"C:\\x\\setup.exe\" -removeonly", "RunDll32", "C:\\PROGRA~2\\COMMON~1\\INSTAL~1\\PROFES~1\\RunTime\\11\\50\\Intel32\\Ctor.dll,LaunchSetup \"C:\\x\\setup.exe\" -removeonly")]
    [InlineData("MsiExec.exe /I{1234}", "MsiExec.exe", "/I{1234}")]
    public void Uninstall_commands_split_into_program_and_arguments(string command, string file, string args)
    {
        var (f, a) = Startup.SplitCommand(command);
        Assert.Equal(file, f);
        Assert.Equal(args, a);
    }
}
