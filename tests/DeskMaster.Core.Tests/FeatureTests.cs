using System.Runtime.InteropServices;
using DeskMaster.Core.Cleanup;
using DeskMaster.Core.Drives;
using DeskMaster.Core.Duplicates;
using DeskMaster.Core.IO;
using DeskMaster.Core.Localization;
using DeskMaster.Core.Monitoring;
using DeskMaster.Core.Platform;
using DeskMaster.Core.Relocation;
using DeskMaster.Core.Scanning;
using DeskMaster.Core.Settings;

namespace DeskMaster.Core.Tests;

public class VerifiedMoveTests
{
    [Fact]
    public void CopyFileVerified_copies_content_and_timestamp()
    {
        using var dir = new TempDir();
        var source = dir.File("a.bin", 3 * 1024 * 1024, TimeSpan.FromDays(2));
        File.WriteAllBytes(source, Enumerable.Range(0, 3 * 1024 * 1024).Select(i => (byte)(i * 7)).ToArray());
        var target = Path.Combine(dir.Path, "b.bin");

        FileOps.CopyFileVerified(source, target);

        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(target));
        Assert.Equal(File.GetLastWriteTimeUtc(source), File.GetLastWriteTimeUtc(target));
    }

    [Fact]
    public async Task Cancelled_move_keeps_the_source_and_removes_the_partial_copy()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        for (var i = 0; i < 20; i++)
            src.File($"models/blob{i}", 512 * 1024);
        var source = Path.Combine(src.Path, "models");
        var target = Path.Combine(dst.Path, "moved");
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<CopyProgress>(p =>
        {
            if (p.CopiedBytes > 2 * 1024 * 1024)
                cts.Cancel();
        });

        // Same-drive moves are an instant rename; exercise the cross-drive copy path directly.
        Assert.ThrowsAny<OperationCanceledException>(() => FileOps.CopyThenDelete(source, target, progress, cts.Token));
        await Task.CompletedTask;

        Assert.Equal(20, Directory.GetFiles(source).Length);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task Leftover_from_an_interrupted_move_is_replaced_on_retry()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        src.File("data/x.bin", 1000);
        var target = Path.Combine(dst.Path, "data");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, FileOps.PartialMarker), "");
        File.WriteAllText(Path.Combine(target, "half-copied.tmp"), "junk");

        await FileOps.MoveDirectoryAsync(Path.Combine(src.Path, "data"), target);

        Assert.True(File.Exists(Path.Combine(target, "x.bin")));
        Assert.False(File.Exists(Path.Combine(target, "half-copied.tmp")));
        Assert.False(File.Exists(Path.Combine(target, FileOps.PartialMarker)));
    }

    [Fact]
    public async Task Refuses_to_merge_into_a_folder_that_is_not_ours()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        src.File("data/x.bin", 10);
        dst.File("data/someone-elses.txt", 10);

        await Assert.ThrowsAsync<IOException>(() => FileOps.MoveDirectoryAsync(Path.Combine(src.Path, "data"), Path.Combine(dst.Path, "data")));
        Assert.True(File.Exists(Path.Combine(src.Path, "data", "x.bin")));
    }

    [Fact]
    public async Task Relocation_moves_a_folder_without_env_var()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        src.File("models/m.bin", 4096);
        var candidate = new RelocationCandidate
        {
            Definition = new RelocationDefinition { Id = "t", Name = "Test", EnvVar = "", TargetFolderName = "app/models" },
            CurrentPath = Path.Combine(src.Path, "models"),
            Bytes = 4096,
        };

        var result = await RelocationService.MoveAsync(candidate, dst.Path);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(Path.Combine(dst.Path, "app", "models", "m.bin")));
        Assert.False(Directory.Exists(candidate.CurrentPath));
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public class CommandRuleTests
{
    private static CommandSpec Command(bool succeed) => OperatingSystem.IsWindows()
        ? new CommandSpec { File = "cmd.exe", Args = ["/c", succeed ? "exit 0" : "exit 3"] }
        : new CommandSpec { File = "sh", Args = ["-c", succeed ? "true" : "exit 3"] };

    [Fact]
    public async Task Successful_command_is_used_instead_of_deleting()
    {
        using var dir = new TempDir();
        var file = dir.File("cache/a.bin", 100);
        var rule = new CleanupRule { Id = "c", Name = "c", Description = "c", Command = Command(true), Paths = new() { ["all"] = [Path.Combine(dir.Path, "cache").Replace('\\', '/')] } };

        var result = await Cleaner.CleanAsync(RuleAnalyzer.Analyze(rule));

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(file)); // the (dummy) command did the cleaning, not DeskMaster
    }

    [Fact]
    public async Task Failed_command_falls_back_to_deleting_the_cache()
    {
        using var dir = new TempDir();
        var file = dir.File("cache/a.bin", 100);
        var rule = new CleanupRule { Id = "c", Name = "c", Description = "c", Command = Command(false), Paths = new() { ["all"] = [Path.Combine(dir.Path, "cache").Replace('\\', '/')] } };

        var result = await Cleaner.CleanAsync(RuleAnalyzer.Analyze(rule));

        Assert.False(File.Exists(file));
        Assert.Equal(100, result.FreedBytes);
    }

    [Fact]
    public void Missing_tool_marks_command_only_rule_unavailable()
    {
        var rule = new CleanupRule { Id = "c", Name = "c", Description = "c", Platforms = ["all"], Command = new CommandSpec { File = "definitely-not-a-real-tool-42" } };
        Assert.Equal(FindingStatus.Unavailable, RuleAnalyzer.Analyze(rule).Status);
    }
}

