using System.Diagnostics;

namespace DeskMaster.Core.Processes;

public static class ProcessGuard
{
    private static readonly Dictionary<string, string> FriendlyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["devenv"] = "Visual Studio",
        ["msedge"] = "Microsoft Edge",
        ["chrome"] = "Google Chrome",
        ["brave"] = "Brave",
        ["firefox"] = "Firefox",
        ["Code"] = "VS Code",
        ["studio64"] = "Android Studio",
        ["java"] = "Java/Gradle",
        ["ms-teams"] = "Microsoft Teams",
        ["ollama"] = "Ollama",
        ["ollama app"] = "Ollama",
        ["dotnet"] = ".NET processes",
    };

    /// <summary>A name people recognise, e.g. "devenv" becomes "Visual Studio".</summary>
    public static string FriendlyName(string processName) =>
        FriendlyNames.TryGetValue(processName, out var friendly) ? friendly : processName;

    public static string FriendlyList(IEnumerable<string> processNames) =>
        string.Join(", ", processNames.Select(FriendlyName).Distinct());

    /// <summary>Returns the names from <paramref name="processNames"/> that are currently running.</summary>
    public static IReadOnlyList<string> FindRunning(IEnumerable<string>? processNames)
    {
        if (processNames is null)
            return [];

        var running = new List<string>();
        foreach (var name in processNames)
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            if (processes.Length > 0)
                running.Add(name);
            foreach (var p in processes)
                p.Dispose();
        }
        return running;
    }
}
