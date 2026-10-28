using static DeskMaster.Core.Localization.Text;
namespace DeskMaster.Core.Platform;

/// <summary>
/// Last line of defence: refuses to clean drive roots, user folders and OS folders,
/// whatever a (possibly community-written) rule says.
/// </summary>
public static class SafetyGuard
{
    private static readonly Lazy<HashSet<string>> LazyProtected = new(BuildProtectedSet);

    public static bool IsSafeToClean(string path, out string reason)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            reason = T("Path is not absolute.");
            return false;
        }

        string full;
        try
        {
            full = Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = T("Path is invalid.");
            return false;
        }

        var root = Path.GetPathRoot(full) ?? "";
        if (full.Length <= Normalize(root).Length)
        {
            reason = T("Refusing to clean a drive root.");
            return false;
        }

        var depth = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;
        if (depth < 2)
        {
            reason = T("Refusing to clean a top-level folder.");
            return false;
        }

        if (LazyProtected.Value.Contains(full))
        {
            reason = T("This is a protected system or user folder.");
            return false;
        }

        reason = "";
        return true;
    }

    private static readonly Lazy<string[]> LazySystemTrees = new(() =>
    {
        var list = new List<string>();
        foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var p = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(p))
                list.Add(Normalize(p));
        }
        if (PlatformInfo.Os != OsKind.Windows)
            list.AddRange(["/bin", "/boot", "/etc", "/lib", "/lib64", "/sbin", "/usr", "/System", "/private/etc", "/proc", "/sys", "/dev"]);
        list.Add(Normalize(AppContext.BaseDirectory));
        return list.ToArray();
    });

    /// <summary>
    /// For files the user picks in the explorer: anything except drive roots, protected folders
    /// and OS/program folders (Windows, Program Files, /usr, /etc, ...).
    /// </summary>
    public static bool IsSafeToDelete(string path, out string reason)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            reason = T("Path is not absolute.");
            return false;
        }

        var full = Normalize(path);
        var root = Path.GetPathRoot(full) ?? "";
        if (full.Length <= Normalize(root).Length)
        {
            reason = T("A whole drive can't be deleted.");
            return false;
        }

        if (LazyProtected.Value.Contains(full))
        {
            reason = T("This is a protected system or user folder.");
            return false;
        }

        foreach (var tree in LazySystemTrees.Value)
        {
            if (string.Equals(full, tree, PlatformInfo.PathComparison) ||
                full.StartsWith(tree + Path.DirectorySeparatorChar, PlatformInfo.PathComparison))
            {
                reason = T("Files of the operating system or installed programs can't be deleted here. Uninstall the program instead.");
                return false;
            }
        }

        reason = "";
        return true;
    }

    internal static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static HashSet<string> BuildProtectedSet()
    {
        var set = new HashSet<string>(PlatformInfo.PathComparer);
        void Add(string? p)
        {
            if (!string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
                set.Add(Normalize(p));
        }

        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures,
                     Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos,
                     Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
                     Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.UserProfile,
                 })
        {
            Add(Environment.GetFolderPath(folder));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var sub in new[] { "Downloads", "OneDrive", "Library", "Library/Application Support",
                     ".config", ".cache", ".local", ".local/share", "AppData", "Applications", ".ssh", ".gnupg" })
        {
            Add(Path.Combine(home, sub));
        }

        foreach (var p in new[] { "/bin", "/boot", "/etc", "/usr", "/usr/local", "/var", "/var/lib", "/opt",
                     "/System", "/Applications", "/Library", "/private", "/private/var", "/home", "/Users" })
        {
            if (PlatformInfo.Os != OsKind.Windows)
                Add(p);
        }
        return set;
    }
}
