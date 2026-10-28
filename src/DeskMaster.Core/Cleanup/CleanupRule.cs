using DeskMaster.Core.Platform;

namespace DeskMaster.Core.Cleanup;

public enum Safety
{
    /// <summary>Caches and leftovers that apps rebuild by themselves.</summary>
    Safe,

    /// <summary>Works, but has a side effect the user should understand first.</summary>
    Caution,
}

public enum CleanMode
{
    /// <summary>Delete everything inside the path, keep the folder itself.</summary>
    Contents,

    /// <summary>Delete the matched file or folder itself.</summary>
    Self,
}

/// <summary>A command that cleans better than deleting files (e.g. <c>npm cache clean</c>).</summary>
public sealed class CommandSpec
{
    public string File { get; set; } = "";
    public List<string> Args { get; set; } = [];
}

/// <summary>
/// One cleanup recipe. Rules are data (see <c>Data/cleanup-rules.json</c>) so anyone can
/// contribute new ones without touching code.
/// </summary>
public sealed class CleanupRule
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "Other";
    public Safety Safety { get; set; } = Safety.Safe;
    public bool RequiresAdmin { get; set; }

    /// <summary>OS key ("windows", "macos", "linux" or "all") to path templates.</summary>
    public Dictionary<string, List<string>> Paths { get; set; } = [];

    /// <summary>Optional explicit platform list; otherwise derived from <see cref="Paths"/> keys.</summary>
    public List<string>? Platforms { get; set; }

    public CleanMode Mode { get; set; } = CleanMode.Contents;

    /// <summary>Only delete files not modified for this many hours.</summary>
    public int MinAgeHours { get; set; }

    /// <summary>Optional file name patterns (e.g. "*.log"); everything when empty.</summary>
    public List<string>? Include { get; set; }

    /// <summary>Process names that must not be running while cleaning (e.g. "chrome").</summary>
    public List<string>? CloseProcesses { get; set; }

    /// <summary>When set and the executable is on PATH, this runs instead of deleting files.</summary>
    public CommandSpec? Command { get; set; }

    /// <summary>Paths are only measured to estimate the gain; they are never deleted directly.</summary>
    public bool EstimateOnly { get; set; }

    /// <summary>Built-in special cleaner, e.g. "recycle-bin".</summary>
    public string? Handler { get; set; }

    public string? Notes { get; set; }

    /// <summary>Safe, no admin: pre-selected in Simple mode.</summary>
    public bool IsRecommended => Safety == Safety.Safe && !RequiresAdmin;

    public bool AppliesTo(OsKind os)
    {
        var key = PlatformInfo.KeyFor(os);
        var platforms = Platforms is { Count: > 0 } ? Platforms : Paths.Keys.ToList();
        if (platforms.Count == 0 && Command is not null)
            return true;
        return platforms.Any(p => p.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                                  p.Equals(key, StringComparison.OrdinalIgnoreCase));
    }
}
