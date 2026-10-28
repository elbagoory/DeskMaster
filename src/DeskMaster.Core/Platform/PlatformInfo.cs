using static DeskMaster.Core.Localization.Text;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskMaster.Core.Platform;

public enum OsKind
{
    Windows,
    MacOS,
    Linux,
    Other,
}

/// <summary>Facts about the operating system DeskMaster is running on.</summary>
public static class PlatformInfo
{
    public static OsKind Os { get; } =
        OperatingSystem.IsWindows() ? OsKind.Windows :
        OperatingSystem.IsMacOS() ? OsKind.MacOS :
        OperatingSystem.IsLinux() ? OsKind.Linux :
        OsKind.Other;

    /// <summary>Key used in rule files: "windows", "macos" or "linux".</summary>
    public static string OsKey => KeyFor(Os);

    public static string KeyFor(OsKind os) => os switch
    {
        OsKind.Windows => "windows",
        OsKind.MacOS => "macos",
        OsKind.Linux => "linux",
        _ => "other",
    };

    public static string OsDescription => RuntimeInformation.OSDescription;

    public static bool IsElevated => Environment.IsPrivilegedProcess;

    /// <summary>Only Windows can show a UAC prompt to relaunch elevated.</summary>
    public static bool CanRequestElevation => Os == OsKind.Windows;

    public static string ElevationHint => Os switch
    {
        OsKind.Windows => T("Restart DeskMaster as administrator to unlock these items."),
        _ => T("Run DeskMaster with sudo to unlock these items (for example: sudo deskmaster-cli clean)."),
    };

    public static StringComparison PathComparison =>
        Os == OsKind.Linux ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public static StringComparer PathComparer =>
        Os == OsKind.Linux ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    /// <summary>Opens a folder, or reveals a file, in the platform file manager.</summary>
    public static void OpenInFileManager(string path)
    {
        var isFile = File.Exists(path);
        try
        {
            switch (Os)
            {
                case OsKind.Windows:
                    Process.Start(new ProcessStartInfo("explorer.exe", isFile ? $"/select,\"{path}\"" : $"\"{path}\"")
                    {
                        UseShellExecute = true,
                    });
                    break;
                case OsKind.MacOS:
                    Process.Start("open", isFile ? ["-R", path] : [path]);
                    break;
                default:
                    Process.Start("xdg-open", isFile ? Path.GetDirectoryName(path) ?? path : path);
                    break;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // No file manager available (headless machine); nothing to do.
        }
    }

    /// <summary>Relaunches the current app with a UAC prompt. Returns false if the user declined.</summary>
    public static bool TryRestartElevated(IEnumerable<string>? extraArgs = null)
    {
        if (!CanRequestElevation)
            return false;

        var processPath = Environment.ProcessPath;
        if (processPath is null)
            return false;

        var args = new List<string>();
        // When launched through "dotnet App.dll" the dll must be passed again.
        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            args.Add(Environment.GetCommandLineArgs()[0]);
        if (extraArgs is not null)
            args.AddRange(extraArgs);

        var info = new ProcessStartInfo(processPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
        };

        try
        {
            Process.Start(info);
            return true;
        }
        catch (Win32Exception)
        {
            return false; // UAC prompt cancelled.
        }
    }
}
