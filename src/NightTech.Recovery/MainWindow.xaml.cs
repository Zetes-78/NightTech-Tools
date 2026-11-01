using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NightTech.Core.Imaging;
using NightTech.Recovery.Windows;

namespace NightTech.Recovery;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;
    private Task? _job;
    private bool _caseNameTouched;
    private bool _settingCaseName;
    private string? _lastCaseFolder;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + App.ToolVersion;
        App.Log.LineWritten += line => Dispatcher.InvokeAsync(() => AddLogLine(line));
        FolderBox.Text = DefaultOutputFolder();
        RefreshDisks();
    }

    // ---------------- Drive list ----------------

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDisks();

    private void RefreshDisks()
    {
        try
        {
            var disks = PhysicalDisks.List();
            DiskList.ItemsSource = disks;
            App.Log.Info($"Found {disks.Count} drive(s).");
            if (disks.Count == 0)
                DiskWarning.Text = "No drives found. Night Tech Recovery must run as administrator.";
        }
        catch (Exception ex)
        {
            App.Log.Error("Could not list drives.", ex);
            DiskWarning.Text = "Could not list drives: " + ex.Message;
        }
    }

    private DiskInfo? SelectedDisk => DiskList.SelectedItem as DiskInfo;

    private void DiskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var d = SelectedDisk;
        DiskWarning.Text = d switch
        {
            null => "",
            { HoldsWindows: true } => "This is the drive this PC is running Windows from. Imaging it while Windows runs gives an inconsistent copy. Use it only for testing.",
            _ when OutputIsOnDisk(d!) => "Your save folder is on this same drive. Pick a different drive to save to.",
            _ => "",
        };
        SuggestCaseName();
    }

    // ---------------- Case details ----------------

    private void CaseInputs_Changed(object sender, TextChangedEventArgs e)
    {
        if (sender == TicketBox) SuggestCaseName();
        UpdateCaseHint();
    }

    private void CaseBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingCaseName) _caseNameTouched = CaseBox.Text.Length > 0;
        UpdateCaseHint();
    }

    private void SuggestCaseName()
    {
        if (_caseNameTouched) return;
        _settingCaseName = true;
        CaseBox.Text = CaseFiles.SuggestName(TicketBox.Text, SelectedDisk?.Serial);
        _settingCaseName = false;
    }

    private void UpdateCaseHint()
    {
        if (string.IsNullOrWhiteSpace(FolderBox.Text) || string.IsNullOrWhiteSpace(CaseBox.Text))
        {
            CaseHint.Text = "";
            StartButton.Content = "Start imaging";
            return;
        }
        var files = CaseFiles.For(FolderBox.Text.Trim(), CaseBox.Text);
        if (files.CanResume)
        {
            CaseHint.Text = $"This case already exists. Starting will resume it from where it stopped.\n{files.Folder}";
            StartButton.Content = "Resume imaging";
        }
        else
        {
            CaseHint.Text = $"Files will be saved in:\n{files.Folder}";
            StartButton.Content = "Start imaging";
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose where to save recovery cases" };
        if (Directory.Exists(FolderBox.Text)) dlg.InitialDirectory = FolderBox.Text;
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    private static string DefaultOutputFolder()
    {
        // Prefer a non-Windows fixed drive with the most free space; fall back to Documents.
        try
        {
            var winRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            var best = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed &&
                            !string.Equals(d.RootDirectory.FullName, winRoot, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.AvailableFreeSpace)
                .FirstOrDefault();
            if (best != null) return Path.Combine(best.RootDirectory.FullName, "Night Tech Recovery Cases");
        }
        catch { /* ignore */ }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Night Tech Recovery Cases");
    }

    private bool OutputIsOnDisk(DiskInfo d)
    {
        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0) return false;
        var probe = Directory.Exists(folder) ? folder : Path.GetPathRoot(Path.GetFullPath(folder)) ?? folder;
        return PhysicalDisks.DisksOfVolume(probe).Contains(d.Number);
    }

    // ---------------- Run ----------------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var disk = SelectedDisk;
        if (disk is null) { Warn("Pick the customer's drive first."); return; }
        if (CheckHealthy.IsChecked != true || CheckWriteSafe.IsChecked != true)
        {
            Warn("Please confirm both items under \"Before you start\".");
            return;
        }
        var root = FolderBox.Text.Trim();
        if (root.Length == 0) { Warn("Choose a folder to save the image in."); return; }
        if (string.IsNullOrWhiteSpace(CaseBox.Text)) { Warn("Give the case a name (the Hub ticket number works well)."); return; }

        try { Directory.CreateDirectory(root); }
        catch (Exception ex) { Warn("Can't use that save folder: " + ex.Message); return; }

        if (OutputIsOnDisk(disk))
        {
            Warn("The save folder is on the drive you're copying. Saving there would overwrite the data you're trying to recover. Pick a folder on a different drive.");
            return;
        }
        if (disk.HoldsWindows &&
            MessageBox.Show(this, "This is the drive Windows is running from. The copy won't be consistent while Windows is using it.\n\nContinue anyway (for testing only)?",
                "Night Tech Recovery", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var files = CaseFiles.For(root, CaseBox.Text);
        Directory.CreateDirectory(files.Folder);
        if (!HasSpace(files, disk)) return;

        var info = new CaseInfo(CaseFiles.SafeName(CaseBox.Text), Clean(TicketBox.Text), Clean(TechBox.Text), Clean(NotesBox.Text));
        _lastCaseFolder = files.Folder;
        SetRunning(true);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var progress = new Progress<ImagingProgress>(ShowProgress);
        App.Log.Info($"{(files.CanResume ? "Resuming" : "Starting")} case '{info.CaseName}' from {disk.Display}.");

        try
        {
            var job = Task.Run(() =>
            {
                using var source = new RawDiskSource(disk);
                var r = new ImagingEngine(App.Log).Run(source, files.ImagePath, files.MapPath, new ImagingOptions(), progress, token);
                var (txt, _) = ImagingReport.Write(r, info, App.ToolVersion);
                return (Result: r, Report: txt);
            }, token);
            _job = job;
            var done = await job;
            Finished(done.Result, done.Report);
        }
        catch (OperationCanceledException)
        {
            PhaseText.Text = "Stopped before it started.";
        }
        catch (Win32Exception ex)
        {
            App.Log.Error("Could not open the drive.", ex);
            Warn(ex.Message);
        }
        catch (Exception ex)
        {
            App.Log.Error("Imaging failed.", ex);
            Warn("Imaging stopped: " + ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _job = null;
            SetRunning(false);
            UpdateCaseHint();
        }
    }

    private bool HasSpace(CaseFiles files, DiskInfo disk)
    {
        try
        {
            if (File.Exists(files.ImagePath)) return true; // resuming: space already reserved
            var root = Path.GetPathRoot(Path.GetFullPath(files.Folder));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < disk.Size + 50L * 1024 * 1024)
            {
                Warn($"Not enough space. The image needs {ImagingReport.FormatBytes(disk.Size)} but {root} has {ImagingReport.FormatBytes(free)} free.");
                return false;
            }
        }
        catch (Exception ex)
        {
            App.Log.Warn("Couldn't check free space: " + ex.Message);
        }
        return true;
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is null) return;
        StopButton.IsEnabled = false;
        PhaseText.Text = "Stopping and saving progress...";
        _cts.Cancel();
    }

    private void ShowProgress(ImagingProgress p)
    {
        PhaseText.Text = p.Phase switch
        {
            ImagingPhase.Starting => "Starting...",
            ImagingPhase.FastCopy => "Pass 1 of 3: fast copy",
            ImagingPhase.Scraping => "Pass 2 of 3: going back over damaged areas",
            ImagingPhase.Retrying => "Pass 3 of 3: retrying unreadable sectors",
            ImagingPhase.Hashing => "Checking the image (SHA-256)...",
            ImagingPhase.Done => "Finished",
            _ => p.Phase.ToString(),
        };
        double pct = p.Phase == ImagingPhase.Hashing && p.SourceLength > 0
            ? 100.0 * p.CurrentOffset / p.SourceLength
            : p.PercentDone;
        Bar.Value = pct;
        PercentText.Text = $"{pct:0.0}%";
        GoodText.Text = ImagingReport.FormatBytes(p.BytesGood);
        BadText.Text = p.BadSectorCount == 0 ? "None" : $"{p.BadSectorCount:N0} sectors ({ImagingReport.FormatBytes(p.BytesBad)})";
        PendingText.Text = ImagingReport.FormatBytes(p.BytesPending);
        SpeedText.Text = p.BytesPerSecond > 0 ? ImagingReport.FormatBytes((long)p.BytesPerSecond) + "/s" : "-";
        EtaText.Text = p.BytesPerSecond > 0 && p.BytesPending > 0 && p.Phase == ImagingPhase.FastCopy
            ? FormatEta(TimeSpan.FromSeconds(p.BytesPending / p.BytesPerSecond))
            : "-";
    }

    private void Finished(ImagingResult r, string reportPath)
    {
        PhaseText.Text = ImagingReport.StatusText(r.Status);
        OpenFolderButton.IsEnabled = true;
        var icon = r.Status switch
        {
            ImagingStatus.Completed => MessageBoxImage.Information,
            ImagingStatus.CompletedWithBadSectors => MessageBoxImage.Warning,
            _ => MessageBoxImage.Exclamation,
        };
        var msg = ImagingReport.StatusText(r.Status);
        if (r.Message is not null) msg += "\n\n" + r.Message;
        msg += $"\n\nCopied: {ImagingReport.FormatBytes(r.Map.BytesGood)}\nUnreadable sectors: {r.Map.BadSectors.Count:N0}";
        if (r.ImageSha256 is not null) msg += $"\nSHA-256: {r.ImageSha256}";
        msg += $"\n\nReport: {reportPath}";
        MessageBox.Show(this, msg, "Night Tech Recovery", MessageBoxButton.OK, icon);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_lastCaseFolder is not null && Directory.Exists(_lastCaseFolder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastCaseFolder}\"") { UseShellExecute = true });
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        RefreshButton.IsEnabled = !running;
        DiskList.IsEnabled = !running;
        FolderBox.IsEnabled = TicketBox.IsEnabled = CaseBox.IsEnabled = !running;
        if (running) OpenFolderButton.IsEnabled = false;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_job is null || _cts is null) return;
        e.Cancel = true;
        if (MessageBox.Show(this, "Imaging is running. Stop it and close? Progress is saved so you can resume later.",
                "Night Tech Recovery", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _cts.Cancel();
        try { await _job; } catch { /* already logged */ }
        Closing -= Window_Closing;
        Close();
    }

    // ---------------- Helpers ----------------

    private void AddLogLine(string line)
    {
        LogList.Items.Add(line);
        if (LogList.Items.Count > 500) LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, "Night Tech Recovery", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static string? Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string FormatEta(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{t.Minutes} min" : "under a minute";
}
