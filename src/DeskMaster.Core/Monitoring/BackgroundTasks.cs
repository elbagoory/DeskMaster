using static DeskMaster.Core.Localization.Text;
using DeskMaster.Core.Cleanup;
using DeskMaster.Core.Drives;
using DeskMaster.Core.Settings;

namespace DeskMaster.Core.Monitoring;

public sealed record LowSpaceAlert(DriveSummary Drive, string Message);

/// <summary>Decides when a drive is low enough to warn about, without nagging.</summary>
public sealed class LowSpaceMonitor(TimeSpan? cooldown = null)
{
    private readonly TimeSpan _cooldown = cooldown ?? TimeSpan.FromHours(6);
    private readonly Dictionary<string, DateTime> _lastAlert = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsLow(DriveSummary drive, int percent, int gigabytes) =>
        drive.TotalBytes > 0 &&
        (drive.FreeBytes < drive.TotalBytes * (percent / 100.0) || drive.FreeBytes < gigabytes * 1024L * 1024 * 1024);

    /// <summary>Drives that are low now and were not reported within the cooldown.</summary>
    public IReadOnlyList<LowSpaceAlert> Check(IEnumerable<DriveSummary> drives, AppSettings settings, DateTime nowUtc)
    {
        var alerts = new List<LowSpaceAlert>();
        if (!settings.LowSpaceAlerts)
            return alerts;

        foreach (var drive in drives.Where(d => !d.IsRemovable))
        {
            if (!IsLow(drive, settings.LowSpacePercent, settings.LowSpaceGb))
            {
                _lastAlert.Remove(drive.RootPath); // Recovered: alert again next time it gets low.
                continue;
            }
            if (_lastAlert.TryGetValue(drive.RootPath, out var last) && nowUtc - last < _cooldown)
                continue;
            _lastAlert[drive.RootPath] = nowUtc;
            alerts.Add(new LowSpaceAlert(drive,
                F("{0} is almost full: {1} free of {2}.", drive.DisplayName, Formatting.ByteSize.Format(drive.FreeBytes), Formatting.ByteSize.Format(drive.TotalBytes))));
        }
        return alerts;
    }
}

public sealed record AutoCleanResult(long FreedBytes, int Cleaned, int Failed);

/// <summary>Scheduled cleanup: only rules marked safe that need no admin and no closed apps.</summary>
public static class AutoCleaner
{
    public static bool IsDue(AppSettings settings, DateTime nowUtc) =>
        settings.AutoClean &&
        (settings.LastAutoCleanUtc is null || nowUtc - settings.LastAutoCleanUtc.Value >= TimeSpan.FromDays(Math.Max(1, settings.AutoCleanDays)));

    public static async Task<AutoCleanResult> RunAsync(IEnumerable<CleanupRule> rules, CancellationToken ct = default)
    {
        var findings = await RuleAnalyzer.AnalyzeAsync(rules.Where(r => r.IsRecommended), null, ct).ConfigureAwait(false);
        long freed = 0;
        int cleaned = 0, failed = 0;
        foreach (var finding in findings.Where(f => f.CanClean && f.SizeKnown))
        {
            var result = await Cleaner.CleanAsync(finding, null, ct).ConfigureAwait(false);
            freed += result.FreedBytes;
            if (result.Succeeded)
                cleaned++;
            else
                failed++;
        }
        return new AutoCleanResult(freed, cleaned, failed);
    }
}
