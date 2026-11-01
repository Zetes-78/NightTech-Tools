using System.Text;

namespace NightTech.Core.Imaging;

/// <summary>File names for a recovery case: one folder per case holding the image, its map and its reports.</summary>
public sealed record CaseFiles(string Folder, string ImagePath, string MapPath)
{
    public static CaseFiles For(string outputRoot, string caseName)
    {
        var safe = SafeName(caseName);
        var folder = Path.Combine(outputRoot, safe);
        return new CaseFiles(folder, Path.Combine(folder, safe + ".img"), Path.Combine(folder, safe + ".map.json"));
    }

    /// <summary>True when a case with this name already has a map, so starting it again resumes.</summary>
    public bool CanResume => File.Exists(MapPath);

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var sb = new StringBuilder();
        foreach (var c in (name ?? "").Trim())
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? '-' : c);
        var s = sb.ToString().Trim('.', ' ', '-');
        if (s.Length == 0) s = "case-" + DateTime.Now.ToString("yyyyMMdd-HHmm");
        return s.Length > 80 ? s[..80] : s;
    }

    public static string SuggestName(string? ticket, string? serial)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(ticket)) parts.Add(ticket.Trim());
        if (!string.IsNullOrWhiteSpace(serial)) parts.Add(serial.Trim());
        parts.Add(DateTime.Now.ToString("yyyyMMdd"));
        return SafeName(string.Join("-", parts));
    }
}
