namespace NightTech.Core.Cleanup;

public enum FlagLevel { None, Review, Unwanted }

public sealed record ProgramFlag(FlagLevel Level, string Why);

public sealed class InstalledProgram
{
    public string Name { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime? Installed { get; init; }
    public long SizeBytes { get; init; }
    public string UninstallCommand { get; init; } = "";
    public bool IsStoreApp { get; init; }
    /// <summary>Store apps: the package family to remove (for example king.com.CandyCrushSaga_kgqvnymyfvs32).</summary>
    public string PackageName { get; init; } = "";
    public FlagLevel Level { get; set; }
    public string Why { get; set; } = "";
}

/// <summary>
/// Recognizes software that's commonly unwanted on customers' PCs: adware, fake "optimizers" and driver updaters,
/// search hijackers, and preinstalled trials and games. "Unwanted" is safe to recommend removing; "Review" means ask
/// the customer. Nothing here removes anything: the technician runs each program's own uninstaller.
/// </summary>
public static class Programs
{
    private static readonly (string Match, FlagLevel Level, string Why)[] Rules =
    {
        // Adware, hijackers and scareware
        ("Wave Browser", FlagLevel.Unwanted, "browser that's bundled with other downloads and changes search"),
        ("WaveBrowser", FlagLevel.Unwanted, "browser that's bundled with other downloads and changes search"),
        ("OneLaunch", FlagLevel.Unwanted, "adware search bar"),
        ("Shift Browser", FlagLevel.Unwanted, "bundled browser"),
        ("WebDiscover", FlagLevel.Unwanted, "adware browser"),
        ("Web Companion", FlagLevel.Unwanted, "adware that changes browser settings"),
        ("Segurazo", FlagLevel.Unwanted, "fake antivirus that's hard to remove"),
        ("SAntivirus", FlagLevel.Unwanted, "fake antivirus that's hard to remove"),
        ("ByteFence", FlagLevel.Unwanted, "bundled 'protection' that changes search"),
        ("Reimage", FlagLevel.Unwanted, "scareware 'repair' tool"),
        ("Restoro", FlagLevel.Unwanted, "scareware 'repair' tool"),
        ("Fortect", FlagLevel.Unwanted, "scareware 'repair' tool"),
        ("PC Accelerate", FlagLevel.Unwanted, "fake optimizer"),
        ("PC Cleaner", FlagLevel.Unwanted, "fake optimizer"),
        ("PCKeeper", FlagLevel.Unwanted, "scareware optimizer"),
        ("MyCleanPC", FlagLevel.Unwanted, "scareware optimizer"),
        ("Advanced System Optimizer", FlagLevel.Unwanted, "fake optimizer"),
        ("Advanced SystemCare", FlagLevel.Unwanted, "optimizer that nags and slows the PC"),
        ("Driver Booster", FlagLevel.Unwanted, "driver updater: can install wrong drivers"),
        ("Driver Support", FlagLevel.Unwanted, "paid driver updater"),
        ("DriverFix", FlagLevel.Unwanted, "paid driver updater"),
        ("Driver Updater", FlagLevel.Unwanted, "paid driver updater"),
        ("Driver Reviver", FlagLevel.Unwanted, "paid driver updater"),
        ("Smart Driver", FlagLevel.Unwanted, "paid driver updater"),
        ("Outbyte", FlagLevel.Unwanted, "paid 'PC repair'/driver tool"),
        ("Auslogics", FlagLevel.Review, "optimizer: usually not needed"),
        ("Slimware", FlagLevel.Unwanted, "paid optimizer"),
        ("SlimCleaner", FlagLevel.Unwanted, "paid optimizer"),
        ("WinZip Driver", FlagLevel.Unwanted, "paid driver updater"),
        ("WinZip System Utilities", FlagLevel.Unwanted, "paid optimizer"),
        ("Ask Toolbar", FlagLevel.Unwanted, "search hijacker toolbar"),
        ("MyWebSearch", FlagLevel.Unwanted, "search hijacker"),
        ("Conduit", FlagLevel.Unwanted, "search hijacker"),
        ("Search Protect", FlagLevel.Unwanted, "search hijacker"),
        ("Coupon", FlagLevel.Unwanted, "coupon adware"),
        ("Hola", FlagLevel.Unwanted, "free VPN that shares the PC's connection"),
        ("PDFSpark", FlagLevel.Unwanted, "PDF tool bundled with adware"),
        ("Easy PDF Combine", FlagLevel.Unwanted, "PDF tool bundled with adware"),
        ("PDF Pro", FlagLevel.Review, "often bundled; check if they use it"),
        ("Total System Care", FlagLevel.Unwanted, "fake optimizer"),
        ("TotalAV", FlagLevel.Review, "paid antivirus with heavy upselling: check whether they meant to buy it"),
        ("Total AV", FlagLevel.Review, "paid antivirus with heavy upselling: check whether they meant to buy it"),
        ("PC Matic", FlagLevel.Review, "paid optimizer/antivirus: check whether they use it"),
        // Trials and preinstalled extras
        ("McAfee", FlagLevel.Review, "preinstalled antivirus trial: remove if expired (Windows Security protects the PC)"),
        ("Norton", FlagLevel.Review, "antivirus: remove if it's an expired trial"),
        ("WebAdvisor", FlagLevel.Review, "McAfee browser add-on, usually unwanted"),
        ("ExpressVPN", FlagLevel.Review, "preinstalled VPN trial"),
        ("Dropbox Promotion", FlagLevel.Unwanted, "preinstalled offer"),
        ("Booking.com", FlagLevel.Unwanted, "preinstalled shortcut"),
        ("Candy Crush", FlagLevel.Review, "preinstalled game"),
        ("CandyCrush", FlagLevel.Review, "preinstalled game"),
        ("Bubble Witch", FlagLevel.Review, "preinstalled game"),
        ("FarmVille", FlagLevel.Review, "preinstalled game"),
        ("Disney+", FlagLevel.Review, "preinstalled app"),
        ("TikTok", FlagLevel.Review, "preinstalled app"),
        ("Hulu", FlagLevel.Review, "preinstalled app"),
        ("Clipchamp", FlagLevel.Review, "preinstalled video editor"),
        ("CCleaner", FlagLevel.Review, "cleaner that's often bundled with other software"),
        ("Opera", FlagLevel.Review, "often installed by accident with other software"),
        ("Avast Cleanup", FlagLevel.Review, "paid optimizer"),
        ("AVG TuneUp", FlagLevel.Review, "paid optimizer"),
        // Mac adware and scareware "cleaners"
        ("MacKeeper", FlagLevel.Unwanted, "scareware cleaner that nags for payment"),
        ("Advanced Mac Cleaner", FlagLevel.Unwanted, "scareware cleaner"),
        ("Mac Auto Fixer", FlagLevel.Unwanted, "scareware cleaner"),
        ("Smart Mac Booster", FlagLevel.Unwanted, "scareware cleaner"),
        ("Mac Cleanup Pro", FlagLevel.Unwanted, "scareware cleaner"),
        ("Mac Tonic", FlagLevel.Unwanted, "scareware cleaner"),
        ("MacBooster", FlagLevel.Unwanted, "scareware cleaner"),
        ("Genieo", FlagLevel.Unwanted, "adware that changes search"),
        ("Search Baron", FlagLevel.Unwanted, "browser hijacker"),
        ("SearchBaron", FlagLevel.Unwanted, "browser hijacker"),
        ("SearchMine", FlagLevel.Unwanted, "browser hijacker"),
        ("Weknow", FlagLevel.Unwanted, "browser hijacker"),
        ("Combo Cleaner", FlagLevel.Review, "paid cleaner often pushed by pop-ups"),
        ("CleanMyMac", FlagLevel.Review, "paid cleaner: keep if the customer bought it"),
        // Remote access: legitimate tools that are also used by scammers.
        ("AnyDesk", FlagLevel.Review, "remote-control program: make sure the customer put it there (scammers use it)"),
        ("TeamViewer", FlagLevel.Review, "remote-control program: make sure the customer put it there (scammers use it)"),
        ("UltraViewer", FlagLevel.Review, "remote-control program often used by scammers"),
        ("ScreenConnect", FlagLevel.Review, "remote-control program: make sure the customer knows who installed it"),
        ("ConnectWise Control", FlagLevel.Review, "remote-control program: make sure the customer knows who installed it"),
        ("RustDesk", FlagLevel.Review, "remote-control program: make sure the customer put it there"),
        ("Supremo", FlagLevel.Review, "remote-control program often used by scammers"),
        ("LogMeIn", FlagLevel.Review, "remote-control program: make sure the customer put it there"),
    };

