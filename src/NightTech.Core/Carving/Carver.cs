using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NightTech.Core.Carving;

public sealed class CarveOptions
{
    /// <summary>Kinds to look for. Empty means all.</summary>
    public HashSet<FileKind> Kinds { get; init; } = new();
    /// <summary>Files start on sector boundaries on every common file system, so only those are checked.</summary>
    public int SectorSize { get; init; } = 512;
    public long MaxPhotoBytes { get; init; } = 100L * 1024 * 1024;
    public long MaxDocumentBytes { get; init; } = 500L * 1024 * 1024;
    public long MaxVideoBytes { get; init; } = 16L * 1024 * 1024 * 1024;
    /// <summary>How far past a PDF's %%EOF to look for a later update of the same PDF.</summary>
    public long PdfUpdateWindow { get; init; } = 1024 * 1024;
    /// <summary>Smallest file worth keeping (tiny images are mostly icons and thumbnails).</summary>
    public long MinBytes { get; init; } = 1024;
    public bool Wants(FileKind k) => Kinds.Count == 0 || Kinds.Contains(k);
}

public sealed record CarvedFile(long Offset, long Length, FileKind Kind, string RelativePath);

public sealed record CarveProgress(long BytesScanned, long BytesTotal, int FilesFound, double BytesPerSecond);

[JsonConverter(typeof(JsonStringEnumConverter<CarveStatus>))]
public enum CarveStatus { Completed, Cancelled, Failed }

/// <summary>What a recovery scan found. The JSON version goes to Night Tech Hub; the text one to the customer file.</summary>
public sealed class CarveReport
{
    public string Tool { get; set; } = "Night Tech Recovery";
    public string ToolVersion { get; set; } = "";
    public string Kind { get; set; } = "file-recovery";
    public string Ticket { get; set; } = "";
    public string Technician { get; set; } = "";
    public string ImagePath { get; set; } = "";
    public long ImageBytes { get; set; }
    public string OutputFolder { get; set; } = "";
    public DateTime Started { get; set; }
    public DateTime Finished { get; set; }
    public CarveStatus Status { get; set; }
    public string Message { get; set; } = "";
    public long BytesScanned { get; set; }
    public Dictionary<string, int> Counts { get; set; } = new();
    public long BytesRecovered { get; set; }
    public List<CarvedFile> Files { get; set; } = new();
    public List<string> Limitations { get; set; } = new()
    {
        "Files are found by their contents, so their original names and folders aren't recovered.",
        "A file stored in pieces (fragmented) may come back damaged or incomplete; check each one before handing it over.",
        "Some found files may be old copies, or damaged where the drive had unreadable areas.",
        "Results depend on how much of each file was still on the drive. Data recovery results are not guaranteed.",
    };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter<FileKind>() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static CarveReport? FromJson(string s) => JsonSerializer.Deserialize<CarveReport>(s.TrimStart('﻿'), Json);

    public string ToText()
    {
        var s = new StringBuilder();
        s.AppendLine($"{Brand.Company}: file recovery report");
        s.AppendLine(new string('=', 60));
        void Row(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) s.AppendLine($"  {k,-16} {v}"); }
        Row("Ticket", Ticket);
        Row("Technician", Technician);
        Row("Drive image", ImagePath);
        Row("Image size", Imaging.ImagingReport.FormatBytes(ImageBytes));
        Row("Started", Started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        Row("Finished", Finished.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        Row("Result", Status switch
        {
            CarveStatus.Completed => "Whole image scanned.",
            CarveStatus.Cancelled => $"Stopped after scanning {Imaging.ImagingReport.FormatBytes(BytesScanned)} of {Imaging.ImagingReport.FormatBytes(ImageBytes)}.",
            _ => "Stopped by an error: " + Message,
        });
        Row("Files found", $"{Files.Count:N0} ({Imaging.ImagingReport.FormatBytes(BytesRecovered)})");
        foreach (var c in Counts.OrderByDescending(c => c.Value)) Row("  " + c.Key, c.Value.ToString("N0", CultureInfo.InvariantCulture));
        Row("Saved to", OutputFolder);
        s.AppendLine();
        s.AppendLine("PLEASE NOTE");
        foreach (var l in Limitations) s.AppendLine("  - " + l);
        s.AppendLine();
        s.AppendLine($"{Brand.Company} · {Brand.Phone} · {Brand.Website}");
        return s.ToString();
    }
}

/// <summary>
/// Finds files in a drive image by their contents (file carving). Reads the image only; found files are written to a
/// separate folder, sorted into Photos, Videos, Documents, Audio and Archives.
/// </summary>
public static class Carver
{
    /// <summary>The folder a kind of file is sorted into: Photos, Videos, Audio, Documents or Archives.</summary>
    public static string CategoryOf(FileKind k) => Formats.Category(k);

