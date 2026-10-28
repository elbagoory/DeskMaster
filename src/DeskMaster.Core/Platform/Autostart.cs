namespace DeskMaster.Core.Platform;

/// <summary>Starts DeskMaster (in the tray) when the user signs in.</summary>
public static class Autostart
{
    public const string BackgroundArgument = "--background";
    private const string Name = "DeskMaster";

    public static bool IsEnabled()
    {
        try
        {
            return PlatformInfo.Os switch
            {
                OsKind.Windows when OperatingSystem.IsWindows() => RunKey()?.GetValue(Name) is string,
                OsKind.MacOS => File.Exists(MacPlist),
                _ => File.Exists(LinuxDesktopFile),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Turns autostart on or off for the current executable. Returns false if it could not be changed.</summary>
    public static bool Set(bool enabled, string? executable = null)
    {
        executable ??= Environment.ProcessPath;
        if (executable is null)
            return false;
        try
        {
            switch (PlatformInfo.Os)
            {
                case OsKind.Windows when OperatingSystem.IsWindows():
                    using (var key = RunKey(writable: true))
                    {
                        if (key is null)
                            return false;
                        key.DeleteValue("DiskMaster", throwOnMissingValue: false); // entry from before the rename
                        if (enabled)
                            key.SetValue(Name, $"\"{executable}\" {BackgroundArgument}");
                        else
                            key.DeleteValue(Name, throwOnMissingValue: false);
                    }
                    return true;

                case OsKind.MacOS:
                    if (!enabled)
                    {
                        File.Delete(MacPlist);
                        return true;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(MacPlist)!);
                    File.WriteAllText(MacPlist, $"""
                        <?xml version="1.0" encoding="UTF-8"?>
                        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                        <plist version="1.0">
                        <dict>
                          <key>Label</key><string>org.deskmaster.app</string>
                          <key>ProgramArguments</key>
                          <array><string>{System.Security.SecurityElement.Escape(executable)}</string><string>{BackgroundArgument}</string></array>
                          <key>RunAtLoad</key><true/>
                        </dict>
                        </plist>
                        """);
                    return true;

                default:
                    if (!enabled)
                    {
                        File.Delete(LinuxDesktopFile);
                        return true;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(LinuxDesktopFile)!);
                    File.WriteAllText(LinuxDesktopFile, $"""
                        [Desktop Entry]
                        Type=Application
                        Name=DeskMaster
                        Comment=Disk space alerts and cleanup
                        Exec="{executable}" {BackgroundArgument}
                        X-GNOME-Autostart-enabled=true
                        """);
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Microsoft.Win32.RegistryKey? RunKey(bool writable = false) =>
        Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable);

    private static string MacPlist =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", "org.deskmaster.app.plist");

    private static string LinuxDesktopFile =>
        Path.Combine(PathResolver.CurrentTokens["Config"], "autostart", "deskmaster.desktop");
}
