using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NightTech.Core.Carving;
using NightTech.Core.Imaging;

namespace NightTech.Recovery;

/// <summary>Step 2 of a recovery: find files inside a finished drive image. Reads the image only.</summary>
public partial class RecoverFilesWindow : Window
{
    private CancellationTokenSource? _cts;
    private Task? _job;
    private bool _outputTouched;
    private bool _settingOutput;
    private string? _lastOutput;
    private bool _closing;

    public RecoverFilesWindow(string? imagePath, string? ticket, string? technician)
    {
        InitializeComponent();
        TicketBox.Text = ticket ?? "";
        TechBox.Text = technician ?? "";
        ImageBox.Text = imagePath ?? "";
    }

    public bool IsRunning => _job is not null;

    // ---------------- Paths ----------------

    private void ImageBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_outputTouched)
        {
            _settingOutput = true;
            OutputBox.Text = DefaultOutput(ImageBox.Text.Trim()) ?? "";
            _settingOutput = false;
        }
        UpdateHint();
    }

    private void OutputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingOutput) _outputTouched = OutputBox.Text.Length > 0;
        UpdateHint();
    }

    /// <summary>"Recovered files" next to the image, in the case folder.</summary>
    private static string? DefaultOutput(string image)
    {
        if (image.Length == 0) return null;
        try { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(image)) ?? "", "Recovered files"); }
        catch { return null; }
    }

    private void UpdateHint()
    {
        var image = ImageBox.Text.Trim();
        if (image.Length == 0) { PathHint.Text = "Pick the .img file Night Tech Recovery made for this case."; return; }
        try
        {
            if (!File.Exists(image)) { PathHint.Text = "That image file doesn't exist."; return; }
            var size = new FileInfo(image).Length;
            PathHint.Text = $"Image size {ImagingReport.FormatBytes(size)}. Found files are copied out, so leave room for them on the destination drive.";
        }
        catch (Exception ex) { PathHint.Text = ex.Message; }
    }

    private void BrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Choose the drive image", Filter = "Drive images (*.img)|*.img|All files (*.*)|*.*" };
        try { if (File.Exists(ImageBox.Text)) dlg.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(ImageBox.Text)); } catch { }
        if (dlg.ShowDialog(this) == true) ImageBox.Text = dlg.FileName;
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose where to save the found files" };
        if (Directory.Exists(OutputBox.Text)) dlg.InitialDirectory = OutputBox.Text;
        if (dlg.ShowDialog(this) == true) OutputBox.Text = dlg.FolderName;
    }

    private HashSet<FileKind> ChosenKinds()
    {
        var wanted = new HashSet<string>();
        if (PhotosBox.IsChecked == true) wanted.Add("Photos");
        if (VideosBox.IsChecked == true) wanted.Add("Videos");
        if (AudioBox.IsChecked == true) wanted.Add("Audio");
        if (DocumentsBox.IsChecked == true) wanted.Add("Documents");
        if (ArchivesBox.IsChecked == true) wanted.Add("Archives");
        return Enum.GetValues<FileKind>().Where(k => wanted.Contains(Carver.CategoryOf(k))).ToHashSet();
    }

    // ---------------- Run ----------------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var image = ImageBox.Text.Trim();
        var output = OutputBox.Text.Trim();
        if (image.Length == 0 || !File.Exists(image)) { Warn("Pick the drive image (.img) first."); return; }
        if (output.Length == 0) { Warn("Choose where to save the found files."); return; }
        var kinds = ChosenKinds();
        if (kinds.Count == 0) { Warn("Tick at least one kind of file to look for."); return; }

        string fullImage, fullOutput;
        try
        {
            fullImage = Path.GetFullPath(image);
            fullOutput = Path.GetFullPath(output);
            Directory.CreateDirectory(fullOutput);
        }
        catch (Exception ex) { Warn("Can't use that folder: " + ex.Message); return; }

        if (Directory.EnumerateFileSystemEntries(fullOutput).Any() &&
            MessageBox.Show(this, "That folder already has files in it. Files found again with the same name will be replaced.\n\nUse it anyway?",
                "Night Tech Recovery", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var root = Path.GetPathRoot(fullOutput);
            if (!string.IsNullOrEmpty(root) && !root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var free = new DriveInfo(root).AvailableFreeSpace;
                if (free < 1024L * 1024 * 1024 &&
                    MessageBox.Show(this, $"{root} has only {ImagingReport.FormatBytes(free)} free. The scan stops if it runs out of space.\n\nStart anyway?",
                        "Night Tech Recovery", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
            }
        }
        catch (Exception ex) { App.Log.Warn("Couldn't check free space: " + ex.Message); }

        var options = new CarveOptions { Kinds = kinds, MinBytes = SmallBox.IsChecked == true ? 1024 : 0 };
        var ticket = TicketBox.Text.Trim();
        var tech = TechBox.Text.Trim();
        _lastOutput = fullOutput;
        SetRunning(true);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var progress = new Progress<CarveProgress>(ShowProgress);
        App.Log.Info($"Recover files: scanning {fullImage} into {fullOutput}.");

        try
        {
            var job = Task.Run(() =>
            {
                using var reader = new FileImageReader(fullImage);
                var r = Carver.Run(reader, fullOutput, options, progress, token);
                r.ImagePath = fullImage;
                r.Ticket = ticket;
                r.Technician = tech;
                r.ToolVersion = App.ToolVersion;
                var (_, text) = Carver.SaveReport(r, fullOutput);
                return (Report: r, Text: text);
            });
            _job = job;
            var done = await job;
            if (!_closing) Finished(done.Report, done.Text);
        }
        catch (Exception ex)
        {
            App.Log.Error("Recover files failed.", ex);
            PhaseText.Text = "Stopped by an error.";
            Warn("The scan couldn't run: " + ex.Message);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _job = null;
            SetRunning(false);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_cts is null) return;
        StopButton.IsEnabled = false;
        PhaseText.Text = "Stopping...";
        _cts.Cancel();
    }

    private void ShowProgress(CarveProgress p)
    {
        if (_cts is { IsCancellationRequested: false }) PhaseText.Text = "Scanning the image...";
        double pct = p.BytesTotal > 0 ? 100.0 * p.BytesScanned / p.BytesTotal : 0;
        Bar.Value = pct;
        PercentText.Text = $"{pct:0.0}%";
        ScannedText.Text = $"{ImagingReport.FormatBytes(p.BytesScanned)} of {ImagingReport.FormatBytes(p.BytesTotal)}";
        FoundText.Text = p.FilesFound.ToString("N0");
        SpeedText.Text = p.BytesPerSecond > 0 ? ImagingReport.FormatBytes((long)p.BytesPerSecond) + "/s" : "-";
        EtaText.Text = p.BytesPerSecond > 0 && p.BytesScanned < p.BytesTotal
            ? FormatEta(TimeSpan.FromSeconds((p.BytesTotal - p.BytesScanned) / p.BytesPerSecond))
            : "-";
    }

    private void Finished(CarveReport r, string reportPath)
    {
        PhaseText.Text = r.Status switch
        {
            CarveStatus.Completed => "Finished: whole image scanned",
            CarveStatus.Cancelled => "Stopped: found files so far are kept",
            _ => "Stopped by an error",
        };
        OpenButton.IsEnabled = true;
        var counts = string.Join("\n", r.Counts.OrderByDescending(c => c.Value).Select(c => $"   {c.Key}: {c.Value:N0}"));
        var msg = $"{PhaseText.Text}.\n\nFound {r.Files.Count:N0} file(s), {ImagingReport.FormatBytes(r.BytesRecovered)}.";
        if (counts.Length > 0) msg += "\n" + counts;
        if (r.Status == CarveStatus.Failed) msg += "\n\n" + r.Message;
        msg += $"\n\nOpen each file before handing it over. Report: {reportPath}";
        App.Log.Info($"Recover files: {r.Status}, {r.Files.Count} file(s).");
        MessageBox.Show(this, msg, "Night Tech Recovery", MessageBoxButton.OK,
            r.Status == CarveStatus.Completed ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutput is not null && Directory.Exists(_lastOutput))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastOutput}\"") { UseShellExecute = true });
    }

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        ImageBox.IsEnabled = OutputBox.IsEnabled = TicketBox.IsEnabled = TechBox.IsEnabled = !running;
        PhotosBox.IsEnabled = VideosBox.IsEnabled = AudioBox.IsEnabled = DocumentsBox.IsEnabled = ArchivesBox.IsEnabled = SmallBox.IsEnabled = !running;
        if (running) { OpenButton.IsEnabled = false; PhaseText.Text = "Starting..."; Bar.Value = 0; }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_job is null || _cts is null) return;
        e.Cancel = true;
        if (_closing) return;                       // already stopping; closes when the scan ends
        if (MessageBox.Show(this, "The scan is running. Stop it and close? Files found so far are kept.",
                "Night Tech Recovery", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (_job is null || _cts is null) { e.Cancel = false; return; }   // finished while the question was open: just close
        _closing = true;
        PhaseText.Text = "Stopping...";
        _cts.Cancel();
        try { await _job; } catch { /* already logged */ }
        Closing -= Window_Closing;
        Close();
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, "Night Tech Recovery", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static string FormatEta(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{t.Minutes} min" : "under a minute";
}
