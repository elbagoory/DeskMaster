using System.IO.Enumeration;
using DeskMaster.Core.IO;
using DeskMaster.Core.Platform;

namespace DeskMaster.Core.Relocation;

public sealed record FolderMoveResult(int Moved, int Duplicates, int Skipped, IReadOnlyList<string> Errors, string DuplicatesFolder)
{
    public bool Succeeded => Errors.Count == 0;
}

public sealed record FolderMoveProgress(int Done, int Total, string CurrentName, long CopiedBytes, long TotalBytes);

/// <summary>What a move would take (movable) and what it leaves behind (unfinished downloads, desktop.ini).</summary>
public sealed record FolderMovePlan(long MovableBytes, int MovableItems, long LeftBytes, int LeftItems);

/// <summary>Moves the contents of a user folder (e.g. Downloads) to another drive without overwriting anything.</summary>
public static class FolderMover
{
    /// <summary>Unfinished downloads and system files that stay where they are.</summary>
    public static readonly string[] SkipPatterns =
        ["*.crdownload", "*.part", "*.partial", "*.download", "*.opdownload", "desktop.ini", ".DS_Store", ".localized"];

    public static bool IsLeftBehind(FileSystemInfo entry) =>
        (entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
        SkipPatterns.Any(p => FileSystemName.MatchesSimpleExpression(p, entry.Name, ignoreCase: true));

    /// <summary>Measures what <see cref="MoveContentsAsync"/> would move and what it would leave.</summary>
    public static FolderMovePlan Plan(string source)
    {
        long movableBytes = 0, leftBytes = 0;
        int movableItems = 0, leftItems = 0;
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            long size;
            try
            {
                size = entry is FileInfo f ? f.Length
                    : (entry.Attributes & FileAttributes.ReparsePoint) != 0 ? 0
                    : FileOps.Measure(entry.FullName).Bytes;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                size = 0;
            }

            if (IsLeftBehind(entry))
            {
                leftBytes += size;
                leftItems++;
            }
            else
            {
                movableBytes += size;
                movableItems++;
            }
        }
        return new FolderMovePlan(movableBytes, movableItems, leftBytes, leftItems);
    }

    public static async Task<FolderMoveResult> MoveContentsAsync(string source, string destination,
        IProgress<FolderMoveProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (FileOps.IsSameOrInside(destination, source) || FileOps.IsSameOrInside(source, destination))
            return new FolderMoveResult(0, 0, 0, ["Source and destination overlap."], "");

        Directory.CreateDirectory(destination);
        var duplicatesFolder = Path.Combine(destination, "from-" + SafeName(source) + "-duplicates");
        int moved = 0, duplicates = 0, skipped = 0;
        var errors = new List<string>();

        var entries = new DirectoryInfo(source).EnumerateFileSystemInfos().ToList();
        var toMove = entries.Where(e => !IsLeftBehind(e)).ToList();
        skipped = entries.Count - toMove.Count;
        var totalBytes = Plan(source).MovableBytes;
        long doneBytes = 0;

        for (var i = 0; i < toMove.Count; i++)
        {
            var entry = toMove[i];
            cancellationToken.ThrowIfCancellationRequested();

            var target = Path.Combine(destination, entry.Name);
            var isDuplicate = File.Exists(target) || Directory.Exists(target);
            if (isDuplicate)
            {
                Directory.CreateDirectory(duplicatesFolder);
                target = UniquePath(Path.Combine(duplicatesFolder, entry.Name));
            }

            var entryStart = doneBytes;
            var index = i;
            progress?.Report(new FolderMoveProgress(i, toMove.Count, entry.Name, doneBytes, totalBytes));
            var entryProgress = new SyncProgress<CopyProgress>(p =>
                progress?.Report(new FolderMoveProgress(index, toMove.Count, entry.Name, entryStart + p.CopiedBytes, totalBytes)));
            try
            {
                var size = entry is FileInfo fi ? fi.Length : FileOps.Measure(entry.FullName).Bytes;
                await FileOps.MoveEntryAsync(entry, target, entryProgress, cancellationToken).ConfigureAwait(false);
                doneBytes += size;
                if (isDuplicate)
                    duplicates++;
                else
                    moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{entry.Name}: {ex.Message}");
            }
        }
        progress?.Report(new FolderMoveProgress(toMove.Count, toMove.Count, "", totalBytes, totalBytes));

        return new FolderMoveResult(moved, duplicates, skipped, errors, duplicates > 0 ? duplicatesFolder : "");
    }

    /// <summary>Calls back synchronously (Progress&lt;T&gt; would post and reorder updates).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    private static string SafeName(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        var letter = PlatformInfo.Os == OsKind.Windows && root.Length >= 1 ? root[..1] : "old";
        return letter + "-" + Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
    }
}
