using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using NightTech.Core.Diagnostics;

namespace NightTech.Core.Wipe;

/// <summary>
/// What Night Tech Wipe did to one drive. Night Tech Hub reads the JSON version to fill in the
/// Certificate of Data Wipe, which documents the work performed; it is not a guarantee that data can't be recovered.
/// </summary>
public sealed class WipeReport
{
    public string Tool { get; set; } = "Night Tech Wipe";
    public string ToolVersion { get; set; } = "";
    public string Ticket { get; set; } = "";
    public string Technician { get; set; } = "";
    public string Notes { get; set; } = "";

    public WipeDisk Disk { get; set; } = new();

    /// <summary>Plain-language method, e.g. "Overwrite, 1 pass of zeros, every sector read back and checked".</summary>
    public string Method { get; set; } = "";
    public string Pattern { get; set; } = "";
    public bool Verified { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<WipeStatus>))]
    public WipeStatus Status { get; set; }
    /// <summary>True only when every sector was overwritten and read back correctly.</summary>
    public bool FullyVerified { get; set; }
    public string Message { get; set; } = "";

    public DateTime Started { get; set; }
    public DateTime Finished { get; set; }
    public long BytesWritten { get; set; }
    public long BytesVerified { get; set; }
    public long WriteErrorSectors { get; set; }
    public long VerifyMismatchSectors { get; set; }
    public long VerifyReadErrorSectors { get; set; }
    public List<WipeProblem> Problems { get; set; } = new();

    /// <summary>What an overwrite can't reach, stated plainly.</summary>
    public List<string> Limitations { get; set; } = new();

    public static string MethodText(WipeOptions o) =>
        $"Overwrite, 1 pass of {(o.Pattern == WipePattern.Zeros ? "zeros" : "random data")}" +
        (o.Verify ? ", every sector read back and checked" : ", not checked afterward");

    public static WipeReport From(WipeResult r, WipeOptions o, WipeDisk disk, string toolVersion, string ticket, string technician, string notes)
    {
        var rep = new WipeReport
        {
            ToolVersion = toolVersion, Ticket = ticket.Trim(), Technician = technician.Trim(), Notes = notes.Trim(),
            Disk = disk,
            Method = MethodText(o), Pattern = o.Pattern.ToString(), Verified = r.Verified,
            Status = r.Status, FullyVerified = r.FullyVerified, Message = r.Message,
            Started = r.Started, Finished = r.Finished,
            BytesWritten = r.BytesWritten, BytesVerified = r.BytesVerified,
            WriteErrorSectors = r.WriteErrorSectors, VerifyMismatchSectors = r.VerifyMismatchSectors,
            VerifyReadErrorSectors = r.VerifyReadErrorSectors, Problems = r.Problems.ToList(),
        };
        rep.Limitations.Add("Hidden areas some drives keep outside normal reach (HPA or DCO) are not overwritten.");
        if (disk.IsSolidState)
            rep.Limitations.Add("This is a solid-state drive. It keeps spare memory the computer can't address, so an overwrite " +
                                "may not reach every copy of old data. For the highest assurance, also use the drive maker's " +
                                "secure erase or sanitize tool, or destroy the drive physically.");
        if (!r.FullyVerified)
            rep.Limitations.Add("This wipe did not finish cleanly. Do not treat the drive as erased.");
        return rep;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static WipeReport? FromJson(string json) => JsonSerializer.Deserialize<WipeReport>(json.TrimStart('﻿'), Json);

    public string BaseName()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Ticket)) parts.Add(Ticket);
        parts.Add(string.IsNullOrWhiteSpace(Disk.Serial) ? "drive" : Disk.Serial);
        parts.Add("wipe");
        parts.Add(Started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        return Format.SafeFileName(string.Join("-", parts), 120);
    }

    /// <summary>Saves &lt;name&gt;.json (for Night Tech Hub) and &lt;name&gt;.txt (to read or print) and returns their paths.</summary>
    public (string Json, string Text) Save(string folder)
    {
        Directory.CreateDirectory(folder);
        var b = Path.Combine(folder, BaseName());
        var bom = new UTF8Encoding(true);
        File.WriteAllText(b + ".json", ToJson(), bom);
        File.WriteAllText(b + ".txt", ToText(), bom);
        return (b + ".json", b + ".txt");
    }

    public string ToText()
    {
        var s = new StringBuilder();
        void Row(string label, string? value) { if (!string.IsNullOrWhiteSpace(value)) s.AppendLine($"  {label,-24} {value}"); }
        string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

        s.AppendLine($"{Brand.Company}: drive wipe record");
        s.AppendLine(new string('=', 60));
        s.AppendLine();
        s.AppendLine(Status switch
        {
            WipeStatus.Completed when FullyVerified => "RESULT: Every sector was overwritten and checked.",
            WipeStatus.Completed => "RESULT: Every sector was overwritten (not checked afterward).",
            WipeStatus.CompletedWithErrors => "RESULT: FINISHED WITH ERRORS. Do not treat this drive as erased.",
            WipeStatus.Cancelled => "RESULT: STOPPED BEFORE FINISHING. The drive is only partly erased.",
            _ => "RESULT: FAILED. The drive is only partly erased.",
        });
        s.AppendLine();
        Row("Ticket", Ticket);
        Row("Technician", Technician);
        Row("Drive", Disk.Model);
        Row("Serial number", Disk.Serial);
        Row("Size", $"{Format.Bytes(Disk.SizeBytes)} ({N(Disk.SizeBytes)} bytes, {Disk.SectorSize}-byte sectors)");
        Row("Connection", Disk.Bus);
        Row("Drive type", Disk.MediaType);
        Row("Method", Method);
        Row("Started", Format.DateAndTime(Started));
        Row("Finished", Format.DateAndTime(Finished));
        Row("Bytes overwritten", N(BytesWritten));
        Row("Bytes checked", Verified ? N(BytesVerified) : "not checked");
        Row("Sectors not written", N(WriteErrorSectors));
        Row("Sectors wrong on check", N(VerifyMismatchSectors));
        Row("Sectors unreadable", N(VerifyReadErrorSectors));
        Row("Notes", Notes);
        Row("Tool", $"{Tool} {ToolVersion}");
        if (Problems.Count > 0)
        {
            s.AppendLine();
            s.AppendLine("PROBLEM SECTORS (first 100)");
            foreach (var p in Problems) s.AppendLine($"  byte {N(p.Offset)}: {p.Kind}");
        }
        s.AppendLine();
        s.AppendLine("LIMITS");
        foreach (var l in Limitations) s.AppendLine("  - " + l);
        s.AppendLine();
        s.AppendLine("This record documents the work performed. It is not a guarantee that data can't be recovered.");
        s.AppendLine($"{Brand.Company} · {Brand.Phone} · {Brand.Website}");
        return s.ToString();
    }
}

public sealed class WipeDisk
{
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public long SizeBytes { get; set; }
    public int SectorSize { get; set; }
    public string Bus { get; set; } = "";
    /// <summary>HDD, SSD or Unknown.</summary>
    public string MediaType { get; set; } = "Unknown";

    [JsonIgnore]
    public bool IsSolidState => MediaType == "SSD" || Bus == "NVMe" || Bus is "USB" or "SD card" or "MMC" && MediaType != "HDD";
}
