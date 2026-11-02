using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NightTech.Core.Diagnostics;

namespace NightTech.Core.Cleanup;

/// <summary>Measurements taken before and after the cleanup, so the customer can see the difference.</summary>
public sealed class Snapshot
{
    public DateTime Taken { get; set; }
    public long FreeBytes { get; set; }
    public long TotalBytes { get; set; }
    public int StartupItemsOn { get; set; }
    public int Processes { get; set; }
    public double MemoryUsedPercent { get; set; }
    public double RamGb { get; set; }
    /// <summary>Seconds from power-on to a usable desktop, as Windows last measured it (null if unknown).</summary>
    public double? BootSeconds { get; set; }
    public string DriveKind { get; set; } = "";   // "SSD", "HDD" or ""
    public string System { get; set; } = "";       // "Windows 11 Pro 24H2", "macOS 15.1"
    public bool? Protection { get; set; }          // antivirus/XProtect on and current
    public double FreePercent => TotalBytes > 0 ? 100.0 * FreeBytes / TotalBytes : 0;
}

public enum ItemResult { Done, Skipped, Warning, Failed, NotRun }

public sealed class TaskLine
{
    public string Area { get; set; } = "";     // "Junk", "Startup", "Programs", "Security", "Repair"
    public string What { get; set; } = "";
    public ItemResult Result { get; set; }
    public string Detail { get; set; } = "";
}

/// <summary>A PC (or Mac) cleanup: what was found, what was done, before and after. Goes to the customer and the Hub ticket.</summary>
public sealed class CleanupReport
{
    public string Tool { get; set; } = "Night Tech Cleanup";
    public string ToolVersion { get; set; } = "";
    public string Kind { get; set; } = "pc-cleanup";
    public string Platform { get; set; } = "Windows";
    public string Ticket { get; set; } = "";
    public string Technician { get; set; } = "";
    public string Customer { get; set; } = "";
    public string Computer { get; set; } = "";
    public DateTime Started { get; set; }
    public DateTime Finished { get; set; }
    public bool RestorePoint { get; set; }
    public string RestorePointNote { get; set; } = "";
    public Snapshot? Before { get; set; }
    public Snapshot? After { get; set; }
    public List<JunkCleanResult> Junk { get; set; } = new();
    public List<TaskLine> Tasks { get; set; } = new();
    public List<string> Threats { get; set; } = new();
    public List<string> Findings { get; set; } = new();
    public List<string> Recommendations { get; set; } = new();
    public string Notes { get; set; } = "";

    public long BytesFreed => Junk.Sum(j => j.BytesFreed);

    /// <summary>Extra notes for the "Please note" list (the standard ones come from <see cref="Notices"/>).</summary>
    public List<string> Limitations { get; set; } = new();

