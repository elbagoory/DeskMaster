using static DeskMaster.Core.Localization.Text;
using System.IO.Hashing;
using DeskMaster.Core.Drives;
using DeskMaster.Core.Platform;

namespace DeskMaster.Core.IO;

public sealed record CopyProgress(long CopiedBytes, long TotalBytes, string CurrentFile);

/// <summary>
/// Cross-volume move helpers. Every copied file is hashed on both sides before the original is
/// deleted, and an interrupted move cleans up after itself so it can simply be started again.
/// </summary>
public static class FileOps
{
    /// <summary>Written into a destination while a move is in progress; marks it as ours to clean up.</summary>
    public const string PartialMarker = ".deskmaster-partial";

    private const int BufferSize = 1024 * 1024;
    private const long ProgressStep = 16L * 1024 * 1024;

    private static readonly EnumerationOptions AllFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static (long Bytes, long Files) Measure(string directory)
    {
        long bytes = 0, files = 0;
        foreach (var f in new DirectoryInfo(directory).EnumerateFiles("*", AllFiles))
        {
            bytes += f.Length;
            files++;
        }
        return (bytes, files);
    }

    /// <summary>True when <paramref name="destination"/> is left over from an interrupted DeskMaster move.</summary>
    public static bool IsPartialMove(string destination) =>
        Directory.Exists(destination) && File.Exists(Path.Combine(destination, PartialMarker));

