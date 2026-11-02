using System.Security.Cryptography;
using System.Text.Json;
using NightTech.Core.Cleanup;

namespace NightTech.MacCleanup;

public sealed class Agent
{
    public string Plist { get; init; } = "";
    public string Label { get; init; } = "";
    public string Program { get; init; } = "";
    public string Scope { get; init; } = "";      // "user (ann)", "all users", "system"
    public string Owner { get; init; } = "";      // home folder for user agents
    public string Signer { get; set; } = "";
    public StartupAdvice Advice { get; set; }
    public string Why { get; set; } = "";
}

/// <summary>One turned-off agent: where it came from, where we keep it, and its fingerprint.</summary>
public sealed record DisabledEntry(string Original, string Stored, string Sha256, string Scope, string Owner, DateTime When);

/// <summary>
/// Background agents and daemons (launchd), where most Mac adware hides. Turning one off stops it and moves its .plist
/// into a folder only root can open; turning it back on puts the same file back. Nothing is deleted.
/// </summary>
public static class Agents
{
    /// <summary>Root-owned, closed to everyone else, so nobody can plant or swap files that get restored as root.</summary>
    public const string DisabledRoot = "/Library/Application Support/Night Tech/Disabled agents";
    private static string Manifest => Path.Combine(DisabledRoot, "manifest.json");

    /// <summary>Folders an agent may be restored into.</summary>
    private static bool IsAgentFolder(string dir) =>
        dir is "/Library/LaunchAgents" or "/Library/LaunchDaemons"
        || (dir.StartsWith("/Users/", StringComparison.Ordinal) && dir.EndsWith("/Library/LaunchAgents", StringComparison.Ordinal)
            && dir.Count(c => c == '/') == 4);

