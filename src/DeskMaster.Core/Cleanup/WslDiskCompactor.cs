using static DeskMaster.Core.Localization.Text;
using System.Runtime.Versioning;
using DeskMaster.Core.Processes;

namespace DeskMaster.Core.Cleanup;

/// <summary>
/// Shrinks WSL 2 / Docker Desktop virtual disks (*.vhdx). They grow as Linux writes data but never
/// give space back on their own, even after files are deleted inside Linux.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WslDiskCompactor
{
    public static async Task<CleanResult> CompactAsync(RuleFinding finding, IProgress<CleanProgress>? progress, CancellationToken ct)
    {
        var result = new CleanResult { Rule = finding.Rule };
        var disks = finding.Paths.Where(p => p.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) && File.Exists(p)).ToList();
        if (disks.Count == 0)
        {
            result.Succeeded = false;
            result.Message = T("No WSL or Docker disk images found.");
            return result;
        }

        var wsl = CommandRunner.FindExecutable("wsl.exe");
        if (wsl is not null)
        {
            progress?.Report(new CleanProgress(finding.Rule.Name + ": " + T("stopping WSL"), 0, 0));
            await CommandRunner.RunAsync(wsl, ["--shutdown"], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false); // let the VM release the files
        }

        var diskpart = CommandRunner.FindExecutable("diskpart.exe");
        if (diskpart is null)
        {
            result.Succeeded = false;
            result.Message = T("diskpart.exe was not found.");
            return result;
        }

        var done = 0;
        foreach (var disk in disks)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new CleanProgress(finding.Rule.Name + ": " + F("compacting {0}", Path.GetFileName(disk)), done, result.FreedBytes));
            var before = new FileInfo(disk).Length;
            var script = Path.Combine(Path.GetTempPath(), $"deskmaster-compact-{Guid.NewGuid():N}.txt");
            await File.WriteAllLinesAsync(script,
            [
                $"select vdisk file=\"{disk}\"",
                "attach vdisk readonly",
                "compact vdisk",
                "detach vdisk",
            ], ct).ConfigureAwait(false);

            try
            {
                var run = await CommandRunner.RunAsync(diskpart, ["/s", script], TimeSpan.FromMinutes(60), ct).ConfigureAwait(false);
                if (!run.Succeeded)
                {
                    result.AddError($"{Path.GetFileName(disk)}: {run.Output.Trim().Split('\n').LastOrDefault()?.Trim()}");
                    // Make sure the disk is not left attached.
                    await File.WriteAllLinesAsync(script, [$"select vdisk file=\"{disk}\"", "detach vdisk"], ct).ConfigureAwait(false);
                    await CommandRunner.RunAsync(diskpart, ["/s", script], TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false);
                    continue;
                }
            }
            finally
            {
                File.Delete(script);
            }

            result.FreedBytes += Math.Max(0, before - new FileInfo(disk).Length);
            done++;
        }

        result.DeletedFiles = 0;
        result.Succeeded = result.Errors.Count == 0;
        result.Message = F("Compacted {0} of {1} disk images.", done, disks.Count);
        return result;
    }
}
