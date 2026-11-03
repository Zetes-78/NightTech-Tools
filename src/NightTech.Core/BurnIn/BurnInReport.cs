using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NightTech.Core.Diagnostics;

namespace NightTech.Core.BurnIn;

public enum Verdict { Pass, Warning, Fail, NotRun }

public sealed class BurnInCheck
{
    public string Area { get; set; } = "";
    public Verdict Result { get; set; }
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public BurnInCheck() { }
    public BurnInCheck(string area, Verdict result, string title, string detail = "") { Area = area; Result = result; Title = title; Detail = detail; }
}

public sealed class CpuResult
{
    public string Processor { get; set; } = "";
    public int Threads { get; set; }
    public TimeSpan Ran { get; set; }
    public long Iterations { get; set; }
    public long Errors { get; set; }
    public string FirstError { get; set; } = "";
    /// <summary>Hottest reading of the board's thermal sensor near the processor, when Windows reports one that moves.</summary>
    public double? MaxTempC { get; set; }
    public double? MinTempC { get; set; }
    /// <summary>Processor speed as a percent of its rated speed: early in the test, and over the last part.</summary>
    public double? PerfStartPercent { get; set; }
    public double? PerfEndPercent { get; set; }
}

/// <summary>What's checked on the machine before and after: drive health and battery come from the Field Kit's checks.</summary>
public sealed class MachineInfo
{
    public string Name { get; set; } = "";
    public string Make { get; set; } = "";
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Processor { get; set; } = "";
    public long RamBytes { get; set; }
    public string Windows { get; set; } = "";
    public List<DiskRecord> Disks { get; set; } = new();
    public BatteryRecord? Battery { get; set; }
}

