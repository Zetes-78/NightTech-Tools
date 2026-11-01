using System.Globalization;
using System.Text;

namespace NightTech.Core.Diagnostics;

public static class Format
{
    private static readonly string[] Units = { "bytes", "KB", "MB", "GB", "TB", "PB" };

    /// <summary>Size the way Windows shows it (1 GB = 1024 MB).</summary>
    public static string Bytes(long? bytes)
    {
        if (bytes is not { } b) return "unknown";
        if (b < 1024) return b.ToString(CultureInfo.InvariantCulture) + " bytes";
        double v = b;
        var i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return v.ToString(v >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + Units[i];
    }

    public static string Date(DateTime? d) =>
        d is { } x ? x.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) : "unknown";

    public static string DateAndTime(DateTime? d) =>
        d is { } x ? x.ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture) : "unknown";

    public static string YesNo(bool? b, string yes = "Yes", string no = "No") => b switch
    {
        true => yes,
        false => no,
        null => "not checked",
    };

    /// <summary>A file name made only of safe characters, e.g. "NT-0012-DESKTOP-ABC-20261101-1830".</summary>
    public static string SafeFileName(string text, int max = 80)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_') sb.Append(ch);
            else if (ch is ' ' or '.' && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        if (s.Length > max) s = s[..max].TrimEnd('-');
        return s.Length == 0 ? "computer" : s;
    }
}
