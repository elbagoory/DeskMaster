using static DeskMaster.Core.Localization.Text;
using DeskMaster.Core.Platform;
using DeskMaster.Core.Processes;

namespace DeskMaster.Core.Cleanup;

public sealed class CleanResult
{
    public required CleanupRule Rule { get; init; }
    public long FreedBytes { get; set; }
    public bool FreedKnown { get; set; } = true;
    public long DeletedFiles { get; set; }
    public long SkippedFiles { get; set; }
    public bool Succeeded { get; set; } = true;
    public string Message { get; set; } = "";
    public List<string> Errors { get; } = [];

    internal void AddError(string error)
    {
        if (Errors.Count < 20)
            Errors.Add(error);
    }
}

public sealed record CleanProgress(string RuleName, long DeletedFiles, long FreedBytes);

/// <summary>Deletes what a <see cref="RuleFinding"/> found. Locked files are skipped, never forced.</summary>
public static class Cleaner
{
    public static TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public static async Task<CleanResult> CleanAsync(RuleFinding finding,
        IProgress<CleanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var rule = finding.Rule;
        var result = new CleanResult { Rule = rule };

        if (rule.RequiresAdmin && !PlatformInfo.IsElevated)
            return Fail(result, T("Needs administrator rights."));

        var running = ProcessGuard.FindRunning(rule.CloseProcesses);
        if (running.Count > 0)
            return Fail(result, F("Close {0} and try again.", ProcessGuard.FriendlyList(running)));

        if (rule.Handler == "recycle-bin" && OperatingSystem.IsWindows())
        {
            var (bytes, items) = WindowsRecycleBin.Query();
            if (!WindowsRecycleBin.Empty())
                return Fail(result, T("Windows refused to empty the Recycle Bin."));
            result.FreedBytes = bytes;
            result.DeletedFiles = items;
            result.Message = T("Recycle Bin emptied.");
            return result;
        }

        if (rule.Handler == "wsl-compact")
        {
            if (!OperatingSystem.IsWindows())
                return Fail(result, T("Only available on Windows."));
            var compacted = await WslDiskCompactor.CompactAsync(finding, progress, cancellationToken).ConfigureAwait(false);
            return compacted;
        }

        var commandPath = rule.Command is null ? null : CommandRunner.FindExecutable(rule.Command.File);
        if (rule.Command is not null && commandPath is not null)
        {
            var before = RuleAnalyzer.Measure(rule, finding.Paths, cancellationToken).Bytes;
            var run = await CommandRunner.RunAsync(commandPath, rule.Command.Args, CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (run.Succeeded)
            {
                if (finding.Paths.Count > 0)
                {
                    var after = RuleAnalyzer.Measure(rule, finding.Paths, cancellationToken).Bytes;
                    result.FreedBytes = Math.Max(0, before - after);
                }
                else
                {
                    result.FreedKnown = false;
                }
                result.Message = F("Ran {0}", rule.Command.File + " " + string.Join(' ', rule.Command.Args));
                return result;
            }

            result.AddError(LastLines(run.Output, 5));
            if (rule.EstimateOnly || finding.Paths.Count == 0)
                return Fail(result, F("{0} failed (exit code {1}).", rule.Command.File, run.ExitCode));
            // Otherwise fall back to deleting the cache files directly.
        }
        else if (rule.Command is not null && (rule.EstimateOnly || finding.Paths.Count == 0))
        {
            return Fail(result, F("'{0}' is not installed.", rule.Command.File));
        }

        if (rule.EstimateOnly)
            return Fail(result, T("This rule can only be cleaned by its command."));

        await Task.Run(() => DeletePaths(rule, finding.Paths, result, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        result.Message = result.SkippedFiles > 0
            ? F("Deleted {0:N0} files; {1:N0} were in use and skipped.", result.DeletedFiles, result.SkippedFiles)
            : F("Deleted {0:N0} files.", result.DeletedFiles);
        return result;
    }

    private static void DeletePaths(CleanupRule rule, IEnumerable<string> paths, CleanResult result,
        IProgress<CleanProgress>? progress, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var cutoff = rule.MinAgeHours > 0 ? now.AddHours(-rule.MinAgeHours) : DateTime.MaxValue;

        foreach (var path in paths)
        {
            if (!SafetyGuard.IsSafeToClean(path, out var reason))
            {
                result.SkippedFiles++;
                result.AddError($"{path}: {reason}");
                continue;
            }

            foreach (var file in CandidateFiles.Enumerate(path, rule, now))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long length;
                try
                {
                    length = file.Length;
                    if (file.IsReadOnly)
                        file.IsReadOnly = false;
                    file.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.SkippedFiles++;
                    continue;
                }

                result.DeletedFiles++;
                result.FreedBytes += length;
                if (result.DeletedFiles % 250 == 0)
                    progress?.Report(new CleanProgress(rule.Name, result.DeletedFiles, result.FreedBytes));
            }

            if (Directory.Exists(path) && rule.Include is not { Count: > 0 })
            {
                foreach (var dir in CandidateFiles.DirectoriesDeepestFirst(path))
                    TryDeleteEmptyDirectory(dir, cutoff);
                if (rule.Mode == CleanMode.Self)
                    TryDeleteEmptyDirectory(new DirectoryInfo(path), DateTime.MaxValue);
            }
        }
        progress?.Report(new CleanProgress(rule.Name, result.DeletedFiles, result.FreedBytes));
    }

    private static void TryDeleteEmptyDirectory(DirectoryInfo dir, DateTime cutoff)
    {
        try
        {
            if (cutoff != DateTime.MaxValue && dir.LastWriteTimeUtc >= cutoff)
                return;
            if (!dir.EnumerateFileSystemInfos().Any())
                dir.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use or not empty; leave it.
        }
    }

    private static CleanResult Fail(CleanResult result, string message)
    {
        result.Succeeded = false;
        result.Message = message;
        return result;
    }

    private static string LastLines(string text, int count) =>
        string.Join(Environment.NewLine, text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(count));
}