    /// <summary>Moves a folder (and everything in it) to <paramref name="destination"/>, across drives if needed.</summary>
    public static async Task MoveDirectoryAsync(string source, string destination,
        IProgress<CopyProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (IsSameOrInside(destination, source))
            throw new IOException(T("The destination cannot be inside the folder being moved."));

        if (IsPartialMove(destination))
            DeleteDirectory(destination); // A previous attempt was interrupted; start clean.
        else if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
            throw new IOException(F("{0} already exists and is not empty.", destination));

        if (SameVolume(source, destination))
        {
            try
            {
                if (Directory.Exists(destination))
                    Directory.Delete(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                Directory.Move(source, destination);
                return;
            }
            catch (IOException)
            {
                // Different mount points on the same root (Linux/macOS): fall back to copying.
            }
        }

        await Task.Run(() => CopyThenDelete(source, destination, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The cross-drive path of <see cref="MoveDirectoryAsync"/>: copy, verify, delete the source.</summary>
    internal static void CopyThenDelete(string source, string destination, IProgress<CopyProgress>? progress, CancellationToken ct)
    {
        // Links and cloud placeholders cannot be copied faithfully; never delete what we could not copy.
        var linkOptions = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false };
        if (new DirectoryInfo(source).EnumerateFileSystemInfos("*", linkOptions).Any(e => (e.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new IOException(T("The folder contains links or cloud-only files. Move it manually."));

        var (totalBytes, totalFiles) = Measure(source);
        var free = DriveService.DriveFor(destination)?.FreeBytes;
        if (free is not null && free < totalBytes)
            throw new IOException(F("Not enough free space at {0}.", destination));

        Directory.CreateDirectory(destination);
        var marker = Path.Combine(destination, PartialMarker);
        File.WriteAllText(marker, $"Interrupted move from {source}. Safe to delete.");
        try
        {
            var sourceDir = new DirectoryInfo(source);
            foreach (var dir in sourceDir.EnumerateDirectories("*", AllFiles))
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir.FullName)));

            long copied = 0;
            foreach (var file in sourceDir.EnumerateFiles("*", AllFiles))
            {
                ct.ThrowIfCancellationRequested();
                var target = Path.Combine(destination, Path.GetRelativePath(source, file.FullName));
                var before = copied;
                CopyFileVerified(file.FullName, target, b => progress?.Report(new CopyProgress(before + b, totalBytes, file.Name)), ct);
                copied += file.Length;
            }

            var (copiedBytes, copiedFiles) = Measure(destination);
            var markerBytes = new FileInfo(marker).Length;
            if (copiedBytes - markerBytes != totalBytes || copiedFiles - 1 != totalFiles)
                throw new IOException(T("Copy check failed: the copy does not match the original."));
            File.Delete(marker);
        }
        catch
        {
            // Leave the original untouched and remove the incomplete copy.
            TryDeleteDirectory(destination);
            throw;
        }

        DeleteDirectory(source);
    }

    /// <summary>
    /// Copies one file and checks the copy by hashing both sides (XxHash128).
    /// <paramref name="onBytes"/> receives the bytes copied so far.
    /// </summary>
    public static void CopyFileVerified(string source, string target, Action<long>? onBytes = null, CancellationToken ct = default)
    {
        var sourceHash = new XxHash128();
        var buffer = new byte[BufferSize];
        long done = 0, lastReport = 0;
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan))
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
            {
                output.SetLength(input.Length);
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sourceHash.Append(buffer.AsSpan(0, read));
                    output.Write(buffer, 0, read);
                    done += read;
                    if (done - lastReport >= ProgressStep)
                    {
                        onBytes?.Invoke(done);
                        lastReport = done;
                    }
                }
            }

            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
            if (!sourceHash.GetCurrentHash().AsSpan().SequenceEqual(HashFile(target, buffer, ct)))
                throw new IOException(F("Copy check failed for {0}: the copy is different from the original.", Path.GetFileName(source)));
            onBytes?.Invoke(done);
        }
        catch
        {
            try { File.Delete(target); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public static byte[] HashFile(string path, byte[]? buffer = null, CancellationToken ct = default)
    {
        buffer ??= new byte[BufferSize];
        var hash = new XxHash128();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.Append(buffer.AsSpan(0, read));
        }
        return hash.GetCurrentHash();
    }

    /// <summary>Moves a single file; across drives it is copied, verified, then the original deleted.</summary>
    public static async Task MoveFileAsync(string source, string target, Action<long>? onBytes = null, CancellationToken ct = default)
    {
        if (File.Exists(target))
            throw new IOException(F("{0} already exists.", target));
        if (SameVolume(source, target))
        {
            try
            {
                File.Move(source, target);
                return;
            }
            catch (IOException) when (!File.Exists(target))
            {
                // Cross-device on Linux/macOS: copy instead.
            }
        }

        await Task.Run(() =>
        {
            CopyFileVerified(source, target, onBytes, ct);
            var info = new FileInfo(source);
            if (info.IsReadOnly)
                info.IsReadOnly = false;
            info.Delete();
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a folder tree, clearing read-only flags first.</summary>
    public static void DeleteDirectory(string path)
    {
        foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", AllFiles))
        {
            if (file.IsReadOnly)
                file.IsReadOnly = false;
        }
        Directory.Delete(path, recursive: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                DeleteDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The marker file stays, so the next attempt cleans it up.
        }
    }

    /// <summary>Moves one file or folder to <paramref name="destinationPath"/>, across drives if needed.</summary>
    public static Task MoveEntryAsync(FileSystemInfo entry, string destinationPath,
        IProgress<CopyProgress>? progress = null, CancellationToken cancellationToken = default) =>
        entry is DirectoryInfo dir
            ? MoveDirectoryAsync(dir.FullName, destinationPath, progress, cancellationToken)
            : MoveFileAsync(entry.FullName, destinationPath,
                b => progress?.Report(new CopyProgress(b, ((FileInfo)entry).Length, entry.Name)), cancellationToken);

    public static bool SameVolume(string a, string b)
    {
        if (PlatformInfo.Os == OsKind.Windows)
            return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        var drives = DriveService.GetDrives();
        return DriveService.DriveFor(a, drives)?.RootPath == DriveService.DriveFor(b, drives)?.RootPath;
    }

    public static bool IsSameOrInside(string path, string folder)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return p.StartsWith(f, PlatformInfo.PathComparison);
    }
}
