using static DeskMaster.Core.Localization.Text;
using System.Globalization;
using DeskMaster.Core.Platform;
using DeskMaster.Core.Processes;

namespace DeskMaster.Core.IO;

public sealed record TrashResult(bool Succeeded, string Message);

/// <summary>Sends files and folders to the Recycle Bin / Trash so the user can restore them.</summary>
public static class Trash
{
    public static async Task<TrashResult> MoveToTrashAsync(string path, CancellationToken ct = default)
    {
        if (!SafetyGuard.IsSafeToDelete(path, out var reason))
            return new TrashResult(false, reason);
        if (!File.Exists(path) && !Directory.Exists(path))
            return new TrashResult(false, T("It no longer exists."));

        try
        {
            switch (PlatformInfo.Os)
            {
                case OsKind.Windows:
                    await Task.Run(() => WindowsRecycle(path), ct).ConfigureAwait(false);
                    break;
                case OsKind.MacOS:
                    await MacTrashAsync(path, ct).ConfigureAwait(false);
                    break;
                default:
                    await LinuxTrashAsync(path, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return new TrashResult(false, ex.Message);
        }

        return File.Exists(path) || Directory.Exists(path)
            ? new TrashResult(false, T("The item is still there; it may be in use."))
            : new TrashResult(true, PlatformInfo.Os == OsKind.Windows ? T("Moved to the Recycle Bin.") : T("Moved to the Trash."));
    }

    /// <summary>Deletes without the Recycle Bin. Same safety checks as <see cref="MoveToTrashAsync"/>.</summary>
    public static async Task<TrashResult> DeletePermanentlyAsync(string path, CancellationToken ct = default)
    {
        if (!SafetyGuard.IsSafeToDelete(path, out var reason))
            return new TrashResult(false, reason);
        try
        {
            await Task.Run(() =>
            {
                if (Directory.Exists(path))
                {
                    FileOps.DeleteDirectory(path);
                }
                else if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if (info.IsReadOnly)
                        info.IsReadOnly = false;
                    info.Delete();
                }
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TrashResult(false, ex.Message);
        }
        return File.Exists(path) || Directory.Exists(path)
            ? new TrashResult(false, T("Some files are in use and were not deleted."))
            : new TrashResult(true, T("Deleted."));
    }

    private static void WindowsRecycle(string path)
    {
        const Microsoft.VisualBasic.FileIO.UIOption ui = Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs;
        const Microsoft.VisualBasic.FileIO.RecycleOption recycle = Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin;
        if (Directory.Exists(path))
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, ui, recycle, Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
        else
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, ui, recycle, Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
    }

    private static async Task MacTrashAsync(string path, CancellationToken ct)
    {
        // Finder keeps "Put Back" information; fall back to a plain move.
        var osascript = CommandRunner.FindExecutable("osascript");
        if (osascript is not null)
        {
            var escaped = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var result = await CommandRunner.RunAsync(osascript,
                ["-e", $"tell application \"Finder\" to delete POSIX file \"{escaped}\""], TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            if (result.Succeeded)
                return;
        }
        var trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
        await MoveIntoAsync(path, UniqueTarget(trash, Path.GetFileName(path)), ct).ConfigureAwait(false);
    }

    private static async Task LinuxTrashAsync(string path, CancellationToken ct)
    {
        var gio = CommandRunner.FindExecutable("gio");
        if (gio is not null)
        {
            var result = await CommandRunner.RunAsync(gio, ["trash", path], TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            if (result.Succeeded)
                return;
        }

        // freedesktop.org Trash specification, home trash.
        var trash = Path.Combine(PathResolver.CurrentTokens["Data"], "Trash");
        var files = Path.Combine(trash, "files");
        var info = Path.Combine(trash, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);
        var target = UniqueTarget(files, Path.GetFileName(path));
        var name = Path.GetFileName(target);
        await File.WriteAllTextAsync(Path.Combine(info, name + ".trashinfo"),
            $"[Trash Info]\nPath={Uri.EscapeDataString(path).Replace("%2F", "/")}\nDeletionDate={DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n", ct)
            .ConfigureAwait(false);
        await MoveIntoAsync(path, target, ct).ConfigureAwait(false);
    }

    private static Task MoveIntoAsync(string path, string target, CancellationToken ct) =>
        Directory.Exists(path)
            ? FileOps.MoveDirectoryAsync(path, target, null, ct)
            : FileOps.MoveFileAsync(path, target, null, ct);

    private static string UniqueTarget(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        var candidate = Path.Combine(folder, name);
        for (var i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} {i}{Path.GetExtension(name)}");
        return candidate;
    }
}