public class DeleteTests
{
    [Fact]
    public void Explorer_delete_guard_blocks_system_and_user_folders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(SafetyGuard.IsSafeToDelete(home, out _));
        Assert.False(SafetyGuard.IsSafeToDelete(Path.GetPathRoot(home)!, out _));
        if (OperatingSystem.IsWindows())
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Assert.False(SafetyGuard.IsSafeToDelete(Path.Combine(windows, "System32", "kernel32.dll"), out _));
            Assert.False(SafetyGuard.IsSafeToDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "App", "x.exe"), out _));
        }
        else
        {
            Assert.False(SafetyGuard.IsSafeToDelete("/usr/bin/ls", out _));
        }
    }

    [Fact]
    public async Task Permanent_delete_removes_a_folder_tree()
    {
        using var dir = new TempDir();
        dir.File("old/a/b.bin", 10);
        var target = Path.Combine(dir.Path, "old");

        var result = await Trash.DeletePermanentlyAsync(target);

        Assert.True(result.Succeeded, result.Message);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task Trash_refuses_protected_folders()
    {
        var result = await Trash.MoveToTrashAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Detach_shrinks_every_parent()
    {
        using var dir = new TempDir();
        dir.File("a/b/big.bin", 1000);
        dir.File("a/small.bin", 10);
        var scan = await DiskScanner.ScanAsync(dir.Path);
        var a = scan.Root.Children.Single(c => c.Name == "a");
        var b = a.Children.Single(c => c.Name == "b");

        b.Detach();

        Assert.Equal(10, a.Size);
        Assert.Equal(10, scan.Root.Size);
        Assert.Equal(1, scan.Root.FileCount);
    }
}

public class DuplicateFinderTests
{
    [Fact]
    public async Task Finds_identical_files_only()
    {
        using var dir = new TempDir();
        var content = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        var other = content.ToArray();
        other[150_000] ^= 0xFF; // same size, different content in the middle
        File.WriteAllBytes(dir.File("a/photo.jpg", 1), content);
        File.WriteAllBytes(dir.File("b/photo copy.jpg", 1), content);
        File.WriteAllBytes(dir.File("c/other.jpg", 1), other);
        dir.File("tiny1.txt", 10);
        dir.File("tiny2.txt", 10);

        var groups = await DuplicateFinder.FindAsync([dir.Path], new DuplicateOptions { MinSize = 1024, SkipSystemFolders = false });

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Equal(content.Length, group.WastedBytes);
        Assert.DoesNotContain(group.Files, f => f.Path.EndsWith("other.jpg", StringComparison.Ordinal));
    }
}

public partial class HardLinkTests
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string newFile, string existingFile, IntPtr security);

    [Fact]
    public async Task Hard_links_are_counted_once()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var dir = new TempDir();
        var original = dir.File("a/data.bin", 50_000);
        Directory.CreateDirectory(Path.Combine(dir.Path, "b"));
        Assert.True(CreateHardLink(Path.Combine(dir.Path, "b", "same.bin"), original, IntPtr.Zero));

        var withLinks = await DiskScanner.ScanAsync(dir.Path, options: new ScanOptions { HardLinkFolders = [dir.Path] });
        var naive = await DiskScanner.ScanAsync(dir.Path, options: new ScanOptions { HardLinkFolders = [] });

        Assert.Equal(50_000, withLinks.Root.Size);
        Assert.Equal(100_000, naive.Root.Size);
    }
}

