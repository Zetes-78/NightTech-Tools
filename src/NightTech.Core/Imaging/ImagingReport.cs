using System.Text;
using System.Text.Json;

namespace NightTech.Core.Imaging;

/// <summary>Who and what the job is for. Goes on the report and links the case to a Night Tech Hub ticket.</summary>
public sealed record CaseInfo(string CaseName, string? TicketNumber, string? Technician, string? Notes);

/// <summary>Writes a plain-text report (for the customer file) and a JSON report (for the Hub) next to the image.</summary>
public static class ImagingReport
{
    public const int MaxBadSectorsListed = 200;

    public static (string TextPath, string JsonPath) Write(ImagingResult result, CaseInfo info, string toolVersion)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(result.ImagePath))!;
        var baseName = Path.GetFileNameWithoutExtension(result.ImagePath);
        var txt = Path.Combine(folder, baseName + "-report.txt");
        var json = Path.Combine(folder, baseName + "-report.json");
        File.WriteAllText(txt, BuildText(result, info, toolVersion), Encoding.UTF8);
        File.WriteAllText(json, BuildJson(result, info, toolVersion), Encoding.UTF8);
        return (txt, json);
    }

    public static string StatusText(ImagingStatus s) => s switch
    {
        ImagingStatus.Completed => "Complete. Every sector was copied.",
        ImagingStatus.CompletedWithBadSectors => "Complete with unreadable sectors. Those sectors are zero-filled in the image.",
        ImagingStatus.Cancelled => "Stopped before finishing. Progress is saved and can be resumed.",
        ImagingStatus.DeviceLost => "Stopped because the drive stopped responding. Progress is saved.",
        _ => s.ToString(),
    };

    public static string BuildText(ImagingResult r, CaseInfo info, string toolVersion)
    {
        var m = r.Map;
        var sb = new StringBuilder();
        sb.AppendLine("NIGHT TECH SOLUTIONS - DRIVE IMAGING REPORT");
        sb.AppendLine($"{Brand.Website}  |  {Brand.Phone}");
        sb.AppendLine(new string('=', 60));
        sb.AppendLine($"Case:            {info.CaseName}");
        if (!string.IsNullOrWhiteSpace(info.TicketNumber)) sb.AppendLine($"Ticket:          {info.TicketNumber}");
        if (!string.IsNullOrWhiteSpace(info.Technician)) sb.AppendLine($"Technician:      {info.Technician}");
        sb.AppendLine($"Started:         {r.StartedLocal:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Finished:        {r.FinishedLocal:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Result:          {StatusText(r.Status)}");
        sb.AppendLine();
        sb.AppendLine("SOURCE DRIVE (read only, never written)");
        sb.AppendLine($"Drive:           {m.SourceDescription}");
        if (!string.IsNullOrWhiteSpace(m.SourceSerial)) sb.AppendLine($"Serial:          {m.SourceSerial}");
        sb.AppendLine($"Size:            {FormatBytes(m.SourceLength)} ({m.SourceLength:N0} bytes)");
        sb.AppendLine($"Sector size:     {m.SectorSize} bytes");
        sb.AppendLine();
        sb.AppendLine("RESULT");
        sb.AppendLine($"Copied:          {FormatBytes(m.BytesGood)} ({Pct(m.BytesGood, m.SourceLength)})");
        sb.AppendLine($"Unreadable:      {FormatBytes(m.BytesBad)} ({m.BadSectors.Count:N0} sectors)");
        sb.AppendLine($"Not yet read:    {FormatBytes(m.BytesPending)}");
        sb.AppendLine($"Retry passes:    {m.RetryPassesDone}");
        sb.AppendLine();
        sb.AppendLine("IMAGE");
        sb.AppendLine($"Image file:      {r.ImagePath}");
        sb.AppendLine($"Map file:        {r.MapPath}");
        if (!string.IsNullOrEmpty(r.ImageSha256)) sb.AppendLine($"SHA-256:         {r.ImageSha256}");
        if (m.BadSectors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"UNREADABLE SECTORS (first {Math.Min(MaxBadSectorsListed, m.BadSectors.Count)} of {m.BadSectors.Count:N0}, as sector numbers)");
            foreach (var chunk in m.BadSectors.Take(MaxBadSectorsListed).Chunk(8))
                sb.AppendLine("  " + string.Join(", ", chunk));
        }
        if (!string.IsNullOrWhiteSpace(info.Notes))
        {
            sb.AppendLine();
            sb.AppendLine("NOTES");
            sb.AppendLine(info.Notes);
        }
        sb.AppendLine();
        sb.AppendLine("Data recovery results can't be guaranteed. Recovery work is done on this image, not on the original drive.");
        sb.AppendLine($"Made with Night Tech Recovery {toolVersion}.");
        return sb.ToString();
    }

    public static string BuildJson(ImagingResult r, CaseInfo info, string toolVersion)
    {
        var m = r.Map;
        var obj = new
        {
            tool = "Night Tech Recovery",
            toolVersion,
            caseName = info.CaseName,
            ticket = info.TicketNumber,
            technician = info.Technician,
            notes = info.Notes,
            status = r.Status.ToString(),
            started = r.StartedLocal,
            finished = r.FinishedLocal,
            source = new { description = m.SourceDescription, serial = m.SourceSerial, length = m.SourceLength, sectorSize = m.SectorSize },
            bytesGood = m.BytesGood,
            bytesBad = m.BytesBad,
            bytesPending = m.BytesPending,
            badSectorCount = m.BadSectors.Count,
            badSectorsSample = m.BadSectors.Take(MaxBadSectorsListed).ToArray(),
            imagePath = r.ImagePath,
            mapPath = r.MapPath,
            imageSha256 = r.ImageSha256,
        };
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1000 && u < units.Length - 1) { v /= 1000; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:0.##} {units[u]}";
    }

    private static string Pct(long part, long whole) => whole == 0 ? "0%" : $"{100.0 * part / whole:0.###}%";
}
