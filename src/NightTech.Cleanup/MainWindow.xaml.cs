using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using NightTech.Cleanup.Windows;
using NightTech.Core.Cleanup;
using Format = NightTech.Core.Diagnostics.Format;

namespace NightTech.Cleanup;

public abstract class Row : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public sealed class JunkRow : Row
{
    private bool _selected;
    private string _size = "-", _files = "-";
    public required JunkCategory Category { get; init; }
    public bool Selected { get => _selected; set { _selected = value; Notify(); } }
    public string Name => Category.Name;
    public string About => Category.Description + (Category.Caution is { } c ? "  " + c : "");
    public string SizeText { get => _size; set { _size = value; Notify(); } }
    public string FilesText { get => _files; set { _files = value; Notify(); } }
    public long Bytes { get; set; }
}

public sealed class StartupRow : Row
{
    public required StartupItem Item { get; init; }
    public Action<StartupRow, bool>? Changed;
    public bool On
    {
        get => Item.Enabled;
        set { if (value == Item.Enabled) return; Changed?.Invoke(this, value); Notify(); }
    }
    public string Name => Item.Name;
    public string Publisher => Item.Publisher;
    public string Why => Item.Why;
    public string AdviceText => Item.Advice switch { StartupAdvice.Keep => "Keep", StartupAdvice.TurnOff => "Turn off", StartupAdvice.Optional => "Ask", _ => "Unknown" };
    public void Refresh() => Notify(nameof(On));
}

public sealed class ProgramRow
{
    public required InstalledProgram P { get; init; }
    public string LevelText => P.Level switch { FlagLevel.Unwanted => "Unwanted", FlagLevel.Review => "Review", _ => "" };
    public string Name => P.Name + (P.IsStoreApp ? "  (Store app)" : "");
    public string Publisher => P.Publisher;
    public string InstalledText => P.Installed is { } d ? d.ToString("MMM yyyy") : "";
    public string SizeText => P.SizeBytes > 0 ? Format.Bytes(P.SizeBytes) : "";
    public string Why => P.Why;
}

