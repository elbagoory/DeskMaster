using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace DeskMaster.App.Services;

/// <summary>
/// Updates from GitHub Releases (published by .github/workflows/release.yml with Velopack).
/// Only works for installed copies; portable or development runs simply report no updates.
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateManager? _manager;

    public UpdateService()
    {
        var repo = typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value;
        if (string.IsNullOrWhiteSpace(repo))
            return;
        try
        {
            _manager = new UpdateManager(new GithubSource(repo, null, false));
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            _manager = null;
        }
    }

    public bool CanUpdate => _manager?.IsInstalled == true;

    public string CurrentVersion =>
        _manager?.CurrentVersion?.ToString() ?? typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>The newer version available, or null.</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        if (!CanUpdate)
            return null;
        try
        {
            return await _manager!.CheckForUpdatesAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            return null; // Offline or GitHub unavailable: try again next start.
        }
    }

    public async Task DownloadAndRestartAsync(UpdateInfo update, Action<int>? progress = null)
    {
        await _manager!.DownloadUpdatesAsync(update, progress);
        _manager.ApplyUpdatesAndRestart(update);
    }
}
