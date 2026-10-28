using System.Text.RegularExpressions;

namespace DeskMaster.Core.Platform;

/// <summary>
/// Turns rule path templates such as <c>{LocalAppData}/Google/Chrome/User Data/*/Cache</c>
/// into real, existing paths on this machine.
/// </summary>
/// <remarks>
/// Templates use forward slashes, <c>{Token}</c> placeholders (see <see cref="CurrentTokens"/>),
/// optional <c>%ENV_VAR%</c> variables and <c>*</c>/<c>?</c> wildcards inside any path segment.
/// A template with a token that does not exist on this OS resolves to nothing.
/// </remarks>
public static partial class PathResolver
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> LazyTokens = new(BuildTokens);

    public static IReadOnlyDictionary<string, string> CurrentTokens => LazyTokens.Value;

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"%[^%\s]+%")]
    private static partial Regex UnexpandedEnvRegex();

    /// <summary>Replaces tokens and environment variables. Returns null if any token is unknown.</summary>
    public static string? Expand(string template, IReadOnlyDictionary<string, string>? tokens = null)
    {
        tokens ??= CurrentTokens;
        var unknown = false;
        var result = TokenRegex().Replace(template, m =>
        {
            if (TryGetToken(tokens, m.Groups[1].Value, out var value))
                return value.TrimEnd('/', '\\');
            unknown = true;
            return m.Value;
        });
        if (unknown)
            return null;

        result = Environment.ExpandEnvironmentVariables(result);
        if (UnexpandedEnvRegex().IsMatch(result))
            return null;

        result = result.Replace('\\', '/');
        if (Path.DirectorySeparatorChar != '/')
            result = result.Replace('/', Path.DirectorySeparatorChar);
        return result;
    }

    /// <summary>Expands a template and returns every existing file or folder it matches.</summary>
    public static IReadOnlyList<string> Resolve(string template, IReadOnlyDictionary<string, string>? tokens = null)
    {
        var expanded = Expand(template, tokens);
        if (expanded is null || !Path.IsPathFullyQualified(expanded))
            return [];
        return Glob(expanded);
    }

    /// <summary>Resolves the templates of an OS-keyed map ("windows", "macos", "linux" or "all").</summary>
    public static IReadOnlyList<string> ResolveForCurrentOs(
        IReadOnlyDictionary<string, List<string>>? map,
        IReadOnlyDictionary<string, string>? tokens = null)
    {
        if (map is null)
            return [];

        var results = new List<string>();
        var seen = new HashSet<string>(PlatformInfo.PathComparer);
        foreach (var template in TemplatesForCurrentOs(map))
        {
            foreach (var path in Resolve(template, tokens))
            {
                if (seen.Add(path))
                    results.Add(path);
            }
        }
        return results;
    }

    public static IEnumerable<string> TemplatesForCurrentOs(IReadOnlyDictionary<string, List<string>> map)
    {
        foreach (var (key, templates) in map)
        {
            if (key.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                key.Equals(PlatformInfo.OsKey, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var t in templates)
                    yield return t;
            }
        }
    }

    internal static IReadOnlyList<string> Glob(string path)
    {
        if (path.IndexOfAny(['*', '?']) < 0)
            return File.Exists(path) || Directory.Exists(path) ? [path] : [];

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
            return [];

        var segments = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchType = MatchType.Simple,
        };

        IEnumerable<string> current = [root];
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var isLast = i == segments.Length - 1;
            var next = new List<string>();
            foreach (var dir in current)
            {
                try
                {
                    if (segment.IndexOfAny(['*', '?']) >= 0)
                    {
                        next.AddRange(isLast
                            ? Directory.EnumerateFileSystemEntries(dir, segment, options)
                            : Directory.EnumerateDirectories(dir, segment, options));
                    }
                    else
                    {
                        var candidate = Path.Combine(dir, segment);
                        if (Directory.Exists(candidate) || (isLast && File.Exists(candidate)))
                            next.Add(candidate);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Skip folders we cannot list.
                }
            }
            current = next;
        }
        return current.ToList();
    }

    private static bool TryGetToken(IReadOnlyDictionary<string, string> tokens, string name, out string value)
    {
        foreach (var (key, v) in tokens)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(v))
            {
                value = v;
                return true;
            }
        }
        value = "";
        return false;
    }

    private static IReadOnlyDictionary<string, string> BuildTokens()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Home"] = home,
            ["Temp"] = Path.GetTempPath(),
            ["Downloads"] = UserFolders.GetPath(UserFolder.Downloads),
        };

        switch (PlatformInfo.Os)
        {
            case OsKind.Windows:
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                tokens["LocalAppData"] = local;
                tokens["AppData"] = roaming;
                tokens["ProgramData"] = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                tokens["WinDir"] = windir;
                tokens["SystemDrive"] = Path.GetPathRoot(windir) ?? "C:\\";
                tokens["Cache"] = local;
                tokens["Config"] = roaming;
                tokens["Data"] = local;
                break;
            case OsKind.MacOS:
                tokens["Cache"] = Path.Combine(home, "Library", "Caches");
                tokens["Config"] = Path.Combine(home, "Library", "Application Support");
                tokens["Data"] = Path.Combine(home, "Library", "Application Support");
                tokens["Library"] = Path.Combine(home, "Library");
                break;
            default:
                tokens["Cache"] = XdgDir("XDG_CACHE_HOME", Path.Combine(home, ".cache"));
                tokens["Config"] = XdgDir("XDG_CONFIG_HOME", Path.Combine(home, ".config"));
                tokens["Data"] = XdgDir("XDG_DATA_HOME", Path.Combine(home, ".local", "share"));
                break;
        }
        return tokens;
    }

    private static string XdgDir(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathFullyQualified(value) ? value : fallback;
    }
}
