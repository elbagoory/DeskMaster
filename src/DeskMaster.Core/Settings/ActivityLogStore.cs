using System.Globalization;

namespace DeskMaster.Core.Settings;

public sealed record StoredLogEntry(DateTime Time, string Message, bool IsError);

/// <summary>Keeps the activity log in monthly text files so it survives restarts.</summary>
public sealed class ActivityLogStore(string folder)
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
    private readonly Lock _lock = new();

    public static ActivityLogStore Default { get; } = new(Path.Combine(AppSettings.Folder, "logs"));

    public string Folder { get; } = folder;

    public void Append(StoredLogEntry entry)
    {
        var line = $"{entry.Time.ToString(TimeFormat, CultureInfo.InvariantCulture)}\t{(entry.IsError ? "ERROR" : "INFO")}\t{entry.Message.Replace('\n', ' ').Replace('\r', ' ')}";
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllLines(FileFor(entry.Time), [line]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never break the app.
        }
    }

    /// <summary>The most recent entries, newest first.</summary>
    public IReadOnlyList<StoredLogEntry> LoadRecent(int max = 200)
    {
        if (!Directory.Exists(Folder))
            return [];
        var result = new List<StoredLogEntry>();
        foreach (var file in Directory.EnumerateFiles(Folder, "activity-*.log").OrderDescending())
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            for (var i = lines.Length - 1; i >= 0 && result.Count < max; i--)
            {
                var parts = lines[i].Split('\t', 3);
                if (parts.Length == 3 && DateTime.TryParseExact(parts[0], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    result.Add(new StoredLogEntry(time, parts[2], parts[1] == "ERROR"));
            }
            if (result.Count >= max)
                break;
        }
        return result;
    }

    private string FileFor(DateTime time) =>
        Path.Combine(Folder, $"activity-{time.ToString("yyyy-MM", CultureInfo.InvariantCulture)}.log");
}
