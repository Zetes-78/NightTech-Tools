using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NightTech.BurnIn.Windows;
using NightTech.Core.BurnIn;
using NightTech.Core.Diagnostics;
using NightTech.FieldKit;

namespace NightTech.BurnIn;

public sealed class DriveRow
{
    public required string Root { get; init; }
    public required string Label { get; init; }
    public bool Selected { get; set; }
    public long Free { get; init; }
}

public sealed class CheckRow
{
    public required BurnInCheck C { get; init; }
    public string Mark => C.Result switch { Verdict.Pass => "PASS", Verdict.Warning => "NOTE", Verdict.Fail => "FAIL", _ => "----" };
    public Brush Color => C.Result switch { Verdict.Pass => Brushes.SeaGreen, Verdict.Warning => Brushes.DarkOrange, Verdict.Fail => Brushes.Firebrick, _ => Brushes.Gray };
    public string Head => $"{C.Area}: {C.Title}";
    public string Detail => C.Detail;
}

public partial class MainWindow : Window
{
    private MachineInfo _machine = new();
    private BurnInReport? _report;
    private CancellationTokenSource? _stop;
    private bool _running, _closeAfter;
    private string? _lastSheet;
    private readonly List<(DateTime At, double? Temp, double? Perf)> _samples = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    // Live values written by the test threads, read by the timer.
    private CpuStress? _cpu;
    private volatile string _memStatus = "-", _driveStatus = "-";

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + App.ToolVersion;
        TechBox.Text = Environment.UserName;
        App.Log.LineWritten += line => Dispatcher.InvokeAsync(() =>
        {
            LogList.Items.Add(line);
            if (LogList.Items.Count > 300) LogList.Items.RemoveAt(0);
            LogList.ScrollIntoView(LogList.Items[^1]);
        });
        _timer.Tick += (_, _) => Tick();
        Loaded += async (_, _) => await ReadMachine();
    }

    private async Task ReadMachine()
    {
        var r = new DiagnosticReport { RanAsAdministrator = Collector.IsAdministrator };
        await Task.Run(() => Collector.CollectHardware(r, new Progress<string>(s => App.Log.Info(s))));
        _machine = ToMachine(r);
        foreach (var n in r.Notes) App.Log.Warn(n);

        var (total, avail) = Sensors.Memory();
        var lines = new List<string>
        {
            $"{_machine.Make} {_machine.Model}".Trim() + (_machine.Serial.Length > 0 ? $"  (serial {_machine.Serial})" : ""),
            _machine.Processor + (r.Computer.CpuThreads is { } t ? $", {t} threads" : ""),
            $"{Format.Bytes(total)} memory, {Format.Bytes(avail)} free now",
            _machine.Windows,
        };
        foreach (var d in _machine.Disks)
            lines.Add($"Drive: {d.Name} ({d.MediaType}, {Format.Bytes(d.SizeBytes)}): {d.Health}" + (d.WearPercent is { } w ? $", {w}% worn" : ""));
        if (_machine.Battery is { } b)
            lines.Add($"Battery: {(b.HealthPercent is { } h ? h + "% of original capacity" : "present")}" + (Sensors.OnMains() == false ? "  (ON BATTERY: plug in)" : ""));
        MachineText.Text = string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)));

        var programDrive = Path.GetPathRoot(AppContext.BaseDirectory) ?? "";
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        DriveList.ItemsSource = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady && !d.Name.Equals(programDrive, StringComparison.OrdinalIgnoreCase)
                        || d.Name.Equals(systemDrive, StringComparison.OrdinalIgnoreCase) && d.IsReady)
            .Select(d => new DriveRow
            {
                Root = d.Name, Free = d.AvailableFreeSpace, Selected = d.Name.Equals(systemDrive, StringComparison.OrdinalIgnoreCase),
                Label = $"{d.Name.TrimEnd('\\')} {(string.IsNullOrWhiteSpace(d.VolumeLabel) ? "" : d.VolumeLabel + " ")}({Format.Bytes(d.AvailableFreeSpace)} free"
                        + (DriveTest.SafeBytes(d.AvailableFreeSpace) == 0 ? ", too full to test)" : $", tests {Format.Bytes(DriveTest.SafeBytes(d.AvailableFreeSpace))})"),
            }).ToList();
        StartButton.IsEnabled = true;
        if (!r.RanAsAdministrator) App.Log.Warn("Not running as administrator: temperatures, drive health and the hardware error log may be missing.");
    }

    private static MachineInfo ToMachine(DiagnosticReport r) => new()
    {
        Name = Environment.MachineName,
        Make = r.Computer.Manufacturer,
        Model = r.Computer.Model,
        Serial = r.Computer.SerialNumber,
        Processor = r.Computer.Cpu,
        RamBytes = r.Computer.RamBytes ?? 0,
        Windows = $"{r.Computer.OsName} (build {r.Computer.OsBuild})".Replace(" (build )", ""),
        Disks = r.Disks,
        Battery = r.Battery,
    };

    private TimeSpan Planned => TimeSpan.FromMinutes(int.Parse((string)((ComboBoxItem)LengthBox.SelectedItem).Tag));

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var drives = (DriveList.ItemsSource as IEnumerable<DriveRow> ?? Enumerable.Empty<DriveRow>()).Where(d => d.Selected).ToList();
        bool doCpu = CpuCheck.IsChecked == true, doMem = MemCheck.IsChecked == true;
        if (!doCpu && !doMem && drives.Count == 0) { Warn("Pick at least one test."); return; }
        if (Sensors.OnMains() == false &&
            MessageBox.Show(this, "This laptop is on battery. Plug it in first: a full-load test drains the battery fast and laptops slow down on battery.\n\nStart anyway?",
                "Night Tech Burn-in", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var planned = Planned;
        _report = new BurnInReport
        {
            ToolVersion = App.ToolVersion, Customer = CustomerBox.Text.Trim(), Ticket = TicketBox.Text.Trim(), Technician = TechBox.Text.Trim(),
            Purpose = (string)((ComboBoxItem)PurposeBox.SelectedItem).Content, Started = DateTime.Now, Planned = planned, Machine = _machine,
        };
        _stop = new CancellationTokenSource();
        var timeUp = new CancellationTokenSource(planned);
        var both = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, timeUp.Token);
        SetRunning(true);
        _samples.Clear();
        ResultCard.Visibility = Visibility.Collapsed;
        Sensors.KeepAwake(true);
        App.Log.Info($"Burn-in started: {BurnInReport.Dur(planned)}, processor {(doCpu ? "on" : "off")}, memory {(doMem ? "on" : "off")}, drives {string.Join(", ", drives.Select(d => d.Root))}.");
        _timer.Start();
        Tick();

        try
        {
            // Drives first (quick), while the processor and memory tests start alongside.
            var cpuTask = Task.CompletedTask;
            if (doCpu)
            {
                _cpu = new CpuStress();
                int threads = Environment.ProcessorCount;
                var started = Stopwatch.StartNew();
                cpuTask = Task.Run(() =>
                {
                    _cpu.Run(threads, planned, both.Token);
                    _report.Cpu = new CpuResult { Processor = _machine.Processor, Threads = threads, Ran = started.Elapsed, Iterations = _cpu.Iterations, Errors = _cpu.Errors, FirstError = _cpu.FirstError };
                });
            }
            var memTask = Task.CompletedTask;
            if (doMem)
            {
                var (_, avail) = Sensors.Memory();
                long bytes = MemoryTest.SafeBytes(avail - (doCpu ? 256L * 1024 * 1024 : 0));
                memTask = Task.Run(() =>
                {
                    _report.Memory = MemoryTest.Run(bytes, int.MaxValue, both.Token,
                        new Progress<(int Pass, double Fraction)>(p => _memStatus = $"pass {p.Pass}, {p.Fraction:P0} through"));
                    if (_report.Memory.BytesTested == 0) _memStatus = _report.Memory.Note;
                });
                _memStatus = $"testing {Format.Bytes(bytes)}";
            }
            var driveTask = Task.Run(() =>
            {
                foreach (var d in drives)
                {
                    if (both.IsCancellationRequested) break;
                    var folder = d.Root.Equals(Path.GetPathRoot(Environment.SystemDirectory), StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Night Tech", "Burn-in test")
                        : Path.Combine(d.Root, "NightTech-BurnIn");
                    var free = new DriveInfo(d.Root).AvailableFreeSpace;
                    _driveStatus = $"{d.Root.TrimEnd('\\')}: writing and checking...";
                    DriveResult res;
                    try
                    {
                        res = DriveTest.Run(folder, DriveTest.SafeBytes(free), both.Token, new Progress<double>(f => _driveStatus = $"{d.Root.TrimEnd('\\')}: {f:P0}"));
                    }
                    catch (OperationCanceledException) { res = new DriveResult { Folder = folder, Note = "Stopped before it finished." }; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        res = new DriveResult { Folder = folder, Note = "Couldn't test: " + ex.Message };
                        App.Log.Warn($"Drive test on {d.Root}: {ex.Message}");
                    }
                    finally { try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                    lock (_report.Drives) _report.Drives.Add(res);
                    App.Log.Info($"Drive {d.Root}: wrote {res.WriteMBps:0} MB/s, read {res.ReadMBps:0} MB/s, {res.Errors} errors. {res.Note}");
                }
                _driveStatus = _report.Drives.Count == 0 ? "-" : string.Join("; ", _report.Drives.Select(r => $"{Path.GetPathRoot(r.Folder)?.TrimEnd('\\')}: {(r.BytesTested > 0 ? $"{r.WriteMBps:0}/{r.ReadMBps:0} MB/s, {(r.Errors == 0 ? "no errors" : r.Errors + " bad bytes")}" : r.Note)}"));
            });

            await Task.WhenAll(cpuTask, memTask, driveTask);
        }
        catch (Exception ex)
        {
            App.Log.Error("The burn-in hit an error.", ex);
            Warn("The burn-in hit an error: " + ex.Message);
        }
        finally
        {
            _timer.Stop();
            Tick();
            Sensors.KeepAwake(false);
            await Finish(_stop.IsCancellationRequested);
            timeUp.Dispose(); both.Dispose(); _stop.Dispose(); _stop = null;
            SetRunning(false);
            if (_closeAfter) { _closeAfter = false; _ = Dispatcher.InvokeAsync(Close); }
        }
    }

    private async Task Finish(bool stopped)
    {
        var r = _report!;
        r.Finished = DateTime.Now;
        r.Stopped = stopped;
        StatusText.Text = "Checking the hardware error log and drive health...";
        if (r.Cpu is { } c && _samples.Count > 0)
        {
            c.MaxTempC = _samples.Where(s => s.Temp is not null).Select(s => s.Temp).DefaultIfEmpty(null).Max();
            var perf = _samples.Where(s => s.Perf is not null).ToList();
            if (perf.Count >= 6)
            {
                var start = perf.Where(s => s.At >= r.Started.AddSeconds(30)).Take(Math.Max(2, perf.Count / 5)).ToList();
                var end = perf.Skip(perf.Count - Math.Max(2, perf.Count / 5)).ToList();
                if (start.Count > 0 && end.Count > 0)
                {
                    c.PerfStartPercent = start.Average(s => s.Perf!.Value);
                    c.PerfEndPercent = end.Average(s => s.Perf!.Value);
                }
            }
        }
        var whea = await Task.Run(() => Sensors.HardwareErrorsSince(r.Started));
        var shut = await Task.Run(() => Sensors.UnexpectedShutdownsSince(r.Started.AddDays(-30)));
        r.EventLogChecked = whea is not null;
        r.HardwareErrorsDuring = whea ?? 0;
        r.UnexpectedShutdowns30d = shut ?? 0;

        // Read drive health again: a drive that struggled under the test may now report a problem.
        var after = new DiagnosticReport();
        await Task.Run(() => Collector.CollectHardware(after, new Progress<string>(_ => { })));
        if (after.Disks.Count > 0) r.Machine.Disks = after.Disks;
        if (after.Battery is not null) r.Machine.Battery = after.Battery;

        r.Grade();
        var o = r.Overall;
        VerdictText.Text = BurnInReport.Word(o);
        (VerdictBox.Background, VerdictText.Foreground) = o switch
        {
            Verdict.Pass => (new SolidColorBrush(Color.FromRgb(0xE6, 0xF4, 0xEA)), (Brush)Brushes.SeaGreen),
            Verdict.Warning => (new SolidColorBrush(Color.FromRgb(0xFF, 0xF4, 0xE5)), Brushes.DarkOrange),
            Verdict.Fail => (new SolidColorBrush(Color.FromRgb(0xFD, 0xEC, 0xEA)), Brushes.Firebrick),
            _ => (new SolidColorBrush(Color.FromRgb(0xED, 0xF2, 0xF7)), Brushes.Gray),
        };
        CheckList.ItemsSource = r.Checks.Select(x => new CheckRow { C = x }).ToList();
        ResultCard.Visibility = Visibility.Visible;
        StatusText.Text = (stopped ? "Stopped early. " : "Finished. ") + BurnInReport.Word(o);
        Progress.Value = 1;
        App.Log.Info($"Burn-in finished: {BurnInReport.Word(o)}.");
        SaveSheet(open: false);
    }

    private void Tick()
    {
        if (_report is null) return;
        var elapsed = DateTime.Now - _report.Started;
        ClockText.Text = $"{BurnInReport.Dur(elapsed)} of {BurnInReport.Dur(_report.Planned)}";
        if (_running) Progress.Value = Math.Min(1, elapsed.TotalSeconds / Math.Max(1, _report.Planned.TotalSeconds));
        if (!_running) return;
        StatusText.Text = "Running. The computer will be hot and loud; leave it alone until it finishes.";

        _ = Task.Run(() =>
        {
            var t = Sensors.TemperatureC();
            var p = Sensors.PerformancePercent();
            Dispatcher.InvokeAsync(() =>
            {
                _samples.Add((DateTime.Now, t, p));
                var max = _samples.Where(s => s.Temp is not null).Select(s => s.Temp!.Value).DefaultIfEmpty(double.NaN).Max();
                TempText.Text = t is { } tc ? $"{tc:0} °C now, {max:0} °C highest" + (max >= BurnInReport.HotC ? "  (HOT)" : "") : "not reported by this computer";
                PerfText.Text = p is { } pp ? $"{pp:0}% of rated speed" : "not available";
            });
        });
        if (_cpu is { } c)
            CpuText.Text = $"{c.Iterations:N0} checked calculations, " + (c.Errors == 0 ? "all correct" : $"{c.Errors:N0} WRONG");
        MemText.Text = _memStatus;
        DriveText.Text = _driveStatus;
    }

    private void SetRunning(bool on)
    {
        _running = on;
        StartButton.IsEnabled = !on;
        StopButton.IsEnabled = on;
        foreach (var c in new Control[] { CustomerBox, TicketBox, TechBox, PurposeBox, CpuCheck, MemCheck, LengthBox, DriveList }) c.IsEnabled = !on;
        if (!on) _cpu = null;
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _stop?.Cancel();
        StatusText.Text = "Stopping...";
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveSheet(open: true);

    private void SaveSheet(bool open)
    {
        if (_report is null) return;
        _report.Notes = NotesBox.Text.Trim();
        try
        {
            var (json, _, html) = _report.Save(App.ReportsFolder);
            _lastSheet = html;
            SavedText.Text = $"Saved in {App.ReportsFolder}. For the Hub ticket: {Path.GetFileName(json)}";
            App.Log.Info("Sheet saved: " + html);
            if (open) Process.Start(new ProcessStartInfo(html) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Warn("Couldn't save the sheet: " + ex.Message); }
    }

    private void OpenReports_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.ReportsFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", _lastSheet is null ? $"\"{App.ReportsFolder}\"" : $"/select,\"{_lastSheet}\"") { UseShellExecute = true });
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_running) return;
        // Never quit mid-test: the drive test's temporary file has to be removed first.
        e.Cancel = true;
        if (_closeAfter) return;
        if (MessageBox.Show(this, "A burn-in is running. Stop it and close?", "Night Tech Burn-in", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _closeAfter = true;
        _stop?.Cancel();
        StatusText.Text = "Stopping, then closing...";
    }

    private void Warn(string m) => MessageBox.Show(this, m, "Night Tech Burn-in", MessageBoxButton.OK, MessageBoxImage.Warning);
}