/// <summary>A burn-in: stress tests and health checks with a pass/fail result for each, for the customer and the Hub ticket.</summary>
public sealed class BurnInReport
{
    public string Tool { get; set; } = "Night Tech Burn-in";
    public string ToolVersion { get; set; } = "";
    public string Kind { get; set; } = "burn-in";
    public string Ticket { get; set; } = "";
    public string Technician { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Purpose { get; set; } = "";   // "Repair check", "Refurbished computer"
    public DateTime Started { get; set; }
    public DateTime Finished { get; set; }
    public TimeSpan Planned { get; set; }
    public bool Stopped { get; set; }
    public MachineInfo Machine { get; set; } = new();
    public CpuResult? Cpu { get; set; }
    public MemoryResult? Memory { get; set; }
    public List<DriveResult> Drives { get; set; } = new();
    /// <summary>Uncorrected hardware errors Windows logged during the test (WHEA errors: processor, memory or bus).</summary>
    public int HardwareErrorsDuring { get; set; }
    /// <summary>Errors the hardware caught and corrected itself during the test (WHEA warnings). Common and often harmless.</summary>
    public int CorrectedErrorsDuring { get; set; }
    public bool CpuSelected { get; set; }
    public bool MemorySelected { get; set; }
    public int DrivesSelected { get; set; }
    /// <summary>Unexpected shutdowns in the 30 days before the test.</summary>
    public int UnexpectedShutdowns30d { get; set; }
    public bool EventLogChecked { get; set; }
    public List<BurnInCheck> Checks { get; set; } = new();
    public string Notes { get; set; } = "";

    private static readonly string[] StressAreas = { "Processor", "Memory", "Drive" };

    /// <summary>
    /// FAIL if anything failed. NOT RUN if none of the stress tests did any work (health checks alone don't make a pass).
    /// Otherwise PASS WITH NOTES if anything needs a note, including a test that was chosen but didn't run, or a run cut short.
    /// </summary>
    public Verdict Overall =>
        Checks.Any(c => c.Result == Verdict.Fail) ? Verdict.Fail
        : !Checks.Any(c => StressAreas.Contains(c.Area) && c.Result != Verdict.NotRun) ? Verdict.NotRun
        : Checks.Any(c => c.Result is Verdict.Warning or Verdict.NotRun) ? Verdict.Warning
        : Verdict.Pass;

    public const double HotC = 90, TooHotC = 100;
    /// <summary>Below this share of its rated speed at the end of the test, the processor is throttling.</summary>
    public const double ThrottledPercent = 85;

    /// <summary>Turns the measurements into pass/warning/fail lines. Call once the tests are done.</summary>
    public void Grade()
    {
        Checks.Clear();
        var ranFor = Finished > Started ? Finished - Started : TimeSpan.Zero;
        if ((CpuSelected || MemorySelected) && Planned > TimeSpan.Zero && (Stopped || ranFor.TotalSeconds < Planned.TotalSeconds * 0.9))
            Checks.Add(new("Length", Verdict.Warning, $"Ran {Dur(ranFor)} of the {Dur(Planned)} planned",
                "Stopped early, so problems that only show after a long time under load may not have appeared. Run it again for the full time to be sure."));
        var c = Cpu;
        if (c is null)
        {
            if (CpuSelected) Checks.Add(new("Processor", Verdict.NotRun, "Processor stress test didn't run"));
        }
        else
        {
            var ran = $"{c.Threads} threads for {Dur(c.Ran)}, {c.Iterations:N0} self-checked calculations";
            if (c.Errors > 0)
                Checks.Add(new("Processor", Verdict.Fail, $"Processor gave {c.Errors:N0} wrong answer(s) under load",
                    $"{c.FirstError} Usually overheating, a failing power supply or bad memory. {ran}."));
            else if (c.Iterations == 0)
                Checks.Add(new("Processor", Verdict.NotRun, "Processor stress test stopped before it started"));
            else
                Checks.Add(new("Processor", Verdict.Pass, "Processor stable under full load", ran + ", every one correct."));

            // A thermal sensor that barely moves under full load isn't really reporting (many boards return a fixed value).
            bool tempReal = c.MaxTempC is { } mx && c.MinTempC is { } mn && mx - mn >= 2;
            if (tempReal && c.MaxTempC is { } t)
            {
                if (t >= TooHotC) Checks.Add(new("Cooling", Verdict.Fail, $"Thermal sensor reached {t:0} °C", "Too hot: clean the fans and heatsink, and replace the thermal paste."));
                else if (t >= HotC) Checks.Add(new("Cooling", Verdict.Warning, $"Thermal sensor reached {t:0} °C", "Running hot: check the fans and clean out dust."));
                else Checks.Add(new("Cooling", Verdict.Pass, $"Thermal sensor peaked at {t:0} °C", "The board's sensor near the processor stayed in a safe range."));
            }
            if (c.PerfEndPercent is { } e)
            {
                var from = c.PerfStartPercent is > 0 and var s ? $"from {s:0}% " : "";
                if (e < ThrottledPercent)
                    Checks.Add(new("Cooling", Verdict.Warning, "Processor slowed down under load",
                        $"Speed went {from}to {e:0}% of its rated speed by the end. Usually heat; on a laptop, also check it's plugged in and on a balanced power plan."));
                else
                    Checks.Add(new("Cooling", Verdict.Pass, "Processor kept its rated speed under load",
                        $"{e:0}% of rated speed at the end{(tempReal ? "" : " (this computer doesn't report a usable temperature)")}."));
            }
        }

        var m = Memory;
        if (m is null) { if (MemorySelected) Checks.Add(new("Memory", Verdict.NotRun, "Memory test didn't run")); }
        else if (m.Errors > 0)
            Checks.Add(new("Memory", Verdict.Fail, $"Memory test found {m.Errors:N0} error(s)",
                $"Replace the faulty memory. First: {m.ErrorSamples.FirstOrDefault()}"));
        else if (m.PassesDone == 0)
            Checks.Add(new("Memory", Verdict.NotRun, "Memory test didn't complete a pass", m.Note));
        else
        {
            var share = Machine.RamBytes > 0 ? $" ({m.BytesTested * 100.0 / Machine.RamBytes:0}% of installed memory)" : "";
            Checks.Add(new("Memory", Verdict.Pass, $"Memory passed {m.PassesDone} pass(es), no errors",
                $"{Format.Bytes(m.BytesTested)} tested{share} with 5 patterns. {m.Note}".Trim()));
        }

        if (Drives.Count < DrivesSelected) Checks.Add(new("Drive", Verdict.NotRun, $"{DrivesSelected - Drives.Count} chosen drive test(s) didn't run"));
        foreach (var d in Drives)
        {
            if (d.BytesTested == 0) { Checks.Add(new("Drive", Verdict.NotRun, $"Drive test on {Root(d.Folder)} not run", d.Note)); continue; }
            var speeds = $"Wrote {d.WriteMBps:0} MB/s, read {d.ReadMBps:0} MB/s over {Format.Bytes(d.BytesTested)}.";
            if (d.Errors > 0) Checks.Add(new("Drive", Verdict.Fail, $"Drive {Root(d.Folder)} returned wrong data", $"{d.Errors:N0} bytes read back differently from what was written. Back up and replace the drive. {speeds}"));
            else if (d.Uncached && Math.Min(d.WriteMBps, d.ReadMBps) < 20)
                Checks.Add(new("Drive", Verdict.Warning, $"Drive {Root(d.Folder)} is very slow", $"{speeds} A healthy drive manages at least 60 MB/s; this one may be failing or be an old model. An SSD upgrade would help."));
            else Checks.Add(new("Drive", Verdict.Pass, $"Drive {Root(d.Folder)} wrote and read back correctly", speeds + (d.Note.Length > 0 ? " " + d.Note : "")));
        }

        foreach (var disk in Machine.Disks)
        {
            var name = string.IsNullOrWhiteSpace(disk.Name) ? "Drive" : disk.Name.Trim();
            if (disk.Health.Equals("Unhealthy", StringComparison.OrdinalIgnoreCase) || disk.ReadErrorsUncorrected is > 0)
                Checks.Add(new("Drive health", Verdict.Fail, $"{name} reports it is failing", "Back up now and replace the drive."));
            else if (disk.WearPercent is >= HealthRules.SsdWearProblem)
                Checks.Add(new("Drive health", Verdict.Fail, $"{name} is nearly worn out", $"{disk.WearPercent}% of its rated life used."));
            else if (disk.Health.Equals("Warning", StringComparison.OrdinalIgnoreCase) || disk.WearPercent is >= HealthRules.SsdWearWarn)
                Checks.Add(new("Drive health", Verdict.Warning, $"{name} reports a warning", disk.WearPercent is { } w ? $"{w}% of its rated life used." : "Windows marks it as needing attention."));
            else if (disk.Health.Equals("Healthy", StringComparison.OrdinalIgnoreCase))
                Checks.Add(new("Drive health", Verdict.Pass, $"{name} reports healthy",
                    string.Join(", ", new[] { disk.PowerOnHours is { } h ? $"{h:N0} hours powered on" : "", disk.WearPercent is { } w2 ? $"{w2}% wear" : "", disk.TemperatureC is { } tc ? $"{tc} °C" : "" }.Where(x => x.Length > 0))));
        }

        if (Machine.Battery?.HealthPercent is { } bh)
        {
            var cyc = Machine.Battery.CycleCount is > 0 ? $", {Machine.Battery.CycleCount} charge cycles" : "";
            if (bh < HealthRules.BatteryProblemPercent) Checks.Add(new("Battery", Verdict.Fail, $"Battery holds only {bh}% of its original charge", "Replace the battery" + cyc + "."));
            else if (bh < HealthRules.BatteryWarnPercent) Checks.Add(new("Battery", Verdict.Warning, $"Battery holds {bh}% of its original charge", "Worn, but usable" + cyc + "."));
            else Checks.Add(new("Battery", Verdict.Pass, $"Battery holds {bh}% of its original charge", "Good" + cyc + "."));
        }

        if (EventLogChecked)
        {
            if (HardwareErrorsDuring > 0)
                Checks.Add(new("Stability", Verdict.Fail, $"Windows logged {HardwareErrorsDuring} uncorrected hardware error(s) during the test", "Processor, memory or bus errors (WHEA). Check cooling, memory and power."));
            else if (CorrectedErrorsDuring > 0)
                Checks.Add(new("Stability", Verdict.Warning, $"{CorrectedErrorsDuring} corrected hardware error(s) during the test",
                    "The hardware caught and fixed these itself (WHEA warnings). Often harmless (PCIe power saving), but repeated processor or memory ones are an early warning."));
            else
                Checks.Add(new("Stability", Verdict.Pass, "No hardware errors logged during the test"));
            if (UnexpectedShutdowns30d >= HealthRules.UnexpectedShutdownsWarn)
                Checks.Add(new("Stability", Verdict.Warning, $"{UnexpectedShutdowns30d} unexpected shutdowns in the last 30 days", "Before this test. Could be power cuts, a held power button, or a power or heat problem."));
        }
    }

    private static string Root(string folder) { var r = Path.GetPathRoot(folder); return string.IsNullOrEmpty(r) ? folder : r.TrimEnd('\\', '/') is { Length: > 0 } x ? x : r; }

    public static string Dur(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{t.Seconds} s";

    public static string Word(Verdict v) => v switch { Verdict.Pass => "PASS", Verdict.Warning => "PASS WITH NOTES", Verdict.Fail => "FAIL", _ => "NOT RUN" };
    private static string Mark(Verdict v) => v switch { Verdict.Pass => "PASS", Verdict.Warning => "NOTE", Verdict.Fail => "FAIL", _ => "----" };

    public List<string> Notices()
    {
        var n = new List<string>
        {
            "This sheet documents the tests performed on the date shown and what they found. It is not a guarantee of how long the computer or any part will last.",
            "The memory test checks the memory Windows wasn't using at the time; Windows' own Memory Diagnostic can test the rest at restart.",
        };
        if (Stopped) n.Insert(0, "The test was stopped early, so it ran for less time than planned.");
        return n;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static BurnInReport? FromJson(string s) => JsonSerializer.Deserialize<BurnInReport>(s, Json);

    private string MachineLine() =>
        string.Join(" · ", new[] { $"{Machine.Make} {Machine.Model}".Trim(), Machine.Serial.Length > 0 ? "Serial " + Machine.Serial : "", Machine.Processor,
            Machine.RamBytes > 0 ? Format.Bytes(Machine.RamBytes) + " memory" : "", Machine.Windows }.Where(x => !string.IsNullOrWhiteSpace(x)));

    public string ToText()
    {
        var s = new StringBuilder();
        s.AppendLine($"{Brand.Company.ToUpperInvariant()} - BURN-IN TEST: {Word(Overall)}");
        s.AppendLine(new string('=', 60));
        if (Customer.Length > 0) s.AppendLine($"Customer:    {Customer}");
        if (Ticket.Length > 0) s.AppendLine($"Ticket:      {Ticket}");
        if (Purpose.Length > 0) s.AppendLine($"Purpose:     {Purpose}");
        s.AppendLine($"Computer:    {Machine.Name}  ({MachineLine()})");
        s.AppendLine($"Tested:      {Started:yyyy-MM-dd HH:mm} to {Finished:HH:mm} ({Dur(Finished - Started)} of {Dur(Planned)} planned{(Stopped ? ", stopped early" : "")})");
        s.AppendLine($"Technician:  {Technician}");
        s.AppendLine();
        foreach (var c in Checks)
            s.AppendLine($"  [{Mark(c.Result)}] {c.Area}: {c.Title}{(c.Detail.Length > 0 ? " - " + c.Detail : "")}");
        if (Memory?.ErrorSamples.Count > 0)
        {
            s.AppendLine(); s.AppendLine("MEMORY ERRORS (first ones)");
            foreach (var e in Memory.ErrorSamples) s.AppendLine("  " + e);
        }
        if (Notes.Length > 0) { s.AppendLine(); s.AppendLine("NOTES"); s.AppendLine("  " + Notes); }
        s.AppendLine();
        foreach (var n in Notices()) s.AppendLine("* " + n);
        s.AppendLine();
        s.AppendLine($"{Brand.Company} · {Brand.Phone} · {Brand.Website}   ({Tool} {ToolVersion})");
        return s.ToString();
    }

    public string ToHtml()
    {
        static string E(string? x) => WebUtility.HtmlEncode(x ?? "");
        var o = Overall;
        var (bg, fg) = o switch { Verdict.Pass => ("#e6f4ea", "#2f855a"), Verdict.Warning => ("#fff4e5", "#c05621"), Verdict.Fail => ("#fdecea", "#c53030"), _ => ("#edf2f7", "#4a5568") };
        var h = new StringBuilder();
        h.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        h.Append($"<title>Burn-in test - {E(Machine.Name)}</title><style>");
        h.Append("body{font-family:Segoe UI,Arial,sans-serif;color:#1a202c;background:#f7fafc;margin:0}main{max-width:820px;margin:0 auto;padding:28px 22px;background:#fff}");
        h.Append("header{display:flex;justify-content:space-between;align-items:flex-end;border-bottom:3px solid #c2410c;padding-bottom:14px;margin-bottom:18px}h1{font-size:24px;margin:0;color:#1a365d}h2{font-size:17px;color:#1a365d;margin:24px 0 8px}");
        h.Append(".verdict{font-size:30px;font-weight:800;padding:14px 18px;border-radius:10px;margin:8px 0 6px}table{width:100%;border-collapse:collapse;font-size:14px}td,th{padding:7px 8px;border-bottom:1px solid #e2e8f0;text-align:left;vertical-align:top}");
        h.Append(".PASS{color:#2f855a;font-weight:700}.NOTE{color:#c05621;font-weight:700}.FAIL{color:#c53030;font-weight:700}.muted{color:#4a5568;font-size:13px}.x{color:#4a5568}ul{padding-left:20px}li{margin:4px 0}");
        h.Append("@media print{body{background:#fff}main{padding:0}}</style></head><body><main>");
        h.Append($"<header><div><h1>{E(Brand.Company)}</h1><div class=\"muted\">Burn-in test · {E(Finished.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture))}</div></div>");
        h.Append($"<div class=\"muted\" style=\"text-align:right\">{E(Customer)}{(Ticket.Length > 0 ? "<br>Ticket " + E(Ticket) : "")}{(Purpose.Length > 0 ? "<br>" + E(Purpose) : "")}</div></header>");
        h.Append($"<div class=\"verdict\" style=\"background:{bg};color:{fg}\">{E(Word(o))}</div>");
        h.Append($"<p><strong>{E(Machine.Name)}</strong><br><span class=\"muted\">{E(MachineLine())}</span><br><span class=\"muted\">Tested {E(Dur(Finished - Started))} of {E(Dur(Planned))} planned{(Stopped ? ", stopped early" : "")}, {E(Started.ToString("h:mm tt", CultureInfo.InvariantCulture))} to {E(Finished.ToString("h:mm tt", CultureInfo.InvariantCulture))}</span></p>");
        h.Append("<h2>Results</h2><table>");
        foreach (var c in Checks)
            h.Append($"<tr><td class=\"{Mark(c.Result).Replace("----", "x")}\" style=\"white-space:nowrap\">{Mark(c.Result)}</td><td><strong>{E(c.Area)}:</strong> {E(c.Title)}{(c.Detail.Length > 0 ? $"<br><span class=\"muted\">{E(c.Detail)}</span>" : "")}</td></tr>");
        h.Append("</table>");
        if (Notes.Length > 0) h.Append($"<h2>Notes</h2><p>{E(Notes)}</p>");
        h.Append("<h2>Please note</h2><ul class=\"muted\">");
        foreach (var n in Notices()) h.Append($"<li>{E(n)}</li>");
        h.Append("</ul>");
        h.Append($"<p class=\"muted\" style=\"margin-top:28px;text-align:center\">{E(Brand.Company)} · {E(Brand.Phone)} · {E(Brand.Website)}<br>Technician: {E(Technician)} · {E(Tool)} {E(ToolVersion)}</p>");
        h.Append("</main></body></html>");
        return h.ToString();
    }

    public (string Json, string Text, string Html) Save(string folder)
    {
        Directory.CreateDirectory(folder);
        var name = Format.SafeFileName($"burnin-{(Ticket.Length > 0 ? Ticket : Machine.Name)}-{Started:yyyyMMdd-HHmm}");
        var json = Path.Combine(folder, name + ".json");
        var text = Path.Combine(folder, name + ".txt");
        var html = Path.Combine(folder, name + ".html");
        var bom = new UTF8Encoding(true);
        File.WriteAllText(json, ToJson(), bom);
        File.WriteAllText(text, ToText(), bom);
        File.WriteAllText(html, ToHtml(), new UTF8Encoding(false));
        return (json, text, html);
    }
}