public class BackgroundTests
{
    private static DriveSummary Drive(long freeGb, long totalGb = 500) =>
        new("C:\\", "Test", "NTFS", totalGb << 30, freeGb << 30, IsSystem: true, IsRemovable: false);

    [Fact]
    public void Low_space_alert_respects_thresholds_and_cooldown()
    {
        var settings = new AppSettings { LowSpaceAlerts = true, LowSpacePercent = 10, LowSpaceGb = 15 };
        var monitor = new LowSpaceMonitor(TimeSpan.FromHours(6));
        var now = DateTime.UtcNow;

        Assert.Empty(monitor.Check([Drive(100)], settings, now));
        Assert.Single(monitor.Check([Drive(8)], settings, now));
        Assert.Empty(monitor.Check([Drive(8)], settings, now.AddHours(1)));      // cooldown
        Assert.Single(monitor.Check([Drive(8)], settings, now.AddHours(7)));     // reminder
        Assert.Empty(monitor.Check([Drive(8)], new AppSettings { LowSpaceAlerts = false }, now.AddDays(1)));
    }

    [Fact]
    public void Auto_clean_is_due_only_when_enabled_and_old_enough()
    {
        var now = DateTime.UtcNow;
        Assert.False(AutoCleaner.IsDue(new AppSettings { AutoClean = false }, now));
        Assert.True(AutoCleaner.IsDue(new AppSettings { AutoClean = true }, now));
        Assert.False(AutoCleaner.IsDue(new AppSettings { AutoClean = true, AutoCleanDays = 7, LastAutoCleanUtc = now.AddDays(-2) }, now));
        Assert.True(AutoCleaner.IsDue(new AppSettings { AutoClean = true, AutoCleanDays = 7, LastAutoCleanUtc = now.AddDays(-8) }, now));
    }

    [Fact]
    public void Activity_log_survives_a_restart()
    {
        using var dir = new TempDir();
        var store = new ActivityLogStore(dir.Path);
        store.Append(new StoredLogEntry(new DateTime(2026, 10, 1, 9, 0, 0), "first", false));
        store.Append(new StoredLogEntry(new DateTime(2026, 10, 2, 9, 0, 0), "second\nline", true));

        var loaded = new ActivityLogStore(dir.Path).LoadRecent();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("second line", loaded[0].Message);
        Assert.True(loaded[0].IsError);
        Assert.Equal("first", loaded[1].Message);
    }

    [Fact]
    public void Old_settings_folder_is_copied_once_after_the_rename()
    {
        using var dir = new TempDir();
        var legacy = Path.Combine(dir.Path, "DiskMaster");
        var target = Path.Combine(dir.Path, "DeskMaster");
        dir.File("DiskMaster/settings.json", 10);
        dir.File("DiskMaster/logs/activity-2026-10.log", 20);

        AppSettings.MigrateLegacyFolder(legacy, target);
        File.WriteAllText(Path.Combine(target, "settings.json"), "new");
        AppSettings.MigrateLegacyFolder(legacy, target); // second run must not overwrite

        Assert.True(File.Exists(Path.Combine(target, "logs", "activity-2026-10.log")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "settings.json")));
    }

    [Fact]
    public void Text_formats_with_the_translator()
    {
        var before = Text.Translator;
        try
        {
            Text.Translator = s => s == "Freed {0}." ? "تم تحرير {0}." : s;
            Assert.Equal("تم تحرير 5 GB.", Text.F("Freed {0}.", "5 GB"));
        }
        finally
        {
            Text.Translator = before;
        }
    }
}
