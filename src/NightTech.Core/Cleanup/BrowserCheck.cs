using System.Text.Json;

namespace NightTech.Core.Cleanup;

public sealed record BrowserFinding(string Where, string Text, bool IsExtension);

/// <summary>
/// Signs of a hijacked Chromium browser (Chrome, Edge, Brave) on Windows or a Mac: a search engine that isn't a
/// well-known one, start pages, and the extensions installed. Reads the browser's own settings files; changes nothing.
/// </summary>
public static class BrowserCheck
{
    private static readonly string[] GoodSearch = { "google.", "bing.com", "duckduckgo.com", "yahoo.com", "ecosia.org", "startpage.com", "brave.com" };

    private static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chrome Web Store Payments", "Google Docs Offline", "Microsoft Edge PDF Viewer", "Web Store", "Edge relevant text changes", "Google Hangouts",
    };

    /// <summary>Checks every profile in a browser's "User Data" folder.</summary>
    public static List<BrowserFinding> Chromium(string userDataDir, string label)
    {
        var found = new List<BrowserFinding>();
        if (!Directory.Exists(userDataDir)) return found;
        foreach (var profile in Directory.GetDirectories(userDataDir).Where(d => File.Exists(Path.Combine(d, "Preferences"))))
        {
            var where = $"{label}, {Path.GetFileName(profile)}";
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, "Preferences")));
                var root = doc.RootElement;
                if (root.TryGetProperty("default_search_provider_data", out var dsp) && dsp.TryGetProperty("template_url_data", out var t)
                    && t.TryGetProperty("url", out var url) && url.GetString() is { } u && !Good(u))
                    found.Add(new(where, $"search engine set to {Short(u)}. Usually a hijacker; reset it in the browser's settings.", false));
                if (root.TryGetProperty("session", out var ses) && ses.TryGetProperty("startup_urls", out var urls) && urls.ValueKind == JsonValueKind.Array)
                    foreach (var s in urls.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").Where(s => s.Length > 0 && !Good(s)))
                        found.Add(new(where, $"opens {Short(s)} at start. Confirm the customer chose this.", false));
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
            foreach (var name in Extensions(profile))
                found.Add(new(where, $"extension \"{name}\". Confirm the customer uses it; remove unknown ones in the browser.", true));
        }
        return found;
    }

    private static bool Good(string url) => GoodSearch.Any(g => url.Contains(g, StringComparison.OrdinalIgnoreCase));
    private static string Short(string s) => s.Length <= 80 ? s : s[..80] + "...";

    /// <summary>Extension names in a profile (built-in ones skipped). Localized names are looked up in the English messages.</summary>
    public static IEnumerable<string> Extensions(string profile)
    {
        var dir = Path.Combine(profile, "Extensions");
        if (!Directory.Exists(dir)) yield break;
        foreach (var ext in Directory.GetDirectories(dir))
        {
            var version = Directory.GetDirectories(ext).OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault();
            if (version is null) continue;
            string? name = null;
            try
            {
                using var m = JsonDocument.Parse(File.ReadAllText(Path.Combine(version, "manifest.json")));
                name = m.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is not null && name.StartsWith("__MSG_", StringComparison.Ordinal))
                {
                    var key = name[6..].TrimEnd('_');
                    var loc = new[] { "en", "en_US", "en_GB" }.Select(l => Path.Combine(version, "_locales", l, "messages.json")).FirstOrDefault(File.Exists);
                    if (loc is not null)
                    {
                        using var msgs = JsonDocument.Parse(File.ReadAllText(loc));
                        foreach (var p in msgs.RootElement.EnumerateObject())
                            if (p.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetProperty("message", out var mv)) { name = mv.GetString(); break; }
                    }
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { continue; }
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith("__MSG_", StringComparison.Ordinal)) name = Path.GetFileName(ext);
            if (BuiltIn.Contains(name)) continue;
            yield return name;
        }
    }
}
