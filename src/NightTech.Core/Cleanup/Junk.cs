namespace NightTech.Core.Cleanup;

/// <summary>A kind of junk the cleanup can remove, shown as one checkbox.</summary>
public sealed record JunkCategory(string Id, string Name, string Description, bool OnByDefault, string? Caution = null);

/// <summary>
/// A folder whose contents may be removed. <see cref="Path"/> may contain "*" segments (for example one per browser
/// profile). Only files older than <see cref="MinAgeHours"/> and matching <see cref="Pattern"/> are removed.
/// </summary>
public sealed record JunkLocation(string CategoryId, string Path, string Pattern = "*", double MinAgeHours = 0, bool Recursive = true, string[]? Exclude = null);

public sealed class JunkScanResult
{
    public required JunkCategory Category { get; init; }
    public long Bytes { get; set; }
    public int Files { get; set; }
    public List<string> Folders { get; } = new();
}

public sealed class JunkCleanResult
{
    public required string CategoryId { get; init; }
    public string Name { get; init; } = "";
    public long BytesFreed { get; set; }
    public int FilesRemoved { get; set; }
    /// <summary>Files Windows or a program was still using (normal; they're cleared at the next restart or later).</summary>
    public int FilesSkipped { get; set; }
}

/// <summary>
/// Junk categories, the folders that hold them, and the rules that keep cleanup safe: it only ever works inside the
/// listed folders, never follows links or junctions out of them, skips files in use and files too new to be junk,
/// and never touches a person's own folders (Documents, Desktop, Downloads, Pictures and so on).
/// </summary>
public static class Junk
{
    public static readonly IReadOnlyList<JunkCategory> Categories = new[]
    {
        new JunkCategory("user-temp", "Temporary files (each person)", "Leftovers programs put in each person's Temp folder. Only files older than a day.", true),
        new JunkCategory("windows-temp", "Windows temporary files", "Windows' own Temp folder. Only files older than a day.", true),
        new JunkCategory("update-cache", "Windows Update downloads", "Update files that are already installed. Windows downloads again anything it still needs.", true),
        new JunkCategory("delivery-opt", "Delivery Optimization cache", "Update pieces Windows keeps to share with other PCs.", true),
        new JunkCategory("error-reports", "Error reports", "Copies of crash reports already sent (or not needed).", true),
        new JunkCategory("crash-dumps", "Crash dumps", "Memory snapshots saved after crashes. Large, and only useful for diagnosing a specific crash.", true,
            "Leave unchecked if you're still diagnosing blue screens."),
        new JunkCategory("thumbnails", "Thumbnail cache", "Picture previews File Explorer rebuilds by itself.", true),
        new JunkCategory("browser-cache", "Browser caches", "Saved copies of web pages in Chrome, Edge, Brave and Firefox. Passwords, history, bookmarks and sign-ins are NOT touched.", true,
            "Close the browsers first, or their busy files are skipped."),
        new JunkCategory("recycle-bin", "Recycle Bin", "Empties every drive's Recycle Bin.", false,
            "Ask the customer first: anything in the Recycle Bin is gone for good."),
    };

    /// <summary>The same idea on a Mac.</summary>
    public static readonly IReadOnlyList<JunkCategory> MacCategories = new[]
    {
        new JunkCategory("mac-user-cache", "App caches (each person)", "Files apps keep to load faster; they rebuild them as needed. Only files older than a day.", true),
        new JunkCategory("mac-user-logs", "App logs (each person)", "Old log files apps have written.", true),
        new JunkCategory("mac-system-logs", "System logs and crash reports", "Old diagnostic logs and crash reports.", true),
        new JunkCategory("mac-browser-cache", "Browser caches", "Saved copies of web pages in Safari, Chrome, Edge, Brave and Firefox. Passwords, history, bookmarks and sign-ins are NOT touched.", true,
            "Close the browsers first, or their busy files are skipped."),
        new JunkCategory("mac-xcode", "Xcode build leftovers", "Developer build files (DerivedData). Only on Macs used for app development.", true),
        new JunkCategory("mac-trash", "Trash", "Empties each person's Trash.", false,
            "Ask the customer first: anything in the Trash is gone for good."),
    };

