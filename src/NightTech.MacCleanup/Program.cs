using System.Reflection;
using NightTech.Core;
using NightTech.Core.Cleanup;
using Format = NightTech.Core.Diagnostics.Format;
using NightTech.MacCleanup;

// Night Tech Mac Cleanup: run in Terminal with "sudo" (see README). Menu-driven; every change is listed first and asked about.

var version = Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
var report = new CleanupReport { Tool = "Night Tech Mac Cleanup", ToolVersion = version, Kind = "mac-cleanup", Platform = "macOS", Started = DateTime.Now };
var homes = Machine.Homes();
var locations = Junk.MacLocations(homes);
List<Agent> agents = new();
var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
var portable = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));
var reportsFolder = portable ? Path.Combine(AppContext.BaseDirectory, "Reports")
    : Path.Combine(sudoUser is { Length: > 0 } su ? Path.Combine("/Users", su) : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "Night Tech Cleanup", "Reports");
string? lastReport = null;

void Title(string t) { Console.WriteLine(); Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine(t); Console.WriteLine(new string('-', Math.Min(70, t.Length))); Console.ResetColor(); }
void Good(string t) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine(t); Console.ResetColor(); }
void Bad(string t) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(t); Console.ResetColor(); }
string Ask(string q, string def = "")
{
    Console.Write(q + (def.Length > 0 ? $" [{def}]" : "") + ": ");
    var line = Console.ReadLine();
    if (line is null) { Console.WriteLine(); Environment.Exit(0); } // input ended (Ctrl+D or no keyboard)
    var a = line.Trim();
    return a.Length == 0 ? def : a;
}
bool YesNo(string q, bool def) { var a = Ask(q + (def ? " (Y/n)" : " (y/N)")).ToLowerInvariant(); return a.Length == 0 ? def : a.StartsWith('y'); }
void AddTask(string area, string what, ItemResult r, string detail = "") => report.Tasks.Add(new TaskLine { Area = area, What = what, Result = r, Detail = detail });

try { if (!Console.IsOutputRedirected) Console.Clear(); } catch (IOException) { }
Console.ForegroundColor = ConsoleColor.White;
Console.WriteLine($"Night Tech Mac Cleanup {version}");
Console.ResetColor();
Console.WriteLine($"{Brand.Company} · {Brand.Phone}");
Console.WriteLine($"{Machine.MacOS()} · {Machine.Model()} · {Machine.Chip()}");
if (!Sh.IsRoot)
{
    Bad("\nNot running as administrator. Quit (0) and start it again with:  sudo ./NightTechMacCleanup");
    Bad("Without it, only this account's junk can be cleared and some checks are skipped.");
}
Console.WriteLine("\nTip: give Terminal \"Full Disk Access\" (System Settings > Privacy & Security) so browser caches can be cleared.");
report.Computer = Sh.Text("/usr/sbin/scutil", "--get", "ComputerName") is { Length: > 0 } cn ? cn : Environment.MachineName;
report.Customer = Ask("\nCustomer name");
report.Ticket = Ask("Hub ticket #");
report.Technician = Ask("Technician", sudoUser ?? Environment.UserName);

agents = Agents.Read(homes);

while (true)
{
    Title("What next?");
    Console.WriteLine("  1) Quick cleanup (measure, junk, background agents, checks)");
    Console.WriteLine("  2) Junk: scan, choose, remove");
    Console.WriteLine("  3) Background agents and login items");
    Console.WriteLine("  4) Security checks");
    Console.WriteLine("  5) Repairs: verify disk, clear DNS");
    Console.WriteLine("  6) Finish: measure after, save and open the report");
    Console.WriteLine("  0) Quit");
    var choice = Ask("Choose");
    try
    {
        switch (choice)
        {
            case "1": Quick(); break;
            case "2": JunkMenu(); break;
            case "3": AgentsMenu(); break;
            case "4": SecurityChecks(true); break;
            case "5": RepairsMenu(); break;
            case "6": Finish(); break;
            case "0":
                if (lastReport is null && (report.Tasks.Count + report.Junk.Count) > 0 && !YesNo("The report hasn't been saved. Quit anyway?", false)) break;
                return 0;
        }
    }
    catch (Exception ex) { Bad("That didn't finish: " + ex.Message); }
}

void Measure(bool after)
{
    Console.WriteLine(after ? "Measuring after..." : "Measuring before...");
    var s = Machine.Measure(agents.Count);
    if (after) report.After = s; else report.Before = s;
    Console.WriteLine($"  Free space: {Format.Bytes(s.FreeBytes)} of {Format.Bytes(s.TotalBytes)} ({s.FreePercent:0}%)  ·  Memory in use: {s.MemoryUsedPercent:0}% of {s.RamGb:0} GB  ·  Background items: {s.StartupItemsOn}  ·  Drive: {s.DriveKind}");
}

