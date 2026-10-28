using System.Diagnostics;
using DeskMaster.Core.Platform;

namespace DeskMaster.Core.Scanning;

public sealed class ScanOptions
{
    /// <summary>Each folder keeps this many of its largest files as separate entries.</summary>
    public int FilesKeptPerFolder { get; init; } = 40;

    /// <summary>How many of the largest files across the whole scan to report.</summary>
    public int TopFilesCount { get; init; } = 200;

    /// <summary>Folders scanned in parallel down to this depth.</summary>
    public int ParallelDepth { get; init; } = 3;

    /// <summary>
    /// Folders where files with several hard links are counted once (Windows). The Windows folder
    /// shares most of its files with WinSxS, which would otherwise be counted twice.
    /// Checking costs one file open per file, so it is limited to these folders.
    /// </summary>
    public IReadOnlyList<string> HardLinkFolders { get; init; } =
        PlatformInfo.Os == OsKind.Windows ? [Environment.GetFolderPath(Environment.SpecialFolder.Windows)] : [];

    /// <summary>Paths never entered (virtual file systems, firmlinks).</summary>
    public ISet<string> ExcludedPaths { get; init; } = DefaultExclusions();

    public static ISet<string> DefaultExclusions()
    {
        var set = new HashSet<string>(PlatformInfo.PathComparer);
        switch (PlatformInfo.Os)
        {
            case OsKind.Linux:
                foreach (var p in new[] { "/proc", "/sys", "/dev", "/run", "/snap", "/var/lib/docker/overlay2" })
                    set.Add(p);
                break;
            case OsKind.MacOS:
                foreach (var p in new[] { "/System/Volumes", "/Volumes", "/dev", "/private/var/vm" })
                    set.Add(p);
                break;
        }
        return set;
    }
}

public sealed record ScanProgress(long Files, long Folders, long Bytes, string CurrentPath);

public sealed class ScanResult
{
    public required ScanNode Root { get; init; }
    public required IReadOnlyList<ScanNode> LargestFiles { get; init; }
    public long Errors { get; init; }
    public TimeSpan Duration { get; init; }
}

/// <summary>Builds a size tree of a folder or a whole drive, in parallel, without following links.</summary>
public sealed class DiskScanner
{
    private const int FileAttributeRecallOnDataAccess = 0x00400000;

    // Not IgnoreInaccessible: a folder we may not open must be reported (Incomplete), not skipped silently.
    private static readonly EnumerationOptions ListOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private readonly ScanOptions _options;
    private readonly TopFiles _topFiles;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(uint, ulong), byte> _hardLinks = new();
    private long _files, _folders, _bytes, _errors;
    private string _currentPath = "";

    public DiskScanner(ScanOptions? options = null)
    {
        _options = options ?? new ScanOptions();
        _topFiles = new TopFiles(_options.TopFilesCount);
    }

    public static Task<ScanResult> ScanAsync(string rootPath, IProgress<ScanProgress>? progress = null,
        ScanOptions? options = null, CancellationToken cancellationToken = default) =>
        new DiskScanner(options).RunAsync(rootPath, progress, cancellationToken);

    public async Task<ScanResult> RunAsync(string rootPath, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        var dir = new DirectoryInfo(rootPath);
        if (!dir.Exists)
            throw new DirectoryNotFoundException(rootPath);

        var stopwatch = Stopwatch.StartNew();
        var root = new ScanNode(DisplayName(dir), dir.FullName, ScanNodeKind.Directory, null);

        using var timer = progress is null ? null : new Timer(_ => Report(progress), null, 0, 200);
        await Task.Run(() => ScanDirectory(dir, root, 0, cancellationToken), cancellationToken).ConfigureAwait(false);
        progress?.Report(new ScanProgress(_files, _folders, _bytes, rootPath));

        return new ScanResult
        {
            Root = root,
            LargestFiles = _topFiles.ToSortedList(),
            Errors = _errors,
            Duration = stopwatch.Elapsed,
        };
    }

    private void Report(IProgress<ScanProgress> progress) =>
        progress.Report(new ScanProgress(Interlocked.Read(ref _files), Interlocked.Read(ref _folders),
            Interlocked.Read(ref _bytes), Volatile.Read(ref _currentPath)));

    private void ScanDirectory(DirectoryInfo dir, ScanNode node, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _folders);
        Volatile.Write(ref _currentPath, dir.FullName);

