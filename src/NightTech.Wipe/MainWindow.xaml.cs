using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using NightTech.Core.Diagnostics;
using NightTech.Core.Wipe;
using NightTech.Wipe.Windows;

namespace NightTech.Wipe;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cts;
    private bool _running;
    private string? _lastRecord;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {App.ToolVersion}";
        ModeText.Text = App.Portable ? "Running from the USB kit" : "Installed on this computer";
        App.Log.LineWritten += line => Dispatcher.InvokeAsync(() =>
        {
            LogList.Items.Add(line);
            if (LogList.Items.Count > 300) LogList.Items.RemoveAt(0);
            LogList.ScrollIntoView(line);
        });
        App.Log.Info("Wipe records are saved in: " + App.ReportsFolder);
        Loaded += (_, _) => LoadDisks();
    }

    private DiskInfo? Selected => DiskList.SelectedItem as DiskInfo;

    // ---------------- Picking the drive ----------------

    private bool _loading;

    /// <summary>Lists drives in the background (card readers and sleeping USB drives can take a few seconds).</summary>
    private async void LoadDisks()
    {
        if (_loading) return;
        _loading = true;
        DiskList.ItemsSource = null;
        ConfirmBox.Text = "";
        CheckOk.IsChecked = false;
        BlockedText.Text = "Looking for drives...";
        UpdateState();
        try
        {
            var disks = await Task.Run(() => Disks.List(App.ReportsFolder));
            DiskList.ItemsSource = disks;
            App.Log.Info($"Found {disks.Count} drive(s); {disks.Count(d => d.IsProtected)} protected.");
        }
        catch (Exception ex)
        {
            App.Log.Error("Couldn't list the drives.", ex);
            MessageBox.Show("Couldn't list the drives: " + ex.Message, "Night Tech Wipe", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _loading = false;
            UpdateState();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadDisks();

    private void DiskList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ConfirmBox.Text = "";
        CheckOk.IsChecked = false;
        var d = Selected;
        if (d is null)
        {
            DetailBox.Visibility = Visibility.Collapsed;
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Disk {d.Number}: {d.Model}");
            sb.AppendLine($"Serial:   {(d.UsableSerial.Length == 0 ? "(no usable serial" + (string.IsNullOrWhiteSpace(d.Serial) ? "" : ": reported \"" + d.Serial + "\"") + ")" : d.UsableSerial)}");
            sb.AppendLine($"Size:     {Format.Bytes(d.Size)} ({d.Size:N0} bytes)");
            sb.AppendLine($"Type:     {d.MediaType}, connected by {d.Bus}");
            if (d.Volumes.Count == 0) sb.AppendLine("On it:    no volumes Windows can read");
            foreach (var v in d.Volumes)
                sb.AppendLine($"On it:    {(v.Paths.Length > 0 ? v.Paths : "(no drive letter)")} {v.Label} " +
                              (v.Size > 0 ? $"{Format.Bytes(v.Size - v.Free)} used of {Format.Bytes(v.Size)}" : ""));
            if (d.IsProtected) sb.AppendLine("PROTECTED: " + string.Join("; ", d.ProtectedReasons));
            if (d.MediaType == "SSD")
                sb.AppendLine("Note: on SSDs an overwrite may not reach spare memory. The record says so; use the maker's secure erase too for the highest assurance.");
            DetailText.Text = sb.ToString().TrimEnd();
            DetailBox.Visibility = Visibility.Visible;
            ConfirmLabel.Text = d.UsableSerial.Length == 0
                ? $"This drive didn't report a usable serial number (common with USB docks). Make sure it's the only customer drive connected, then type  ERASE DISK {d.Number}  to confirm:"
                : "Type the drive's serial number to confirm (check it against the label on the drive):";
        }
        UpdateState();
    }

    private void Inputs_Changed(object sender, RoutedEventArgs e) => UpdateState();

    private static string Squash(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    private bool ConfirmMatches(DiskInfo d)
    {
        var typed = Squash(ConfirmBox.Text);
        if (typed.Length == 0) return false;
        return d.UsableSerial.Length == 0 ? typed == Squash($"ERASE DISK {d.Number}") : typed == Squash(d.UsableSerial);
    }

    /// <summary>The Erase button only lights up when every safety step is done. The reason it's off is always shown.</summary>
    private void UpdateState()
    {
        if (WipeButton is null) return; // during startup
        string why =
            _running ? "" :
            _loading ? "Looking for drives..." :
            Selected is not { } d ? "Pick a drive." :
            d.IsProtected ? "This drive is protected and can't be erased: " + string.Join("; ", d.ProtectedReasons) + "." :
            d.WriteProtected ? "This drive is write-protected. Check for a lock switch on the drive or adapter." :
            string.IsNullOrWhiteSpace(TicketBox.Text) ? "Enter the ticket number." :
            CheckOk.IsChecked != true ? "Tick the box to confirm the customer agreed." :
            !ConfirmMatches(d) ? (string.IsNullOrWhiteSpace(ConfirmBox.Text) ? "Type the confirmation above." : "That doesn't match.") :
            "";
        BlockedText.Text = why;
        WipeButton.IsEnabled = !_running && !_loading && why.Length == 0;
        RefreshButton.IsEnabled = !_running && !_loading;
        DiskList.IsEnabled = !_running && !_loading;
    }

    // ---------------- Erasing ----------------

    private async void Wipe_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } disk || !WipeButton.IsEnabled) return;

        var volumes = disk.Volumes.Count == 0 ? "nothing Windows can read"
            : string.Join(", ", disk.Volumes.Select(v => v.Paths.Length > 0 ? v.Paths : "a volume without a letter"));
        var answer = MessageBox.Show(
            $"PERMANENTLY ERASE this drive?\n\nDisk {disk.Number}: {disk.Model}\nSerial: {(disk.UsableSerial.Length > 0 ? disk.UsableSerial : "none reported")}\nSize: {Format.Bytes(disk.Size)}\nOn it: {volumes}\n\n" +
            "Everything on it will be gone. This can't be undone.",
            "Night Tech Wipe: last check", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        var options = new WipeOptions { Pattern = RandomRadio.IsChecked == true ? WipePattern.Random : WipePattern.Zeros, Verify = true };
        var ticket = TicketBox.Text.Trim();
        var tech = TechBox.Text.Trim();
        var notes = NotesBox.Text.Trim();

        _running = true;
        UpdateState();
        SetupPanel.IsEnabled = false;
        StopButton.IsEnabled = true;
        ResultCard.Visibility = Visibility.Collapsed;
        _cts = new CancellationTokenSource();
        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);

        WipeResult? result = null;
        string? setupError = null;
        try
        {
            PhaseText.Text = "Getting the drive ready...";
            App.Log.Info($"Wipe requested: disk {disk.Number} {disk.Model} S/N {disk.Serial}, ticket {ticket}, {WipeReport.MethodText(options)}.");
            var progress = new Progress<WipeProgress>(ShowProgress);
            var token = _cts.Token;
            result = await Task.Run(() =>
            {
                // 1. Open the drive, and check through that same handle that it's the drive that was confirmed.
                using var target = new RawWipeTarget(disk);
                // 2. Lock and dismount its volumes (if any is in use, nothing is erased).
                using var locks = Disks.LockVolumes(disk);
                App.Log.Info($"Locked and dismounted {locks.Count} volume(s) on disk {disk.Number}.");
                // 3. Re-list every drive: still the same drive in the same place, and still not protected.
                if (!Disks.StillTheSame(disk, App.ReportsFolder, out var problem))
                    throw new InvalidOperationException(problem + " Nothing was erased. Refresh the drive list.");
                // 4. And one last time through the handle we're about to write with.
                target.CheckIdentity();
                App.Log.Info($"Erasing disk {disk.Number}: {disk.Model} S/N {disk.Serial}.");
                return WipeEngine.Run(target, options, progress, token);
            });
        }
        catch (Exception ex)
        {
            setupError = ex.Message;
            App.Log.Error("The wipe couldn't start.", ex);
        }
        finally
        {
            NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
            try { Disks.Refresh(disk); } catch { /* Windows will notice on its own */ }
        }

        try
        {
        if (result is null)
        {
            PhaseText.Text = "Didn't start";
            ResultText.Text = (setupError ?? "Unknown problem.") ;
            ResultCard.Visibility = Visibility.Visible;
            MessageBox.Show("The wipe didn't start:\n\n" + setupError, "Night Tech Wipe", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            var report = WipeReport.From(result, options, disk.ToWipeDisk(), App.ToolVersion, ticket, tech, notes);
            try
            {
                var saved = report.Save(App.ReportsFolder);
                _lastRecord = saved.Text;
                App.Log.Info("Wipe record saved: " + saved.Json);
            }
            catch (Exception ex)
            {
                App.Log.Error("Saving the wipe record failed.", ex);
                MessageBox.Show("The wipe record couldn't be saved: " + ex.Message, "Night Tech Wipe", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            App.Log.Info($"Wipe {result.Status}: {result.Message}");
            PhaseText.Text = result.FullyVerified ? "Erased and checked" : result.Status switch
            {
                WipeStatus.Cancelled => "Stopped",
                WipeStatus.Failed => "Failed",
                _ => "Finished with errors",
            };
            ResultText.Text = result.Message + (result.FullyVerified ? "" : "\n\nDon't give this drive away or reuse it as erased.");
            ResultText.Foreground = result.FullyVerified
                ? (System.Windows.Media.Brush)FindResource("Navy")
                : (System.Windows.Media.Brush)FindResource("Danger");
            ResultCard.Visibility = Visibility.Visible;
        }
        }
        finally
        {
            _running = false;
            StopButton.IsEnabled = false;
            SetupPanel.IsEnabled = true;
            LoadDisks();
        }
    }

    private void ShowProgress(WipeProgress p)
    {
        var fraction = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal : 0;
        // Two passes: erasing fills the first half of the bar, checking the second.
        Bar.Value = (p.Phase == "Erasing" ? fraction / 2 : 0.5 + fraction / 2) * Bar.Maximum;
        PhaseText.Text = $"{(p.Phase == "Erasing" ? "Step 1 of 2: erasing" : "Step 2 of 2: checking")}  {fraction:P1}";
        var left = p.BytesPerSecond > 0 ? TimeSpan.FromSeconds((p.BytesTotal - p.BytesDone) / p.BytesPerSecond) : (TimeSpan?)null;
        StatsText.Text = $"{Format.Bytes(p.BytesDone)} of {Format.Bytes(p.BytesTotal)} at {Format.Bytes((long)p.BytesPerSecond)}/s" +
                         (left is { } l ? $", about {(int)l.TotalHours}h {l.Minutes:00}m left in this step" : "") +
                         (p.ProblemSectors > 0 ? $"\nProblem sectors so far: {p.ProblemSectors:N0}" : "");
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is null) return;
        if (MessageBox.Show("Stop the wipe? The drive will be only partly erased, and the record will say so.",
                "Night Tech Wipe", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
        {
            _cts.Cancel();
            StopButton.IsEnabled = false;
            App.Log.Warn("Stop requested by the technician.");
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_running) return;
        MessageBox.Show("A wipe is running. Click Stop first, then close the window.", "Night Tech Wipe", MessageBoxButton.OK, MessageBoxImage.Information);
        e.Cancel = true;
    }

    private void OpenRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_lastRecord is not null && File.Exists(_lastRecord)) Open(_lastRecord);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.ReportsFolder);
        Open(App.ReportsFolder);
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log.Error("Couldn't open " + path, ex); }
    }
}