void Quick()
{
    Title("Quick cleanup");
    Console.WriteLine("Ask the customer to save their work, then QUIT their apps and browsers (Cmd+Q). A Mac lets an open app's files be removed while it's using them, so clean with apps closed.");
    if (!YesNo("Start", true)) return;
    if (report.Before is null) Measure(false);
    Console.WriteLine("Scanning for junk...");
    var picks = Junk.MacCategories.Where(c => c.OnByDefault).ToList();
    foreach (var c in picks) Clean(c);
    Console.WriteLine($"Junk removed so far: {Format.Bytes(report.BytesFreed)}");
    var off = agents.Where(a => a.Advice == StartupAdvice.TurnOff).ToList();
    if (off.Count > 0)
    {
        Title("Suggested background items to turn off");
        foreach (var a in off) Console.WriteLine($"  - {a.Label}  ({a.Scope}){(a.Signer.Length > 0 ? " · " + a.Signer : "")}\n      {a.Why}");
        if (YesNo($"Turn off these {off.Count}? Check the list with the customer first", false)) foreach (var a in off) DisableAgent(a);
    }
    SecurityChecks(checkUpdates: true);
    var dns = Machine.FlushDns();
    AddTask("Repair", "Cleared saved web addresses (DNS)", dns.Ok ? ItemResult.Done : ItemResult.Skipped, dns.Summary);
    Measure(true);
    Good($"\nQuick cleanup done: {Format.Bytes(report.BytesFreed)} of junk removed. Review the Applications folder with the customer (menu 3 shows unwanted apps), then Finish (6).");
    ListFlaggedApps();
}

void Clean(JunkCategory c)
{
    var r = Junk.Clean(c, locations, DateTime.Now, homes);
    var existing = report.Junk.FirstOrDefault(j => j.CategoryId == c.Id);
    if (existing is null) report.Junk.Add(r); else { existing.BytesFreed += r.BytesFreed; existing.FilesRemoved += r.FilesRemoved; existing.FilesSkipped += r.FilesSkipped; }
    Console.WriteLine($"  {c.Name,-34} {Format.Bytes(r.BytesFreed),10} removed{(r.FilesSkipped > 0 ? $"  ({r.FilesSkipped:N0} busy or protected files left)" : "")}");
}

void JunkMenu()
{
    Title("Junk");
    var cats = Junk.MacCategories.ToList();
    for (int i = 0; i < cats.Count; i++)
    {
        var s = Junk.Scan(cats[i], locations, DateTime.Now, homes);
        Console.WriteLine($"  {i + 1}) {cats[i].Name,-34} {Format.Bytes(s.Bytes),10}  {s.Files:N0} files{(cats[i].OnByDefault ? "" : "   (off unless chosen)")}");
        Console.WriteLine($"       {cats[i].Description}{(cats[i].Caution is { } x ? " " + x : "")}");
    }
    var def = string.Join(",", cats.Select((c, i) => (c, i)).Where(t => t.c.OnByDefault).Select(t => t.i + 1));
    var pick = Ask("Numbers to remove, separated by commas (or Enter for the usual ones)", def);
    var chosen = pick.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(p => int.TryParse(p, out var n) ? n - 1 : -1).Where(n => n >= 0 && n < cats.Count).Select(n => cats[n]).Distinct().ToList();
    if (chosen.Any(c => c.Id == "mac-trash") && !YesNo("Empty the Trash? Anything in it is gone for good. The customer agrees", false)) chosen.RemoveAll(c => c.Id == "mac-trash");
    foreach (var c in chosen) Clean(c);
    Good($"Total junk removed: {Format.Bytes(report.BytesFreed)}");
}

void DisableAgent(Agent a)
{
    try
    {
        Agents.Disable(a);
        agents.Remove(a);
        AddTask("Startup", "Turned off " + a.Label, ItemResult.Done, a.Why);
        Good($"  Turned off {a.Label}");
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Bad($"  Couldn't turn off {a.Label}: {e.Message} (run with sudo)"); }
}

void AgentsMenu()
{
    Title("Background agents (start automatically)");
    agents = Agents.Read(homes);
    for (int i = 0; i < agents.Count; i++)
    {
        var a = agents[i];
        var tag = a.Advice switch { StartupAdvice.TurnOff => "TURN OFF", StartupAdvice.Keep => "keep", StartupAdvice.Optional => "ask", _ => "unknown" };
        Console.WriteLine($"  {i + 1,2}) [{tag,-8}] {a.Label}  ({a.Scope})");
        Console.WriteLine($"        {a.Program}{(a.Signer.Length > 0 ? "  · signed: " + a.Signer : "")}");
        Console.WriteLine($"        {a.Why}");
    }
    if (agents.Count == 0) Console.WriteLine("  None found.");
    ListFlaggedApps();
    Console.WriteLine("\nLogin items (apps that open at sign-in) are in System Settings > General > Login Items: review them there with the customer.");
    var pick = Ask("\nNumbers to turn off (commas), 'r' to put back everything turned off before, or Enter to go back");
    if (pick.Equals("r", StringComparison.OrdinalIgnoreCase))
    {
        var n = Agents.RestoreAll();
        report.Tasks.RemoveAll(t => t.Area == "Startup");
        Good($"Put back {n} item(s). Anything that didn't start right away starts at the next sign-in or restart.");
        agents = Agents.Read(homes);
        return;
    }
    foreach (var n in pick.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => int.TryParse(p, out var x) ? x - 1 : -1).Where(x => x >= 0 && x < agents.Count).Distinct().OrderByDescending(x => x).ToList())
        DisableAgent(agents[n]);
}

