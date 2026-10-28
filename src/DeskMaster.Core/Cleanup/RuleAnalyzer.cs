using static DeskMaster.Core.Localization.Text;
using DeskMaster.Core.Platform;
using DeskMaster.Core.Processes;

namespace DeskMaster.Core.Cleanup;

public enum FindingStatus
{
    Ready,
    NothingToClean,
    NeedsAdmin,
    AppRunning,
    Unavailable,
}

/// <summary>What one rule found on this machine.</summary>
public sealed class RuleFinding
{
    public required CleanupRule Rule { get; init; }
    public IReadOnlyList<string> Paths { get; init; } = [];
    public long Bytes { get; init; }
    public long Files { get; init; }

    /// <summary>False for commands like DISM or docker prune whose gain is unknown up front.</summary>
    public bool SizeKnown { get; init; } = true;

    public FindingStatus Status { get; init; }
    public IReadOnlyList<string> RunningApps { get; init; } = [];
    public string? CommandPath { get; init; }

    public bool CanClean => Status == FindingStatus.Ready;

    public string StatusText => Status switch
    {
        FindingStatus.Ready => SizeKnown ? T("Ready") : T("Ready (size unknown)"),
        FindingStatus.NothingToClean => T("Nothing to clean"),
        FindingStatus.NeedsAdmin => T("Needs administrator"),
        FindingStatus.AppRunning => F("Close {0} first", ProcessGuard.FriendlyList(RunningApps)),
        FindingStatus.Unavailable => Rule.Command is not null ? F("'{0}' not installed", Rule.Command.File) : T("Not available"),
        _ => "",
    };
}

public sealed record AnalysisProgress(int Completed, int Total, string RuleName);

/// <summary>Measures every rule without deleting anything.</summary>
public static class RuleAnalyzer
{
    public static async Task<IReadOnlyList<RuleFinding>> AnalyzeAsync(
        IEnumerable<CleanupRule> rules,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var list = rules.ToList();
        var results = new RuleFinding[list.Count];
        var completed = 0;

        await Parallel.ForEachAsync(Enumerable.Range(0, list.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            (i, ct) =>
            {
                results[i] = Analyze(list[i], ct);
                progress?.Report(new AnalysisProgress(Interlocked.Increment(ref completed), list.Count, list[i].Name));
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

        return results;
    }

    public static RuleFinding Analyze(CleanupRule rule, CancellationToken cancellationToken = default)
    {
        var paths = PathResolver.ResolveForCurrentOs(rule.Paths);
        long bytes = 0, files = 0;
        var sizeKnown = true;

        if (rule.Handler == "recycle-bin" && OperatingSystem.IsWindows())
        {
            (bytes, files) = WindowsRecycleBin.Query();
        }
        else
        {
            (bytes, files) = Measure(rule, paths, cancellationToken);
        }

        // Compacting a disk image frees an unknown part of it.
        if (rule.Handler == "wsl-compact")
            sizeKnown = paths.Count == 0;

        var commandOnly = rule.Command is not null && !PathResolver.TemplatesForCurrentOs(rule.Paths).Any();
        if (commandOnly)
            sizeKnown = false;

        string? commandPath = rule.Command is null ? null : CommandRunner.FindExecutable(rule.Command.File);
        var commandRequired = rule.Command is not null && (rule.EstimateOnly || commandOnly);
        var running = ProcessGuard.FindRunning(rule.CloseProcesses);

        FindingStatus status;
        if (commandRequired && commandPath is null)
            status = FindingStatus.Unavailable;
        else if (sizeKnown && bytes == 0 && files == 0)
            status = FindingStatus.NothingToClean;
        else if (rule.RequiresAdmin && !PlatformInfo.IsElevated)
            status = FindingStatus.NeedsAdmin;
        else if (running.Count > 0)
            status = FindingStatus.AppRunning;
        else
            status = FindingStatus.Ready;

        return new RuleFinding
        {
            Rule = rule,
            Paths = paths,
            Bytes = bytes,
            Files = files,
            SizeKnown = sizeKnown,
            Status = status,
            RunningApps = running,
            CommandPath = commandPath,
        };
    }

    internal static (long Bytes, long Files) Measure(CleanupRule rule, IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        long bytes = 0, files = 0;
        var now = DateTime.UtcNow;
        // Size estimates ignore age filters for estimate-only paths (e.g. hiberfil.sys).
        var measureRule = rule.EstimateOnly ? new CleanupRule { Mode = CleanMode.Self } : rule;
        foreach (var path in paths)
        {
            foreach (var file in CandidateFiles.Enumerate(path, measureRule, now))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    bytes += file.Length;
                    files++;
                }
                catch (IOException)
                {
                    // File vanished while measuring.
                }
            }
        }
        return (bytes, files);
    }
}
