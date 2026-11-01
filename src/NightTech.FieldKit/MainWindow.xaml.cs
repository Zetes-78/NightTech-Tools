using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NightTech.Core.Diagnostics;

namespace NightTech.FieldKit;

public partial class MainWindow : Window
{
    private DiagnosticReport? _report;
    private ReportWriter.SavedFiles? _files;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {App.ToolVersion}";
        ModeText.Text = KitPaths.Portable ? "Running from the USB kit" : "Installed on this computer";
        AdminCard.Visibility = Collector.IsAdministrator ? Visibility.Collapsed : Visibility.Visible;
        App.Log.LineWritten += line => Dispatcher.InvokeAsync(() =>
        {
            LogList.Items.Add(line);
            if (LogList.Items.Count > 300) LogList.Items.RemoveAt(0);
            LogList.ScrollIntoView(line);
        });
        BuildTools();
        App.Log.Info(KitPaths.Portable
            ? $"Reports are saved on the USB kit: {KitPaths.Reports}"
            : $"Reports are saved in: {KitPaths.Reports}");
    }

    // ---------------- Running the checks ----------------

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        RunButton.IsEnabled = false;
        OpenReportButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        SummaryText.Text = "Checking...";
        FindingsList.ItemsSource = null;

        var report = new DiagnosticReport
        {
            ToolVersion = App.ToolVersion,
            Ticket = TicketBox.Text.Trim(),
            Customer = CustomerBox.Text.Trim(),
            Technician = TechBox.Text.Trim(),
            Started = DateTime.Now,
            RanAsAdministrator = Collector.IsAdministrator,
        };
        var progress = new Progress<string>(msg =>
        {
            StatusText.Text = msg;
            App.Log.Info(msg);
        });

        try
        {
            await Task.Run(() => Collector.CollectAll(report, progress));
            report.Finished = DateTime.Now;
            report.Findings = HealthRules.Evaluate(report, DateTime.Now);
            _report = report;
            ShowReport(report);

            try
            {
                _files = ReportWriter.Save(report, KitPaths.Reports);
                SavedText.Text = "Saved: " + Path.GetFileNameWithoutExtension(_files.Html) + " (.html for the customer, .txt for the Hub ticket)";
                App.Log.Info("Report saved: " + _files.Html);
                OpenReportButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                _files = null;
                SavedText.Text = "The report couldn't be saved: " + ex.Message;
                App.Log.Error("Saving the report failed.", ex);
            }
            CopyButton.IsEnabled = true;
            StatusText.Text = $"Finished in {(report.Finished - report.Started).TotalSeconds:0} seconds.";
        }
        catch (Exception ex)
        {
            App.Log.Error("The checks stopped.", ex);
            StatusText.Text = "The checks stopped: " + ex.Message;
            SummaryText.Text = "The checks didn't finish.";
        }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            RunButton.IsEnabled = true;
            RunButton.Content = "Run the checks again";
        }
    }

    private void ShowReport(DiagnosticReport r)
    {
        SummaryText.Text = ReportWriter.Summary(r);
        FindingsList.ItemsSource = r.Findings.Select(f => new FindingView(f)).ToList();
        FullText.Text = ReportWriter.ToText(r);
        Tabs.SelectedIndex = 0;
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        if (_files is not null) Open(_files.Html);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(KitPaths.Reports);
            Open(KitPaths.Reports);
        }
        catch (Exception ex) { App.Log.Error("Couldn't open the reports folder.", ex); }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_report is null) return;
        var lines = new List<string> { $"Field Kit check {Format.DateAndTime(_report.Started)} on {_report.Computer.Name}: {ReportWriter.Summary(_report)}" };
        lines.AddRange(_report.Findings.Where(f => f.Severity >= Severity.Info).Select(f => $"- {f.Area}: {f.Title}"));
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            StatusText.Text = "Summary copied. Paste it into the ticket's notes in Night Tech Hub.";
        }
        catch (Exception ex) { App.Log.Error("Copy failed.", ex); }
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (Win32Exception) { /* the user said no to the administrator prompt */ }
    }

    // ---------------- Tools tab ----------------

    private void BuildTools()
    {
        void Add(string title, string detail, Action action)
        {
            var b = new Button { Style = (Style)FindResource("ToolButton") };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("Muted") });
            b.Content = panel;
            b.Click += (_, _) =>
            {
                try
                {
                    App.Log.Info("Opened: " + title);
                    action();
                }
                catch (Win32Exception) { /* cancelled at the administrator prompt */ }
                catch (Exception ex) { App.Log.Error("Couldn't open " + title + ".", ex); }
            };
            ToolsPanel.Children.Add(b);
        }

        var recovery = KitPaths.FindRecovery();
        if (recovery is not null)
            Add("Night Tech Recovery", "Image a drive before recovery work (admin)", () => Launch(recovery));

        Add("Battery report", "Windows' full battery history, saved with the reports", BatteryReport);
        Add("Device Manager", "Missing drivers and failed devices", () => Launch("devmgmt.msc"));
        Add("Disk Management", "Partitions, drive letters, unallocated space", () => Launch("diskmgmt.msc"));
        Add("Event Viewer", "Windows' detailed logs", () => Launch("eventvwr.msc"));
        Add("Reliability history", "Crashes and failures day by day", () => Launch("perfmon.exe", "/rel"));
        Add("System Information", "Full hardware and driver details", () => Launch("msinfo32.exe"));
        Add("Task Manager", "What's using the processor, memory and disk", () => Launch("taskmgr.exe"));
        Add("Startup apps", "Turn off programs that start with Windows", () => Launch("ms-settings:startupapps"));
        Add("Installed apps", "Remove unwanted programs", () => Launch("ms-settings:appsfeatures"));
        Add("Windows Update", "Check for and install updates", () => Launch("ms-settings:windowsupdate"));
        Add("Windows Security", "Antivirus scan and firewall", () => Launch("windowsdefender:"));
        Add("Services", "Start, stop and check Windows services", () => Launch("services.msc"));
        Add("Network connections", "Adapters, Wi-Fi and IP settings", () => Launch("ncpa.cpl"));
        Add("Check the Windows drive", "Read-only scan for file system errors (admin)",
            () => AdminCommand("chkdsk " + (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\')));
        Add("Repair system files", "sfc /scannow (admin)", () => AdminCommand("sfc /scannow"));
        Add("Repair Windows image", "DISM restore health; needs internet (admin)", () => AdminCommand("DISM /Online /Cleanup-Image /RestoreHealth"));
        Add("Memory test", "Restarts the computer to test memory", MemoryTest);
        Add("Command prompt (admin)", "For anything else", () => Launch("cmd.exe", "", admin: true));
    }

    private static void Launch(string file, string args = "", bool admin = false)
    {
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = true };
        if (admin) psi.Verb = "runas";
        Process.Start(psi);
    }

    private static void Open(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    /// <summary>Opens an administrator command prompt that runs the command and stays open to show the result.</summary>
    private static void AdminCommand(string command) => Launch("cmd.exe", "/k " + command, admin: true);

    private void BatteryReport()
    {
        Directory.CreateDirectory(KitPaths.Reports);
        var name = Format.SafeFileName($"{(string.IsNullOrWhiteSpace(TicketBox.Text) ? "" : TicketBox.Text.Trim() + "-")}{Environment.MachineName}-battery-{DateTime.Now:yyyyMMdd-HHmm}");
        var output = Path.Combine(KitPaths.Reports, name + ".html");
        var psi = new ProcessStartInfo("powercfg.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("/batteryreport");
        psi.ArgumentList.Add("/output");
        psi.ArgumentList.Add(output);
        using var p = Process.Start(psi);
        p?.WaitForExit(30000);
        if (File.Exists(output))
        {
            App.Log.Info("Battery report saved: " + output);
            Open(output);
        }
        else
        {
            MessageBox.Show("Windows didn't create a battery report. This computer may not have a battery.",
                "Night Tech Field Kit", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void MemoryTest()
    {
        var answer = MessageBox.Show(
            "The Windows Memory Diagnostic asks to restart the computer and tests memory before Windows starts.\n\n" +
            "Save any open work first. Continue?",
            "Memory test", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Yes) Launch("mdsched.exe", "", admin: true);
    }
}

/// <summary>How one finding is shown in the list.</summary>
public sealed class FindingView
{
    private static readonly Dictionary<Severity, (string Tag, string Accent, string Soft)> Look = new()
    {
        [Severity.Problem] = ("PROBLEM", "#C53030", "#FFF5F5"),
        [Severity.Warning] = ("WATCH", "#C2410C", "#FFF7ED"),
        [Severity.Info] = ("NOTE", "#2B6CB0", "#EBF8FF"),
        [Severity.Good] = ("GOOD", "#2F855A", "#F0FFF4"),
    };

    public FindingView(Finding f)
    {
        var look = Look[f.Severity];
        Tag = look.Tag;
        Area = f.Area;
        Title = f.Title;
        Detail = f.Detail;
        Accent = MakeBrush(look.Accent);
        Soft = MakeBrush(look.Soft);
    }

    public string Tag { get; }
    public string Area { get; }
    public string Title { get; }
    public string Detail { get; }
    public Brush Accent { get; }
    public Brush Soft { get; }

    private static Brush MakeBrush(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