public partial class MainWindow : Window
{
    private readonly CleanupReport _report = new() { Started = DateTime.Now, Computer = Environment.MachineName };
    private readonly List<string> _profiles = Machine.UserProfiles();
    private List<JunkLocation> _locations = new();
    private readonly List<JunkRow> _junk;
    private List<StartupRow> _startup = new();
    private List<ProgramRow> _programs = new();
    private readonly List<(StartupItem Item, bool WasOn)> _startupChanges = new();
    private CancellationTokenSource? _cts;
    private bool _restoreDone;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + App.ToolVersion;
        _report.ToolVersion = App.ToolVersion;
        App.Log.LineWritten += line => Dispatcher.InvokeAsync(() =>
        {
            LogList.Items.Add(line);
            if (LogList.Items.Count > 400) LogList.Items.RemoveAt(0);
            LogList.ScrollIntoView(LogList.Items[^1]);
        });
        TechBox.Text = Environment.UserName;
        _locations = Junk.Locations(Machine.WindowsDir, Machine.ProgramData, _profiles);
        _junk = Junk.Categories.Select(c => new JunkRow { Category = c, Selected = c.OnByDefault }).ToList();
        JunkGrid.ItemsSource = _junk;
        UserNote.Text = $"Signed in as {Environment.UserName}. Startup changes apply to this Windows account (and to everyone for machine-wide items). " +
                        $"Junk is cleared for all {_profiles.Count} account(s) on this PC.";
        if (Machine.ConsoleUser() is { Length: > 0 } console && !console.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase))
        {
            UserNote.Text = $"CAUTION: Windows is signed in as {console}, but this tool is running as {Environment.UserName}. Startup, proxy and Recycle Bin changes " +
                            $"would apply to {Environment.UserName}, not the customer. Close this, sign in as the customer and use right-click > Run as administrator.";
            App.Log.Warn(UserNote.Text);
        }
        Loaded += async (_, _) =>
        {
            await Job("Reading this PC", ct => { LoadStartup(); return Task.CompletedTask; });
            _ = RefreshDefenderText();
            _ = Task.Run(CheckHijacks);
        };
    }

    // ---------------- Busy handling ----------------

    private bool IsBusy => _cts is not null;

    private async Task<bool> Job(string title, Func<CancellationToken, Task> work)
    {
        if (IsBusy) { Warn("Wait for the current job to finish (or press Stop)."); return false; }
        _cts = new CancellationTokenSource();
        BusyText.Text = title + "...";
        Busy.IsIndeterminate = true;
        StopButton.IsEnabled = true;
        App.Log.Info(title + ".");
        try { await work(_cts.Token); return true; }
        catch (OperationCanceledException) { App.Log.Warn(title + ": stopped."); return false; }
        catch (Exception ex) { App.Log.Error(title + " failed.", ex); Warn(title + " didn't finish: " + ex.Message); return false; }
        finally
        {
            _cts.Dispose();
            _cts = null;
            Busy.IsIndeterminate = false;
            StopButton.IsEnabled = false;
            BusyText.Text = "Ready";
            if (_closeAfterJob) { _closeAfterJob = false; _ = Dispatcher.InvokeAsync(Close); }
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e) { _cts?.Cancel(); BusyText.Text = "Stopping..."; }

    private void Say(string text) => Dispatcher.InvokeAsync(() => BusyText.Text = text);

    private void AddTask(string area, string what, ItemResult result, string detail = "")
    {
        _report.Tasks.Add(new TaskLine { Area = area, What = what, Result = result, Detail = detail });
        App.Log.Info($"{area}: {what} [{result}] {detail}");
    }

    // ---------------- Start ----------------

    private async void Restore_Click(object sender, RoutedEventArgs e) => await Job("Making a restore point", async ct => await MakeRestorePoint());

    private async Task<bool> MakeRestorePoint()
    {
        var problem = await Task.Run(Machine.CreateRestorePoint);
        if (problem is not null && problem.Contains("turned off") &&
            MessageBox.Show(this, "System Restore is turned off on this PC, so no restore point can be made.\n\nTurn it on for the Windows drive and try again? (It uses a little disk space.)",
                "Night Tech Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            if (await Task.Run(Machine.EnableSystemRestore)) problem = await Task.Run(Machine.CreateRestorePoint);
        }
        _restoreDone = problem is null;
        _report.RestorePoint = _restoreDone;
        _report.RestorePointNote = problem ?? "";
        RestoreText.Text = problem is null ? $"Restore point made at {DateTime.Now:h:mm tt}." : problem;
        App.Log.Info(problem is null ? "Restore point made." : "Restore point: " + problem);
        return _restoreDone;
    }

    private async void Measure_Click(object sender, RoutedEventArgs e) => await Job("Measuring", async ct => await MeasureBefore());

    private async Task MeasureBefore()
    {
        var on = _startup.Count(s => s.Item.Enabled);
        _report.Before = await Task.Run(() => Machine.Measure(on));
        BeforeText.Text = Describe(_report.Before);
    }

    private static string Describe(Snapshot s) =>
        $"Free space:        {Format.Bytes(s.FreeBytes)} of {Format.Bytes(s.TotalBytes)} ({s.FreePercent:0}%)\n" +
        $"Startup programs:  {s.StartupItemsOn} on\n" +
        $"Programs running:  {s.Processes}\n" +
        $"Memory in use:     {s.MemoryUsedPercent:0}% of {s.RamGb:0.#} GB\n" +
        $"Start-up time:     {(s.BootSeconds is { } b ? $"{b:0} seconds (last measured by Windows)" : "not measured")}\n" +
        $"Drive:             {(s.DriveKind.Length > 0 ? s.DriveKind : "unknown type")}\n" +
        $"Protection:        {(s.Protection switch { true => "on and up to date", false => "NOT confirmed", _ => "not checked" })}\n" +
        $"Windows:           {s.System}";

    // ---------------- Quick cleanup ----------------

    private async void Quick_Click(object sender, RoutedEventArgs e)
    {
        var willTurnOff = _startup.Where(r => r.Item.Advice == StartupAdvice.TurnOff && r.Item.Enabled).Select(r => r.Item.Name).ToList();
        var msg = "Run the quick cleanup now?\n\nAsk the customer to save their work and close their programs first. Browsers will be closed so their caches can be cleared.";
        msg += willTurnOff.Count == 0 ? "\n\nNo startup programs will be turned off."
            : $"\n\nThese startup programs will be turned off (check with the customer; you can turn any back on):\n  - {string.Join("\n  - ", willTurnOff.Take(15))}{(willTurnOff.Count > 15 ? $"\n  ...and {willTurnOff.Count - 15} more" : "")}";
        if (_junk.Any(j => j.Selected && j.Category.Id == "recycle-bin")) msg += "\n\nThe Recycle Bin will be EMPTIED: anything in it is gone for good. Make sure the customer agrees.";
        if (MessageBox.Show(this, msg, "Night Tech Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Job("Quick cleanup", async ct =>
        {
            Say("Making a restore point...");
            if (!_restoreDone && !await MakeRestorePoint() &&
                MessageBox.Show(this, $"No restore point: {_report.RestorePointNote}\n\nContinue without one?", "Night Tech Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            Say("Measuring before...");
            if (_report.Before is null) await MeasureBefore();
            ct.ThrowIfCancellationRequested();

            CloseBrowsers();
            Say("Scanning for junk...");
            await ScanJunk(ct);
            Say("Removing junk...");
            await CleanJunk(ct);

            Say("Turning off suggested startup programs...");
            ApplyStartupSuggestions();

            var d = await Task.Run(Defender.Status);
            if (d.Active)
            {
                Say("Updating Microsoft Defender...");
                await UpdateDefender(ct);
                Say("Quick scan with Microsoft Defender (5-15 minutes)...");
                await RunScan(full: false, ct);
            }
            else AddTask("Security", "Microsoft Defender scan", ItemResult.Skipped, d.Message);

            Say("Removing old Windows components (can take 10+ minutes)...");
            await RunRepair("components", ct);
            Say("Optimizing the drive...");
            await RunRepair("optimize", ct);
            ct.ThrowIfCancellationRequested();

            Say("Measuring after...");
            await MeasureAfter();
            Tabs.SelectedIndex = 6;
            MessageBox.Show(this, $"Quick cleanup done: {Format.Bytes(_report.BytesFreed)} of junk removed.\n\nNow review the Programs and Security tabs with the customer, then save the report on the Finish tab.",
                "Night Tech Cleanup", MessageBoxButton.OK, MessageBoxImage.Information);
        });
    }

    // ---------------- Junk ----------------

    private async void ScanJunk_Click(object sender, RoutedEventArgs e) => await Job("Scanning for junk", ScanJunk);

    private async Task ScanJunk(CancellationToken ct)
    {
        long total = 0;
        foreach (var row in _junk)
        {
            ct.ThrowIfCancellationRequested();
            Say($"Scanning: {row.Name}...");
            if (row.Category.Id == "recycle-bin")
            {
                var (bytes, items) = await Task.Run(SpecialJunk.RecycleBinSize, ct);
                row.Bytes = bytes; row.SizeText = Format.Bytes(bytes); row.FilesText = items.ToString("N0");
            }
            else
            {
                var r = await Task.Run(() => Junk.Scan(row.Category, _locations, DateTime.Now, _profiles, ct), ct);
                row.Bytes = r.Bytes; row.SizeText = Format.Bytes(r.Bytes); row.FilesText = r.Files.ToString("N0");
            }
            total += row.Bytes;
        }
        JunkTotal.Text = $"Found {Format.Bytes(total)} in total. Checked items: {Format.Bytes(_junk.Where(j => j.Selected).Sum(j => j.Bytes))}.";
        CleanJunkButton.IsEnabled = true;
    }

    private async void CleanJunk_Click(object sender, RoutedEventArgs e)
    {
        if (_junk.Any(j => j.Selected && j.Category.Id == "recycle-bin") &&
            MessageBox.Show(this, "Empty the Recycle Bin? Anything in it is gone for good. Make sure the customer agrees.", "Night Tech Cleanup",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await Job("Removing junk", CleanJunk);
    }

    private async Task CleanJunk(CancellationToken ct)
    {
        foreach (var row in _junk.Where(j => j.Selected))
        {
            ct.ThrowIfCancellationRequested();
            Say($"Removing: {row.Name}...");
            JunkCleanResult r;
            switch (row.Category.Id)
            {
                case "recycle-bin":
                    var (before, _) = SpecialJunk.RecycleBinSize();
                    var ok = await Task.Run(SpecialJunk.EmptyRecycleBin, ct);
                    r = new JunkCleanResult { CategoryId = row.Category.Id, Name = row.Name, BytesFreed = ok ? before : 0 };
                    break;
                case "delivery-opt":
                    var dBefore = (await Task.Run(() => Junk.Scan(row.Category, _locations, DateTime.Now, _profiles, ct), ct)).Bytes;
                    var dOk = await Task.Run(SpecialJunk.ClearDeliveryOptimization, ct);
                    if (dOk)
                    {
                        var dAfter = (await Task.Run(() => Junk.Scan(row.Category, _locations, DateTime.Now, _profiles, ct), ct)).Bytes;
                        r = new JunkCleanResult { CategoryId = row.Category.Id, Name = row.Name, BytesFreed = Math.Max(0, dBefore - dAfter) };
                    }
                    else r = await Task.Run(() => Junk.Clean(row.Category, _locations, DateTime.Now, _profiles, ct), ct);
                    break;
                case "windows-temp" when Machine.RestartPending():
                    App.Log.Warn("Windows temporary files skipped: an update or install is waiting for a restart. Restart, then clean again.");
                    AddTask("Junk", "Windows temporary files", ItemResult.Skipped, "an update or install was waiting for a restart; restart, then clean again");
                    continue;
                case "update-cache":
                    await Task.Run(SpecialJunk.PauseUpdates, ct);
                    try { r = await Task.Run(() => Junk.Clean(row.Category, _locations, DateTime.Now, _profiles, ct), ct); }
                    finally { await Task.Run(SpecialJunk.ResumeUpdates); }
                    break;
                default:
                    r = await Task.Run(() => Junk.Clean(row.Category, _locations, DateTime.Now, _profiles, ct), ct);
                    break;
            }
            var existing = _report.Junk.FirstOrDefault(j => j.CategoryId == r.CategoryId);
            if (existing is null) _report.Junk.Add(r);
            else { existing.BytesFreed += r.BytesFreed; existing.FilesRemoved += r.FilesRemoved; existing.FilesSkipped += r.FilesSkipped; }
            row.SizeText = "removed " + Format.Bytes(r.BytesFreed);
            row.FilesText = r.FilesSkipped > 0 ? $"{r.FilesSkipped:N0} in use" : "0";
            App.Log.Info($"Junk: {row.Name}: {Format.Bytes(r.BytesFreed)} removed, {r.FilesRemoved} files, {r.FilesSkipped} in use.");
        }
        JunkTotal.Text = $"Removed {Format.Bytes(_report.BytesFreed)} so far. Files in use are left alone; Windows clears most of them later.";
    }

    private void CloseBrowsers_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Close Chrome, Edge, Brave and Firefox now? Unsaved work in them is lost. Browsers reopen tabs normally.", "Night Tech Cleanup",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) CloseBrowsers();
    }

    /// <summary>Closes the browsers in this Windows session only (never another signed-in person's): windows first, so they shut down cleanly, then anything left.</summary>
    private static void CloseBrowsers()
    {
        var session = Process.GetCurrentProcess().SessionId;
        foreach (var name in new[] { "chrome", "msedge", "brave", "firefox" })
        {
            var mine = Process.GetProcessesByName(name).Where(p => { try { return p.SessionId == session; } catch (Exception e) when (e is InvalidOperationException or Win32Exception) { return false; } }).ToList();
            foreach (var p in mine) { try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch (Exception e) when (e is InvalidOperationException or Win32Exception) { } }
            var until = DateTime.Now.AddSeconds(8);
            while (DateTime.Now < until && mine.Any(p => { try { return !p.HasExited; } catch (InvalidOperationException) { return false; } })) Thread.Sleep(250);
            foreach (var p in mine)
            {
                try { if (!p.HasExited) p.Kill(); }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
            }
        }
    }

    // ---------------- Startup ----------------

    private void LoadStartup_Click(object sender, RoutedEventArgs e) => LoadStartup();

    private void LoadStartup()
    {
        _startup = StartupReader.Read().Select(i => new StartupRow { Item = i, Changed = ChangeStartup }).ToList();
        StartupGrid.ItemsSource = _startup;
    }

    private void ChangeStartup(StartupRow row, bool on)
    {
        try
        {
            var was = row.Item.Enabled;
            StartupReader.Set(row.Item, on);
            if (!_startupChanges.Any(c => c.Item.Name == row.Item.Name && c.Item.Source == row.Item.Source)) _startupChanges.Add((row.Item, was));
            _report.Tasks.RemoveAll(t => t.Area == "Startup" && (t.What == "Turned on " + row.Item.Name || t.What == "Turned off " + row.Item.Name));
            var original = _startupChanges.First(c => c.Item.Name == row.Item.Name && c.Item.Source == row.Item.Source).WasOn;
            if (on != original) AddTask("Startup", (on ? "Turned on " : "Turned off ") + row.Item.Name, ItemResult.Done, row.Item.Why);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Warn($"Couldn't change {row.Item.Name}: {ex.Message}");
            row.Refresh();
        }
    }

    private void ApplyStartup_Click(object sender, RoutedEventArgs e)
    {
        var n = ApplyStartupSuggestions();
        MessageBox.Show(this, n == 0 ? "Nothing else marked 'Turn off' is on." : $"Turned off {n} startup program(s). Tick 'On' to bring any back.", "Night Tech Cleanup");
    }

    private int ApplyStartupSuggestions()
    {
        int n = 0;
        foreach (var row in _startup.Where(r => r.Item.Advice == StartupAdvice.TurnOff && r.Item.Enabled))
        {
            row.On = false;
            n++;
        }
        return n;
    }

    private void UndoStartup_Click(object sender, RoutedEventArgs e)
    {
        int n = 0;
        foreach (var (item, wasOn) in _startupChanges.ToList())
        {
            var row = _startup.FirstOrDefault(r => r.Item.Name == item.Name && r.Item.Source == item.Source);
            if (row is not null && row.On != wasOn) { row.On = wasOn; n++; }
        }
        _report.Tasks.RemoveAll(t => t.Area == "Startup");
        _startupChanges.Clear();
        MessageBox.Show(this, $"Put back {n} startup program(s).", "Night Tech Cleanup");
    }

    private void StartupGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) { /* changes apply as soon as a box is ticked */ }

    // ---------------- Programs ----------------

    private async void LoadPrograms_Click(object sender, RoutedEventArgs e) => await LoadPrograms();

    private async Task LoadPrograms()
    {
        await Job("Listing installed programs", async ct =>
        {
            var desktop = await Task.Run(ProgramReader.Desktop, ct);
            var notes = Programs.Review(desktop);
            var store = await Task.Run(ProgramReader.StoreAppsWorthReviewing, ct);
            _programs = desktop.Concat(store).Select(p => new ProgramRow { P = p })
                .OrderByDescending(r => r.P.Level).ThenBy(r => r.P.Name, StringComparer.OrdinalIgnoreCase).ToList();
            ShowPrograms();
            var flagged = _programs.Count(p => p.P.Level != FlagLevel.None);
            ProgramNotes.Text = $"{desktop.Count} programs and {store.Count} flagged Store apps. {flagged} flagged. " +
                                "Uninstall opens each program's own uninstaller: watch for 'keep my settings' or extra offers, and decline them. " + string.Join(" ", notes);
            foreach (var n in notes) if (!_report.Findings.Contains(n)) _report.Findings.Add(n);
        });
    }

    private void FlaggedOnly_Click(object sender, RoutedEventArgs e) => ShowPrograms();

    private void ShowPrograms() =>
        ProgramGrid.ItemsSource = FlaggedOnlyBox.IsChecked == true ? _programs.Where(p => p.P.Level != FlagLevel.None).ToList() : _programs;

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var chosen = ProgramGrid.SelectedItems.OfType<ProgramRow>().ToList();
        if (chosen.Count == 0) { Warn("Select the programs to remove first (Ctrl+click for several)."); return; }
        if (MessageBox.Show(this, $"Uninstall {chosen.Count} program(s)?\n\n{string.Join("\n", chosen.Select(c => "  " + c.P.Name))}\n\nMake sure the customer agrees. Each program's own uninstaller opens in turn.",
                "Night Tech Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Job("Uninstalling", async ct =>
        {
            foreach (var row in chosen)
            {
                ct.ThrowIfCancellationRequested();
                Say($"Uninstalling {row.P.Name} (finish its uninstaller)...");
                int code;
                try { code = await Task.Run(() => ProgramReader.Uninstall(row.P, ct), ct); }
                catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException) { AddTask("Programs", "Remove " + row.P.Name, ItemResult.Failed, ex.Message); continue; }
                var still = !row.P.IsStoreApp
                    ? (await Task.Run(ProgramReader.Desktop, ct)).Any(p => p.Name == row.P.Name)
                    : (await Task.Run(ProgramReader.StoreAppsWorthReviewing, ct)).Any(p => p.PackageName == row.P.PackageName);
                AddTask("Programs", "Removed " + row.P.Name, still ? ItemResult.Warning : ItemResult.Done,
                    still ? "the uninstaller closed but the program is still listed (it may need a restart, or the uninstall was cancelled)" : row.P.Why);
            }
        });
        await LoadPrograms();
    }

    // ---------------- Security ----------------

    private async Task RefreshDefenderText()
    {
        var d = await Task.Run(Defender.Status);
        DefenderText.Text = d.Message;
        QuickScanButton.IsEnabled = FullScanButton.IsEnabled = d.Available && d.Active;
    }

    private async void QuickScan_Click(object sender, RoutedEventArgs e) => await Job("Updating Defender and running a quick scan", async ct =>
    {
        await UpdateDefender(ct);
        await RunScan(false, ct);
    });

    private async void FullScan_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "A full scan checks every file and usually takes an hour or more. The PC can be used meanwhile. Start it?", "Night Tech Cleanup",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Job("Full scan with Microsoft Defender", async ct => { await UpdateDefender(ct); await RunScan(true, ct); });
    }

    private async Task UpdateDefender(CancellationToken ct)
    {
        var u = await Task.Run(() => Defender.UpdateSignatures(Say, ct), ct);
        _report.Tasks.RemoveAll(t => t.Area == "Security" && t.What == "Updated Microsoft Defender's virus definitions");
        AddTask("Security", "Updated Microsoft Defender's virus definitions", u.ExitCode == 0 ? ItemResult.Done : ItemResult.Warning,
            u.ExitCode == 0 ? "" : "the update didn't finish (no internet?): the scan used the definitions already on the PC");
    }

    private async Task RunScan(bool full, CancellationToken ct)
    {
        var start = DateTime.Now.AddMinutes(-1);
        var r = await Task.Run(() => Defender.Scan(full, Say, ct), ct);
        var threats = await Task.Run(() => Defender.ThreatsSince(start), ct);
        foreach (var t in threats) if (!_report.Threats.Contains(t)) _report.Threats.Add(t);
        var what = full ? "Full scan with Microsoft Defender" : "Quick scan with Microsoft Defender";
        if (r.ExitCode == -1) { AddTask("Security", what, ItemResult.Skipped, "stopped before it finished"); return; }
        // MpCmdRun returns 2 both for "threats found" and for errors, so a scan only counts as done when it says it finished.
        bool finished = r.ExitCode == 0 || r.Output.Contains("Scan finished", StringComparison.OrdinalIgnoreCase);
        if (!finished && threats.Count == 0)
        {
            AddTask("Security", what, ItemResult.Warning, "the scan didn't finish: " + Proc.Trim(r.Output, 160));
            ThreatText.Text = $"{what} didn't finish. Open Windows Security and run it there.";
            await RefreshDefenderText();
            return;
        }
        AddTask("Security", what, threats.Count == 0 ? ItemResult.Done : ItemResult.Warning, threats.Count == 0 ? "no threats found" : $"{threats.Count} threat(s) found: see below");
        ThreatText.Text = threats.Count == 0 ? $"{what}: no threats found." : $"{what} found:\n" + string.Join("\n", threats);
        await RefreshDefenderText();
    }

    private void OpenSecurity_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("windowsdefender:") { UseShellExecute = true });

    private void CheckHijacks_Click(object sender, RoutedEventArgs e) => _ = Task.Run(CheckHijacks);

    /// <summary>Runs in the background; everything it changes on screen or in the report happens back on the window's thread.</summary>
    private void CheckHijacks()
    {
        var found = new List<string>();
        string? proxy = null;
        var hosts = new List<string>();
        try { found.AddRange(Hijacks.Browsers(_profiles)); } catch (Exception ex) { App.Log.Warn("Browser check: " + ex.Message); }
        try { proxy = Hijacks.Proxy(); } catch (Exception ex) { App.Log.Warn("Proxy check: " + ex.Message); }
        if (proxy is not null) found.Insert(0, "PROXY: " + proxy);
        try { hosts = Hijacks.HostsEntries(); } catch (Exception ex) { App.Log.Warn("Hosts check: " + ex.Message); }
        if (hosts.Count > 0) found.Insert(0, $"HOSTS FILE: {hosts.Count} custom entr{(hosts.Count == 1 ? "y" : "ies")}, e.g. {hosts[0]}");
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var f in found.Where(f => !f.Contains(": extension \"")))
            {
                var clean = f.Replace("PROXY: ", "").Replace("HOSTS FILE: ", "The hosts file has ");
                if (!_report.Findings.Contains(clean)) _report.Findings.Add(clean);
            }
            HijackList.ItemsSource = found.Count == 0 ? new List<string> { "Nothing unusual found." } : found;
            ResetProxyButton.IsEnabled = proxy is not null;
            ResetHostsButton.IsEnabled = hosts.Count > 0;
        });
    }

    private void ResetProxy_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Turn off the proxy for this Windows account? If the customer's work set it on purpose (some businesses do), leave it.", "Night Tech Cleanup",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Hijacks.ResetProxy();
        AddTask("Security", "Turned off a proxy setting", ItemResult.Done, "web traffic now goes directly");
        _ = Task.Run(CheckHijacks);
    }

    private void ResetHosts_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Reset the hosts file to Windows' default? A copy of the current one is saved next to it.", "Night Tech Cleanup",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var backup = Hijacks.ResetHosts();
            AddTask("Security", "Reset the hosts file", ItemResult.Done, "old copy saved as " + Path.GetFileName(backup));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn("Couldn't reset the hosts file: " + ex.Message + " (antivirus may be protecting it)."); }
        _ = Task.Run(CheckHijacks);
    }

    // ---------------- Repairs ----------------

    private async void Repair_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((Button)sender).Tag;
        if (tag == "update") { Process.Start(new ProcessStartInfo("ms-settings:windowsupdate") { UseShellExecute = true }); return; }
        await Job(((Button)sender).Content.ToString() ?? "Repair", ct => RunRepair(tag, ct));
    }

    private async Task RunRepair(string tag, CancellationToken ct)
    {
        string what;
        Func<(bool, string)> run;
        switch (tag)
        {
            case "sfc": what = "Checked Windows files (SFC)"; run = () => Repairs.Sfc(Say, ct); break;
            case "dism": what = "Repaired the Windows image (DISM)"; run = () => Repairs.RestoreHealth(Say, ct); break;
            case "components": what = "Removed old Windows components"; run = () => Repairs.ComponentCleanup(Say, ct); break;
            case "optimize": what = "Optimized the drive"; run = () => Repairs.Optimize(Say, ct); break;
            case "chkdsk": what = "Checked the drive for errors"; run = () => Repairs.CheckDisk(Say, ct); break;
            case "dns": what = "Cleared saved web addresses (DNS)"; run = () => Repairs.FlushDns(ct); break;
            default: throw new ArgumentException(tag);
        }
        var (ok, summary) = await Task.Run(run, ct);
        ct.ThrowIfCancellationRequested();
        AddTask("Repair", what, ok ? ItemResult.Done : ItemResult.Warning, summary);
        RepairText.Text = $"{what}: {summary}";
    }

    // ---------------- Finish ----------------

    private async void MeasureAfter_Click(object sender, RoutedEventArgs e) => await Job("Measuring after", async ct => await MeasureAfter());

    private async Task MeasureAfter()
    {
        if (_report.Before is null) { Warn("There's no 'before' measurement to compare with. Measure on the Start tab first."); return; }
        var on = _startup.Count(s => s.Item.Enabled);
        var after = await Task.Run(() => Machine.Measure(on));
        after.BootSeconds = _report.Before.BootSeconds; // only the next restart measures start-up again
        _report.After = after;
        CompareText.Text = string.Join("\n", _report.Compare().Select(c => $"{c.Label,-22} {c.Before,-20} -> {c.After}")) +
                           $"\n\nJunk removed: {Format.Bytes(_report.BytesFreed)}";
        _report.Recommendations = CleanupReport.Recommend(after, Machine.IsWindows10);
        RecommendText.Text = _report.Recommendations.Count == 0 ? "None: this PC's hardware is in good shape." : "• " + string.Join("\n• ", _report.Recommendations);
    }

    private string? _lastReport;

    private async void Report_Click(object sender, RoutedEventArgs e)
    {
        if (_report.After is null && _report.Before is not null) await Job("Measuring after", async ct => await MeasureAfter());
        _report.Customer = CustomerBox.Text.Trim();
        _report.Ticket = TicketBox.Text.Trim();
        _report.Technician = TechBox.Text.Trim();
        _report.Notes = NotesBox.Text.Trim();
        _report.Finished = DateTime.Now;
        if (_report.Recommendations.Count == 0 && _report.After is { } a) _report.Recommendations = CleanupReport.Recommend(a, Machine.IsWindows10);
        try
        {
            var (json, _, html) = _report.Save(App.ReportsFolder);
            _lastReport = html;
            ReportText.Text = $"Saved. For the Hub ticket: {Path.GetFileName(json)}";
            App.Log.Info("Report saved: " + html);
            Process.Start(new ProcessStartInfo(html) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn("Couldn't save the report: " + ex.Message); }
    }

    private void OpenReports_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.ReportsFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", _lastReport is null ? $"\"{App.ReportsFolder}\"" : $"/select,\"{_lastReport}\"") { UseShellExecute = true });
    }

    private bool _closeAfterJob;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (IsBusy)
        {
            // Never exit mid-job: Windows Update could be left paused or a setting half-changed. Stop, let it tidy up, then close.
            e.Cancel = true;
            if (_closeAfterJob) return;
            if (MessageBox.Show(this, "A job is still running. Stop it and close when it has tidied up?", "Night Tech Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _closeAfterJob = true;
            BusyText.Text = "Stopping safely, then closing...";
            _cts?.Cancel();
            return;
        }
        if (_report.Tasks.Count + _report.Junk.Count > 0 && _lastReport is null &&
            MessageBox.Show(this, "The report hasn't been saved. Close anyway?", "Night Tech Cleanup", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    private void Warn(string m) => MessageBox.Show(this, m, "Night Tech Cleanup", MessageBoxButton.OK, MessageBoxImage.Warning);
}
