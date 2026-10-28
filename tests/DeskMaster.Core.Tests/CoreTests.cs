using DeskMaster.Core.Cleanup;
using DeskMaster.Core.Formatting;
using DeskMaster.Core.IO;
using DeskMaster.Core.Platform;
using DeskMaster.Core.Relocation;
using DeskMaster.Core.Scanning;
using DeskMaster.Core.Visualization;

namespace DeskMaster.Core.Tests;

public class ByteSizeTests
{
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(10L * 1024 * 1024, "10.0 MB")]
    [InlineData(150L * 1024 * 1024 * 1024, "150 GB")]
    public void Formats(long bytes, string expected) => Assert.Equal(expected, ByteSize.Format(bytes));
}

public class PathResolverTests
{
    [Fact]
    public void Expands_tokens_and_globs()
    {
        using var dir = new TempDir();
        dir.File("profiles/a/cache/x.bin", 10);
        dir.File("profiles/b/cache/y.bin", 10);
        dir.File("profiles/c/other/z.bin", 10);
        var tokens = new Dictionary<string, string> { ["Root"] = dir.Path };

        var matches = PathResolver.Resolve("{Root}/profiles/*/cache", tokens);

        Assert.Equal(2, matches.Count);
        Assert.All(matches, m => Assert.EndsWith("cache", m));
    }

    [Fact]
    public void Unknown_token_resolves_to_nothing()
    {
        Assert.Null(PathResolver.Expand("{NoSuchToken}/x", new Dictionary<string, string>()));
        Assert.Empty(PathResolver.Resolve("{NoSuchToken}/x", new Dictionary<string, string>()));
    }

    [Fact]
    public void Current_os_tokens_include_home_and_cache()
    {
        Assert.True(PathResolver.CurrentTokens.ContainsKey("Home"));
        Assert.True(PathResolver.CurrentTokens.ContainsKey("Cache"));
    }
}

public class SafetyGuardTests
{
    [Fact]
    public void Refuses_roots_home_and_system_folders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(SafetyGuard.IsSafeToClean(Path.GetPathRoot(home)!, out _));
        Assert.False(SafetyGuard.IsSafeToClean(home, out _));
        Assert.False(SafetyGuard.IsSafeToClean(Path.Combine(home, "Downloads"), out _));
        Assert.False(SafetyGuard.IsSafeToClean("relative/path", out _));
        if (OperatingSystem.IsWindows())
            Assert.False(SafetyGuard.IsSafeToClean(Environment.GetFolderPath(Environment.SpecialFolder.Windows), out _));
    }

    [Fact]
    public void Allows_cache_folders()
    {
        using var dir = new TempDir();
        Assert.True(SafetyGuard.IsSafeToClean(dir.Path, out var reason), reason);
    }
}

public class RuleCatalogTests
{
    [Fact]
    public void Built_in_rules_parse_with_unique_ids()
    {
        var rules = RuleCatalog.LoadBuiltIn();
        Assert.True(rules.Count > 30);
        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rules, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Name));
            Assert.False(string.IsNullOrWhiteSpace(r.Description));
            Assert.True(r.Paths.Count > 0 || r.Command is not null || r.Handler is not null, r.Id);
        });
    }

    [Fact]
    public void Every_rule_applies_to_at_least_one_os()
    {
        foreach (var rule in RuleCatalog.LoadBuiltIn())
            Assert.True(rule.AppliesTo(OsKind.Windows) || rule.AppliesTo(OsKind.MacOS) || rule.AppliesTo(OsKind.Linux), rule.Id);
    }

    [Fact]
    public void Relocations_parse()
    {
        var defs = RelocationService.LoadDefinitions();
        Assert.Contains(defs, d => d.EnvVar == "OLLAMA_MODELS");
    }
}

public class CleanerTests
{
    private static CleanupRule RuleFor(string path, int minAgeHours = 0, CleanMode mode = CleanMode.Contents, List<string>? include = null) => new()
    {
        Id = "test",
        Name = "Test",
        Description = "Test rule",
        Paths = new() { ["all"] = [path.Replace('\\', '/')] },
        MinAgeHours = minAgeHours,
        Mode = mode,
        Include = include,
    };

    [Fact]
    public async Task Deletes_only_files_older_than_min_age()
    {
        using var dir = new TempDir();
        var oldFile = dir.File("cache/old.tmp", 1000, TimeSpan.FromDays(3));
        var newFile = dir.File("cache/new.tmp", 1000);
        var rule = RuleFor(Path.Combine(dir.Path, "cache"), minAgeHours: 24);

        var finding = RuleAnalyzer.Analyze(rule);
        Assert.Equal(1000, finding.Bytes);
        Assert.Equal(FindingStatus.Ready, finding.Status);

        var result = await Cleaner.CleanAsync(finding);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1000, result.FreedBytes);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
        Assert.True(Directory.Exists(Path.Combine(dir.Path, "cache")));
    }

    [Fact]
    public async Task Include_patterns_limit_deletion()
    {
        using var dir = new TempDir();
        dir.File("x/thumbcache_1.db", 10);
        var keep = dir.File("x/settings.ini", 10);
        var rule = RuleFor(Path.Combine(dir.Path, "x"), include: ["thumbcache_*.db"]);

        var result = await Cleaner.CleanAsync(RuleAnalyzer.Analyze(rule));

        Assert.Equal(1, result.DeletedFiles);
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public async Task Self_mode_removes_the_folder()
    {
        using var dir = new TempDir();
        dir.File("leftover/sub/a.bin", 10);
        var target = Path.Combine(dir.Path, "leftover");

        await Cleaner.CleanAsync(RuleAnalyzer.Analyze(RuleFor(target, mode: CleanMode.Self)));

        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public async Task Never_cleans_protected_folders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var rule = RuleFor(Path.Combine(home, "Downloads"));
        var finding = new RuleFinding { Rule = rule, Paths = [Path.Combine(home, "Downloads")], Status = FindingStatus.Ready };

        var result = await Cleaner.CleanAsync(finding);

        Assert.Equal(0, result.DeletedFiles);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task Self_mode_files_respect_min_age()
    {
        using var dir = new TempDir();
        var stale = dir.File("dl/old.crdownload", 10, TimeSpan.FromDays(10));
        var active = dir.File("dl/new.crdownload", 10);
        var rule = new CleanupRule
        {
            Id = "t", Name = "t", Description = "t", Mode = CleanMode.Self, MinAgeHours = 72,
            Paths = new() { ["all"] = [Path.Combine(dir.Path, "dl").Replace('\\', '/') + "/*.crdownload"] },
        };

        var result = await Cleaner.CleanAsync(RuleAnalyzer.Analyze(rule));

        Assert.Equal(1, result.DeletedFiles);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(active));
    }

    [Fact]
    public void Empty_folder_reports_nothing_to_clean()
    {
        using var dir = new TempDir();
        Assert.Equal(FindingStatus.NothingToClean, RuleAnalyzer.Analyze(RuleFor(dir.Path)).Status);
    }
}

