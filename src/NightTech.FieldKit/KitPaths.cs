using NightTech.Core;

namespace NightTech.FieldKit;

/// <summary>
/// Where the Field Kit keeps its files. Run from a USB stick (a "portable.txt" file next to the program), reports
/// and logs stay on the stick, so nothing is saved on the customer's computer. Installed, they go in Documents.
/// </summary>
public static class KitPaths
{
    public static string ProgramFolder { get; } = AppContext.BaseDirectory;

    public static bool Portable { get; } = File.Exists(Path.Combine(ProgramFolder, "portable.txt"));

    public static string Reports { get; } = Portable
        ? Path.Combine(ProgramFolder, "Reports")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Night Tech Field Kit", "Reports");

    public static string Logs { get; } = Portable
        ? Path.Combine(ProgramFolder, "Logs")
        : Path.Combine(Brand.AppDataFolder("Field Kit"), "logs");

    /// <summary>Night Tech Wipe next to the kit on the USB stick, or installed on this computer.</summary>
    public static string? FindWipe() => FindTool("Wipe", "NightTechWipe.exe", "Night Tech Wipe");

    private static string? FindTool(string kitFolder, string exe, string installedFolder)
    {
        var candidates = new[]
        {
            Path.Combine(ProgramFolder, "..", kitFolder, exe),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", installedFolder, exe),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    /// <summary>Night Tech Recovery next to the kit on the USB stick, or installed on this computer.</summary>
    public static string? FindRecovery()
    {
        var candidates = new[]
        {
            Path.Combine(ProgramFolder, "..", "Recovery", "NightTechRecovery.exe"),
            Path.Combine(ProgramFolder, "Recovery", "NightTechRecovery.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Night Tech Recovery", "NightTechRecovery.exe"),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }
}