        var subDirs = new List<(DirectoryInfo Info, ScanNode Node)>();
        var files = new List<FileInfo>();
        try
        {
            node.LastWriteUtc = dir.LastWriteTimeUtc;
            foreach (var entry in dir.EnumerateFileSystemInfos("*", ListOptions))
            {
                if (entry is DirectoryInfo sub)
                {
                    // Junctions and symlinks point at data counted elsewhere.
                    if ((sub.Attributes & FileAttributes.ReparsePoint) != 0 || _options.ExcludedPaths.Contains(sub.FullName))
                        continue;
                    subDirs.Add((sub, new ScanNode(sub.Name, sub.FullName, ScanNodeKind.Directory, node)));
                }
                else if (entry is FileInfo file)
                {
                    // Cloud-only placeholders (OneDrive, iCloud) take no local space.
                    var attributes = (int)file.Attributes;
                    if ((attributes & ((int)FileAttributes.Offline | FileAttributeRecallOnDataAccess)) != 0)
                        continue;
                    files.Add(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            node.Incomplete = true;
            Interlocked.Increment(ref _errors);
        }

        if (depth < _options.ParallelDepth && subDirs.Count > 1)
        {
            Parallel.ForEach(subDirs, new ParallelOptions { CancellationToken = ct },
                s => ScanDirectory(s.Info, s.Node, depth + 1, ct));
        }
        else
        {
            foreach (var s in subDirs)
                ScanDirectory(s.Info, s.Node, depth + 1, ct);
        }

        var children = new List<ScanNode>(subDirs.Count + Math.Min(files.Count, _options.FilesKeptPerFolder) + 1);
        long size = 0, count = 0;
        foreach (var (_, child) in subDirs)
        {
            children.Add(child);
            size += child.Size;
            count += child.FileCount;
            if (child.Incomplete)
                node.Incomplete = true;
        }

        var fileEntries = new List<(FileInfo Info, long Length)>(files.Count);
        long filesBytes = 0;
        var checkLinks = OperatingSystem.IsWindows() && InHardLinkFolder(dir.FullName);
        foreach (var f in files)
        {
            long length;
            try
            {
                length = DiskLength(f, checkLinks);
            }
            catch (IOException)
            {
                continue;
            }
            fileEntries.Add((f, length));
            filesBytes += length;
        }
        Interlocked.Add(ref _files, fileEntries.Count);
        Interlocked.Add(ref _bytes, filesBytes);

        fileEntries.Sort((a, b) => b.Length.CompareTo(a.Length));
        long smallBytes = 0, smallCount = 0;
        for (var i = 0; i < fileEntries.Count; i++)
        {
            var (info, length) = fileEntries[i];
            if (i < _options.FilesKeptPerFolder)
            {
                var fileNode = new ScanNode(info.Name, info.FullName, ScanNodeKind.File, node)
                {
                    Size = length,
                    FileCount = 1,
                    LastWriteUtc = SafeLastWrite(info),
                };
                children.Add(fileNode);
                _topFiles.Offer(fileNode);
            }
            else
            {
                smallBytes += length;
                smallCount++;
            }
        }

        if (smallCount > 0)
        {
            children.Add(new ScanNode($"{smallCount:N0} smaller files", node.FullPath, ScanNodeKind.SmallFiles, node)
            {
                Size = smallBytes,
                FileCount = smallCount,
            });
        }

        node.Size = size + filesBytes;
        node.FileCount = count + fileEntries.Count;
        node.SetChildren(children);
    }

    /// <summary>Space the file really takes: compressed/sparse size, and 0 for extra hard links.</summary>
    private long DiskLength(FileInfo file, bool checkLinks)
    {
        var length = file.Length;
        if (!OperatingSystem.IsWindows())
            return length;

        if ((file.Attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) != 0 &&
            WindowsFileInfo.AllocatedSize(file.FullName) is { } allocated)
        {
            length = Math.Min(length, allocated);
        }

        if (checkLinks && WindowsFileInfo.HardLinkId(file.FullName) is { } id && !_hardLinks.TryAdd(id, 0))
            return 0; // Another name of a file already counted.
        return length;
    }

    private bool InHardLinkFolder(string path)
    {
        foreach (var folder in _options.HardLinkFolders)
        {
            if (!string.IsNullOrEmpty(folder) &&
                (path.Equals(folder, PlatformInfo.PathComparison) || path.StartsWith(folder + Path.DirectorySeparatorChar, PlatformInfo.PathComparison)))
                return true;
        }
        return false;
    }

    private static DateTime SafeLastWrite(FileInfo info)
    {
        try
        {
            return info.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return default;
        }
    }

    private static string DisplayName(DirectoryInfo dir) =>
        dir.Parent is null ? dir.FullName : dir.Name;

    /// <summary>Thread-safe bounded collection of the largest files seen.</summary>
    private sealed class TopFiles(int capacity)
    {
        private readonly PriorityQueue<ScanNode, long> _heap = new();
        private readonly Lock _lock = new();

        public void Offer(ScanNode file)
        {
            if (capacity <= 0)
                return;
            lock (_lock)
            {
                if (_heap.Count < capacity)
                {
                    _heap.Enqueue(file, file.Size);
                }
                else if (_heap.TryPeek(out _, out var smallest) && file.Size > smallest)
                {
                    _heap.DequeueEnqueue(file, file.Size);
                }
            }
        }

        public IReadOnlyList<ScanNode> ToSortedList()
        {
            lock (_lock)
                return _heap.UnorderedItems.Select(x => x.Element).OrderByDescending(n => n.Size).ToList();
        }
    }
}
