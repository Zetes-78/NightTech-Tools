namespace NightTech.Core.Cleanup;

public enum StartupSource { UserRun, MachineRun, MachineRun32, UserStartupFolder, CommonStartupFolder }

public enum StartupAdvice { Keep, Optional, TurnOff, Unknown }

public sealed class StartupItem
{
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public StartupSource Source { get; init; }
    public bool Enabled { get; set; }
    public string Publisher { get; set; } = "";
    public StartupAdvice Advice { get; set; }
    public string Why { get; set; } = "";
    public string Exe => Startup.ExecutablePath(Command);
}

/// <summary>
/// Startup programs are turned off and on the same way Task Manager does it: an "approved" flag in the registry
/// (Explorer\StartupApproved). The program's own entry is never deleted, so every change can be undone.
/// </summary>
public static class Startup
{
    public const string UserRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string MachineRun32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>The StartupApproved subkey for a source: Run, Run32 or StartupFolder.</summary>
    public static string ApprovedKey(StartupSource s) => ApprovedBase + "\\" + s switch
    {
        StartupSource.UserRun or StartupSource.MachineRun => "Run",
        StartupSource.MachineRun32 => "Run32",
        _ => "StartupFolder",
    };

    public static bool IsMachineWide(StartupSource s) => s is StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.CommonStartupFolder;

    /// <summary>A StartupApproved value means "turned off" when the low bit of its first byte is set (03 or 07); no value means on.</summary>
    public static bool IsEnabled(byte[]? approved) => approved is not { Length: > 0 } || (approved[0] & 1) == 0;

    /// <summary>The value Task Manager writes to turn an item off: 03 00 00 00, then when it was turned off.</summary>
    public static byte[] DisabledValue(DateTime whenUtc)
    {
        var v = new byte[12];
        v[0] = 3;
        BitConverter.GetBytes(whenUtc.ToFileTimeUtc()).CopyTo(v, 4);
        return v;
    }

