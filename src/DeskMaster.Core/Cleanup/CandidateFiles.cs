using System.IO.Enumeration;

namespace DeskMaster.Core.Cleanup;

/// <summary>Lists the files a rule would delete under one resolved path. Never follows links.</summary>
internal static class CandidateFiles
{
    private static readonly EnumerationOptions RecursiveOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    public static IEnumerable<FileInfo> Enumerate(string path, CleanupRule rule, DateTime nowUtc)
    {
        var cutoff = rule.MinAgeHours > 0 ? nowUtc.AddHours(-rule.MinAgeHours) : DateTime.MaxValue;

        if (File.Exists(path))
        {
            var file = new FileInfo(path);
            if (rule.Mode == CleanMode.Self ? cutoff == DateTime.MaxValue || IsOld(file, cutoff) : Matches(file, rule, cutoff))
                yield return file;
            yield break;
        }

        if (!Directory.Exists(path))
            yield break;

        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(path).EnumerateFiles("*", RecursiveOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        using var enumerator = files.GetEnumerator();
        while (true)
        {
            FileInfo current;
            try
            {
                if (!enumerator.MoveNext())
                    yield break;
                current = enumerator.Current;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                yield break;
            }

            if (rule.Mode == CleanMode.Self ? cutoff == DateTime.MaxValue || IsOld(current, cutoff) : Matches(current, rule, cutoff))
                yield return current;
        }
    }

    private static bool Matches(FileInfo file, CleanupRule rule, DateTime cutoff)
    {
        if (cutoff != DateTime.MaxValue && !IsOld(file, cutoff))
            return false;
        if (rule.Include is not { Count: > 0 })
            return true;
        foreach (var pattern in rule.Include)
        {
            if (FileSystemName.MatchesSimpleExpression(pattern, file.Name, ignoreCase: true))
                return true;
        }
        return false;
    }

    private static bool IsOld(FileSystemInfo info, DateTime cutoff)
    {
        try
        {
            return info.LastWriteTimeUtc < cutoff;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Folders under <paramref name="root"/>, deepest first, for removing ones left empty.</summary>
    public static IEnumerable<DirectoryInfo> DirectoriesDeepestFirst(string root)
    {
        List<DirectoryInfo> dirs;
        try
        {
            dirs = new DirectoryInfo(root).EnumerateDirectories("*", RecursiveOptions).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        return dirs.OrderByDescending(d => d.FullName.Length);
    }
}