    /// <summary>The "Please note" list, written for what was actually done on this computer.</summary>
    public List<string> Notices()
    {
        var list = new List<string> { "This report documents the work performed on the date shown. It is not a guarantee of speed or that the computer is free of every problem." };
        var emptied = Junk.FirstOrDefault(j => j.CategoryId is "recycle-bin" or "mac-trash" && j.BytesFreed > 0);
        list.Add(emptied is null
            ? "Your documents, photos, passwords, browser history and bookmarks were not deleted."
            : $"Your documents, photos, passwords, browser history and bookmarks were not deleted, apart from what was in the {(emptied.CategoryId == "mac-trash" ? "Trash" : "Recycle Bin")}, which was emptied with your agreement.");
        if (Tasks.Any(t => t.Area == "Startup" && t.Result == ItemResult.Done && t.What.StartsWith("Turned off ", StringComparison.Ordinal)))
            list.Add(Platform == "macOS"
                ? "Background items that were turned off were set aside, not deleted. We can put them back at any time."
                : "Startup programs that were turned off can be turned back on at any time (Task Manager > Startup apps).");
        list.AddRange(Limitations);
        return list;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static CleanupReport? FromJson(string s) => JsonSerializer.Deserialize<CleanupReport>(s.TrimStart('﻿'), Json);

    /// <summary>What's worth suggesting after the cleanup, from the measurements. Facts only; no prices.</summary>
    public static List<string> Recommend(Snapshot s, bool windows10 = false)
    {
        var r = new List<string>();
        if (s.RamGb > 0 && s.RamGb < 7.5) r.Add($"Memory: this computer has {s.RamGb:0} GB. Upgrading to 8 or 16 GB (if it can be upgraded) is usually the biggest speed improvement.");
        if (s.DriveKind == "HDD") r.Add("Storage: the main drive is a hard disk. Replacing it with an SSD typically makes starting up and opening programs several times faster.");
        if (s.TotalBytes > 0 && s.FreePercent < 15) r.Add($"Space: only {s.FreePercent:0}% of the drive is free. Keep at least 15% free; consider moving photos and videos to an external drive or cloud storage.");
        if (s.BootSeconds is > 90) r.Add($"Start-up time: Windows measured about {s.BootSeconds:0} seconds to start. If it's still slow after this cleanup, check the drive's health and the startup programs again.");
        if (windows10) r.Add("Windows 10 no longer gets free security updates (support ended October 14, 2025). Upgrading to Windows 11 (if the computer supports it) or a newer computer keeps it protected.");
        if (s.Protection == false) r.Add("Protection: antivirus wasn't confirmed on and up to date. Turn on Windows Security (or the antivirus you pay for) and keep it updated.");
        if (s.MemoryUsedPercent > 85) r.Add($"Memory use stays high ({s.MemoryUsedPercent:0}%). Fewer programs open at once, or more memory, will help.");
        return r;
    }

    // ---------------- Text ----------------

    public string ToText()
    {
        var s = new StringBuilder();
        s.AppendLine($"{Brand.Company}: {(Platform == "macOS" ? "Mac" : "PC")} cleanup report");
        s.AppendLine(new string('=', 60));
        void Row(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) s.AppendLine($"  {k,-18} {v}"); }
        Row("For", Customer);
        Row("Ticket", Ticket);
        Row("Computer", Computer);
        Row("Technician", Technician);
        Row("Date", Finished.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        Row("Restore point", RestorePoint ? "Created before any change" : RestorePointNote);
        s.AppendLine();
        s.AppendLine("BEFORE AND AFTER");
        foreach (var (label, b, a) in Compare()) s.AppendLine($"  {label,-22} {b,-18} -> {a}");
        s.AppendLine();
        s.AppendLine($"JUNK REMOVED: {Format.Bytes(BytesFreed)}");
        foreach (var j in Junk.Where(j => j.FilesRemoved > 0 || j.BytesFreed > 0))
            s.AppendLine($"  {j.Name,-34} {Format.Bytes(j.BytesFreed),10}  ({j.FilesRemoved:N0} files{(j.FilesSkipped > 0 ? $", {j.FilesSkipped:N0} in use left alone" : "")})");
        foreach (var area in Tasks.Select(t => t.Area).Distinct())
        {
            s.AppendLine();
            s.AppendLine(area.ToUpperInvariant());
            foreach (var t in Tasks.Where(t => t.Area == area))
                s.AppendLine($"  [{Mark(t.Result)}] {t.What}{(t.Detail.Length > 0 ? ": " + t.Detail : "")}");
        }
        if (Threats.Count > 0)
        {
            s.AppendLine();
            s.AppendLine("THREATS FOUND BY THE SECURITY SCAN (and what was done about each)");
            foreach (var t in Threats) s.AppendLine("  - " + t);
        }
        if (Findings.Count > 0)
        {
            s.AppendLine();
            s.AppendLine("ALSO FOUND");
            foreach (var f in Findings) s.AppendLine("  - " + f);
        }
        if (Recommendations.Count > 0)
        {
            s.AppendLine();
            s.AppendLine("RECOMMENDATIONS");
            foreach (var r in Recommendations) s.AppendLine("  - " + r);
        }
        if (!string.IsNullOrWhiteSpace(Notes)) { s.AppendLine(); s.AppendLine("NOTES"); s.AppendLine("  " + Notes.Trim()); }
        s.AppendLine();
        s.AppendLine("PLEASE NOTE");
        foreach (var l in Notices()) s.AppendLine("  - " + l);
        s.AppendLine();
        s.AppendLine($"{Brand.Company} · {Brand.Phone} · {Brand.Website}");
        return s.ToString();
    }

    private static string Mark(ItemResult r) => r switch
    {
        ItemResult.Done => "OK", ItemResult.Warning => "!!", ItemResult.Failed => "XX", ItemResult.Skipped => "--", _ => "  ",
    };

    public List<(string Label, string Before, string After)> Compare()
    {
        var rows = new List<(string, string, string)>();
        if (Before is null) return rows;
        var a = After;
        string Or(Func<Snapshot, string> f) => a is null ? "-" : f(a);
        rows.Add(("Free space", $"{Format.Bytes(Before.FreeBytes)} ({Before.FreePercent:0}%)", Or(x => $"{Format.Bytes(x.FreeBytes)} ({x.FreePercent:0}%)")));
        rows.Add(("Startup programs on", Before.StartupItemsOn.ToString(CultureInfo.InvariantCulture), Or(x => x.StartupItemsOn.ToString(CultureInfo.InvariantCulture))));
        rows.Add(("Programs running", Before.Processes.ToString(CultureInfo.InvariantCulture), Or(x => x.Processes.ToString(CultureInfo.InvariantCulture))));
        rows.Add(("Memory in use", $"{Before.MemoryUsedPercent:0}%", Or(x => $"{x.MemoryUsedPercent:0}%")));
        if (Before.BootSeconds is { } bs) rows.Add(("Start-up time", $"{bs:0} seconds", Or(x => x.BootSeconds is { } s2 && s2 != bs ? $"{s2:0} seconds" : "measured at next restart")));
        return rows;
    }

    // ---------------- Customer page (HTML) ----------------

    public string ToHtml()
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var h = new StringBuilder();
        h.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        h.Append($"<title>{E(Platform == "macOS" ? "Mac" : "PC")} cleanup report - {E(Customer)}</title><style>");
        h.Append("body{font-family:'Segoe UI',system-ui,-apple-system,Arial,sans-serif;color:#1a202c;margin:0;background:#f7fafc}main{max-width:820px;margin:0 auto;background:#fff;padding:32px 36px}");
        h.Append("header{display:flex;justify-content:space-between;align-items:flex-end;border-bottom:3px solid #c2410c;padding-bottom:14px;margin-bottom:18px}h1{font-size:24px;margin:0;color:#1a365d}h2{font-size:17px;color:#1a365d;margin:24px 0 8px}");
        h.Append(".big{font-size:34px;font-weight:700;color:#c2410c}table{width:100%;border-collapse:collapse;font-size:14px}td,th{padding:7px 8px;border-bottom:1px solid #e2e8f0;text-align:left}th{color:#4a5568;font-weight:600}");
        h.Append(".ok{color:#2f855a;font-weight:700}.warn{color:#c05621;font-weight:700}.bad{color:#c53030;font-weight:700}.muted{color:#4a5568;font-size:13px}ul{padding-left:20px}li{margin:4px 0}");
        h.Append("@media print{body{background:#fff}main{padding:0}}</style></head><body><main>");
        h.Append($"<header><div><h1>{E(Brand.Company)}</h1><div class=\"muted\">{E(Platform == "macOS" ? "Mac" : "PC")} cleanup report · {E(Finished.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture))}</div></div>");
        h.Append($"<div class=\"muted\" style=\"text-align:right\">{E(Customer)}<br>{E(Computer)}{(Ticket.Length > 0 ? "<br>Ticket " + E(Ticket) : "")}</div></header>");
        h.Append($"<p><span class=\"big\">{E(Format.Bytes(BytesFreed))}</span> of junk removed");
        var startup = Tasks.Count(t => t.Area == "Startup" && t.Result == ItemResult.Done && t.What.StartsWith("Turned off ", StringComparison.Ordinal));
        if (startup > 0) h.Append($", <strong>{startup}</strong> startup program{(startup == 1 ? "" : "s")} turned off");
        var removed = Tasks.Count(t => t.Area == "Programs" && t.Result == ItemResult.Done);
        if (removed > 0) h.Append($", <strong>{removed}</strong> program{(removed == 1 ? "" : "s")} removed");
        h.Append(".</p>");
        if (RestorePoint) h.Append("<p class=\"muted\">A restore point was created before any change.</p>");

        var cmp = Compare();
        if (cmp.Count > 0)
        {
            h.Append("<h2>Before and after</h2><table><tr><th></th><th>Before</th><th>After</th></tr>");
            foreach (var (l, b, a) in cmp) h.Append($"<tr><td>{E(l)}</td><td>{E(b)}</td><td>{E(a)}</td></tr>");
            h.Append("</table>");
        }
        h.Append("<h2>What was done</h2><table>");
        foreach (var j in Junk.Where(j => j.BytesFreed > 0))
            h.Append($"<tr><td><span class=\"ok\">OK</span></td><td>Removed {E(j.Name.ToLowerInvariant())}</td><td>{E(Format.Bytes(j.BytesFreed))}</td></tr>");
        foreach (var t in Tasks)
        {
            var (cls, mark) = t.Result switch { ItemResult.Done => ("ok", "OK"), ItemResult.Warning => ("warn", "Note"), ItemResult.Failed => ("bad", "Problem"), _ => ("muted", "Skipped") };
            h.Append($"<tr><td><span class=\"{cls}\">{mark}</span></td><td>{E(t.What)}</td><td>{E(t.Detail)}</td></tr>");
        }
        h.Append("</table>");
        if (Threats.Count > 0) { h.Append("<h2>Threats found by the security scan</h2><ul>"); foreach (var t in Threats) h.Append($"<li>{E(t)}</li>"); h.Append("</ul>"); }
        if (Findings.Count > 0) { h.Append("<h2>Also found</h2><ul>"); foreach (var f in Findings) h.Append($"<li>{E(f)}</li>"); h.Append("</ul>"); }
        if (Recommendations.Count > 0) { h.Append("<h2>Recommendations</h2><ul>"); foreach (var r in Recommendations) h.Append($"<li>{E(r)}</li>"); h.Append("</ul>"); }
        if (!string.IsNullOrWhiteSpace(Notes)) h.Append($"<h2>Notes</h2><p>{E(Notes)}</p>");
        h.Append("<h2>Please note</h2><ul class=\"muted\">");
        foreach (var l in Notices()) h.Append($"<li>{E(l)}</li>");
        h.Append("</ul>");
        h.Append($"<p class=\"muted\" style=\"margin-top:28px;text-align:center\">{E(Brand.Company)} · {E(Brand.Phone)} · {E(Brand.Website)}<br>Technician: {E(Technician)}</p>");
        h.Append("</main></body></html>");
        return h.ToString();
    }

    public (string Json, string Text, string Html) Save(string folder)
    {
        Directory.CreateDirectory(folder);
        var name = Format.SafeFileName($"cleanup-{(Ticket.Length > 0 ? Ticket : Computer)}-{Started:yyyyMMdd-HHmm}");
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