    /// <summary>Where Mac junk lives, for the given home folders.</summary>
    public static List<JunkLocation> MacLocations(IEnumerable<string> homes)
    {
        var L = new List<JunkLocation>
        {
            new("mac-system-logs", "/Library/Logs/DiagnosticReports", MinAgeHours: 24 * 7),
            new("mac-system-logs", "/private/var/log", "*.gz"),
            new("mac-system-logs", "/private/var/log", "*.bz2"),
        };
        foreach (var home in homes)
        {
            var lib = P(home, "Library");
            // Browser caches are their own category, so they aren't counted twice.
            L.Add(new("mac-user-cache", P(lib, "Caches"), MinAgeHours: 24, Exclude: new[] { "com.apple.Safari", "Google", "Microsoft Edge", "BraveSoftware", "Firefox" }));
            L.Add(new("mac-user-logs", P(lib, "Logs"), MinAgeHours: 24 * 3));
            L.Add(new("mac-browser-cache", P(lib, "Caches", "com.apple.Safari")));
            L.Add(new("mac-browser-cache", P(lib, "Caches", "Google", "Chrome")));
            L.Add(new("mac-browser-cache", P(lib, "Caches", "Microsoft Edge")));
            L.Add(new("mac-browser-cache", P(lib, "Caches", "BraveSoftware")));
            L.Add(new("mac-browser-cache", P(lib, "Caches", "Firefox", "Profiles", "*", "cache2")));
            L.Add(new("mac-xcode", P(lib, "Developer", "Xcode", "DerivedData")));
            L.Add(new("mac-trash", P(home, ".Trash")));
        }
        return L;
    }

    public static JunkCategory Category(string id) => Categories.Concat(MacCategories).First(c => c.Id == id);

