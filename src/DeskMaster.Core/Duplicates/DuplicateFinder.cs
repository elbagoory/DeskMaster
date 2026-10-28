using System.Collections.Concurrent;
using System.IO.Hashing;
using DeskMaster.Core.Platform;

namespace DeskMaster.Core.Duplicates;

public sealed record DuplicateFile(string Path, DateTime LastWriteUtc);

/// <summary>Files with identical content.</summary>
public sealed class DuplicateGroup
{
    public required long Size { get; init; }
    public required IReadOnlyList<DuplicateFile> Files { get; init; }

    /// <summary>Space freed by keeping just one copy.</summary>
    public long WastedBytes => Size * (Files.Count - 1);
}

public sealed record DuplicateProgress(string Stage, long Done, long Total);

public sealed class DuplicateOptions
{
    /// <summary>Smaller files are ignored; tiny duplicates rarely matter.</summary>
    public long MinSize { get; init; } = 1024 * 1024;

    /// <summary>The OS and program folders are skipped: their duplicates are intentional.</summary>
    public bool SkipSystemFolders { get; init; } = true;
}

/// <summary>
/// Finds duplicate files in three passes: same size, then same hash of the first and last
/// 64 KB, then same full XxHash128. Only full-hash matches are reported.
/// </summary>
public static class DuplicateFinder
{
    private const int SampleSize = 64 * 1024;

    public static async Task<IReadOnlyList<DuplicateGroup>> FindAsync(IEnumerable<string> roots,
        DuplicateOptions? options = null, IProgress<DuplicateProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new DuplicateOptions();
        return await Task.Run(() => Find(roots.ToList(), options, progress, ct), ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<DuplicateGroup> Find(List<string> roots, DuplicateOptions options,
        IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        // 1. Group by size.
        var bySize = new Dictionary<long, List<FileInfo>>();
        var seen = new HashSet<string>(PlatformInfo.PathComparer);
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline,
        };
        long listed = 0;
        foreach (var root in roots)
        {
            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(root).EnumerateFiles("*", enumeration);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                if (++listed % 2000 == 0)
                    progress?.Report(new DuplicateProgress("Listing files", listed, 0));
                long length;
                try { length = file.Length; } catch (IOException) { continue; }
                if (length < options.MinSize || !seen.Add(file.FullName))
                    continue;
                if (options.SkipSystemFolders && !SafetyGuard.IsSafeToDelete(file.FullName, out _))
                    continue;
                if (!bySize.TryGetValue(length, out var list))
                    bySize[length] = list = [];
                list.Add(file);
            }
        }

        var candidates = bySize.Where(kv => kv.Value.Count > 1).ToList();
        var total = candidates.Sum(kv => (long)kv.Value.Count);
        long hashed = 0;

        // 2 + 3. Quick hash, then full hash, in parallel.
        var groups = new ConcurrentBag<DuplicateGroup>();
        Parallel.ForEach(candidates, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 }, kv =>
        {
            var (size, files) = (kv.Key, kv.Value);
            foreach (var quick in GroupBy(files, f => SampleHash(f.FullName, size)).Where(g => g.Count > 1))
            {
                foreach (var full in GroupBy(quick, f => FullHash(f.FullName, ct)).Where(g => g.Count > 1))
                {
                    groups.Add(new DuplicateGroup
                    {
                        Size = size,
                        Files = full.Select(f => new DuplicateFile(f.FullName, f.LastWriteTimeUtc))
                            .OrderByDescending(f => f.LastWriteUtc).ToList(),
                    });
                }
            }
            var done = Interlocked.Add(ref hashed, files.Count);
            progress?.Report(new DuplicateProgress("Comparing contents", done, total));
        });

        return groups.OrderByDescending(g => g.WastedBytes).ToList();
    }

    private static List<List<FileInfo>> GroupBy(IEnumerable<FileInfo> files, Func<FileInfo, string?> key)
    {
        var map = new Dictionary<string, List<FileInfo>>();
        foreach (var f in files)
        {
            string? k;
            try { k = key(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (k is null)
                continue;
            if (!map.TryGetValue(k, out var list))
                map[k] = list = [];
            list.Add(f);
        }
        return map.Values.ToList();
    }

    private static string SampleHash(string path, long size)
    {
        var hash = new XxHash128();
        var buffer = new byte[SampleSize];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
        var read = stream.ReadAtLeast(buffer, (int)Math.Min(SampleSize, size), throwOnEndOfStream: false);
        hash.Append(buffer.AsSpan(0, read));
        if (size > 2 * SampleSize)
        {
            stream.Seek(-SampleSize, SeekOrigin.End);
            read = stream.ReadAtLeast(buffer, SampleSize, throwOnEndOfStream: false);
            hash.Append(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(hash.GetCurrentHash());
    }

    private static string FullHash(string path, CancellationToken ct) =>
        Convert.ToHexString(IO.FileOps.HashFile(path, null, ct));
}