public class ScannerTests
{
    [Fact]
    public async Task Sums_sizes_and_folds_small_files()
    {
        using var dir = new TempDir();
        dir.File("a/big.bin", 5000);
        for (var i = 0; i < 5; i++)
            dir.File($"a/small{i}.bin", 10);
        dir.File("b/c/deep.bin", 300);

        var result = await DiskScanner.ScanAsync(dir.Path, options: new ScanOptions { FilesKeptPerFolder = 2 });

        Assert.Equal(5350, result.Root.Size);
        Assert.Equal(7, result.Root.FileCount);
        var a = result.Root.Children.Single(c => c.Name == "a");
        Assert.Equal(5050, a.Size);
        Assert.Equal(3, a.Children.Count); // 2 kept files + 1 "smaller files" entry
        Assert.Contains(a.Children, c => c.Kind == ScanNodeKind.SmallFiles && c.FileCount == 4);
        Assert.Equal("big.bin", result.LargestFiles[0].Name);
        Assert.Equal("a", result.Root.Children[0].Name); // sorted by size
    }
}

public class TreemapTests
{
    [Fact]
    public void Tiles_fill_the_area_proportionally()
    {
        var weights = new[] { 6.0, 6, 4, 3, 2, 2, 1 };
        var bounds = new TreemapRect(0, 0, 600, 400);

        var tiles = Treemap.Layout(weights, w => w, bounds);

        Assert.Equal(weights.Length, tiles.Count);
        Assert.Equal(bounds.Area, tiles.Sum(t => t.Bounds.Area), 3);
        foreach (var tile in tiles)
        {
            Assert.Equal(bounds.Area * tile.Item / weights.Sum(), tile.Bounds.Area, 3);
            Assert.True(tile.Bounds.X >= -0.001 && tile.Bounds.Y >= -0.001);
            Assert.True(tile.Bounds.X + tile.Bounds.Width <= 600.001 && tile.Bounds.Y + tile.Bounds.Height <= 400.001);
        }
    }

    [Fact]
    public void Ignores_zero_weights()
    {
        Assert.Empty(Treemap.Layout(new[] { 0.0, 0 }, w => w, new TreemapRect(0, 0, 10, 10)));
    }
}

public class FolderMoverTests
{
    [Fact]
    public async Task Moves_without_overwriting_and_skips_partial_downloads()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        src.File("movie.mp4", 100);
        src.File("report.pdf", 20);
        src.File("Unconfirmed 1.crdownload", 5);
        src.File("folder/inner.txt", 5);
        var existing = dst.File("report.pdf", 999);

        var result = await FolderMover.MoveContentsAsync(src.Path, dst.Path);

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal(2, result.Moved);
        Assert.Equal(1, result.Duplicates);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(999, new FileInfo(existing).Length);
        Assert.True(File.Exists(Path.Combine(dst.Path, "movie.mp4")));
        Assert.True(File.Exists(Path.Combine(dst.Path, "folder", "inner.txt")));
        Assert.True(File.Exists(Path.Combine(result.DuplicatesFolder, "report.pdf")));
        Assert.True(File.Exists(Path.Combine(src.Path, "Unconfirmed 1.crdownload")));
    }

    [Fact]
    public void Plan_separates_movable_files_from_unfinished_downloads()
    {
        using var src = new TempDir();
        src.File("movie.mp4", 100);
        src.File("folder/inner.txt", 5);
        src.File("Unconfirmed 1.crdownload", 50);
        src.File("desktop.ini", 1);

        var plan = FolderMover.Plan(src.Path);

        Assert.Equal(105, plan.MovableBytes);
        Assert.Equal(2, plan.MovableItems);
        Assert.Equal(51, plan.LeftBytes);
        Assert.Equal(2, plan.LeftItems);
    }

    [Fact]
    public async Task MoveDirectory_copies_then_removes_source()
    {
        using var src = new TempDir();
        using var dst = new TempDir();
        src.File("models/blobs/a", 1234);
        var target = Path.Combine(dst.Path, "ollama", "models");

        await FileOps.MoveDirectoryAsync(Path.Combine(src.Path, "models"), target);

        Assert.False(Directory.Exists(Path.Combine(src.Path, "models")));
        Assert.Equal(1234, new FileInfo(Path.Combine(target, "blobs", "a")).Length);
    }
}
