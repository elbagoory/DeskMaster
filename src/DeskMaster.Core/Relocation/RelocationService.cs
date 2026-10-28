using static DeskMaster.Core.Localization.Text;
using System.Text.Json;
using DeskMaster.Core.Drives;
using DeskMaster.Core.IO;
using DeskMaster.Core.Json;
using DeskMaster.Core.Platform;
using DeskMaster.Core.Processes;

namespace DeskMaster.Core.Relocation;

public sealed class RelocationCandidate
{
    public required RelocationDefinition Definition { get; init; }
    public required string CurrentPath { get; init; }
    public long Bytes { get; init; }
    public long Files { get; init; }
    public string? DriveRoot { get; init; }
    public bool IsOnSystemDrive { get; init; }
}

public sealed record RelocationResult(bool Succeeded, string Message, string? NewPath = null);

public static class RelocationService
{
    private const string Resource = "DeskMaster.Core.Data.relocations.json";

    public static IReadOnlyList<RelocationDefinition> LoadDefinitions()
    {
        using var stream = typeof(RelocationService).Assembly.GetManifestResourceStream(Resource)
                           ?? throw new InvalidOperationException("Built-in relocation file is missing from the build.");
        var definitions = JsonSerializer.Deserialize(stream, DeskMasterJsonContext.Default.ListRelocationDefinition) ?? [];
        Localization.Translations.Apply(definitions);
        return definitions;
    }

    /// <summary>Finds which relocatable folders exist and how big they are.</summary>
    public static Task<IReadOnlyList<RelocationCandidate>> DetectAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<RelocationCandidate>>(() =>
        {
            var drives = DriveService.GetDrives();
            var systemRoot = DriveService.SystemRoot();
            var list = new List<RelocationCandidate>();
            foreach (var def in LoadDefinitions())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = CurrentPathOf(def);
                if (path is null)
                    continue;

                long bytes = 0, files = 0;
                try
                {
                    (bytes, files) = FileOps.Measure(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }
                if (bytes == 0)
                    continue;

                var drive = DriveService.DriveFor(path, drives);
                list.Add(new RelocationCandidate
                {
                    Definition = def,
                    CurrentPath = path,
                    Bytes = bytes,
                    Files = files,
                    DriveRoot = drive?.RootPath,
                    IsOnSystemDrive = drive is null || string.Equals(drive.RootPath, systemRoot, PlatformInfo.PathComparison),
                });
            }
            return list.OrderByDescending(c => c.Bytes).ToList();
        }, cancellationToken);

    public static string? CurrentPathOf(RelocationDefinition def)
    {
        if (!string.IsNullOrEmpty(def.EnvVar))
        {
            var configured = UserEnvironment.Get(def.EnvVar);
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
                return configured;
        }
        return PathResolver.ResolveForCurrentOs(def.DefaultPaths).FirstOrDefault(Directory.Exists);
    }

    /// <summary>Moves the folder under <paramref name="destinationRoot"/> and points the app at it.</summary>
    public static async Task<RelocationResult> MoveAsync(RelocationCandidate candidate, string destinationRoot,
        IProgress<CopyProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var def = candidate.Definition;
        var running = ProcessGuard.FindRunning(def.CloseProcesses);
        if (running.Count > 0)
            return new RelocationResult(false, F("Close {0} first, then try again.", ProcessGuard.FriendlyList(running)));

        var target = Path.GetFullPath(Path.Combine(destinationRoot, def.TargetFolderName));
        if (FileOps.IsSameOrInside(target, candidate.CurrentPath) || FileOps.IsSameOrInside(candidate.CurrentPath, target))
            return new RelocationResult(false, T("Pick a destination outside the current folder."));
        if (Directory.Exists(target) && !FileOps.IsPartialMove(target) && Directory.EnumerateFileSystemEntries(target).Any())
            return new RelocationResult(false, F("{0} already exists and is not empty.", target));

        var drive = DriveService.DriveFor(target);
        if (drive is not null && drive.FreeBytes < candidate.Bytes + 1024L * 1024 * 1024)
            return new RelocationResult(false, F("Not enough free space on {0}.", drive.DisplayName));

        try
        {
            await FileOps.MoveDirectoryAsync(candidate.CurrentPath, target, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new RelocationResult(false, T("Move cancelled. The original folder was not changed."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new RelocationResult(false, T("Move failed:") + " " + ex.Message + " " + T("The original folder was not changed."));
        }

        if (string.IsNullOrEmpty(def.EnvVar))
            return new RelocationResult(true, F("Moved to {0}. Point the app at this folder in its settings.", target), target);

        var note = UserEnvironment.Set(def.EnvVar, target);
        return new RelocationResult(true, F("Moved to {0}.", target) + " " + note, target);
    }
}
