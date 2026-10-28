using System.Text.Json;
using DeskMaster.Core.Json;
using DeskMaster.Core.Platform;

namespace DeskMaster.Core.Settings;

public sealed class AppSettings
{
    public bool ProMode { get; set; }

    /// <summary>"system", "light" or "dark".</summary>
    public string Theme { get; set; } = "system";

    /// <summary>"en" or "ar"; empty follows the OS language.</summary>
    public string Language { get; set; } = "";

    public string? LastScanPath { get; set; }
    public string? PreferredDestination { get; set; }

    // Background features (tray)
    public bool KeepRunningInTray { get; set; } = PlatformInfo.Os != OsKind.Linux; // not every Linux desktop has a tray
    public bool StartWithSystem { get; set; }
    public bool LowSpaceAlerts { get; set; } = true;
    public int LowSpacePercent { get; set; } = 10;
    public int LowSpaceGb { get; set; } = 15;
    public bool AutoClean { get; set; }
    public int AutoCleanDays { get; set; } = 7;
    public DateTime? LastAutoCleanUtc { get; set; }
    public bool CheckForUpdates { get; set; } = true;

    public static string Folder => Path.Combine(PathResolver.CurrentTokens["Config"], "DeskMaster");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>Settings folder used before the project was renamed from DiskMaster.</summary>
    private static string LegacyFolder => Path.Combine(PathResolver.CurrentTokens["Config"], "DiskMaster");

    public static AppSettings Load()
    {
        MigrateLegacyFolder();
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), DeskMasterJsonContext.Default.AppSettings) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings: start fresh.
        }
        return new AppSettings();
    }

    /// <summary>Copies settings, rules and logs from the old DiskMaster folder once.</summary>
    internal static void MigrateLegacyFolder(string? legacy = null, string? target = null)
    {
        legacy ??= LegacyFolder;
        target ??= Folder;
        if (Directory.Exists(target) || !Directory.Exists(legacy))
            return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(target, Path.GetRelativePath(legacy, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starting fresh is fine if the old folder can't be read.
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, DeskMasterJsonContext.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; never crash over them.
        }
    }
}
