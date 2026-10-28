using DeskMaster.Core.Platform;

namespace DeskMaster.Core.Drives;

public sealed record DriveSummary(
    string RootPath,
    string Label,
    string Format,
    long TotalBytes,
    long FreeBytes,
    bool IsSystem,
    bool IsRemovable)
{
    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsedFraction => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;

    public string DisplayName => string.IsNullOrWhiteSpace(Label) || Label == RootPath
        ? RootPath
        : $"{Label} ({RootPath.TrimEnd('\\')})";

    /// <summary>Less than 10% or 15 GB free.</summary>
    public bool IsLow => TotalBytes > 0 && (FreeBytes < TotalBytes * 0.10 || FreeBytes < 15L * 1024 * 1024 * 1024);
}

public static class DriveService
{
    private static readonly HashSet<string> PseudoFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "tmpfs", "devtmpfs", "proc", "sysfs", "squashfs", "overlay", "cgroup", "cgroup2", "devpts",
        "mqueue", "debugfs", "tracefs", "securityfs", "pstore", "bpf", "autofs", "fusectl", "configfs",
        "ramfs", "hugetlbfs", "nsfs", "efivarfs", "binfmt_misc", "devfs", "autofs", "nullfs",
    };

    public static IReadOnlyList<DriveSummary> GetDrives()
    {
        var systemRoot = SystemRoot();
        var result = new List<DriveSummary>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.TotalSize <= 0)
                    continue;
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;
                if (PseudoFormats.Contains(drive.DriveFormat))
                    continue;
                var root = drive.RootDirectory.FullName;
                if (PlatformInfo.Os == OsKind.MacOS && root.StartsWith("/System/Volumes/", StringComparison.Ordinal))
                    continue;
                if (PlatformInfo.Os == OsKind.Linux && (root.StartsWith("/boot", StringComparison.Ordinal) || root.StartsWith("/snap", StringComparison.Ordinal)))
                    continue;

                string label;
                try { label = drive.VolumeLabel; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { label = ""; }

                result.Add(new DriveSummary(root, label, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace,
                    string.Equals(root, systemRoot, PlatformInfo.PathComparison), drive.DriveType == DriveType.Removable));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Drive disappeared or is locked (BitLocker).
            }
        }
        return result.OrderByDescending(d => d.IsSystem).ThenBy(d => d.RootPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The drive (mount point) that holds <paramref name="path"/>.</summary>
    public static DriveSummary? DriveFor(string path, IReadOnlyList<DriveSummary>? drives = null)
    {
        drives ??= GetDrives();
        var full = Path.GetFullPath(path);
        return drives
            .Where(d => full.StartsWith(d.RootPath, PlatformInfo.PathComparison))
            .MaxBy(d => d.RootPath.Length);
    }

    public static string SystemRoot() =>
        PlatformInfo.Os == OsKind.Windows
            ? Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? "C:\\"
            : "/";
}