    /// <summary>Where each category lives, for one computer's folders and user profiles.</summary>
    public static List<JunkLocation> Locations(string windowsDir, string programData, IEnumerable<string> userProfiles)
    {
        var L = new List<JunkLocation>
        {
            new("windows-temp", P(windowsDir, "Temp"), MinAgeHours: 24),
            new("update-cache", P(windowsDir, "SoftwareDistribution", "Download")),
            new("delivery-opt", P(windowsDir, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache")),
            new("error-reports", P(programData, "Microsoft", "Windows", "WER", "ReportArchive")),
            new("error-reports", P(programData, "Microsoft", "Windows", "WER", "ReportQueue")),
            new("crash-dumps", P(windowsDir, "Minidump"), "*.dmp"),
            new("crash-dumps", windowsDir, "MEMORY.DMP", Recursive: false),
            new("crash-dumps", P(windowsDir, "LiveKernelReports"), "*.dmp"),
        };
        foreach (var home in userProfiles)
        {
            var local = P(home, "AppData", "Local");
            L.Add(new("user-temp", P(local, "Temp"), MinAgeHours: 24));
            L.Add(new("error-reports", P(local, "Microsoft", "Windows", "WER")));
            L.Add(new("crash-dumps", P(local, "CrashDumps"), "*.dmp"));
            L.Add(new("thumbnails", P(local, "Microsoft", "Windows", "Explorer"), "thumbcache_*.db", Recursive: false));
            L.Add(new("thumbnails", P(local, "Microsoft", "Windows", "Explorer"), "iconcache_*.db", Recursive: false));
            foreach (var browser in new[] { P(local, "Google", "Chrome", "User Data"), P(local, "Microsoft", "Edge", "User Data"), P(local, "BraveSoftware", "Brave-Browser", "User Data") })
                foreach (var cache in new[] { "Cache", "Code Cache", "GPUCache", P("Service Worker", "CacheStorage") })
                    L.Add(new("browser-cache", P(browser, "*", cache)));
            L.Add(new("browser-cache", P(local, "Mozilla", "Firefox", "Profiles", "*", "cache2")));
        }
        return L;
    }

    private static string P(params string[] parts) => System.IO.Path.Combine(parts);

    /// <summary>The user's own folders: cleanup never works inside these, whatever a location says.</summary>
    public static readonly string[] ProtectedNames = { "Documents", "Desktop", "Downloads", "Pictures", "Music", "Videos", "Movies", "OneDrive", "Favorites", "Contacts", "Saved Games", "Links", "Searches", "iCloud Drive", "Public" };

    /// <summary>True when a folder sits inside a person's own files (for example C:\Users\Ann\Documents) and so is never cleaned.</summary>
    public static bool IsProtected(string path, IEnumerable<string> userProfiles)
    {
        var full = Normalize(path);
        foreach (var home in userProfiles)
        {
            var h = Normalize(home);
            if (!full.StartsWith(h + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && !full.Equals(h, StringComparison.OrdinalIgnoreCase)) continue;
            if (full.Equals(h, StringComparison.OrdinalIgnoreCase)) return true;
            var first = full[(h.Length + 1)..].Split(System.IO.Path.DirectorySeparatorChar)[0];
            if (ProtectedNames.Contains(first, StringComparer.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string Normalize(string p) => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(p));

    /// <summary>Real folders a location names: "*" segments expand to each subfolder that isn't a link or junction.</summary>
    public static IEnumerable<string> Expand(string path)
    {
        var parts = path.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        int star = Array.IndexOf(parts, "*");
        if (star < 0)
        {
            if (Directory.Exists(path) && !IsLink(path)) yield return path;
            yield break;
        }
        var head = string.Join(System.IO.Path.DirectorySeparatorChar, parts[..star]);
        if (head.Length == 0) head = System.IO.Path.DirectorySeparatorChar.ToString();
        if (!Directory.Exists(head) || IsLink(head)) yield break;
        string[] subs;
        try { subs = Directory.GetDirectories(head); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        var tail = parts[(star + 1)..];
        foreach (var sub in subs)
        {
            if (IsLink(sub)) continue;
            foreach (var x in Expand(tail.Length == 0 ? sub : System.IO.Path.Combine(new[] { sub }.Concat(tail).ToArray()))) yield return x;
        }
    }

    public static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }

    /// <summary>Files a location would remove. Never steps into links or junctions.</summary>
    public static IEnumerable<FileInfo> Files(JunkLocation loc, DateTime now, IEnumerable<string> userProfiles)
    {
        var profiles = userProfiles.ToList();
        foreach (var root in Expand(loc.Path))
        {
            if (IsProtected(root, profiles)) continue;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                IEnumerable<string> files = Array.Empty<string>(), dirs = Array.Empty<string>();
                try
                {
                    files = Directory.EnumerateFiles(dir, loc.Pattern).ToList();
                    if (loc.Recursive) dirs = Directory.EnumerateDirectories(dir).ToList();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var f in files)
                {
                    FileInfo fi;
                    try
                    {
                        fi = new FileInfo(f);
                        if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (loc.MinAgeHours > 0 && (now - Newest(fi)).TotalHours < loc.MinAgeHours) continue;
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                    yield return fi;
                }
                foreach (var d in dirs)
                {
                    if (IsLink(d)) continue;
                    if (dir == root && loc.Exclude is { } ex && ex.Contains(System.IO.Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)) continue;
                    stack.Push(d);
                }
            }
        }
    }

    private static DateTime Newest(FileInfo f) => new[] { f.LastWriteTime, f.CreationTime }.Max();

    public static JunkScanResult Scan(JunkCategory cat, IEnumerable<JunkLocation> locations, DateTime now, IReadOnlyList<string> profiles, CancellationToken ct = default)
    {
        var r = new JunkScanResult { Category = cat };
        foreach (var loc in locations.Where(l => l.CategoryId == cat.Id))
        {
            foreach (var f in Files(loc, now, profiles))
            {
                ct.ThrowIfCancellationRequested();
                try { r.Bytes += f.Length; r.Files++; } catch (IOException) { }
            }
            r.Folders.AddRange(Expand(loc.Path));
        }
        return r;
    }

    /// <summary>Removes the files, then any folders left empty inside the location (never the location itself).</summary>
    public static JunkCleanResult Clean(JunkCategory cat, IEnumerable<JunkLocation> locations, DateTime now, IReadOnlyList<string> profiles, CancellationToken ct = default)
    {
        var r = new JunkCleanResult { CategoryId = cat.Id, Name = cat.Name };
        foreach (var loc in locations.Where(l => l.CategoryId == cat.Id))
        {
            foreach (var f in Files(loc, now, profiles).ToList())
            {
                ct.ThrowIfCancellationRequested();
                long size = 0;
                try
                {
                    size = f.Length;
                    if (f.IsReadOnly) f.IsReadOnly = false;
                    f.Delete();
                    r.BytesFreed += size;
                    r.FilesRemoved++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { r.FilesSkipped++; }
            }
            if (loc.Recursive)
                foreach (var root in Expand(loc.Path))
                    RemoveEmpty(root, isRoot: true, loc.Exclude);
        }
        return r;
    }

    private static void RemoveEmpty(string dir, bool isRoot, string[]? exclude = null)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(dir))
            {
                if (IsLink(d)) continue;
                if (isRoot && exclude is not null && exclude.Contains(System.IO.Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)) continue;
                RemoveEmpty(d, false);
            }
            if (!isRoot && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
