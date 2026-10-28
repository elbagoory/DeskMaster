using static DeskMaster.Core.Localization.Text;
namespace DeskMaster.Core.Platform;

/// <summary>Sets environment variables that survive a restart, for the current user.</summary>
public static class UserEnvironment
{
    private const string Marker = "# Added by DeskMaster";

    public static string? Get(string name) =>
        PlatformInfo.Os == OsKind.Windows
            ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
              ?? Environment.GetEnvironmentVariable(name)
            : Environment.GetEnvironmentVariable(name) ?? ReadFromProfile(name);

    /// <summary>
    /// Windows: user environment (registry). macOS/Linux: an export line in ~/.profile and
    /// ~/.zprofile. Returns a note for the user about when it takes effect.
    /// </summary>
    public static string Set(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value); // this process
        if (PlatformInfo.Os == OsKind.Windows)
        {
            Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
            return F("{0} is set. Restart the app that uses it (and any open terminals).", name);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var line = $"export {name}=\"{value.Replace("\"", "\\\"")}\" {Marker}";
        foreach (var profile in new[] { ".profile", ".zprofile" })
        {
            var path = Path.Combine(home, profile);
            if (profile == ".zprofile" && PlatformInfo.Os != OsKind.MacOS && !File.Exists(path))
                continue;
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
            lines.RemoveAll(l => l.StartsWith($"export {name}=", StringComparison.Ordinal) && l.Contains(Marker));
            lines.Add(line);
            File.WriteAllLines(path, lines);
        }

        if (PlatformInfo.Os == OsKind.Linux)
            return F("{0} is set in ~/.profile. Log out and back in. If the app runs as a systemd service, also add it to the service with 'systemctl edit'.", name);
        return F("{0} is set in ~/.zprofile. Log out and back in, then restart the app that uses it.", name);
    }

    private static string? ReadFromProfile(string name)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var profile in new[] { ".zprofile", ".profile" })
        {
            var path = Path.Combine(home, profile);
            if (!File.Exists(path))
                continue;
            var prefix = $"export {name}=\"";
            var line = File.ReadLines(path).LastOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal) && l.Contains(Marker));
            if (line is not null)
            {
                var end = line.LastIndexOf('"');
                if (end > prefix.Length)
                    return line[prefix.Length..end].Replace("\\\"", "\"");
            }
        }
        return null;
    }
}