    public static byte[] EnabledValue() => new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    /// <summary>The program a startup command runs: handles quotes, arguments and rundll32/cmd wrappers.</summary>
    public static string ExecutablePath(string command)
    {
        var c = (command ?? "").Trim();
        if (c.Length == 0) return "";
        string exe;
        string rest;
        if (c[0] == '"')
        {
            int end = c.IndexOf('"', 1);
            exe = end > 0 ? c[1..end] : c.Trim('"');
            rest = end > 0 ? c[(end + 1)..].Trim() : "";
        }
        else
        {
            // Unquoted paths with spaces: take the shortest prefix ending in .exe/.cmd/.bat/.lnk.
            int idx = -1;
            foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".lnk", ".com", ".dll" })
            {
                int i = c.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                if (i >= 0 && (idx < 0 || i + ext.Length < idx)) idx = i + ext.Length;
            }
            if (idx > 0) { exe = c[..idx]; rest = c[idx..].Trim(); }
            else { int sp = c.IndexOf(' '); exe = sp > 0 ? c[..sp] : c; rest = sp > 0 ? c[(sp + 1)..] : ""; }
        }
        var name = exe.Split('\\', '/')[^1].ToLowerInvariant(); // Windows paths, whatever the platform
        if (name is "rundll32.exe" or "rundll32" && rest.Length > 0)
            return ExecutablePath(rest.Split(',')[0]);
        return Environment.ExpandEnvironmentVariables(exe);
    }

    private static readonly (string Match, StartupAdvice Advice, string Why)[] Rules =
    {
        // Keep: security, drivers and hardware helpers.
        ("SecurityHealth", StartupAdvice.Keep, "Windows Security"),
        ("WindowsDefender", StartupAdvice.Keep, "Windows Security"),
        ("Realtek", StartupAdvice.Keep, "Sound driver"),
        ("RtkAud", StartupAdvice.Keep, "Sound driver"),
        ("Waves", StartupAdvice.Keep, "Sound driver"),
        ("Synaptics", StartupAdvice.Keep, "Touchpad driver"),
        ("ETDCtrl", StartupAdvice.Keep, "Touchpad driver"),
        ("Elan", StartupAdvice.Keep, "Touchpad driver"),
        ("igfx", StartupAdvice.Keep, "Intel graphics"),
        ("Intel", StartupAdvice.Keep, "Intel hardware helper"),
        ("NVIDIA", StartupAdvice.Keep, "Graphics driver"),
        ("AMD", StartupAdvice.Keep, "Graphics driver"),
        ("Radeon", StartupAdvice.Keep, "Graphics driver"),
        ("OneDrive", StartupAdvice.Keep, "Keeps files backed up to OneDrive"),
        ("Bitdefender", StartupAdvice.Keep, "Antivirus"),
        ("Malwarebytes", StartupAdvice.Keep, "Antivirus"),
        ("Norton", StartupAdvice.Keep, "Antivirus"),
        ("McAfee", StartupAdvice.Keep, "Antivirus"),
        ("Avast", StartupAdvice.Keep, "Antivirus"),
        ("AVG", StartupAdvice.Keep, "Antivirus"),
        ("ESET", StartupAdvice.Keep, "Antivirus"),
        ("Kaspersky", StartupAdvice.Keep, "Antivirus"),
        ("Logitech", StartupAdvice.Optional, "Mouse/keyboard software: keep if they use its buttons"),
        ("Dropbox", StartupAdvice.Optional, "Keep if they use Dropbox sync"),
        ("Google Drive", StartupAdvice.Optional, "Keep if they use Google Drive sync"),
        ("GoogleDrive", StartupAdvice.Optional, "Keep if they use Google Drive sync"),
        ("iCloud", StartupAdvice.Optional, "Keep if they sync an iPhone's photos"),
        ("Teams", StartupAdvice.Optional, "Keep if they use Teams for work"),
        ("Zoom", StartupAdvice.TurnOff, "Opens fine when needed"),
        // Turn off: launchers, updaters and helpers that only slow sign-in.
        ("Steam", StartupAdvice.TurnOff, "Game launcher: opens fine when needed"),
        ("EpicGames", StartupAdvice.TurnOff, "Game launcher: opens fine when needed"),
        ("Epic Games", StartupAdvice.TurnOff, "Game launcher: opens fine when needed"),
        ("Discord", StartupAdvice.TurnOff, "Chat app: opens fine when needed"),
        ("Spotify", StartupAdvice.TurnOff, "Music app: opens fine when needed"),
        ("Skype", StartupAdvice.TurnOff, "Opens fine when needed"),
        ("Cortana", StartupAdvice.TurnOff, "Not needed at startup"),
        ("EADM", StartupAdvice.TurnOff, "Game launcher: opens fine when needed"),
        ("Origin", StartupAdvice.TurnOff, "Game launcher: opens fine when needed"),
        ("Battle.net", StartupAdvice.TurnOff, "Game launcher: opens fine when needed"),
        ("Ubisoft", StartupAdvice.TurnOff, "Game launcher: opens fine when needed"),
        ("AdobeAAMUpdater", StartupAdvice.TurnOff, "Updater: Adobe checks for updates when opened"),
        ("Adobe Creative Cloud", StartupAdvice.TurnOff, "Opens fine when needed"),
        ("AdobeGCInvoker", StartupAdvice.TurnOff, "Adobe license checker"),
        ("CCleaner", StartupAdvice.TurnOff, "Cleaner pop-ups"),
        ("Opera", StartupAdvice.TurnOff, "Browser launcher"),
        ("Edge", StartupAdvice.TurnOff, "Pre-loads Edge at sign-in"),
        ("MicrosoftEdgeAutoLaunch", StartupAdvice.TurnOff, "Pre-loads Edge at sign-in"),
        ("GoogleChromeAutoLaunch", StartupAdvice.TurnOff, "Pre-loads Chrome at sign-in"),
        ("iTunesHelper", StartupAdvice.TurnOff, "Only needed while an iPhone is plugged in"),
        ("QuickTime", StartupAdvice.TurnOff, "Not needed"),
        ("Java", StartupAdvice.TurnOff, "Java update checker"),
        ("jusched", StartupAdvice.TurnOff, "Java update checker"),
        ("HP", StartupAdvice.Optional, "HP helper: turn off unless they use it"),
        ("Lenovo", StartupAdvice.Optional, "Lenovo helper: turn off unless they use it"),
        ("Dell", StartupAdvice.Optional, "Dell helper: turn off unless they use it"),
        ("ASUS", StartupAdvice.Optional, "ASUS helper: turn off unless they use it"),
        ("Acer", StartupAdvice.Optional, "Acer helper: turn off unless they use it"),
    };

    /// <summary>Our suggestion for a startup item. Anything flagged as unwanted software is always "turn off".</summary>
    public static void Advise(StartupItem item)
    {
        var text = item.Name + " " + item.Command + " " + item.Publisher;
        if (Programs.Flag(item.Name, item.Publisher) is { Level: FlagLevel.Unwanted } bad)
        {
            item.Advice = StartupAdvice.TurnOff;
            item.Why = "Unwanted software: " + bad.Why;
            return;
        }
        foreach (var (m, a, w) in Rules)
            if (text.Contains(m, StringComparison.OrdinalIgnoreCase)) { item.Advice = a; item.Why = w; return; }
        if (item.Command.Contains("update", StringComparison.OrdinalIgnoreCase) || item.Name.Contains("update", StringComparison.OrdinalIgnoreCase))
        { item.Advice = StartupAdvice.TurnOff; item.Why = "Updater: the program checks for updates when it's opened"; return; }
        item.Advice = StartupAdvice.Unknown;
        item.Why = string.IsNullOrEmpty(item.Publisher) ? "Unknown program with no publisher: look it up before deciding" : "Ask the customer if they use it";
    }
}
