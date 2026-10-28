using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DeskMaster.App.Views;

namespace DeskMaster.App.Services;

/// <summary>What view models need from the UI layer (dialogs, pickers, pop-ups, shutdown).</summary>
public interface IUiServices
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false);
    Task AlertAsync(string title, string message);
    Task<string?> PickFolderAsync(string title);
    void ShowSettings();

    /// <summary>A pop-up in the screen corner; works while the main window is hidden in the tray.</summary>
    void Notify(string title, string message, string actionText, Action action);

    void Shutdown();
}

public sealed class UiServices(Window owner, Action quit, Func<Window> createSettingsWindow) : IUiServices
{
    private Window? _settings;

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive = false)
    {
        var dialog = new ConfirmWindow(title, message, confirmText, destructive);
        return owner.IsVisible ? await dialog.ShowDialog<bool>(owner) : await ShowStandalone(dialog);
    }

    public async Task AlertAsync(string title, string message)
    {
        var dialog = new ConfirmWindow(title, message, "OK", destructive: false, showCancel: false);
        if (owner.IsVisible)
            await dialog.ShowDialog<bool>(owner);
        else
            await ShowStandalone(dialog);
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public void ShowSettings()
    {
        if (_settings is { IsVisible: true })
        {
            _settings.Activate();
            return;
        }
        _settings = createSettingsWindow();
        _settings.Closed += (_, _) => _settings = null;
        if (owner.IsVisible)
            _settings.Show(owner);
        else
            _settings.Show();
    }

    public void Notify(string title, string message, string actionText, Action action) =>
        new NotificationWindow(title, message, actionText, action).Show();

    public void Shutdown() => quit();

    private static Task<bool> ShowStandalone(ConfirmWindow dialog)
    {
        var done = new TaskCompletionSource<bool>();
        dialog.Closed += (_, _) => done.TrySetResult(dialog.Result);
        dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.Show();
        return done.Task;
    }
}