    /// <summary>Never flagged: Windows parts, drivers and runtimes that other programs need.</summary>
    private static readonly string[] NeverFlag = { "Microsoft Visual C++", ".NET", "Windows Driver", "Microsoft Edge WebView", "Realtek", "Intel(R)", "NVIDIA", "AMD Software", "Microsoft Update Health" };

    public static ProgramFlag? Flag(string name, string publisher)
    {
        if (NeverFlag.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))) return null;
        foreach (var (m, level, why) in Rules)
            if (name.Contains(m, StringComparison.OrdinalIgnoreCase)) return new ProgramFlag(level, why);
        return null;
    }

    public static readonly string[] AntivirusNames = { "McAfee", "Norton", "Avast", "AVG", "Bitdefender", "Kaspersky", "ESET", "Malwarebytes", "TotalAV", "Total AV", "Webroot", "Sophos", "Trend Micro", "Avira", "PC Matic" };

    /// <summary>Flags every program and returns notes that apply to the whole list (for example two antivirus programs).</summary>
    public static List<string> Review(IList<InstalledProgram> programs)
    {
        foreach (var p in programs)
            if (Flag(p.Name, p.Publisher) is { } f) { p.Level = f.Level; p.Why = f.Why; }
        var notes = new List<string>();
        var av = programs.Where(p => !p.IsStoreApp && AntivirusNames.Any(a => p.Name.StartsWith(a, StringComparison.OrdinalIgnoreCase))
                                     && !p.Name.Contains("WebAdvisor", StringComparison.OrdinalIgnoreCase)).Select(p => p.Name).Distinct().ToList();
        if (av.Count > 1) notes.Add($"More than one antivirus is installed ({string.Join(", ", av)}). Two at once slow the PC and can conflict: keep one (or just Windows Security).");
        return notes;
    }
}
