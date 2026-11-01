using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NightTech.Core.Diagnostics;

/// <summary>Saves a Field Kit report three ways: HTML for the customer, TXT for the Hub ticket, JSON for tools.</summary>
public static class ReportWriter
{
    public const string Disclaimer =
        "This report shows what these checks found at the time they were run. It does not guarantee the computer " +
        "will keep working, or that every problem was found.";

    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed record SavedFiles(string Html, string Text, string Json);

    /// <summary>"NT-0012-DESKTOP-ABC-20261101-183005" (the ticket part is left out when there is none).</summary>
    public static string BaseName(DiagnosticReport r)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.Ticket)) parts.Add(r.Ticket);
        parts.Add(string.IsNullOrWhiteSpace(r.Computer.Name) ? "computer" : r.Computer.Name);
        parts.Add(r.Started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        return Format.SafeFileName(string.Join("-", parts), 120);
    }

    public static SavedFiles Save(DiagnosticReport r, string folder)
    {
        Directory.CreateDirectory(folder);
        var b = Path.Combine(folder, BaseName(r));
        var files = new SavedFiles(b + ".html", b + ".txt", b + ".json");
        File.WriteAllText(files.Html, ToHtml(r), Utf8Bom);
        File.WriteAllText(files.Text, ToText(r), Utf8Bom);
        File.WriteAllText(files.Json, ToJson(r), Utf8Bom);
        return files;
    }

    public static string ToJson(DiagnosticReport r) => JsonSerializer.Serialize(r, JsonOptions);

    public static DiagnosticReport? FromJson(string json) =>
        JsonSerializer.Deserialize<DiagnosticReport>(json.TrimStart('﻿'), JsonOptions);

    // ---------- Plain text (attach to the Hub ticket) ----------

    public static string ToText(DiagnosticReport r)
    {
        var s = new StringBuilder();
        void Line(string text = "") => s.AppendLine(text);
        void Row(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) Line($"  {label,-22} {value}"); }

        Line($"{Brand.Company}: computer check report");
        Line(new string('=', 60));
        Row("Ticket", r.Ticket);
        Row("Customer", r.Customer);
        Row("Technician", r.Technician);
        Row("Checked", Format.DateAndTime(r.Started));
        Row("Tool", $"{r.Tool} {r.ToolVersion}" + (r.RanAsAdministrator ? "" : " (not as administrator)"));
        Line();

        Line("FINDINGS");
        foreach (var f in r.Findings)
        {
            Line($"  [{Tag(f.Severity)}] {f.Area}: {f.Title}");
            if (!string.IsNullOrWhiteSpace(f.Detail)) Line($"      {f.Detail}");
        }
        Line();

        var c = r.Computer;
        Line("COMPUTER");
        Row("Name", c.Name);
        Row("Make and model", Join(" ", c.Manufacturer, c.Model));
        Row("Serial number", c.SerialNumber);
        Row("Windows", Join(" ", c.OsName, c.OsArchitecture, string.IsNullOrEmpty(c.OsBuild) ? "" : "build " + c.OsBuild));
        Row("Installed", c.OsInstallDate is null ? null : Format.Date(c.OsInstallDate));
        Row("Last restart", c.LastBoot is null ? null : Format.DateAndTime(c.LastBoot));
        Row("Newest update", c.LastUpdateInstalled is null ? null : Format.Date(c.LastUpdateInstalled));
        Row("Processor", Join(", ", c.Cpu, c.CpuCores is null ? "" : $"{c.CpuCores} cores / {c.CpuThreads} threads"));
        Row("Memory", c.RamBytes is null ? null : Format.Bytes(c.RamBytes));
        foreach (var m in c.Memory)
            Row("  " + m.Slot, Join(", ", Format.Bytes(m.CapacityBytes), m.SpeedMhz is null ? "" : m.SpeedMhz + " MHz", m.Manufacturer, m.PartNumber));
        Row("BIOS", Join(", ", c.BiosVersion, c.BiosDate is null ? "" : Format.Date(c.BiosDate)));
        Line();

        Line("DRIVES");
        foreach (var d in r.Disks)
        {
            Line($"  {d.Name} ({Format.Bytes(d.SizeBytes)}, {d.MediaType}, {d.BusType})");
            Row("  Health", d.Health);
            Row("  Serial", d.Serial);
            Row("  Temperature", d.TemperatureC is null ? null : d.TemperatureC + " °C");
            Row("  Wear used", d.WearPercent is null ? null : d.WearPercent + "%");
            Row("  Power-on hours", d.PowerOnHours?.ToString(CultureInfo.InvariantCulture));
            Row("  Uncorrected errors", d.ReadErrorsUncorrected?.ToString(CultureInfo.InvariantCulture));
        }
        foreach (var v in r.Volumes)
            Row(v.Name + (v.IsSystem ? " (Windows)" : ""),
                $"{Format.Bytes(v.FreeBytes)} free of {Format.Bytes(v.SizeBytes)}, {v.FileSystem}" +
                (string.IsNullOrEmpty(v.BitLocker) ? "" : ", BitLocker " + v.BitLocker));
        Line();

        if (r.Battery is { } bat)
        {
            Line("BATTERY");
            Row("Health", bat.HealthPercent is null ? null : bat.HealthPercent + "% of original capacity");
            Row("Charge cycles", bat.CycleCount?.ToString(CultureInfo.InvariantCulture));
            Row("Charge now", bat.ChargePercent is null ? null : bat.ChargePercent + "%");
            Line();
        }

        Line("NETWORK");
        Row("Router answers", Format.YesNo(r.Network.GatewayReachable));
        Row("Internet answers", Format.YesNo(r.Network.InternetReachable) + (r.Network.InternetPingMs is null ? "" : $" ({r.Network.InternetPingMs} ms)"));
        Row("Name lookup (DNS)", Format.YesNo(r.Network.DnsWorks, "Works", "Failed"));
        foreach (var a in r.Network.Adapters)
            Row(a.Name, Join(", ", string.Join(" ", a.IPv4), a.SpeedMbps is null ? "" : a.SpeedMbps + " Mbps", a.Mac));
        Line();

        Line("SECURITY");
        if (r.Antivirus.Count == 0) Row("Antivirus", "not reported by Windows");
        foreach (var av in r.Antivirus)
            Row("Antivirus", $"{av.Name}: {(av.Enabled ? "on" : "off")}, {(av.UpToDate ? "up to date" : "out of date")}");
        Line();

        Line($"WINDOWS EVENTS (last {r.Events.Days} days)");
        Row("Unexpected shutdowns", r.Events.UnexpectedShutdowns?.ToString(CultureInfo.InvariantCulture) ?? "not checked");
        Row("Blue screens", r.Events.BlueScreens?.ToString(CultureInfo.InvariantCulture) ?? "not checked");
        Row("Bad blocks", r.Events.DiskErrors?.ToString(CultureInfo.InvariantCulture) ?? "not checked");
        Row("Retried reads", r.Events.DiskWarnings?.ToString(CultureInfo.InvariantCulture) ?? "not checked");
        if (r.Events.DiskEventDrives.Count > 0) Row("Drives involved", string.Join(", ", r.Events.DiskEventDrives));
        Line();

        Line($"STARTS WITH WINDOWS ({r.Startup.Count})");
        foreach (var st in r.Startup) Line($"  {st.Name}  [{st.Location}]");
        Line();

        Line($"INSTALLED PROGRAMS ({r.Programs.Count})");
        foreach (var p in r.Programs) Line("  " + Join("  ", p.Name, p.Version, p.Publisher));
        Line();

        if (r.Notes.Count > 0)
        {
            Line("NOT CHECKED");
            foreach (var n in r.Notes) Line("  - " + n);
            Line();
        }

        Line(Disclaimer);
        Line($"{Brand.Company} · {Brand.Phone} · {Brand.Website}");
        return s.ToString();
    }

    // ---------- HTML (show or print for the customer) ----------

    public static string ToHtml(DiagnosticReport r)
    {
        static string E(string? x) => WebUtility.HtmlEncode(x ?? "");
        var s = new StringBuilder();
        var c = r.Computer;

        s.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        s.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        s.Append("<title>Computer check: ").Append(E(c.Name)).Append("</title><style>");
        s.Append(Css);
        s.Append("</style></head><body><header><div><strong>Night Tech</strong> Solutions</div>");
        s.Append("<div class=\"sub\">Computer check report</div></header><main>");

        s.Append("<section class=\"meta\"><table>");
        MetaRow(s, "Ticket", r.Ticket);
        MetaRow(s, "Customer", r.Customer);
        MetaRow(s, "Computer", Join(" ", c.Manufacturer, c.Model));
        MetaRow(s, "Serial number", c.SerialNumber);
        MetaRow(s, "Checked", Format.DateAndTime(r.Started));
        MetaRow(s, "Technician", r.Technician);
        s.Append("</table></section>");

        var worst = r.Findings.Count == 0 ? Severity.Good : r.Findings.Max(f => f.Severity);
        s.Append("<section><h2>What we found</h2><p class=\"summary ").Append(Css_(worst)).Append("\">")
         .Append(E(Summary(r))).Append("</p><ul class=\"findings\">");
        foreach (var f in r.Findings)
        {
            s.Append("<li class=\"").Append(Css_(f.Severity)).Append("\"><span class=\"tag\">").Append(E(Tag(f.Severity)))
             .Append("</span><b>").Append(E(f.Title)).Append("</b>");
            if (!string.IsNullOrWhiteSpace(f.Detail)) s.Append("<div>").Append(E(f.Detail)).Append("</div>");
            s.Append("</li>");
        }
        s.Append("</ul></section>");

        s.Append("<section><h2>Computer</h2><table>");
        Row2(s, "Name", c.Name);
        Row2(s, "Windows", Join(" ", c.OsName, c.OsArchitecture, string.IsNullOrEmpty(c.OsBuild) ? "" : "build " + c.OsBuild));
        Row2(s, "Newest update", c.LastUpdateInstalled is null ? "" : Format.Date(c.LastUpdateInstalled));
        Row2(s, "Processor", c.Cpu);
        Row2(s, "Memory", c.RamBytes is null ? "" : Format.Bytes(c.RamBytes));
        s.Append("</table></section>");

        if (r.Disks.Count > 0 || r.Volumes.Count > 0)
        {
            s.Append("<section><h2>Drives</h2><table><tr><th>Drive</th><th>Size</th><th>Type</th><th>Health</th><th>Wear</th><th>Temp</th></tr>");
            foreach (var d in r.Disks)
                s.Append("<tr><td>").Append(E(d.Name)).Append("</td><td>").Append(E(Format.Bytes(d.SizeBytes)))
                 .Append("</td><td>").Append(E(Join(" ", d.MediaType, d.BusType))).Append("</td><td>").Append(E(d.Health))
                 .Append("</td><td>").Append(d.WearPercent is null ? "" : d.WearPercent + "%")
                 .Append("</td><td>").Append(d.TemperatureC is null ? "" : d.TemperatureC + " °C").Append("</td></tr>");
            s.Append("</table><table><tr><th>Volume</th><th>Free</th><th>Size</th><th>BitLocker</th></tr>");
            foreach (var v in r.Volumes)
                s.Append("<tr><td>").Append(E(v.Name + (v.IsSystem ? " (Windows)" : ""))).Append("</td><td>")
                 .Append(E($"{Format.Bytes(v.FreeBytes)} ({v.FreePercent:0}%)")).Append("</td><td>").Append(E(Format.Bytes(v.SizeBytes)))
                 .Append("</td><td>").Append(E(v.BitLocker)).Append("</td></tr>");
            s.Append("</table></section>");
        }

        if (r.Battery?.HealthPercent is { } h)
            s.Append("<section><h2>Battery</h2><p>Holds about <b>").Append(h).Append("%</b> of its original charge")
             .Append(r.Battery.CycleCount is > 0 ? $" after {r.Battery.CycleCount} charge cycles" : "").Append(".</p></section>");

        s.Append("<section><h2>Internet and security</h2><table>");
        Row2(s, "Router answers", Format.YesNo(r.Network.GatewayReachable));
        Row2(s, "Internet answers", Format.YesNo(r.Network.InternetReachable));
        Row2(s, "Website names look up", Format.YesNo(r.Network.DnsWorks));
        foreach (var av in r.Antivirus)
            Row2(s, "Antivirus", $"{av.Name}: {(av.Enabled ? "on" : "off")}, {(av.UpToDate ? "up to date" : "out of date")}");
        s.Append("</table></section>");

        s.Append("<footer><p>").Append(E(Disclaimer)).Append("</p><p>").Append(E(Brand.Company)).Append(" &middot; ")
         .Append(E(Brand.Phone)).Append(" &middot; nighttech.solutions &middot; ").Append(E(Brand.ServiceArea))
         .Append("</p><p class=\"tool\">").Append(E($"{r.Tool} {r.ToolVersion}")).Append("</p></footer></main></body></html>");
        return s.ToString();

        static void MetaRow(StringBuilder sb, string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) Row2(sb, label, value);
        }
        static void Row2(StringBuilder sb, string label, string? value) =>
            sb.Append("<tr><th>").Append(E(label)).Append("</th><td>").Append(E(value)).Append("</td></tr>");
    }

    public static string Summary(DiagnosticReport r)
    {
        var problems = r.Findings.Count(f => f.Severity == Severity.Problem);
        var warnings = r.Findings.Count(f => f.Severity == Severity.Warning);
        if (problems == 0 && warnings == 0) return "No problems found in these checks.";
        var parts = new List<string>();
        if (problems > 0) parts.Add(problems == 1 ? "1 problem" : $"{problems} problems");
        if (warnings > 0) parts.Add(warnings == 1 ? "1 thing to watch" : $"{warnings} things to watch");
        return string.Join(" and ", parts) + " found.";
    }

    private static string Tag(Severity s) => s switch
    {
        Severity.Problem => "PROBLEM",
        Severity.Warning => "WATCH",
        Severity.Info => "NOTE",
        _ => "GOOD",
    };

    private static string Css_(Severity s) => s.ToString().ToLowerInvariant();

    private static string Join(string sep, params string?[] parts) =>
        string.Join(sep, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

    private const string Css = """
        body{margin:0;font-family:"Segoe UI",Arial,sans-serif;color:#1a202c;background:#f7fafc;font-size:15px;line-height:1.45}
        header{background:#1a365d;color:#fff;padding:18px 24px;font-size:22px}
        header .sub{font-size:14px;color:#dbe7f5}
        main{max-width:860px;margin:0 auto;padding:16px}
        section{background:#fff;border:1px solid #d1d5db;border-radius:8px;padding:14px 18px;margin-bottom:12px}
        h2{font-size:17px;color:#1a365d;margin:0 0 8px}
        table{border-collapse:collapse;width:100%;margin-bottom:6px}
        th,td{text-align:left;padding:5px 8px;border-bottom:1px solid #edf2f7;vertical-align:top}
        th{width:34%;color:#4a5568;font-weight:600}
        .meta th{width:22%}
        .summary{font-size:17px;font-weight:600;padding:8px 12px;border-radius:6px}
        .findings{list-style:none;padding:0;margin:0}
        .findings li{padding:8px 10px;border-left:5px solid #a0aec0;margin:6px 0;background:#f7fafc}
        .findings li div{color:#4a5568;margin-top:2px}
        .tag{display:inline-block;font-size:11px;font-weight:700;letter-spacing:.04em;margin-right:8px;padding:1px 6px;border-radius:4px;background:#e2e8f0}
        li.problem{border-color:#c53030}li.problem .tag,.summary.problem{background:#fed7d7;color:#822727}
        li.warning{border-color:#c2410c}li.warning .tag,.summary.warning{background:#ffedd5;color:#7c2d12}
        li.info{border-color:#3182ce}li.info .tag,.summary.info{background:#e6f2ff;color:#1a365d}
        li.good{border-color:#2f855a}li.good .tag,.summary.good{background:#dcfce7;color:#14532d}
        footer{font-size:12px;color:#4a5568;padding:8px 4px}
        footer .tool{color:#a0aec0}
        @media print{body{background:#fff}section{break-inside:avoid;border-color:#ccc}header{-webkit-print-color-adjust:exact;print-color-adjust:exact}}
        """;
}