void ListFlaggedApps()
{
    var apps = new List<string>();
    foreach (var dir in new[] { "/Applications" }.Concat(homes.Select(h => Path.Combine(h, "Applications"))))
    {
        if (!Directory.Exists(dir)) continue;
        foreach (var app in Directory.GetDirectories(dir, "*.app"))
        {
            var name = Path.GetFileNameWithoutExtension(app);
            if (Programs.Flag(name, "") is { } f) apps.Add($"  - {name} [{(f.Level == FlagLevel.Unwanted ? "UNWANTED" : "review")}]: {f.Why}");
        }
    }
    if (apps.Count == 0) return;
    Title("Apps worth reviewing (in Applications)");
    foreach (var a in apps) Console.WriteLine(a);
    Console.WriteLine("Remove unwanted ones with the customer: drag to the Trash, or use the app's own uninstaller if it has one.");
    foreach (var a in apps.Where(a => a.Contains("UNWANTED"))) { var t = a.Trim().TrimStart('-').Trim(); if (!report.Findings.Contains(t)) report.Findings.Add(t); }
}

void SecurityChecks(bool checkUpdates)
{
    Title("Security checks (read only)");
    if (checkUpdates) Console.WriteLine("Checking for updates too (up to a few minutes)...");
    foreach (var c in Machine.Security(checkUpdates))
    {
        var mark = c.Ok switch { true => "OK  ", false => "FIX ", _ => "--  " };
        if (c.Ok == false) Bad($"  {mark}{c.Name}: {c.Detail}"); else Console.WriteLine($"  {mark}{c.Name}: {c.Detail}");
        report.Tasks.RemoveAll(t => t.Area == "Security" && t.What == c.Name);
        AddTask("Security", c.Name, c.Ok switch { true => ItemResult.Done, false => ItemResult.Warning, _ => ItemResult.Skipped }, c.Detail);
    }
    if (Machine.Proxy() is { } p) { Bad("  FIX Proxy: " + p); if (!report.Findings.Contains(p)) report.Findings.Add(p); }
    var profiles = Machine.Profiles();
    if (profiles.Count > 0)
    {
        var t = $"Configuration profiles installed: {string.Join(", ", profiles)}. If the customer's work or school didn't install them, remove them in System Settings > Privacy & Security > Profiles.";
        Bad("  CHECK " + t);
        if (!report.Findings.Contains(t)) report.Findings.Add(t);
    }
    foreach (var home in homes)
    {
        var lib = Path.Combine(home, "Library", "Application Support");
        foreach (var (name, rel) in new[] { ("Chrome", "Google/Chrome"), ("Edge", "Microsoft Edge"), ("Brave", "BraveSoftware/Brave-Browser") })
            foreach (var f in BrowserCheck.Chromium(Path.Combine(lib, rel), $"{name} ({Path.GetFileName(home)}"))
            {
                var line = $"{f.Where}): {f.Text}";
                Console.WriteLine("  " + (f.IsExtension ? "    " : "CHECK ") + line);
                if (!f.IsExtension && !report.Findings.Contains(line)) report.Findings.Add(line);
            }
    }
    Console.WriteLine("  Safari extensions: check Safari > Settings > Extensions with the customer.");
}

void RepairsMenu()
{
    Title("Repairs");
    Console.WriteLine("  1) Verify the startup disk (read only, a few minutes)");
    Console.WriteLine("  2) Clear saved web addresses (DNS)");
    switch (Ask("Choose"))
    {
        case "1":
            Console.WriteLine("Verifying...");
            var v = Machine.VerifyDisk();
            (v.Ok ? (Action<string>)Good : Bad)(v.Summary);
            AddTask("Repair", "Verified the startup disk", v.Ok ? ItemResult.Done : ItemResult.Warning, v.Summary);
            break;
        case "2":
            var d = Machine.FlushDns();
            Console.WriteLine(d.Summary);
            AddTask("Repair", "Cleared saved web addresses (DNS)", d.Ok ? ItemResult.Done : ItemResult.Skipped, d.Summary);
            break;
    }
}

void Finish()
{
    if (report.Before is null) { Bad("There's no 'before' measurement. Run the quick cleanup (1) first, or measure now for a report without a comparison."); if (!YesNo("Measure now", true)) return; Measure(false); }
    Measure(true);
    report.Recommendations = CleanupReport.Recommend(report.After!);
    report.Notes = Ask("Notes for the customer (optional)");
    report.Finished = DateTime.Now;
    var (json, text, html) = report.Save(reportsFolder);
    if (sudoUser is { Length: > 0 }) Sh.Exec("/usr/sbin/chown", new[] { "-R", "-P", sudoUser, reportsFolder }, 20);
    lastReport = html;
    Good($"\nSaved: {html}\nFor the Hub ticket: {Path.GetFileName(json)}");
    Sh.Exec("/usr/bin/open", new[] { html }, 20);
}
