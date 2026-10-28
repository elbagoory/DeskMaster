using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DeskMaster.Core.Platform;

namespace DeskMaster.Core.Processes;

public sealed record CommandResult(int ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

public static class CommandRunner
{
    /// <summary>Finds an executable on PATH (adds PATHEXT extensions on Windows).</summary>
    public static string? FindExecutable(string file)
    {
        if (Path.IsPathFullyQualified(file))
            return File.Exists(file) ? file : null;

        var extensions = PlatformInfo.Os == OsKind.Windows
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        var hasExtension = Path.HasExtension(file);

        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (PlatformInfo.Os == OsKind.Windows)
            dirs.Insert(0, Environment.SystemDirectory);

        foreach (var dir in dirs)
        {
            try
            {
                if (hasExtension || PlatformInfo.Os != OsKind.Windows)
                {
                    var candidate = Path.Combine(dir.Trim('"'), file);
                    if (File.Exists(candidate))
                        return candidate;
                }
                if (!hasExtension)
                {
                    foreach (var ext in extensions)
                    {
                        var candidate = Path.Combine(dir.Trim('"'), file + ext.ToLowerInvariant());
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return null;
    }

    public static async Task<CommandResult> RunAsync(string executable, IEnumerable<string> args,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            info.ArgumentList.Add(a);

        // .cmd/.bat shims (npm, pnpm) must run through cmd.exe.
        if (PlatformInfo.Os == OsKind.Windows &&
            (executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            var original = info.ArgumentList.ToList();
            info.FileName = "cmd.exe";
            info.ArgumentList.Clear();
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(executable);
            foreach (var a in original)
                info.ArgumentList.Add(a);
        }

        var output = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return new CommandResult(-1, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            lock (output) output.AppendLine(cancellationToken.IsCancellationRequested ? "Cancelled." : "Timed out.");
            return new CommandResult(-1, output.ToString());
        }

        lock (output)
            return new CommandResult(process.ExitCode, output.ToString());
    }
}