    public static IReadOnlyList<string> Categories { get; } = new[] { "Photos", "Videos", "Audio", "Documents", "Archives" };

    public static CarveReport Run(IImageReader image, string outputFolder, CarveOptions o, IProgress<CarveProgress>? progress,
        CancellationToken cancel, Func<DateTime>? clock = null)
    {
        clock ??= () => DateTime.Now;
        var report = new CarveReport { Started = clock(), ImageBytes = image.Length, OutputFolder = outputFolder };
        var src = new ByteSource(image);
        int sector = Math.Max(512, o.SectorSize);
        var chunk = new byte[4 * 1024 * 1024];
        var sw = Stopwatch.StartNew();
        long lastReport = -1;
        long pos = 0;
        try
        {
            Directory.CreateDirectory(outputFolder);
            while (pos < image.Length)
            {
                cancel.ThrowIfCancellationRequested();
                int n = image.Read(pos, chunk);
                if (n <= 0) break;
                long next = pos + n;                      // where the next chunk starts, unless a found file moves it
                for (int i = 0; i + 16 <= n; i += sector)
                {
                    var parser = Formats.Candidate(chunk.AsSpan(i, Math.Min(64, n - i)));
                    if (parser is null) continue;
                    long at = pos + i;
                    Measured? m;
                    try { m = parser(src, at, o); }
                    catch (Exception) { m = null; }      // a damaged structure is simply not a file
                    if (m is not { } found || !o.Wants(found.Kind) || found.Length < Math.Min(o.MinBytes, 64)) continue;
                    if (found.Length < o.MinBytes && found.Kind is FileKind.Jpeg or FileKind.Png or FileKind.Gif or FileKind.Bmp) continue;

                    var rel = Save(image, at, found, outputFolder);
                    report.Files.Add(new CarvedFile(at, found.Length, found.Kind, rel));
                    report.BytesRecovered += found.Length;
                    var key = Formats.Extension(found.Kind).ToUpperInvariant();
                    report.Counts[key] = report.Counts.GetValueOrDefault(key) + 1;

                    // Skip past the file so pictures inside it aren't found again; continue at the next sector.
                    long after = (at + found.Length + sector - 1) / sector * sector;
                    if (after >= pos + n) { next = after; break; }
                    i = (int)(after - pos) - sector;
                    cancel.ThrowIfCancellationRequested();
                }
                pos = next;
                report.BytesScanned = Math.Min(pos, image.Length);
                var ms = sw.ElapsedMilliseconds;
                if (progress is not null && ms - lastReport >= 250)
                {
                    lastReport = ms;
                    progress.Report(new CarveProgress(report.BytesScanned, image.Length, report.Files.Count, ms > 0 ? report.BytesScanned * 1000.0 / ms : 0));
                }
            }
            report.BytesScanned = image.Length;
            report.Status = CarveStatus.Completed;
            report.Message = $"Found {report.Files.Count:N0} file(s).";
        }
        catch (OperationCanceledException)
        {
            report.Status = CarveStatus.Cancelled;
            report.Message = $"Stopped by the technician. Found {report.Files.Count:N0} file(s) so far.";
        }
        catch (Exception ex)
        {
            report.Status = CarveStatus.Failed;
            report.Message = ex.Message;
        }
        progress?.Report(new CarveProgress(report.BytesScanned, image.Length, report.Files.Count, 0));
        report.Finished = clock();
        return report;
    }

    /// <summary>Writes one found file: &lt;Category&gt;\&lt;ext&gt;-&lt;sector&gt;.&lt;ext&gt;, named by where it was on the drive.</summary>
    private static string Save(IImageReader image, long offset, Measured m, string root)
    {
        var ext = Formats.Extension(m.Kind);
        var rel = Path.Combine(Formats.Category(m.Kind), $"{ext}-{offset / 512:D10}.{ext}");
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var outFile = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
        var buf = new byte[1024 * 1024];
        long done = 0;
        while (done < m.Length)
        {
            int want = (int)Math.Min(buf.Length, m.Length - done);
            int n = image.Read(offset + done, buf.AsSpan(0, want));
            if (n <= 0) throw new IOException($"Couldn't read the image at byte {offset + done:N0}.");
            outFile.Write(buf, 0, n);
            done += n;
        }
        return rel;
    }

    /// <summary>Saves report.json (for Night Tech Hub) and report.txt next to the recovered files.</summary>
    public static (string Json, string Text) SaveReport(CarveReport r, string folder)
    {
        Directory.CreateDirectory(folder);
        var bom = new UTF8Encoding(true);
        var stamp = r.Started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var json = Path.Combine(folder, $"recovered-files-{stamp}-report.json");
        var text = Path.Combine(folder, $"recovered-files-{stamp}-report.txt");
        File.WriteAllText(json, r.ToJson(), bom);
        File.WriteAllText(text, r.ToText(), bom);
        return (json, text);
    }
}
