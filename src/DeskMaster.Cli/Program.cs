using DeskMaster.Core.Cleanup;
using DeskMaster.Core.Drives;
using DeskMaster.Core.Formatting;
using DeskMaster.Core.Platform;
using DeskMaster.Core.Relocation;
using DeskMaster.Core.Scanning;

namespace DeskMaster.Cli;

internal static class Program
{
    private const string Usage = """
        DeskMaster CLI - find what fills your disk and clean it safely.

        Usage:
          deskmaster-cli drives                          Show drives and free space
          deskmaster-cli analyze [--all]                 Measure cleanup items (deletes nothing)
          deskmaster-cli clean [--ids a,b] [--include-caution] [--dry-run] [--yes]
                                                         Clean recommended items (or the given ids)
          deskmaster-cli scan <path> [--top 20]          Largest folders and files under a path
          deskmaster-cli movable                         Big app folders that can live on another drive
          deskmaster-cli move <id> <destination> [--yes] Move one of them (e.g. move ollama-models D:\)
          deskmaster-cli move-folder downloads <destination> [--set-default] [--yes]
                                                         Move a user folder's files (downloads, videos, pictures, music)
          deskmaster-cli duplicates <path> [--min-mb 1]  Find files with identical content
          deskmaster-cli rules                           List every cleanup rule for this OS

        Items that need administrator rights are skipped unless you run elevated (sudo / admin terminal).
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return args[0] switch
            {
                "drives" => Drives(),
                "analyze" => await Analyze(args, cts.Token),
                "clean" => await Clean(args, cts.Token),
                "scan" => await Scan(args, cts.Token),
                "movable" => await Movable(cts.Token),
                "move" => await Move(args, cts.Token),
                "move-folder" => await MoveFolder(args, cts.Token),
                "duplicates" => await Duplicates(args, cts.Token),
                "rules" => Rules(),
                _ => Unknown(args[0]),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.\n");
        Console.WriteLine(Usage);
        return 2;
    }

    private static int Drives()
    {
        foreach (var d in DriveService.GetDrives())
        {
            var bar = Bar(d.UsedFraction, 24);
            var flag = d.IsLow ? "  LOW" : "";
            Console.WriteLine($"{d.DisplayName,-28} {bar} {ByteSize.Format(d.FreeBytes),10} free of {ByteSize.Format(d.TotalBytes)}{flag}");
        }
        return 0;
    }

    private static async Task<IReadOnlyList<RuleFinding>> AnalyzeAll(CancellationToken ct)
    {
        var warnings = new List<string>();
        var rules = RuleCatalog.LoadForCurrentOs(warnings);
        foreach (var w in warnings)
            Console.Error.WriteLine("warning: " + w);

        var progress = new Progress<AnalysisProgress>(p =>
        {
            if (!Console.IsOutputRedirected)
                Console.Write($"\rMeasuring {p.Completed}/{p.Total}...".PadRight(40));
        });
        var findings = await RuleAnalyzer.AnalyzeAsync(rules, progress, ct);
        if (!Console.IsOutputRedirected)
            Console.Write("\r".PadRight(41) + "\r");
        return findings;
    }

    private static async Task<int> Analyze(string[] args, CancellationToken ct)
    {
        var showAll = args.Contains("--all");
        var findings = await AnalyzeAll(ct);
        var shown = findings
            .Where(f => showAll || f.Status != FindingStatus.NothingToClean && f.Status != FindingStatus.Unavailable)
            .OrderByDescending(f => f.Bytes);

        Console.WriteLine($"{"ID",-28} {"SIZE",10}  {"SAFETY",-8} STATUS");
        foreach (var f in shown)
        {
            var size = f.SizeKnown ? ByteSize.Format(f.Bytes) : "?";
            Console.WriteLine($"{f.Rule.Id,-28} {size,10}  {f.Rule.Safety,-8} {f.StatusText}");
        }

        var ready = findings.Where(f => f.CanClean && f.Rule.IsRecommended).Sum(f => f.Bytes);
        Console.WriteLine($"\nRecommended and ready to clean: {ByteSize.Format(ready)}. Run 'deskmaster-cli clean' to free it.");
        if (findings.Any(f => f.Status == FindingStatus.NeedsAdmin))
            Console.WriteLine(PlatformInfo.ElevationHint);
        return 0;
    }

    private static async Task<int> Clean(string[] args, CancellationToken ct)
    {
        var ids = Option(args, "--ids")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var includeCaution = args.Contains("--include-caution");
        var dryRun = args.Contains("--dry-run");
        var yes = args.Contains("--yes") || args.Contains("-y");

        var findings = await AnalyzeAll(ct);
        var selected = findings.Where(f => f.CanClean && (ids is not null
            ? ids.Contains(f.Rule.Id, StringComparer.OrdinalIgnoreCase)
            : f.Rule.Safety == Safety.Safe || includeCaution)).ToList();

        if (ids is not null)
        {
            foreach (var id in ids.Where(id => !selected.Any(f => f.Rule.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
            {
                var f = findings.FirstOrDefault(x => x.Rule.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                Console.WriteLine($"Skipping {id}: {(f is null ? "unknown id" : f.StatusText)}");
            }
        }

        if (selected.Count == 0)
        {
            Console.WriteLine("Nothing to clean.");
            return 0;
        }

        Console.WriteLine("Will clean:");
        foreach (var f in selected)
            Console.WriteLine($"  {f.Rule.Name,-40} {(f.SizeKnown ? ByteSize.Format(f.Bytes) : "?"),10}");
        Console.WriteLine($"  {"Total",-40} {ByteSize.Format(selected.Sum(f => f.Bytes)),10}");

        if (dryRun)
            return 0;
        if (!yes)
        {
            if (Console.IsInputRedirected)
            {
                Console.Error.WriteLine("Add --yes to clean without a prompt.");
                return 1;
            }
            Console.Write("Continue? [y/N] ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                return 1;
        }

        long freed = 0;
        foreach (var f in selected)
        {
            Console.Write($"{f.Rule.Name}... ");
            var r = await Cleaner.CleanAsync(f, null, ct);
            freed += r.FreedBytes;
            Console.WriteLine(r.Succeeded ? $"{(r.FreedKnown ? ByteSize.Format(r.FreedBytes) : "done")}. {r.Message}" : $"failed: {r.Message}");
        }
        Console.WriteLine($"\nFreed {ByteSize.Format(freed)}.");
        return 0;
    }

    private static async Task<int> Scan(string[] args, CancellationToken ct)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: deskmaster-cli scan <path> [--top 20]");
            return 2;
        }
        var top = int.TryParse(Option(args, "--top"), out var n) ? n : 20;
        var progress = new Progress<ScanProgress>(p =>
        {
            if (!Console.IsOutputRedirected)
                Console.Write($"\r{p.Files:N0} files, {ByteSize.Format(p.Bytes)}...".PadRight(50));
        });

        var result = await DiskScanner.ScanAsync(args[1], progress, new ScanOptions { TopFilesCount = top }, ct);
        if (!Console.IsOutputRedirected)
            Console.Write("\r".PadRight(51) + "\r");

        var root = result.Root;
        Console.WriteLine($"{root.FullPath}: {ByteSize.Format(root.Size)} in {root.FileCount:N0} files ({result.Duration.TotalSeconds:0.0}s, {result.Errors} unreadable folders)\n");
        Console.WriteLine("Largest items:");
        foreach (var c in root.Children.Take(top))
            Console.WriteLine($"  {ByteSize.Format(c.Size),10}  {Bar(root.Size > 0 ? (double)c.Size / root.Size : 0, 16)}  {c.Name}{(c.IsDirectory ? Path.DirectorySeparatorChar.ToString() : "")}");
        Console.WriteLine("\nLargest files:");
        foreach (var f in result.LargestFiles.Take(top))
            Console.WriteLine($"  {ByteSize.Format(f.Size),10}  {f.FullPath}");
        return 0;
    }

    private static async Task<int> Movable(CancellationToken ct)
    {
        var candidates = await RelocationService.DetectAsync(ct);
        if (candidates.Count == 0)
        {
            Console.WriteLine("No movable app folders found.");
            return 0;
        }
        foreach (var c in candidates)
            Console.WriteLine($"{c.Definition.Id,-18} {ByteSize.Format(c.Bytes),10}  {c.CurrentPath}{(c.IsOnSystemDrive ? "" : "  (already off the system drive)")}");
        return 0;
    }

    private static async Task<int> Move(string[] args, CancellationToken ct)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: deskmaster-cli move <id> <destination> [--yes]");
            return 2;
        }
        var candidates = await RelocationService.DetectAsync(ct);
        var candidate = candidates.FirstOrDefault(c => c.Definition.Id.Equals(args[1], StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
        {
            Console.Error.WriteLine($"'{args[1]}' was not found. Run 'deskmaster-cli movable' to list ids.");
            return 1;
        }

        var target = Path.Combine(args[2], candidate.Definition.TargetFolderName);
        Console.WriteLine($"Move {candidate.Definition.Name} ({ByteSize.Format(candidate.Bytes)})\n  from {candidate.CurrentPath}\n  to   {Path.GetFullPath(target)}");
        if (!args.Contains("--yes"))
        {
            Console.Write("Continue? [y/N] ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                return 1;
        }

        var progress = new Progress<DeskMaster.Core.IO.CopyProgress>(p =>
        {
            if (!Console.IsOutputRedirected && p.TotalBytes > 0)
                Console.Write($"\r{Bar((double)p.CopiedBytes / p.TotalBytes, 30)} {ByteSize.Format(p.CopiedBytes)}".PadRight(60));
        });
        var result = await RelocationService.MoveAsync(candidate, args[2], progress, ct);
        Console.WriteLine();
        Console.WriteLine(result.Message);
        return result.Succeeded ? 0 : 1;
    }

    private static async Task<int> MoveFolder(string[] args, CancellationToken ct)
    {
        if (args.Length < 3 || !Enum.TryParse<UserFolder>(args[1], ignoreCase: true, out var folder))
        {
            Console.Error.WriteLine("Usage: deskmaster-cli move-folder <downloads|videos|pictures|music> <destination> [--set-default] [--yes]");
            return 2;
        }

        var source = UserFolders.GetPath(folder);
        var target = Path.Combine(args[2], folder.ToString());
        var plan = FolderMover.Plan(source);
        Console.WriteLine($"Move {plan.MovableItems} items ({ByteSize.Format(plan.MovableBytes)})\n  from {source}\n  to   {Path.GetFullPath(target)}");
        if (plan.LeftItems > 0)
            Console.WriteLine($"  {plan.LeftItems} unfinished downloads/system files ({ByteSize.Format(plan.LeftBytes)}) stay where they are.");
        if (!args.Contains("--yes"))
        {
            Console.Write("Continue? [y/N] ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
                return 1;
        }

        var progress = new Progress<FolderMoveProgress>(p =>
        {
            if (!Console.IsOutputRedirected && p.Total > 0)
                Console.Write($"\r{Bar((double)p.Done / p.Total, 30)} {p.Done}/{p.Total} {p.CurrentName}".PadRight(80)[..80]);
        });
        var result = await FolderMover.MoveContentsAsync(source, target, progress, ct);
        Console.WriteLine();
        Console.WriteLine($"Moved {result.Moved + result.Duplicates} items. {(result.Duplicates > 0 ? $"{result.Duplicates} name clashes are in {result.DuplicatesFolder}." : "")}");
        foreach (var e in result.Errors)
            Console.Error.WriteLine("  failed: " + e);

        var exit = result.Succeeded ? 0 : 1;
        if (args.Contains("--set-default"))
        {
            var ok = UserFolders.TrySetPath(folder, target, out var note);
            Console.WriteLine(note);
            if (!ok)
                exit = 1;
        }
        return exit;
    }

    private static async Task<int> Duplicates(string[] args, CancellationToken ct)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: deskmaster-cli duplicates <path> [--min-mb 1]");
            return 2;
        }
        var minMb = int.TryParse(Option(args, "--min-mb"), out var mb) ? mb : 1;
        var progress = new Progress<DeskMaster.Core.Duplicates.DuplicateProgress>(p =>
        {
            if (!Console.IsOutputRedirected)
                Console.Write($"\r{p.Stage}: {p.Done:N0}{(p.Total > 0 ? $" of {p.Total:N0}" : "")} files".PadRight(60));
        });
        var groups = await DeskMaster.Core.Duplicates.DuplicateFinder.FindAsync([args[1]],
            new DeskMaster.Core.Duplicates.DuplicateOptions { MinSize = minMb * 1024L * 1024 }, progress, ct);
        if (!Console.IsOutputRedirected)
            Console.Write("\r".PadRight(61) + "\r");

        foreach (var g in groups)
        {
            Console.WriteLine($"{ByteSize.Format(g.Size)} x {g.Files.Count} ({ByteSize.Format(g.WastedBytes)} wasted)");
            foreach (var f in g.Files)
                Console.WriteLine("    " + f.Path);
        }
        Console.WriteLine($"\n{groups.Count} sets of duplicates; {ByteSize.Format(groups.Sum(g => g.WastedBytes))} could be freed.");
        return 0;
    }

    private static int Rules()
    {
        foreach (var group in RuleCatalog.LoadForCurrentOs().GroupBy(r => r.Category))
        {
            Console.WriteLine(group.Key);
            foreach (var r in group)
                Console.WriteLine($"  {r.Id,-28} {r.Safety,-8}{(r.RequiresAdmin ? "admin  " : "       ")}{r.Name}");
        }
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static string Bar(double fraction, int width)
    {
        var filled = (int)Math.Round(Math.Clamp(fraction, 0, 1) * width);
        return "[" + new string('#', filled) + new string('.', width - filled) + "]";
    }
}