    public static List<Agent> Read(IEnumerable<string> homes)
    {
        var list = new List<Agent>();
        foreach (var home in homes)
            list.AddRange(From(Path.Combine(home, "Library", "LaunchAgents"), $"user ({Path.GetFileName(home)})", home));
        list.AddRange(From("/Library/LaunchAgents", "all users", ""));
        list.AddRange(From("/Library/LaunchDaemons", "system", ""));
        return list.OrderBy(a => a.Advice).ThenBy(a => a.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<Agent> From(string dir, string scope, string owner)
    {
        if (!Directory.Exists(dir) || Junk.HasLinkOnPath(dir)) yield break;
        foreach (var plist in Directory.GetFiles(dir, "*.plist"))
        {
            if (Junk.IsLink(plist)) continue;
            string label = Path.GetFileNameWithoutExtension(plist), program = "";
            var json = Sh.Exec("/usr/bin/plutil", new[] { "-convert", "json", "-o", "-", plist }, 15);
            if (json.Code == 0)
            {
                try
                {
                    using var d = JsonDocument.Parse(json.Out);
                    var r = d.RootElement;
                    if (r.TryGetProperty("Label", out var l) && l.ValueKind == JsonValueKind.String) label = l.GetString() ?? label;
                    if (r.TryGetProperty("Program", out var p) && p.ValueKind == JsonValueKind.String) program = p.GetString() ?? "";
                    else if (r.TryGetProperty("ProgramArguments", out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0
                             && a[0].ValueKind == JsonValueKind.String) program = a[0].GetString() ?? "";
                }
                catch (JsonException) { }
            }
            var agent = new Agent { Plist = plist, Label = label, Program = program, Scope = scope, Owner = owner };
            Judge(agent);
            yield return agent;
        }
    }

    /// <summary>Security, device-management and printer software that must not be switched off by a cleanup.</summary>
    private static readonly (string Prefix, StartupAdvice Advice, string Why)[] KnownVendors =
    {
        ("com.apple.", StartupAdvice.Keep, "Part of macOS"),
        ("com.jamf", StartupAdvice.Keep, "Company device management (Jamf): don't turn off"),
        ("com.jamfsoftware", StartupAdvice.Keep, "Company device management (Jamf): don't turn off"),
        ("io.kandji", StartupAdvice.Keep, "Company device management (Kandji): don't turn off"),
        ("com.kandji", StartupAdvice.Keep, "Company device management (Kandji): don't turn off"),
        ("com.mosyle", StartupAdvice.Keep, "Company device management (Mosyle): don't turn off"),
        ("com.addigy", StartupAdvice.Keep, "Company device management (Addigy): don't turn off"),
        ("com.crowdstrike", StartupAdvice.Keep, "Security software (CrowdStrike)"),
        ("com.sentinelone", StartupAdvice.Keep, "Security software (SentinelOne)"),
        ("com.sophos", StartupAdvice.Keep, "Security software (Sophos)"),
        ("com.trendmicro", StartupAdvice.Keep, "Security software (Trend Micro)"),
        ("com.malwarebytes", StartupAdvice.Keep, "Security software (Malwarebytes)"),
        ("com.bitdefender", StartupAdvice.Keep, "Security software (Bitdefender)"),
        ("com.eset", StartupAdvice.Keep, "Security software (ESET)"),
        ("com.avast", StartupAdvice.Keep, "Security software (Avast)"),
        ("com.avg", StartupAdvice.Keep, "Security software (AVG)"),
        ("com.norton", StartupAdvice.Keep, "Security software (Norton)"),
        ("com.symantec", StartupAdvice.Keep, "Security software (Norton/Symantec)"),
        ("com.mcafee", StartupAdvice.Keep, "Security software (McAfee)"),
        ("com.webroot", StartupAdvice.Keep, "Security software (Webroot)"),
        ("com.kaspersky", StartupAdvice.Keep, "Security software (Kaspersky)"),
        ("com.paloaltonetworks", StartupAdvice.Keep, "Company security/VPN (Palo Alto)"),
        ("com.cisco", StartupAdvice.Keep, "Company VPN or security (Cisco)"),
        ("com.huntress", StartupAdvice.Keep, "Security software (Huntress)"),
        ("com.objective-see", StartupAdvice.Keep, "Security tool (Objective-See)"),
        ("com.microsoft.wdav", StartupAdvice.Keep, "Microsoft Defender"),
        ("com.microsoft.intune", StartupAdvice.Keep, "Company device management (Intune): don't turn off"),
        ("com.hp.", StartupAdvice.Optional, "HP printer helper: keep if they print to an HP"),
        ("com.canon.", StartupAdvice.Optional, "Canon printer/scanner helper: keep if they use it"),
        ("jp.co.canon.", StartupAdvice.Optional, "Canon printer/scanner helper: keep if they use it"),
        ("com.epson.", StartupAdvice.Optional, "Epson printer/scanner helper: keep if they use it"),
        ("com.brother.", StartupAdvice.Optional, "Brother printer/scanner helper: keep if they use it"),
        ("com.logi.", StartupAdvice.Optional, "Logitech mouse/keyboard software: keep if they use its buttons"),
        ("com.logitech.", StartupAdvice.Optional, "Logitech mouse/keyboard software: keep if they use its buttons"),
    };

    private static bool IsScript(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            return f.ReadByte() == '#' && f.ReadByte() == '!';
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>A program path we can actually check: absolute, and not on a disk that isn't plugged in.</summary>
    private static bool Checkable(string p)
    {
        if (!p.StartsWith('/')) return false; // "sh", "bash": launchd finds these itself
        if (p.StartsWith("/Volumes/", StringComparison.Ordinal))
        {
            var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 && Directory.Exists("/Volumes/" + parts[1]);
        }
        return true;
    }

    private static void Judge(Agent a)
    {
        var p = a.Program;
        bool exists = p.Length > 0 && File.Exists(p);
        bool script = exists && IsScript(p);
        if (exists && !script)
        {
            var cs = Sh.Exec("/usr/bin/codesign", new[] { "-dv", "--verbose=2", "--", p }, 20).Out;
            var auth = cs.Split('\n').FirstOrDefault(l => l.StartsWith("Authority=", StringComparison.Ordinal));
            a.Signer = auth is not null ? auth["Authority=".Length..].Replace("Developer ID Application: ", "") : cs.Contains("not signed") ? "NOT SIGNED" : "";
        }
        else if (script) a.Signer = "script";

        var vendor = KnownVendors.FirstOrDefault(v => a.Label.StartsWith(v.Prefix, StringComparison.OrdinalIgnoreCase));
        if (vendor.Prefix is not null) { a.Advice = vendor.Advice; a.Why = vendor.Why; return; }

        var item = new StartupItem { Name = a.Label, Command = p, Publisher = a.Signer is "NOT SIGNED" or "script" ? "" : a.Signer };
        Startup.Advise(item);
        a.Advice = item.Advice;
        a.Why = item.Why;
        if (a.Advice == StartupAdvice.TurnOff) return; // known unwanted software

        bool hiddenOrTemp = p.Contains("/.", StringComparison.Ordinal) || p.StartsWith("/tmp/", StringComparison.Ordinal) || p.StartsWith("/private/tmp/", StringComparison.Ordinal)
                            || p.StartsWith("/private/var/tmp/", StringComparison.Ordinal) || p.StartsWith("/Users/Shared/", StringComparison.Ordinal);
        if (p.Length > 0 && !exists && Checkable(p))
        { a.Advice = StartupAdvice.TurnOff; a.Why = "Leftover: points to a program that's no longer there"; }
        else if (a.Signer == "NOT SIGNED" && hiddenOrTemp)
        { a.Advice = StartupAdvice.TurnOff; a.Why = "Unsigned program in a hidden or temporary folder: typical of adware (Adload and similar)"; }
        else if (a.Signer == "NOT SIGNED" && p.Contains("/Library/Application Support/", StringComparison.Ordinal))
        { a.Advice = StartupAdvice.Unknown; a.Why = "Unsigned program in a support folder: adware often hides here, but so do some drivers. Look it up first"; }
        else if (a.Label.StartsWith("com.google.keystone", StringComparison.Ordinal) || a.Label.StartsWith("com.google.GoogleUpdater", StringComparison.Ordinal))
        { a.Advice = StartupAdvice.Optional; a.Why = "Google's updater for Chrome: keep if they use Chrome"; }
        else if (a.Label.StartsWith("com.microsoft.", StringComparison.Ordinal))
        { a.Advice = StartupAdvice.Keep; a.Why = "Microsoft Office or Teams helper"; }
        else if (a.Label.StartsWith("com.adobe.", StringComparison.Ordinal) && a.Label.Contains("update", StringComparison.OrdinalIgnoreCase))
        { a.Advice = StartupAdvice.Optional; a.Why = "Adobe updater: Adobe apps check for updates when opened"; }
    }

    // ---- The disabled folder ----

    /// <summary>Creates (or checks) the disabled folder: real folders all the way down, owned by root, closed to others.</summary>
    private static void EnsureStore()
    {
        foreach (var dir in new[] { "/Library/Application Support/Night Tech", DisabledRoot })
        {
            if (Directory.Exists(dir) && Junk.IsLink(dir)) throw new IOException($"{dir} is a link: refusing to use it.");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        }
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(DisabledRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (Sh.IsRoot && Sh.Text("/usr/bin/stat", "-f", "%u", DisabledRoot) is { Length: > 0 } uid && uid != "0")
                throw new IOException($"{DisabledRoot} isn't owned by root: refusing to use it.");
        }
    }

    public static List<DisabledEntry> Disabled()
    {
        try
        {
            if (!File.Exists(Manifest) || Junk.IsLink(Manifest)) return new();
            return JsonSerializer.Deserialize<List<DisabledEntry>>(File.ReadAllText(Manifest)) ?? new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    private static void Save(List<DisabledEntry> list)
    {
        var tmp = Manifest + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, Manifest, true);
    }

    private static string Hash(string file)
    {
        using var f = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(f));
    }

    /// <summary>Stops the agent and moves its .plist into the disabled folder. Returns where it's kept.</summary>
    public static string Disable(Agent a)
    {
        if (Junk.IsLink(a.Plist) || !IsAgentFolder(Path.GetDirectoryName(a.Plist) ?? "")) throw new IOException("Not a normal startup file: left alone.");
        EnsureStore();
        var stored = Path.Combine(DisabledRoot, Guid.NewGuid().ToString("N") + ".plist");
        var hash = Hash(a.Plist);
        Stop(a);
        File.Move(a.Plist, stored);
        var list = Disabled();
        list.Add(new DisabledEntry(a.Plist, stored, hash, a.Scope, a.Owner, DateTime.Now));
        Save(list);
        return stored;
    }

    /// <summary>Puts every turned-off agent back where it was (unchanged files only) and starts it again. Returns how many.</summary>
    public static int RestoreAll()
    {
        if (!Directory.Exists(DisabledRoot) || Junk.IsLink(DisabledRoot)) return 0;
        var list = Disabled();
        var left = new List<DisabledEntry>();
        int n = 0;
        foreach (var e in list)
        {
            try
            {
                var dir = Path.GetDirectoryName(e.Original) ?? "";
                bool ok = Path.GetDirectoryName(e.Stored) == DisabledRoot && File.Exists(e.Stored) && !Junk.IsLink(e.Stored)
                          && IsAgentFolder(dir) && Directory.Exists(dir) && !Junk.HasLinkOnPath(dir) && !File.Exists(e.Original)
                          && Hash(e.Stored) == e.Sha256;
                if (!ok) { left.Add(e); continue; }
                File.Move(e.Stored, e.Original);
                Start(e);
                n++;
            }
            catch (Exception x) when (x is IOException or UnauthorizedAccessException) { left.Add(e); }
        }
        Save(left);
        return n;
    }

    private static string Uid(string home) => home.Length > 0 ? Sh.Text("/usr/bin/stat", "-f", "%u", home) : "";

    private static IEnumerable<string> Domains(string scope, string owner)
    {
        if (scope == "system") return new[] { "system" };
        var uid = Uid(owner);
        if (uid.Length > 0) return new[] { "gui/" + uid };
        return Machine.Homes().Select(Uid).Where(u => u.Length > 0).Select(u => "gui/" + u);
    }

    private static void Stop(Agent a)
    {
        foreach (var d in Domains(a.Scope, a.Owner)) Sh.Exec("/bin/launchctl", new[] { "bootout", d, a.Plist }, 20);
    }

    private static void Start(DisabledEntry e)
    {
        // Fails harmlessly when that person isn't logged in; it then starts at their next login.
        foreach (var d in Domains(e.Scope, e.Owner)) Sh.Exec("/bin/launchctl", new[] { "bootstrap", d, e.Original }, 20);
    }
}
