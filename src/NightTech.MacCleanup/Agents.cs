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

/// <summary>
/// Background agents and daemons (launchd), where most Mac adware hides. Turning one off moves its .plist to a
/// "Night Tech disabled" folder and stops it; turning it back on moves it back. Nothing is deleted.
/// </summary>
public static class Agents
{
    public static string DisabledRoot => Path.Combine("/Users/Shared", "Night Tech disabled");

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
        if (!Directory.Exists(dir)) yield break;
        foreach (var plist in Directory.GetFiles(dir, "*.plist"))
        {
            string label = Path.GetFileNameWithoutExtension(plist), program = "";
            var json = Sh.Exec("/usr/bin/plutil", $"-convert json -o - \"{plist}\"", 15);
            if (json.Code == 0)
            {
                try
                {
                    using var d = JsonDocument.Parse(json.Out);
                    var r = d.RootElement;
                    if (r.TryGetProperty("Label", out var l) && l.ValueKind == JsonValueKind.String) label = l.GetString() ?? label;
                    if (r.TryGetProperty("Program", out var p) && p.ValueKind == JsonValueKind.String) program = p.GetString() ?? "";
                    else if (r.TryGetProperty("ProgramArguments", out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0) program = a[0].GetString() ?? "";
                }
                catch (JsonException) { }
            }
            var agent = new Agent { Plist = plist, Label = label, Program = program, Scope = scope, Owner = owner };
            Judge(agent);
            yield return agent;
        }
    }

    private static void Judge(Agent a)
    {
        if (a.Program.Length > 0 && File.Exists(a.Program))
        {
            var cs = Sh.Exec("/usr/bin/codesign", $"-dv --verbose=2 \"{a.Program}\"", 20).Out;
            var auth = cs.Split('\n').FirstOrDefault(l => l.StartsWith("Authority=", StringComparison.Ordinal));
            a.Signer = auth is not null ? auth["Authority=".Length..].Replace("Developer ID Application: ", "") : cs.Contains("not signed") ? "NOT SIGNED" : "";
        }
        var item = new StartupItem { Name = a.Label, Command = a.Program, Publisher = a.Signer };
        Startup.Advise(item);
        a.Advice = item.Advice;
        a.Why = item.Why;

        var p = a.Program;
        bool hiddenOrTemp = p.Contains("/.", StringComparison.Ordinal) || p.StartsWith("/tmp/", StringComparison.Ordinal) || p.StartsWith("/private/tmp/", StringComparison.Ordinal)
                            || p.StartsWith("/private/var/tmp/", StringComparison.Ordinal) || p.StartsWith("/Users/Shared/", StringComparison.Ordinal);
        if (p.Length > 0 && !File.Exists(p))
        { a.Advice = StartupAdvice.TurnOff; a.Why = "Leftover: points to a program that's no longer there"; }
        else if (a.Signer == "NOT SIGNED" && (hiddenOrTemp || p.Contains("/Library/Application Support/", StringComparison.Ordinal)))
        { a.Advice = StartupAdvice.TurnOff; a.Why = "Unsigned program in a hidden or support folder: typical of adware (Adload and similar)"; }
        else if (a.Label.StartsWith("com.google.keystone", StringComparison.Ordinal) || a.Label.StartsWith("com.google.GoogleUpdater", StringComparison.Ordinal))
        { a.Advice = StartupAdvice.Optional; a.Why = "Google's updater for Chrome: keep if they use Chrome"; }
        else if (a.Label.StartsWith("com.microsoft.", StringComparison.Ordinal))
        { a.Advice = StartupAdvice.Keep; a.Why = "Microsoft Office or Teams helper"; }
        else if (a.Label.StartsWith("com.adobe.", StringComparison.Ordinal) && a.Label.Contains("update", StringComparison.OrdinalIgnoreCase))
        { a.Advice = StartupAdvice.TurnOff; a.Why = "Adobe updater: Adobe apps check for updates when opened"; }
    }

    /// <summary>Stops the agent and moves its .plist into the disabled folder (same path inside it, for undo).</summary>
    public static string Disable(Agent a)
    {
        var dest = Path.Combine(DisabledRoot, a.Plist.TrimStart('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        Stop(a);
        if (File.Exists(dest)) File.Delete(dest);
        File.Move(a.Plist, dest);
        return dest;
    }

    /// <summary>Moves every disabled .plist back where it was and starts it again.</summary>
    public static int RestoreAll()
    {
        if (!Directory.Exists(DisabledRoot)) return 0;
        int n = 0;
        foreach (var f in Directory.GetFiles(DisabledRoot, "*.plist", SearchOption.AllDirectories))
        {
            var original = "/" + Path.GetRelativePath(DisabledRoot, f);
            if (File.Exists(original)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            File.Move(f, original);
            n++;
        }
        return n;
    }

    private static void Stop(Agent a)
    {
        if (a.Scope == "system") { Sh.Exec("/bin/launchctl", $"bootout system \"{a.Plist}\"", 20); return; }
        var home = a.Owner.Length > 0 ? a.Owner : null;
        var uid = home is null ? "" : Sh.Text("/usr/bin/stat", $"-f %u \"{home}\"");
        if (uid.Length > 0) Sh.Exec("/bin/launchctl", $"bootout gui/{uid} \"{a.Plist}\"", 20);
        else foreach (var u in Machine.Homes().Select(h => Sh.Text("/usr/bin/stat", $"-f %u \"{h}\"")).Where(u => u.Length > 0))
            Sh.Exec("/bin/launchctl", $"bootout gui/{u} \"{a.Plist}\"", 20);
    }
}
