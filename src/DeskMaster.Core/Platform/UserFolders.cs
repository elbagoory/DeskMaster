using static DeskMaster.Core.Localization.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DeskMaster.Core.Platform;

public enum UserFolder
{
    Downloads,
    Videos,
    Pictures,
    Music,
    Documents,
}

/// <summary>Finds user folders and changes where the OS puts new files (e.g. the Downloads folder).</summary>
public static partial class UserFolders
{
    private static readonly Dictionary<UserFolder, Guid> WindowsIds = new()
    {
        [UserFolder.Downloads] = new("374DE290-123F-4565-9164-B4C1D2B7F0F0"),
        [UserFolder.Videos] = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC"),
        [UserFolder.Pictures] = new("33E28130-4E1E-4676-835A-98395C3BC3BB"),
        [UserFolder.Music] = new("4BD8D571-6D19-48D3-BE97-422220080E43"),
        [UserFolder.Documents] = new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"),
    };

    private static readonly Dictionary<UserFolder, string> XdgKeys = new()
    {
        [UserFolder.Downloads] = "XDG_DOWNLOAD_DIR",
        [UserFolder.Videos] = "XDG_VIDEOS_DIR",
        [UserFolder.Pictures] = "XDG_PICTURES_DIR",
        [UserFolder.Music] = "XDG_MUSIC_DIR",
        [UserFolder.Documents] = "XDG_DOCUMENTS_DIR",
    };

    public static string GetPath(UserFolder folder)
    {
        if (PlatformInfo.Os == OsKind.Windows && TryGetWindows(folder, out var winPath))
            return winPath;
        if (PlatformInfo.Os == OsKind.Linux && TryGetXdg(folder, out var xdgPath))
            return xdgPath;

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return folder switch
        {
            UserFolder.Downloads => Path.Combine(home, "Downloads"),
            UserFolder.Videos => PlatformInfo.Os == OsKind.MacOS ? Path.Combine(home, "Movies") : Path.Combine(home, "Videos"),
            UserFolder.Pictures => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            UserFolder.Music => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            _ => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
    }

    public static bool CanChangeLocation => PlatformInfo.Os is OsKind.Windows or OsKind.Linux;

    /// <summary>Makes <paramref name="newPath"/> the folder the OS uses from now on.</summary>
    public static bool TrySetPath(UserFolder folder, string newPath, out string message)
    {
        Directory.CreateDirectory(newPath);
        if (OperatingSystem.IsWindows())
            return TrySetWindows(folder, newPath, out message);

        if (PlatformInfo.Os == OsKind.Linux)
        {
            var file = Path.Combine(XdgConfigHome(), "user-dirs.dirs");
            var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
            var key = XdgKeys[folder];
            lines.RemoveAll(l => l.StartsWith(key + "=", StringComparison.Ordinal));
            lines.Add($"{key}=\"{newPath}\"");
            File.WriteAllLines(file, lines);
            message = F("{0} now points to {1}. Log out and back in to apply it everywhere.", T(folder.ToString()), newPath);
            return true;
        }

        message = T("macOS does not support moving this folder. Set the download location in your browser instead.");
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool TrySetWindows(UserFolder folder, string newPath, out string message)
    {
        var id = WindowsIds[folder];
        var hr = SHSetKnownFolderPath(id, 0, IntPtr.Zero, newPath);
        if (hr != 0)
        {
            // The API fails when the folder's registry entry is missing or damaged.
            // Writing the entries directly is what Explorer's "Location" tab does.
            try
            {
                var key = id.ToString("B").ToUpperInvariant();
                using var userShell = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                userShell.SetValue(key, newPath, Microsoft.Win32.RegistryValueKind.ExpandString);
                using var shell = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
                shell.SetValue(key, newPath, Microsoft.Win32.RegistryValueKind.String);
                SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                message = F("Windows refused the change (0x{0:X8}): {1}", hr, ex.Message);
                return false;
            }
        }

        if (TryGetWindowsCore(folder, out var actual) &&
            string.Equals(Path.TrimEndingDirectorySeparator(actual), Path.TrimEndingDirectorySeparator(newPath), StringComparison.OrdinalIgnoreCase))
        {
            message = F("New downloads will be saved to {0}. Restart your browser so it picks up the change.", newPath);
            return true;
        }

        using var definition = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FolderDescriptions\" + id.ToString("B"));
        message = definition is null
            ? F("Windows' own definition of the {0} folder is missing on this PC, so its location can't be changed. Set the download folder to {1} in your browser's settings instead.", T(folder.ToString()), newPath)
            : F("Windows did not accept {0} as the new {1} folder (0x{2:X8}). Set it in File Explorer: right-click {1} > Properties > Location.", newPath, T(folder.ToString()), hr);
        return false;
    }

    private static bool TryGetWindows(UserFolder folder, out string path)
    {
        path = "";
        if (!OperatingSystem.IsWindows())
            return false;
        return TryGetWindowsCore(folder, out path);
    }

    [SupportedOSPlatform("windows")]
    private static bool TryGetWindowsCore(UserFolder folder, out string path)
    {
        path = "";
        if (SHGetKnownFolderPath(WindowsIds[folder], 0, IntPtr.Zero, out var ptr) != 0)
            return false;
        try
        {
            path = Marshal.PtrToStringUni(ptr) ?? "";
            return path.Length > 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    private static string XdgConfigHome()
    {
        var value = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value)
            ? value
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
    }

    private static bool TryGetXdg(UserFolder folder, out string path)
    {
        path = "";
        var file = Path.Combine(XdgConfigHome(), "user-dirs.dirs");
        if (!File.Exists(file))
            return false;
        var key = XdgKeys[folder] + "=";
        var line = File.ReadLines(file).LastOrDefault(l => l.StartsWith(key, StringComparison.Ordinal));
        if (line is null)
            return false;
        var value = line[key.Length..].Trim('"').Replace("$HOME", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        path = value;
        return Path.IsPathFullyQualified(value);
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHSetKnownFolderPath(in Guid id, uint flags, IntPtr token, string path);

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
